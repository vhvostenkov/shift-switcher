import base64
import hashlib
import tempfile
import unittest
from pathlib import Path

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

from commerce.ledger import Ledger, amount, bank_token


class LedgerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.signer = Ed25519PrivateKey.generate()
        self.ledger = Ledger(Path(self.temp.name) / 'orders.sqlite', terminal='testDEMO', password='test-only-password', signing_key=self.signer, key_id='test-only-key')
        self.order, self.key, self.price = self.ledger.create_order()
        self.ledger.bind_payment(self.order, '12345')
        self.device = 'a' * 64

    def tearDown(self):
        self.temp.cleanup()

    def event(self, **overrides):
        event = dict(TerminalKey='testDEMO', PaymentId='12345', OrderId=self.order, Amount=self.price, Status='CONFIRMED', Success=True, ErrorCode='0')
        event.update(overrides)
        event['Token'] = bank_token(event, 'test-only-password')
        return event

    def state(self, **overrides):
        state = dict(TerminalKey='testDEMO', PaymentId='12345', Amount=self.price, Status='CONFIRMED', Success=True, ErrorCode='0')
        state.update(overrides)
        return lambda payment: state

    def paid(self):
        self.ledger.notification(self.event(), self.state())

    def test_token_has_independent_known_concatenation(self):
        raw = {'TerminalKey': 'testDEMO', 'Success': True, 'Amount': 49900, 'DATA': {'ignored': 7}, 'null': None, 'Token': 'ignored'}
        expected = hashlib.sha256(b'49900test-only-passwordtruetestDEMO').hexdigest()
        self.assertEqual(bank_token(raw, 'test-only-password'), expected)

    def test_bool_is_not_money(self):
        for invalid in [True, False, -1, 4.99, '-10', '４９９００']:
            with self.assertRaises(ValueError):
                amount(invalid)

    def test_server_price_is_fixed(self):
        self.assertEqual(self.price, 49900)

    def test_unpaid_key_cannot_activate(self):
        with self.assertRaises(ValueError):
            self.ledger.activate(self.key, self.device)

    def test_tampered_notification_cannot_pay(self):
        event = self.event()
        event['Amount'] += 1
        with self.assertRaises(ValueError):
            self.ledger.notification(event, self.state())

    def test_wrong_terminal_cannot_pay(self):
        with self.assertRaises(ValueError):
            self.ledger.notification(self.event(TerminalKey='otherDEMO'), self.state())

    def test_unknown_payment_cannot_pay(self):
        with self.assertRaises(ValueError):
            self.ledger.notification(self.event(PaymentId='99999'), self.state())

    def test_signed_wrong_amount_rejected(self):
        with self.assertRaises(ValueError):
            self.ledger.notification(self.event(Amount=1), self.state())

    def test_authorized_is_not_paid(self):
        self.assertEqual(self.ledger.notification(self.event(Status='AUTHORIZED'), lambda _: self.fail('GetState not needed for AUTHORIZED')), 'OK')
        with self.assertRaises(ValueError):
            self.ledger.activate(self.key, self.device)

    def test_failed_notification_is_acknowledged_only_after_bank_verification(self):
        for status in ['REJECTED', 'DEADLINE_EXPIRED', 'CANCELED']:
            self.assertEqual(self.ledger.notification(self.event(Status=status, Success=False, ErrorCode='100'), self.state(Status=status)), 'OK')
            with self.assertRaises(ValueError): self.ledger.activate(self.key, self.device)
        with self.assertRaises(ValueError):
            self.ledger.notification(self.event(Status='REJECTED', Success=False), self.state(Status='CONFIRMED'))

    def test_late_failed_event_cannot_revoke_paid_order(self):
        self.paid()
        self.ledger.notification(self.event(Status='REJECTED', Success=False), self.state(Status='REJECTED'))
        self.ledger.activate(self.key, self.device)

    def test_bank_state_must_agree(self):
        for override in [{'PaymentId': '999'}, {'Amount': 1}, {'TerminalKey': 'otherDEMO'}, {'Status': 'AUTHORIZED'}, {'Success': False}]:
            with self.assertRaises(ValueError):
                self.ledger.notification(self.event(), self.state(**override))

    def test_duplicate_payment_and_activation_are_idempotent(self):
        self.paid(); self.paid()
        first = self.ledger.activate(self.key, self.device, now=1000)
        self.assertEqual(first, self.ledger.activate(self.key, self.device, now=1000))

    def test_signed_certificate_and_tampering(self):
        self.paid()
        token = self.ledger.activate(self.key, self.device, now=1000)
        version, payload, signature = token.split('.')
        message = f'{version}.{payload}'.encode()
        raw = base64.urlsafe_b64decode(signature + '=' * (-len(signature) % 4))
        self.signer.public_key().verify(raw, message)
        with self.assertRaises(InvalidSignature):
            self.signer.public_key().verify(raw, message + b'x')

    def test_two_devices_and_transfer(self):
        self.paid()
        self.ledger.activate(self.key, self.device)
        self.ledger.activate(self.key, 'b' * 64)
        with self.assertRaises(ValueError):
            self.ledger.activate(self.key, 'c' * 64)
        self.ledger.release_device(self.key, self.device)
        self.ledger.activate(self.key, 'c' * 64)

    def test_release_requires_the_correct_license_key(self):
        self.paid(); self.ledger.activate(self.key, self.device)
        with self.assertRaises(ValueError):
            self.ledger.release_device('wrong-key', self.device)

    def test_refund_revokes_and_late_confirm_cannot_resurrect(self):
        self.paid()
        self.ledger.notification(self.event(Status='REFUNDED'), self.state(Status='REFUNDED', Amount=0))
        self.ledger.notification(self.event(), self.state())
        with self.assertRaises(ValueError):
            self.ledger.activate(self.key, self.device)

    def test_database_survives_process_reopen(self):
        self.paid(); self.ledger.activate(self.key, self.device)
        reopened = Ledger(Path(self.temp.name) / 'orders.sqlite', terminal='testDEMO', password='test-only-password', signing_key=self.signer, key_id='test-only-key')
        reopened.activate(self.key, self.device)

    def test_secrets_are_not_saved_as_plaintext(self):
        self.paid()
        data = (Path(self.temp.name) / 'orders.sqlite').read_bytes()
        self.assertNotIn(self.key.encode(), data)
        self.assertNotIn(b'test-only-password', data)

    def test_sandbox_and_live_cannot_mix(self):
        with self.assertRaises(ValueError):
            Ledger(Path(self.temp.name) / 'bad.sqlite', terminal='testDEMO', password='test-only-password', signing_key=self.signer, key_id='test-only-key', sandbox=False)


if __name__ == '__main__':
    unittest.main()
