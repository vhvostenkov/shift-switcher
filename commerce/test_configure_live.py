import unittest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
import base64
from commerce.configure_live import prepare


class ConfigureLiveTests(unittest.TestCase):
    def setUp(self):
        key=Ed25519PrivateKey.generate()
        self.key=key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()).decode()
        self.keys={'LIVE:ss-live-1':base64.b64encode(key.public_key().public_bytes(serialization.Encoding.Raw,serialization.PublicFormat.Raw)).decode()}
        self.base=dict(bind='127.0.0.1',port=18490,edge_key='test-edge',password='demo-only-password',encryption_key='demo-cipher',checkout_enabled=True,smtp_enabled=True)
    def test_preserves_routing_but_closes_every_money_and_delivery_gate(self):
        config=prepare(self.base,self.key,self.keys,'1234LIVE','fresh-test-only',fresh_confirmed=True)
        self.assertEqual(config['edge_key'],'test-edge');self.assertEqual(config['environment'],'LIVE')
        self.assertNotEqual(config['encryption_key'],self.base['encryption_key'])
        self.assertEqual(config['password'],'fresh-test-only')
        for name in ['checkout_enabled','control_checkout_approved','legal_terms_approved','receipt_delivery_verified','live_checkout_approved','smtp_enabled']:
            self.assertIs(config[name],False)
        self.assertNotIn('products',config) # Default draft terms cannot silently become published.
    def test_rejects_demo_invalid_credentials_and_unconfirmed_rotation(self):
        for terminal,password,fresh in [('1234DEMO','fresh-test-only',True),('x','fresh-test-only',True),('1234LIVE','bad\npassword',True),('1234LIVE','fresh-test-only',False)]:
            with self.assertRaises(ValueError):prepare(self.base,self.key,self.keys,terminal,password,fresh_confirmed=fresh)
    def test_requires_exact_shipped_live_signing_key(self):
        with self.assertRaises(ValueError):prepare(self.base,self.key,{'DEMO:ss-live-1':self.keys['LIVE:ss-live-1']},'1234LIVE','fresh-test-only',fresh_confirmed=True)


if __name__=='__main__':unittest.main()
