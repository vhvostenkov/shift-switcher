"""Free service-account grants, separate from paid orders and bank state."""
import base64
import hashlib
import json
import re
import secrets
import time
import uuid
from contextlib import closing
from urllib.parse import urlencode, urlsplit

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

from .ledger import PRODUCT, b64


def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()


def hex_id(value, length=64):
    return isinstance(value,str) and re.fullmatch('[a-f0-9]{'+str(length)+'}',value) is not None


class ServiceBenefits:
    NAMES={'winston':'Winston','radar':'Radar'}
    HOSTS={'winston':{'agentg.vhvost.com','2w.ru'},'radar':{'radar.vhvost.com'}}

    def __init__(self, ledger, cipher, config, environment):
        self.ledger,self.cipher,self.config,self.environment=ledger,cipher,config,environment
        self.providers=config.get('benefit_issuers',{})
        if config.get('benefits_enabled') is True:
            product=ledger.products[PRODUCT]
            if config.get('legal_terms_approved') is not True or any(not product.get(k) or product[k].endswith('-draft') for k in ['terms_version','privacy_version']):
                raise ValueError('benefit_terms_not_published')
        for name,p in self.providers.items():
            if name not in self.NAMES:raise ValueError('unknown_benefit_issuer')
            url=urlsplit(p['authorization_url'])
            if url.scheme!='https' or url.hostname not in self.HOSTS[name] or url.path!='/api/product-benefits/shift-switcher' or url.username or url.password or url.port not in (None,443) or url.query or url.fragment:
                raise ValueError('invalid_benefit_issuer_url')
            if len(base64.b64decode(p['public_key'],validate=True))!=32:raise ValueError('invalid_benefit_issuer_key')
        with closing(ledger.connect()) as db:
            db.executescript('''
                CREATE TABLE IF NOT EXISTS benefit_grants (
                    id TEXT PRIMARY KEY, issuer TEXT NOT NULL, subject TEXT NOT NULL,
                    product TEXT NOT NULL, secret_hash TEXT UNIQUE NOT NULL,
                    encrypted_key TEXT NOT NULL, created INTEGER NOT NULL,
                    status TEXT NOT NULL, UNIQUE(issuer,subject,product)
                );
                CREATE TABLE IF NOT EXISTS benefit_devices (
                    grant_id TEXT NOT NULL REFERENCES benefit_grants(id), device TEXT NOT NULL,
                    PRIMARY KEY(grant_id,device)
                );
                CREATE TABLE IF NOT EXISTS benefit_flows (
                    hash TEXT PRIMARY KEY, issuer TEXT NOT NULL, expires INTEGER NOT NULL,
                    grant_id TEXT REFERENCES benefit_grants(id), terms_version TEXT NOT NULL,
                    privacy_version TEXT NOT NULL, accepted_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS benefit_proofs (
                    issuer TEXT NOT NULL, jti TEXT NOT NULL, flow_hash TEXT NOT NULL,
                    PRIMARY KEY(issuer,jti)
                );
            ''')

    def catalogue(self):
        if self.config.get('benefits_enabled') is not True:return []
        return [dict(id=name,name=self.NAMES[name]) for name in self.providers]

    def start(self, provider, consent=None):
        if self.config.get('benefits_enabled') is not True or provider not in self.providers:raise ValueError('benefits_unavailable')
        product=self.ledger.products[PRODUCT]
        if not isinstance(consent,dict) or consent.get('accepted') is not True or any(consent.get(k)!=product[k] for k in ['terms_version','privacy_version']):raise ValueError('benefit_consent_required')
        token=secrets.token_hex(32)
        with closing(self.ledger.connect()) as db:
            now=int(time.time())
            db.execute('INSERT INTO benefit_flows VALUES(?,?,?,NULL,?,?,?)',(digest(token),provider,now+600,product['terms_version'],product['privacy_version'],now))
        return token,dict(authorization_url=self.providers[provider]['authorization_url']+'?'+urlencode({'state':token}))

    def proof(self, ticket, provider, token, now):
        if not isinstance(ticket,str) or len(ticket)>8192:raise ValueError('invalid_benefit_proof')
        try:
            parts=ticket.split('.')
            if len(parts)!=3 or parts[0]!='sb1':raise ValueError()
            def decode(value):
                if not re.fullmatch('[A-Za-z0-9_-]+',value):raise ValueError()
                return base64.urlsafe_b64decode(value+'='*(-len(value)%4))
            raw,signature=decode(parts[1]),decode(parts[2])
            key=Ed25519PublicKey.from_public_bytes(base64.b64decode(self.providers[provider]['public_key'],validate=True))
            key.verify(signature,('sb1.'+parts[1]).encode())
            p=json.loads(raw)
            if not isinstance(p,dict) or type(p.get('schema')) is not int or p.get('schema')!=1 or p.get('issuer')!=provider or p.get('audience')!='shift-switcher-benefit' or p.get('product')!=PRODUCT or p.get('environment')!=self.environment:
                raise ValueError()
            if not hex_id(p.get('subject')) or not hex_id(p.get('jti'),32) or not isinstance(p.get('state'),str) or not secrets.compare_digest(p['state'],token):raise ValueError()
            issued,expires=p.get('issued_at'),p.get('expires_at')
            if type(issued) is not int or type(expires) is not int or issued<0 or issued>now+30 or expires<=now or not 0<expires-issued<=120:raise ValueError()
            return p
        except (ValueError,TypeError,KeyError,InvalidSignature,json.JSONDecodeError):
            raise ValueError('invalid_benefit_proof') from None

    def claim(self, token, ticket):
        if not hex_id(token) or self.config.get('benefits_enabled') is not True:raise ValueError('unknown_benefit_flow')
        now=int(time.time())
        with closing(self.ledger.connect()) as db:
            db.execute('BEGIN IMMEDIATE')
            flow=db.execute('SELECT * FROM benefit_flows WHERE hash=?',(digest(token),)).fetchone()
            if not flow or flow['expires']<=now or flow['issuer'] not in self.providers:raise ValueError('unknown_benefit_flow')
            p=self.proof(ticket,flow['issuer'],token,now)
            grant=db.execute('SELECT * FROM benefit_grants WHERE issuer=? AND subject=? AND product=?',(flow['issuer'],p['subject'],PRODUCT)).fetchone()
            used=db.execute('SELECT flow_hash FROM benefit_proofs WHERE issuer=? AND jti=?',(flow['issuer'],p['jti'])).fetchone()
            if used and used['flow_hash']!=digest(token):raise ValueError('benefit_proof_used')
            if flow['grant_id'] and (not grant or flow['grant_id']!=grant['id']):raise ValueError('benefit_account_changed')
            if not grant:
                grant_id='benefit_'+uuid.uuid4().hex;key=secrets.token_urlsafe(32)
                db.execute("INSERT INTO benefit_grants VALUES(?,?,?,?,?,?,?,'ACTIVE')",(grant_id,flow['issuer'],p['subject'],PRODUCT,digest(key),self.cipher.encrypt(key.encode()).decode(),now))
            else:
                if grant['status']!='ACTIVE':raise ValueError('benefit_revoked')
                grant_id=grant['id']
            db.execute('INSERT OR IGNORE INTO benefit_proofs VALUES(?,?,?)',(flow['issuer'],p['jti'],digest(token)))
            db.execute('UPDATE benefit_flows SET grant_id=?,expires=? WHERE hash=?',(grant_id,now+604800,digest(token)))
            db.commit()
        return self.file(token)

    def file(self, token):
        if not hex_id(token):raise ValueError('unknown_benefit_flow')
        with closing(self.ledger.connect()) as db:
            row=db.execute("SELECT g.* FROM benefit_flows f JOIN benefit_grants g ON g.id=f.grant_id WHERE f.hash=? AND f.expires>? AND g.status='ACTIVE'",(digest(token),int(time.time()))).fetchone()
        if not row:raise ValueError('unknown_benefit_flow')
        return dict(schema=1,product=PRODUCT,environment=self.environment,license_key=self.cipher.decrypt(row['encrypted_key'].encode()).decode())

    def activate(self, key, device):
        with closing(self.ledger.connect()) as db:
            db.execute('BEGIN IMMEDIATE')
            grant=db.execute('SELECT * FROM benefit_grants WHERE secret_hash=?',(digest(key),)).fetchone()
            if not grant:return None
            if grant['status']!='ACTIVE':raise ValueError('license_revoked')
            if not hex_id(device):raise ValueError('invalid_device')
            devices={r[0] for r in db.execute('SELECT device FROM benefit_devices WHERE grant_id=?',(grant['id'],))}
            # One shared limit, without separate Mac/Windows buckets.
            if device not in devices and len(devices)>=2:raise ValueError('device_limit')
            db.execute('INSERT OR IGNORE INTO benefit_devices VALUES(?,?)',(grant['id'],device));db.commit()
        now=int(time.time())
        payload=dict(schema=1,product=PRODUCT,license=grant['id'],device=device,issued_at=now,
            valid_until=now+30*86400,key_id=self.ledger.key_id,environment=self.environment)
        message='ss1.'+b64(json.dumps(payload,sort_keys=True,separators=(',',':')).encode())
        return message+'.'+b64(self.ledger.signing_key.sign(message.encode()))

    def release(self, key, device):
        with closing(self.ledger.connect()) as db:
            grant=db.execute('SELECT id FROM benefit_grants WHERE secret_hash=?',(digest(key),)).fetchone()
            if not grant:return False
            if not hex_id(device):raise ValueError('invalid_device')
            db.execute('DELETE FROM benefit_devices WHERE grant_id=? AND device=?',(grant['id'],device))
        return True
