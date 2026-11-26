import base64
import json
import unittest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from commerce.owner_license import issue


class OwnerLicenseTests(unittest.TestCase):
    def setUp(self):
        self.signer = Ed25519PrivateKey.generate()
        self.config = dict(environment='DEMO', key_id='test-only', signing_key=self.signer.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode())

    def test_signed_device_and_expiry(self):
        envelope = issue(self.config, 'a' * 64, now=1000)
        parts = envelope['certificate'].split('.')
        decode = lambda value: base64.urlsafe_b64decode(value + '=' * (-len(value) % 4))
        self.signer.public_key().verify(decode(parts[2]), (parts[0] + '.' + parts[1]).encode())
        payload = json.loads(decode(parts[1]))
        self.assertEqual(payload['device'], 'a' * 64)
        self.assertEqual(payload['kind'], 'owner')
        self.assertTrue(payload['license'].startswith('owner_'))
        self.assertEqual(payload['valid_until'], 1000 + 365 * 86400)

    def test_no_live_owner_issuance(self):
        with self.assertRaises(ValueError):
            issue(dict(self.config, environment='LIVE'), 'a' * 64)

    def test_explicit_live_owner_grant(self):
        result = issue(dict(self.config, environment='LIVE'), 'a' * 64, now=1000, allow_live=True)
        self.assertEqual(result['environment'], 'LIVE')
        body = result['certificate'].split('.')[1]
        payload = json.loads(base64.urlsafe_b64decode(body + '=' * (-len(body) % 4)))
        self.assertEqual(payload['environment'], 'LIVE')
        self.assertEqual(payload['kind'], 'owner')

    def test_invalid_device(self):
        with self.assertRaises(ValueError):
            issue(self.config, 'not-a-device')

    def test_bounded_term(self):
        for days in [0, 366]:
            with self.assertRaises(ValueError):
                issue(self.config, 'a' * 64, days=days)
