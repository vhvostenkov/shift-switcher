import json
import tempfile
import unittest
from pathlib import Path
from cryptography.fernet import Fernet
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from commerce.catalog import DEFAULT_CATALOG
from commerce.delivery import deliver_one, enqueue, load_config
from commerce.ledger import bank_token
from commerce.service import Commerce


class MailQueueTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.sent=[]
        product=dict(DEFAULT_CATALOG['shift-switcher-pro-v1'],terms_version='fixture-v1',privacy_version='fixture-v1')
        signer=Ed25519PrivateKey.generate().private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()).decode()
        self.config=dict(environment='LIVE',terminal='testLIVE',password='test-only-password',signing_key=signer,key_id='test-key',encryption_key=Fernet.generate_key().decode(),edge_key='test-edge',origin='https://example.test',checkout_enabled=True,bank_approved=True,fresh_credentials_verified=True,receipt_delivery_verified=True,legal_terms_approved=True,live_checkout_approved=True,products={'shift-switcher-pro-v1':product},smtp_enabled=True,smtp_from='seller@example.test')
        class BankFixture:
            status='CONFIRMED'
            def get_state(self,p):return dict(TerminalKey='testLIVE',Success=True,ErrorCode='0',PaymentId=p,Amount=49900,Status=self.status)
        self.bank=BankFixture();self.service=Commerce(self.temp.name,self.config,bank=self.bank)
        _,order=self.service.create_checkout('a'*64,'buyer@example.test',consent=dict(accepted=True,terms_version='fixture-v1',privacy_version='fixture-v1'))
        self.order=order['order'];self.service.ledger.bind_payment(self.order,'123')
        self.event=dict(TerminalKey='testLIVE',Success=True,ErrorCode='0',PaymentId='123',OrderId=self.order,Amount=49900,Status='CONFIRMED')
        self.event['Token']=bank_token(self.event,'test-only-password');self.service.notification(self.event)
    def tearDown(self):self.temp.cleanup()
    def sender(self,message,recipient):self.sent.append((message,recipient))
    def test_private_mail_file_cannot_enable_sending_or_override_environment(self):
        folder=Path(self.temp.name)
        mail=folder/'mail.json';mail.write_text(json.dumps(dict(smtp_host='smtp.example.test',smtp_user='sender@example.test',smtp_password='fixture-only',smtp_from='sender@example.test',smtp_enabled=True,environment='LIVE',checkout_enabled=True)))
        config=folder/'config.json';config.write_text(json.dumps(dict(environment='DEMO',smtp_enabled=False,checkout_enabled=False,smtp_config=str(mail))))
        loaded=load_config(config)
        self.assertEqual(loaded['environment'],'DEMO')
        self.assertFalse(loaded['smtp_enabled']);self.assertFalse(loaded['checkout_enabled'])
        self.assertEqual(loaded['smtp_password'],'fixture-only')

    def test_license_goes_only_to_stored_buyer_with_matching_attachment(self):
        self.assertEqual(deliver_one(self.temp.name,self.config,sender=self.sender),'SENT')
        message,recipient=self.sent[0]
        self.assertEqual(recipient,'buyer@example.test')
        attachment=list(message.iter_attachments())[0]
        body=json.loads(attachment.get_payload(decode=True))
        self.assertEqual(body['license_key'],self.service.license_file('a'*64)['license_key'])
        self.assertEqual(body['environment'],'LIVE')
    def test_repeated_webhook_and_worker_do_not_duplicate_mail(self):
        self.service.notification(self.event)
        self.assertEqual(deliver_one(self.temp.name,self.config,sender=self.sender),'SENT')
        self.service.notification(self.event)
        self.assertEqual(deliver_one(self.temp.name,self.config,sender=self.sender),'EMPTY')
        self.assertEqual(len(self.sent),1)
    def test_uncertain_delivery_is_never_automatically_repeated(self):
        def disconnect(message,recipient):raise OSError('fixture disconnect after DATA')
        self.assertEqual(deliver_one(self.temp.name,self.config,sender=disconnect),'UNCERTAIN')
        self.assertEqual(deliver_one(self.temp.name,self.config,sender=self.sender),'EMPTY')
        self.assertEqual(self.sent,[])
    def test_demo_config_can_never_send(self):
        config=dict(self.config,environment='DEMO')
        self.assertEqual(deliver_one(self.temp.name,config,sender=self.sender),'DISABLED')
        self.assertEqual(self.sent,[])
    def test_refund_cancels_pending_delivery(self):
        self.bank.status='REFUNDED';self.event['Status']='REFUNDED';self.event['Token']=bank_token(self.event,'test-only-password')
        self.service.notification(self.event)
        self.assertEqual(deliver_one(self.temp.name,self.config,sender=self.sender),'EMPTY')
        self.assertEqual(self.sent,[])
    def test_receipt_requires_owner_attached_link(self):
        deliver_one(self.temp.name,self.config,sender=self.sender)
        with self.service.ledger.connect() as db:
            db.execute("UPDATE receipt_tasks SET status='ISSUED',url='https://lknpd.nalog.ru/api/v1/receipt/fixture/fixture/print' WHERE order_id=?",(self.order,))
            enqueue(db,self.order,'receipt')
        self.assertEqual(deliver_one(self.temp.name,self.config,sender=self.sender),'SENT')
        self.assertIn('/receipt/fixture/fixture/print',self.sent[-1][0].get_body().get_content())


if __name__=='__main__':unittest.main()
