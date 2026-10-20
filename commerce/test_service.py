import json
import tempfile
import threading
import unittest
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from cryptography.fernet import Fernet
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

from commerce.ledger import bank_token
from commerce.service import Commerce, handler_for, ThreadingHTTPServer


class FakeBank:
    def __init__(self): self.calls=0; self.status='CONFIRMED'
    def init_connectivity_probe(self,order,**kw):
        self.calls+=1
        return {'PaymentId':'123','PaymentURL':'https://pay.tbank-online.com/test-only'}
    def get_state(self,payment):
        return {'TerminalKey':'testDEMO','Success':True,'ErrorCode':'0','PaymentId':payment,'Amount':49900,'Status':self.status}


class ServiceTests(unittest.TestCase):
    def setUp(self):
        self.folder=tempfile.TemporaryDirectory(); self.bank=FakeBank()
        signer=Ed25519PrivateKey.generate().private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()).decode()
        self.config=dict(terminal='testDEMO',password='test-only-password',signing_key=signer,key_id='test-key',encryption_key=Fernet.generate_key().decode(),edge_key='test-edge',origin='https://example.test/shift-switcher',checkout_enabled=True)
        self.service=Commerce(self.folder.name,self.config,bank=self.bank)
        self.token='a'*64
    def tearDown(self): self.folder.cleanup()
    def create(self): return self.service.create_checkout(self.token,'buyer@example.test')[1]
    def pay(self):
        row=self.create(); self.service.start_payment(self.token)
        event=dict(TerminalKey='testDEMO',Success=True,ErrorCode='0',PaymentId='123',OrderId=row['order'],Amount=49900,Status='CONFIRMED')
        event['Token']=bank_token(event,'test-only-password')
        self.service.notification(event); return event
    def test_checkout_retry_is_same_order(self):
        a=self.create(); b=self.create(); self.assertEqual(a['order'],b['order'])
        self.service.start_payment(self.token); self.service.start_payment(self.token)
        self.assertEqual(self.bank.calls,1)
    def test_retry_cannot_change_customer(self):
        self.create()
        with self.assertRaises(ValueError): self.service.create_checkout(self.token,'other@example.test')
    def test_no_license_before_payment(self):
        self.create()
        with self.assertRaises(ValueError): self.service.license_file(self.token)
    def test_verified_payment_issues_license_and_receipt_task(self):
        self.pay(); key=self.service.license_file(self.token)['license_key']
        self.assertIn('certificate',self.service.activate(key,'b'*64))
        self.assertEqual(self.service.checkout_status(self.token)['receipt_status'],'DEMO_NO_FISCAL_RECEIPT')
    def test_notification_repeat_does_not_duplicate_receipt(self):
        event=self.pay(); self.service.notification(event)
        with self.service.ledger.connect() as db: self.assertEqual(db.execute('SELECT COUNT(*) FROM receipt_tasks').fetchone()[0],1)
    def test_refund_revokes_activation(self):
        event=self.pay(); key=self.service.license_file(self.token)['license_key']
        self.bank.status='REFUNDED'; event['Status']='REFUNDED'; event['Token']=bank_token(event,'test-only-password')
        self.service.notification(event)
        with self.assertRaises(ValueError):self.service.activate(key,'b'*64)
    def test_restart_preserves_checkout_and_license(self):
        self.pay(); before=self.service.license_file(self.token)
        other=Commerce(self.folder.name,self.config,bank=self.bank)
        self.assertEqual(other.license_file(self.token),before)
    def test_closed_checkout(self):
        self.config['checkout_enabled']=False
        with self.assertRaises(ValueError):self.create()
    def test_email_and_id_validation(self):
        for token,email in [('short','buyer@example.test'),(self.token,'bad\naddress')]:
            with self.assertRaises(ValueError):self.service.create_checkout(token,email)
    def test_http_origin_cookie_and_edge(self):
        server=ThreadingHTTPServer(('127.0.0.1',0),handler_for(self.service))
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        base='http://127.0.0.1:'+str(server.server_port)
        def post(headers):
            return urlopen(Request(base+'/api/checkout',data=json.dumps({'request_id':self.token,'email':'buyer@example.test'}).encode(),headers=headers),timeout=3)
        try:
            with self.assertRaises(HTTPError) as error:post({'Content-Type':'application/json'})
            self.assertEqual(error.exception.code,403)
            with self.assertRaises(HTTPError):post({'Content-Type':'application/json','X-Edge-Key':'test-edge','Origin':'https://attacker.test'})
            with post({'Content-Type':'application/json','X-Edge-Key':'test-edge','Origin':'https://example.test'}) as response:
                self.assertIn('HttpOnly',response.headers['Set-Cookie']);self.assertIn('Secure',response.headers['Set-Cookie'])
                self.assertEqual(json.load(response)['environment'],'DEMO')
        finally:server.shutdown();server.server_close();thread.join()
    def test_unknown_session_cannot_read_license(self):
        self.pay()
        with self.assertRaises(ValueError):self.service.license_file('c'*64)


if __name__=='__main__':unittest.main()
