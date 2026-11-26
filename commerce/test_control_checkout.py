"""An owner-created pilot capability cannot open the public checkout."""
import copy
import json
import tempfile
import threading
import unittest
from contextlib import closing
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from cryptography.fernet import Fernet
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

from commerce.catalog import DEFAULT_CATALOG
from commerce.service import Commerce, handler_for, ThreadingHTTPServer


class Bank:
    def __init__(self): self.calls=0
    def init_product(self, order, amount, **kwargs):
        self.calls+=1
        return dict(PaymentId='123',PaymentURL='https://pay.tbank-online.com/test-only')


class ControlCheckoutTests(unittest.TestCase):
    def setUp(self):
        self.folder=tempfile.TemporaryDirectory();self.bank=Bank()
        signer=Ed25519PrivateKey.generate().private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()).decode()
        products=copy.deepcopy(DEFAULT_CATALOG)
        products['shift-switcher-pro-v1'].update(terms_version='test-published-1',privacy_version='test-published-1')
        self.config=dict(environment='LIVE',terminal='testLIVE',password='test-only',signing_key=signer,key_id='test',encryption_key=Fernet.generate_key().decode(),edge_key='test-edge',origin='https://example.test/pay',checkout_enabled=False,bank_approved=True,fresh_credentials_verified=True,legal_terms_approved=True,control_checkout_approved=True,products=products)
        self.service=Commerce(Path(self.folder.name)/'live',self.config,bank=self.bank)
        self.consent=dict(accepted=True,terms_version='test-published-1',privacy_version='test-published-1')
    def tearDown(self): self.folder.cleanup()
    def prepare(self): return self.service.prepare_control_checkout('owner@example.test')
    def accept(self,token): return self.service.accept_control_checkout(token,'shift-switcher-pro-v1',self.consent)
    def test_public_checkout_stays_closed_even_with_control_flag(self):
        with self.assertRaises(ValueError): self.service.create_checkout('a'*64,'buyer@example.test',consent=self.consent)
        self.assertEqual(self.bank.calls,0)
    def test_preparation_does_not_contact_bank_or_grant_license(self):
        token,order=self.prepare()
        self.assertEqual(order['amount'],49900);self.assertEqual(order['status'],'NEW')
        self.assertEqual(self.bank.calls,0)
        with self.assertRaises(ValueError): self.service.license_file(token)
    def test_terms_required_before_init(self):
        token,_=self.prepare()
        with self.assertRaises(ValueError): self.service.start_payment(token)
        with self.assertRaises(ValueError): self.service.accept_control_checkout(token,'shift-switcher-pro-v1',dict(self.consent,accepted=False))
        self.assertEqual(self.bank.calls,0)
    def test_control_order_consent_and_init_are_idempotent(self):
        token,order=self.prepare();self.accept(token);self.accept(token)
        self.service.start_payment(token);self.service.start_payment(token)
        self.assertEqual(self.bank.calls,1)
        with closing(self.service.ledger.connect()) as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM orders').fetchone()[0],1)
            self.assertEqual(db.execute('SELECT COUNT(*) FROM consents').fetchone()[0],1)
            self.assertEqual(db.execute('SELECT status FROM orders WHERE id=?',(order['order'],)).fetchone()[0],'NEW')
    def test_capability_cannot_change_product_or_accept_old_terms(self):
        token,_=self.prepare()
        with self.assertRaises(ValueError): self.service.accept_control_checkout(token,'other-product',self.consent)
        with self.assertRaises(ValueError): self.service.accept_control_checkout(token,'shift-switcher-pro-v1',dict(self.consent,terms_version='old'))
        with self.assertRaises(ValueError): self.accept('a'*64)
    def test_expiry_and_disable_block_even_a_previously_bound_payment(self):
        token,order=self.prepare();self.accept(token);self.service.start_payment(token)
        with closing(self.service.ledger.connect()) as db:db.execute('UPDATE control_sessions SET expires=0 WHERE order_id=?',(order['order'],))
        with self.assertRaises(ValueError):self.accept(token)
        with self.assertRaises(ValueError):self.service.start_payment(token)
        self.config['control_checkout_approved']=False
        with self.assertRaises(ValueError):self.prepare()
        self.assertEqual(self.bank.calls,1)
    def test_legal_gate_is_real_but_receipt_verification_is_not_fabricated(self):
        self.assertNotIn('receipt_delivery_verified',self.config)
        for name in ['legal_terms_approved','bank_approved','fresh_credentials_verified']:
            config=dict(self.config,**{name:False})
            with self.assertRaises(ValueError):Commerce(Path(self.folder.name)/name,config,bank=self.bank)
        config=dict(self.config,products=DEFAULT_CATALOG)
        with self.assertRaises(ValueError):Commerce(Path(self.folder.name)/'draft',config,bank=self.bank)
    def test_demo_and_open_public_checkout_cannot_prepare_pilot(self):
        self.config['environment']='DEMO';self.config['terminal']='testDEMO'
        demo=Commerce(Path(self.folder.name)/'demo',self.config,bank=self.bank)
        with self.assertRaises(ValueError):demo.prepare_control_checkout('owner@example.test')
        self.config['checkout_enabled']=True
        with self.assertRaises(ValueError):self.prepare()
    def test_http_only_accepts_existing_token_with_same_origin(self):
        token,order=self.prepare()
        server=ThreadingHTTPServer(('127.0.0.1',0),handler_for(self.service))
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        base='http://127.0.0.1:'+str(server.server_port)
        def post(path,body,origin='https://example.test'):
            return urlopen(Request(base+path,data=json.dumps(body).encode(),headers={'Content-Type':'application/json','X-Edge-Key':'test-edge','Origin':origin}),timeout=3)
        payload=dict(request_id=token,product='shift-switcher-pro-v1',consent=self.consent)
        try:
            for path,body,origin in [('/api/control-checkout',payload,'https://attacker.test'),('/api/control-checkout',dict(payload,request_id='a'*64),'https://example.test'),('/api/checkout',dict(payload,email='owner@example.test'),'https://example.test')]:
                try:post(path,body,origin)
                except HTTPError as error:self.assertEqual(error.code,400);error.close()
                else:self.fail('unapproved checkout was accepted')
            with post('/api/control-checkout',payload) as response:
                self.assertIn('HttpOnly',response.headers['Set-Cookie']);self.assertIn('Secure',response.headers['Set-Cookie'])
                self.assertEqual(json.load(response)['order'],order['order'])
            self.assertEqual(self.bank.calls,0)
        finally:server.shutdown();server.server_close();thread.join()


if __name__=='__main__':unittest.main()
