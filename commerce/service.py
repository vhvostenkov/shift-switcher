"""Independent Shift Switcher commerce service. Database and secrets stay in RF."""
import hashlib
import hmac
import json
import re
import secrets
import sqlite3
import time
from contextlib import closing
from http import HTTPStatus
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Lock
from urllib.parse import urlsplit, urlunsplit, parse_qsl, urlencode

from cryptography.fernet import Fernet
from cryptography.hazmat.primitives import serialization

from .ledger import Ledger, PRODUCT
from .tbank import TBankDemo, TBankLive
from .catalog import catalog_from
from .delivery import ensure_tables, enqueue


def digest(text):
    return hashlib.sha256(text.encode()).hexdigest()


class EntitlementError(ValueError):
    pass


class Commerce:
    def __init__(self, folder, config, *, bank=None):
        self.folder, self.config = Path(folder), config
        self.folder.mkdir(mode=0o700, parents=True, exist_ok=True)
        self.environment = config.get('environment','DEMO')
        if self.environment not in {'DEMO','LIVE'}: raise ValueError('invalid_environment')
        self.sandbox = self.environment == 'DEMO'
        required=['bank_approved','fresh_credentials_verified']
        if config.get('checkout_enabled'): required += ['receipt_delivery_verified','legal_terms_approved','live_checkout_approved']
        if not self.sandbox and not all(config.get(k) is True for k in required):
            raise ValueError('LIVE readiness is incomplete')
        self.products = catalog_from(config)
        if not self.sandbox and config.get('checkout_enabled') and any(not p.get(k) or p[k].endswith('-draft') for p in self.products.values() for k in ['terms_version','privacy_version']):
            raise ValueError('LIVE legal versions are not published')
        self.cipher = Fernet(config['encryption_key'].encode())
        signer = serialization.load_pem_private_key(config['signing_key'].encode(), password=None)
        self.ledger = Ledger(self.folder/'orders.sqlite', terminal=config['terminal'], password=config['password'],
            signing_key=signer, key_id=config['key_id'], sandbox=self.sandbox, products=self.products)
        self.bank = bank or (TBankDemo(config['terminal'], config['password'], ca_file=config.get('ca_file')) if self.sandbox else TBankLive(config['terminal'], config['password'], approved=True, ca_file=config.get('ca_file')))
        with closing(self.ledger.connect()) as db:
            db.executescript('''
                CREATE TABLE IF NOT EXISTS sessions (
                    hash TEXT PRIMARY KEY, request_id TEXT UNIQUE NOT NULL,
                    order_id TEXT UNIQUE NOT NULL REFERENCES orders(id),
                    encrypted_key TEXT NOT NULL, email TEXT NOT NULL,
                    payment_url TEXT, created INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS events (
                    id INTEGER PRIMARY KEY, at INTEGER NOT NULL, order_id TEXT,
                    kind TEXT NOT NULL, detail TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS receipt_tasks (
                    order_id TEXT PRIMARY KEY REFERENCES orders(id), status TEXT NOT NULL,
                    url TEXT, updated INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS consents (
                    order_id TEXT PRIMARY KEY REFERENCES orders(id),
                    terms_version TEXT NOT NULL, privacy_version TEXT NOT NULL,
                    accepted_at INTEGER NOT NULL
                );
            ''')
        with closing(self.ledger.connect()) as db: ensure_tables(db)
        self.limits, self.lock = {}, Lock()

    def limit(self, ip, group, maximum=30):
        now = int(time.time()//60)
        with self.lock:
            self.limits = {k:v for k,v in self.limits.items() if k[2] >= now-1}
            key = (ip, group, now)
            count = self.limits.get(key, 0) + 1
            self.limits[key] = count
            if count > maximum: raise ValueError('rate_limit')

    def event(self, order, kind, detail=''):
        with closing(self.ledger.connect()) as db:
            db.execute('INSERT INTO events(at,order_id,kind,detail) VALUES(?,?,?,?)',
                       (int(time.time()), order, kind, detail))

    def create_checkout(self, request_id, email, product=PRODUCT, consent=None):
        if product not in self.products: raise ValueError('unknown_product')
        if not self.config.get('checkout_enabled'): raise ValueError('checkout_closed')
        selected=self.products[product]
        accepted=isinstance(consent,dict) and consent.get('accepted') is True and all(consent.get(k)==selected.get(k) and selected.get(k) for k in ['terms_version','privacy_version'])
        if not self.sandbox and not accepted: raise ValueError('terms_acceptance_required')
        if not isinstance(request_id,str) or not re.fullmatch(r'[a-f0-9]{64}', request_id): raise ValueError('invalid_request')
        if not isinstance(email,str) or len(email)>254 or not re.fullmatch(r'[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+',email): raise ValueError('invalid_email')
        # Session identity comes from a high-entropy browser-owned request ID.
        # One transaction prevents duplicate orders for concurrent retries.
        with closing(self.ledger.connect()) as db:
            db.execute('BEGIN IMMEDIATE')
            row=db.execute('SELECT * FROM sessions WHERE request_id=?',(digest(request_id),)).fetchone()
            if row:
                if self.cipher.decrypt(row['email'].encode()).decode()!=email or db.execute('SELECT product FROM orders WHERE id=?',(row['order_id'],)).fetchone()[0]!=product: raise ValueError('request_conflict')
                return request_id, self.checkout_status(request_id)
            import uuid
            order='ss_'+uuid.uuid4().hex
            key=secrets.token_urlsafe(32)
            db.execute("INSERT INTO orders VALUES(?,?,?,NULL,'NEW',?)",(order,product,self.products[product]['amount'],digest(key)))
            db.execute('INSERT INTO sessions VALUES(?,?,?,?,?,?,?)',
                (digest(request_id),digest(request_id),order,self.cipher.encrypt(key.encode()).decode(),self.cipher.encrypt(email.encode()).decode(),None,int(time.time())))
            if accepted:
                db.execute('INSERT INTO consents VALUES(?,?,?,?)',(order,selected['terms_version'],selected['privacy_version'],int(time.time())))
            db.commit()
        self.event(order,'created',self.environment+'; '+product)
        return request_id, self.checkout_status(request_id)

    def session(self, token):
        if not isinstance(token,str) or not re.fullmatch(r'[a-f0-9]{64}',token): raise ValueError('unknown_session')
        with closing(self.ledger.connect()) as db:
            row=db.execute('SELECT sessions.*,orders.status,orders.amount,orders.product,orders.payment_id FROM sessions JOIN orders ON orders.id=sessions.order_id WHERE hash=?',(digest(token),)).fetchone()
        if not row or row['created'] < time.time()-7*86400: raise ValueError('unknown_session')
        return row

    def start_payment(self, token):
        if not self.config.get('checkout_enabled'): raise ValueError('checkout_closed')
        # Serialize Init per process. A failed/uncertain Init is never retried
        # automatically: its order remains for operator reconciliation.
        with self.lock:
            row=self.session(token)
            if row['payment_url']: return row['payment_url']
            with closing(self.ledger.connect()) as db:
                attempted=db.execute("SELECT 1 FROM events WHERE order_id=? AND kind='init_started'",(row['order_id'],)).fetchone()
            if attempted: raise ValueError('payment_pending_review')
            self.event(row['order_id'],'init_started')
            origin=self.config['origin'].rstrip('/')
            checkout=self.config.get('checkout_url',origin+'/checkout')
            parts=urlsplit(checkout)
            def return_url(result):
                query=dict(parse_qsl(parts.query));query.update(product=row['product'],result=result)
                return urlunsplit((parts.scheme,parts.netloc,parts.path,urlencode(query),parts.fragment))
            callbacks=dict(notification_url=origin+'/api/notification',success_url=return_url('success'),fail_url=return_url('fail'))
            if self.sandbox:
                result=self.bank.init_connectivity_probe(row['order_id'],price=row['amount'],**callbacks)
            else:
                result=self.bank.init_product(row['order_id'],row['amount'],description=self.products[row['product']]['name'],**callbacks)
            self.ledger.bind_payment(row['order_id'],str(result['PaymentId']))
            with closing(self.ledger.connect()) as db:
                db.execute('UPDATE sessions SET payment_url=? WHERE order_id=?',(result['PaymentURL'],row['order_id']))
            self.event(row['order_id'],'init_bound')
            return result['PaymentURL']

    def checkout_status(self, token):
        row=self.session(token)
        return {'order':row['order_id'],'status':row['status'],'amount':row['amount'],
                'product':row['product'],'name':self.products[row['product']]['name'],'environment':self.environment,'receipt_status':self.receipt_status(row['order_id']), 'receipt_url':self.receipt_url(row['order_id'])}

    def receipt_url(self, order):
        with closing(self.ledger.connect()) as db:
            row=db.execute("SELECT url FROM receipt_tasks WHERE order_id=? AND status='ISSUED'",(order,)).fetchone()
        return row[0] if row else None

    def receipt_status(self, order):
        with closing(self.ledger.connect()) as db:
            row=db.execute('SELECT status FROM receipt_tasks WHERE order_id=?',(order,)).fetchone()
        return row[0] if row else 'not_due'

    def notification(self, payload):
        result=self.ledger.notification(payload,self.bank.get_state)
        order=payload.get('OrderId')
        with closing(self.ledger.connect()) as db:
            state=db.execute('SELECT status FROM orders WHERE id=?',(order,)).fetchone()
            if state and state[0]=='CONFIRMED':
                db.execute("INSERT OR IGNORE INTO receipt_tasks VALUES(?,?,NULL,?)",(order,'DEMO_NO_FISCAL_RECEIPT' if self.sandbox else 'PENDING_OWNER',int(time.time())))
                if not self.sandbox: enqueue(db,order,'license')
            elif state and state[0] in {'REFUNDED','PARTIAL_REFUNDED','REVERSED','PARTIAL_REVERSED'}:
                db.execute("UPDATE receipt_tasks SET status=?,updated=? WHERE order_id=?",('DEMO_REFUNDED' if self.sandbox else 'REFUND_REVIEW',int(time.time()),order))
                db.execute("UPDATE mail_jobs SET status='CANCELED',updated=? WHERE order_id=? AND status='PENDING'",(int(time.time()),order))
        self.event(order,'notification_verified',state[0] if state else 'unknown')
        return result

    def license_file(self, token):
        row=self.session(token)
        if row['status']!='CONFIRMED': raise ValueError('license_not_paid')
        return {'schema':1,'product':row['product'],'environment':self.environment,'license_key':self.cipher.decrypt(row['encrypted_key'].encode()).decode()}

    def activate(self, key, device):
        if not isinstance(key,str) or not 30<=len(key)<=100: raise ValueError('invalid_license')
        try:
            cert=self.ledger.activate(key,device)
        except ValueError as error:
            if str(error)=='license not paid or revoked': raise EntitlementError('license_revoked') from None
            if str(error)=='device limit reached': raise EntitlementError('device_limit') from None
            raise
        return {'certificate':cert,'environment':self.environment}


def handler_for(service):
    class Handler(BaseHTTPRequestHandler):
        server_version='ShiftCommerce'
        def log_message(self,*args): pass # Never log URLs, keys, email, cookies, or bank bodies.
        def setup(self):
            super().setup(); self.connection.settimeout(20)
        def send(self,status,body,*,kind='application/json',cookie=None):
            raw=(json.dumps(body,ensure_ascii=False) if kind=='application/json' else body).encode()
            self.send_response(status)
            for k,v in {'Content-Type':kind+'; charset=utf-8','Content-Length':str(len(raw)),
                'Cache-Control':'no-store','X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer',
                'Content-Security-Policy':"default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"}.items(): self.send_header(k,v)
            if cookie: self.send_header('Set-Cookie',cookie)
            self.end_headers(); self.wfile.write(raw)
        def token(self):
            cookies=SimpleCookie(); cookies.load(self.headers.get('Cookie',''))
            return cookies['ss_session'].value if 'ss_session' in cookies else ''
        def route(self,method):
            if not hmac.compare_digest(self.headers.get('X-Edge-Key',''),service.config['edge_key']):
                self.send(403,{'error':'forbidden'}); return
            path=urlsplit(self.path).path
            ip=self.headers.get('X-Real-IP',self.client_address[0])
            try:
                service.limit(ip,'general',120)
                if method=='GET':
                    if path=='/api/health': self.send(200,{'ok':True,'environment':service.environment,'revision':service.config.get('revision','local')}); return
                    if path=='/api/catalog': self.send(200,{'products':service.products,'environment':service.environment,'checkout_enabled':service.config.get('checkout_enabled') is True}); return
                    if path=='/api/order': self.send(200,service.checkout_status(self.token())); return
                    if path=='/api/license': self.send(200,service.license_file(self.token())); return
                    self.send(404,{'error':'not_found'}); return
                if self.headers.get('Transfer-Encoding'): raise ValueError('invalid_body')
                length=int(self.headers.get('Content-Length','0'))
                if not 0<length<=16384: raise ValueError('invalid_body')
                if self.headers.get_content_type()!='application/json': raise ValueError('json_required')
                if path not in {'/api/notification','/api/activate','/api/release-device'} and self.headers.get('Origin')!=('{0.scheme}://{0.netloc}'.format(urlsplit(service.config['origin']))):
                    raise ValueError('origin_rejected')
                data=json.loads(self.rfile.read(length))
                if not isinstance(data,dict): raise ValueError('object_required')
                if path=='/api/checkout':
                    service.limit(ip,'checkout',5)
                    token,result=service.create_checkout(data.get('request_id'),data.get('email'),data.get('product',PRODUCT),data.get('consent'))
                    self.send(200,result,cookie='ss_session='+token+'; Secure; HttpOnly; SameSite=Lax; Path=/; Max-Age=604800'); return
                if path=='/api/pay': self.send(200,{'payment_url':service.start_payment(self.token())}); return
                if path=='/api/notification':
                    self.send(200,service.notification(data),kind='text/plain'); return
                if path=='/api/activate':
                    service.limit(ip,'activate',10)
                    self.send(200,service.activate(data.get('key'),data.get('device'))); return
                if path=='/api/release-device':
                    service.limit(ip,'activate',10)
                    if not isinstance(data.get('key'),str): raise ValueError('invalid_license')
                    service.ledger.release_device(data['key'],data.get('device'))
                    self.send(200,{'released':True}); return
                self.send(404,{'error':'not_found'})
            except EntitlementError as error:
                self.send(403 if str(error)=='license_revoked' else 409,{'error':str(error)})
            except (ValueError,TypeError,KeyError,json.JSONDecodeError):
                self.send(400,{'error':'request_rejected'})
            except Exception:
                self.send(503,{'error':'temporarily_unavailable'})
        def do_GET(self): self.route('GET')
        def do_POST(self): self.route('POST')
    return Handler


def main():
    import argparse
    parser=argparse.ArgumentParser(); parser.add_argument('--config',required=True); parser.add_argument('--data',required=True)
    args=parser.parse_args(); path=Path(args.config)
    if path.stat().st_mode & 0o077: raise SystemExit('private configuration required')
    config=json.loads(path.read_text())
    service=Commerce(args.data,config)
    ThreadingHTTPServer((config.get('bind','127.0.0.1'),config.get('port',18490)),handler_for(service)).serve_forever()


if __name__=='__main__': main()
