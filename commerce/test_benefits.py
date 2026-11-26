import base64
import hashlib
import json
import secrets
import tempfile
import threading
import time
import unittest
from contextlib import closing
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from cryptography.fernet import Fernet
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

from copy import deepcopy
from commerce.catalog import DEFAULT_CATALOG
from commerce.ledger import PRODUCT,b64
from commerce.service import Commerce,EntitlementError,ThreadingHTTPServer,handler_for


class Bank:
    def __getattr__(self,name):raise AssertionError('Free grants must never contact the bank')


class BenefitTests(unittest.TestCase):
    def setUp(self):
        self.folder=tempfile.TemporaryDirectory();self.signer=Ed25519PrivateKey.generate()
        self.issuers={name:Ed25519PrivateKey.generate() for name in ['winston','radar']}
        providers={name:dict(public_key=base64.b64encode(key.public_key().public_bytes(serialization.Encoding.Raw,serialization.PublicFormat.Raw)).decode(),authorization_url='https://'+('agentg.vhvost.com' if name=='winston' else 'radar.vhvost.com')+'/api/product-benefits/shift-switcher') for name,key in self.issuers.items()}
        products=deepcopy(DEFAULT_CATALOG);products[PRODUCT].update(terms_version='test-published-1',privacy_version='test-published-1')
        self.consent=dict(accepted=True,terms_version='test-published-1',privacy_version='test-published-1')
        self.config=dict(products=products,legal_terms_approved=True,environment='LIVE',terminal='testLIVE',password='test-only-password',signing_key=self.signer.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()).decode(),key_id='test',encryption_key=Fernet.generate_key().decode(),edge_key='test-edge',origin='https://example.test/pay',checkout_enabled=False,bank_approved=True,existing_credentials_owner_approved=True,benefits_enabled=True,benefit_issuers=providers)
        self.service=Commerce(self.folder.name,self.config,bank=Bank());self.b=self.service.benefits
    def tearDown(self):self.folder.cleanup()
    def ticket(self,token,issuer='winston',**changes):
        now=int(time.time());payload=dict(schema=1,issuer=issuer,audience='shift-switcher-benefit',product=PRODUCT,environment='LIVE',subject='a'*64,state=token,jti=secrets.token_hex(16),issued_at=now,expires_at=now+90)
        payload.update(changes);message='sb1.'+b64(json.dumps(payload,sort_keys=True,separators=(',',':')).encode())
        return message+'.'+b64(self.issuers[issuer].sign(message.encode()))
    def start(self,issuer):return self.b.start(issuer,self.consent)
    def grant(self,issuer='winston'):
        token,_=self.start(issuer);return token,self.b.claim(token,self.ticket(token,issuer))
    def test_both_services_can_grant_without_opening_checkout_or_creating_money_records(self):
        for issuer in self.issuers:
            token,license=self.grant(issuer);self.assertEqual(license['product'],PRODUCT);self.assertEqual(license['environment'],'LIVE');self.assertEqual(self.b.file(token),license)
        with closing(self.service.ledger.connect()) as db:
            for table in ['orders','receipt_tasks','mail_jobs']:self.assertEqual(db.execute('SELECT COUNT(*) FROM '+table).fetchone()[0],0)
        with self.assertRaises(ValueError):self.service.create_checkout('b'*64,'buyer@example.test')
    def test_repeat_claim_and_new_login_return_same_account_key(self):
        token,_=self.start('winston');ticket=self.ticket(token)
        first=self.b.claim(token,ticket);self.assertEqual(self.b.claim(token,ticket),first)
        another,_=self.start('winston');self.assertEqual(self.b.claim(another,self.ticket(another)),first)
        with closing(self.service.ledger.connect()) as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM benefit_grants').fetchone()[0],1)
            row=db.execute('SELECT * FROM benefit_grants').fetchone()
            self.assertNotIn(first['license_key'],row['encrypted_key']);self.assertNotEqual(first['license_key'],row['secret_hash'])
    def test_cannot_replace_the_claimed_browser_account(self):
        token,first=self.grant()
        with self.assertRaises(ValueError):self.b.claim(token,self.ticket(token,subject='b'*64))
        self.assertEqual(self.b.file(token),first)
    def test_invalid_claims_and_signatures_do_not_mint_a_grant(self):
        token,_=self.start('winston');now=int(time.time())
        for changes in [dict(schema=True),dict(schema=2),dict(audience='other'),dict(product='other'),dict(environment='DEMO'),dict(subject='name@example.test'),dict(state='b'*64),dict(jti='bad'),dict(issued_at=now+31),dict(expires_at=now),dict(expires_at=now+121)]:
            with self.subTest(changes=changes),self.assertRaises(ValueError):self.b.claim(token,self.ticket(token,**changes))
        with self.assertRaises(ValueError):self.b.claim(token,self.ticket(token,'radar'))
        ticket=self.ticket(token)
        with self.assertRaises(ValueError):self.b.claim(token,ticket[:-8]+'AAAAAAAA')
        with closing(self.service.ledger.connect()) as db:self.assertEqual(db.execute('SELECT COUNT(*) FROM benefit_grants').fetchone()[0],0)
    def test_state_cookie_and_one_time_proof_are_bound_to_the_same_flow(self):
        first,_=self.start('winston');second,_=self.start('winston');jti='c'*32
        self.b.claim(first,self.ticket(first,jti=jti))
        with self.assertRaises(ValueError):self.b.claim(second,self.ticket(first))
        with self.assertRaises(ValueError):self.b.claim(second,self.ticket(second,jti=jti))
        with self.assertRaises(ValueError):self.b.claim('',self.ticket(second))
    def test_expired_flow_cannot_claim_or_download(self):
        token,_=self.start('winston')
        with closing(self.service.ledger.connect()) as db:db.execute('UPDATE benefit_flows SET expires=0')
        with self.assertRaises(ValueError):self.b.claim(token,self.ticket(token))
        with self.assertRaises(ValueError):self.b.file(token)
    def test_two_devices_share_one_limit_regardless_of_operating_system(self):
        _,license=self.grant();key=license['license_key']
        mac,windows,third=[hashlib.sha256(name.encode()).hexdigest() for name in ['mac-installation','windows-installation','third-installation']]
        for device in [mac,windows,mac]:
            cert=self.service.activate(key,device)['certificate'];parts=cert.split('.')
            self.signer.public_key().verify(base64.urlsafe_b64decode(parts[2]+'='*(-len(parts[2])%4)),('ss1.'+parts[1]).encode())
            payload=json.loads(base64.urlsafe_b64decode(parts[1]+'='*(-len(parts[1])%4)))
            self.assertEqual(payload['device'],device);self.assertEqual(payload['valid_until']-payload['issued_at'],30*86400)
            self.assertNotIn('kind',payload) # Existing Mac and Windows clients use the ordinary signed format.
        with self.assertRaisesRegex(EntitlementError,'device_limit'):self.service.activate(key,third)
        self.service.release_device(key,mac);self.service.activate(key,third)
        with closing(self.service.ledger.connect()) as db:self.assertEqual(db.execute('SELECT COUNT(*) FROM benefit_devices').fetchone()[0],2)
    def test_revoke_blocks_renewal_without_fabricating_a_refund(self):
        token,license=self.grant()
        with closing(self.service.ledger.connect()) as db:db.execute("UPDATE benefit_grants SET status='REVOKED'")
        with self.assertRaisesRegex(EntitlementError,'license_revoked'):self.service.activate(license['license_key'],'b'*64)
        with self.assertRaises(ValueError):self.b.file(token)
    def test_rollout_off_blocks_new_claims_without_revoking_existing_gifts(self):
        _,license=self.grant();self.config['benefits_enabled']=False
        self.assertEqual(self.b.catalogue(),[])
        with self.assertRaises(ValueError):self.start('winston')
        self.assertIn('certificate',self.service.activate(license['license_key'],'b'*64))
    def test_http_requires_owned_origin_and_private_cookie(self):
        server=ThreadingHTTPServer(('127.0.0.1',0),handler_for(self.service));thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        base='http://127.0.0.1:'+str(server.server_port)
        def post(path,body,origin='https://example.test',cookie=''):
            return urlopen(Request(base+'/api/benefit/'+path,data=json.dumps(body).encode(),headers={'Content-Type':'application/json','X-Edge-Key':'test-edge','Origin':origin,'Cookie':cookie}),timeout=3)
        try:
            try:post('start',dict(provider='winston',consent=self.consent),origin='https://attacker.test')
            except HTTPError as error:self.assertEqual(error.code,400);error.close()
            else:self.fail('Cross-origin claim start accepted')
            with post('start',dict(provider='winston',consent=self.consent)) as response:
                cookie=response.headers['Set-Cookie'];self.assertIn('HttpOnly',cookie);self.assertIn('Path=/pay/',cookie)
                self.assertIn('https://agentg.vhvost.com/',json.load(response)['authorization_url'])
            token=cookie.split(';')[0].split('=')[1]
            with post('claim',dict(ticket=self.ticket(token)),cookie=cookie.split(';')[0]) as response:self.assertIn('license_key',json.load(response))
            try:post('claim',dict(ticket=self.ticket(token)))
            except HTTPError as error:self.assertEqual(error.code,400);error.close()
            else:self.fail('A proof without its browser cookie was accepted')
        finally:server.shutdown();server.server_close();thread.join()
    def test_consent_and_published_terms_are_required_for_free_grants(self):
        for consent in [None,dict(self.consent,accepted=False),dict(self.consent,terms_version='old')]:
            with self.assertRaises(ValueError):self.b.start('winston',consent)
        self.config['legal_terms_approved']=False
        with self.assertRaises(ValueError):Commerce(self.folder.name,self.config,bank=Bank())


if __name__=='__main__':unittest.main()
