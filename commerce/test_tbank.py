import ssl
import unittest

from commerce.ledger import bank_token
from commerce.tbank import TBankDemo, TBankLive, BankError


class DemoTransportTests(unittest.TestCase):
    def client(self, result=None):
        def transport(method, payload):
            self.assertEqual(payload['Token'], bank_token(payload, 'test-only-password'))
            self.assertNotIn('Password', payload)
            return result or {'TerminalKey': 'testDEMO', 'Success': True, 'ErrorCode': '0', 'PaymentId': '123', 'OrderId': 'ss_test', 'Amount': 49900, 'PaymentURL': 'https://securepay.tinkoff.ru/new/test-only'}
        return TBankDemo('testDEMO', 'test-only-password', transport=transport)

    def test_live_credentials_rejected(self):
        with self.assertRaises(ValueError):
            TBankDemo('live-terminal', 'test-only-password')

    def test_live_requires_explicit_readiness_and_rejects_demo(self):
        for terminal, approved in [('testLIVE',False),('testDEMO',True)]:
            with self.assertRaises(ValueError): TBankLive(terminal,'test-only-password',approved=approved)

    def test_live_init_uses_server_product_and_no_fabricated_receipt(self):
        def transport(method,payload):
            self.assertEqual(payload['Description'],'Test fixture app')
            self.assertEqual(payload['Amount'],9900)
            self.assertNotIn('Receipt',payload)
            return dict(TerminalKey='testLIVE',Success=True,ErrorCode='0',PaymentId='123',OrderId='ss_test',Amount=9900,PaymentURL='https://pay.tbank-online.com/test-only')
        client=TBankLive('testLIVE','test-only-password',approved=True,transport=transport)
        client.init_product('ss_test',9900,description='Test fixture app',notification_url='https://example.test/api/notification',success_url='https://example.test/pay/',fail_url='https://example.test/pay/')
        with self.assertRaises(ValueError): client.init_connectivity_probe('ss_test')

    def test_tls_checks_are_enabled(self):
        client = self.client()
        self.assertTrue(client.context.check_hostname)
        self.assertEqual(client.context.verify_mode, ssl.CERT_REQUIRED)

    def test_get_state_and_signature(self):
        self.assertEqual(self.client().get_state('123')['PaymentId'], '123')

    def test_get_state_cannot_substitute_payment(self):
        with self.assertRaises(BankError):
            self.client().get_state('999')

    def init(self, client, **overrides):
        args = dict(notification_url='https://example.test/webhook', success_url='https://example.test/success', fail_url='https://example.test/fail', receipt={'test-only': True})
        args.update(overrides)
        return client.init_payment('ss_test', 49900, **args)

    def test_init_validates_response(self):
        self.assertEqual(self.init(self.client())['Amount'], 49900)

    def test_checkout_cannot_use_an_arbitrary_url(self):
        response = {'TerminalKey': 'testDEMO', 'Success': True, 'ErrorCode': '0', 'PaymentId': '123', 'OrderId': 'ss_test', 'Amount': 49900, 'PaymentURL': 'https://attacker.example/' }
        with self.assertRaises(BankError):
            self.init(self.client(response))

    def test_current_bank_payment_host(self):
        response = {'TerminalKey': 'testDEMO', 'Success': True, 'ErrorCode': '0', 'PaymentId': '123', 'OrderId': 'ss_test', 'Amount': 49900, 'PaymentURL': 'https://pay.tbank-online.com/test-only'}
        self.assertEqual(self.init(self.client(response))['PaymentURL'], response['PaymentURL'])

    def test_bank_host_on_arbitrary_port_rejected(self):
        response = {'TerminalKey': 'testDEMO', 'Success': True, 'ErrorCode': '0', 'PaymentId': '123', 'OrderId': 'ss_test', 'Amount': 49900, 'PaymentURL': 'https://pay.tbank-online.com:8443/test-only'}
        with self.assertRaises(BankError): self.init(self.client(response))

    def test_probe_does_not_fabricate_a_receipt(self):
        def transport(method, payload):
            self.assertNotIn('Receipt', payload)
            self.assertEqual(payload['Amount'], 49900)
            self.assertIn('DEMO', payload['Description'])
            return {'TerminalKey': 'testDEMO', 'Success': True, 'ErrorCode': '0', 'PaymentId': '123', 'OrderId': 'ss_test', 'Amount': 49900, 'PaymentURL': 'https://pay.tbank-online.com/test-only'}
        client = TBankDemo('testDEMO', 'test-only-password', transport=transport)
        client.init_connectivity_probe('ss_test', notification_url='https://example.test/webhook', success_url='https://example.test/success', fail_url='https://example.test/fail')

    def test_receipt_is_required(self):
        with self.assertRaises(ValueError):
            self.init(self.client(), receipt={})

    def test_cross_origin_callbacks_rejected(self):
        with self.assertRaises(ValueError):
            self.init(self.client(), success_url='https://attacker.example/')

    def test_http_callbacks_rejected(self):
        with self.assertRaises(ValueError):
            self.init(self.client(), notification_url='http://example.test/webhook')

    def test_bank_identity_mismatch_rejected(self):
        with self.assertRaises(BankError):
            self.client({'TerminalKey': 'otherDEMO', 'Success': True, 'ErrorCode': '0'}).get_state('123')


if __name__ == '__main__':
    unittest.main()
