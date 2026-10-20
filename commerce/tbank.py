"""T-Bank DEMO-only transport. No live payments or refunds are enabled."""
import json
import ssl
import urllib.request
from urllib.parse import urlparse

from .ledger import bank_token, amount


class BankError(Exception):
    pass


class TBankDemo:
    endpoint = 'https://securepay.tinkoff.ru/v2/'

    def __init__(self, terminal: str, password: str, *, ca_file=None, transport=None):
        if not terminal.endswith('DEMO') or not password:
            raise ValueError('only explicit DEMO credentials accepted')
        self.terminal, self.password = terminal, password
        self.context = ssl.create_default_context()
        if ca_file:
            self.context.load_verify_locations(cafile=ca_file)
        self.transport = transport or self._http

    def _http(self, method, payload):
        request = urllib.request.Request(self.endpoint + method,
            data=json.dumps(payload).encode(), headers={'Content-Type': 'application/json'}, method='POST')
        with urllib.request.urlopen(request, timeout=15, context=self.context) as response:
            raw = response.read(1048577)
        if len(raw) > 1048576:
            raise BankError('bank response too large')
        return json.loads(raw)

    def _request(self, method, data):
        payload = dict(data, TerminalKey=self.terminal)
        payload['Token'] = bank_token(payload, self.password)
        result = self.transport(method, payload)
        if not isinstance(result, dict) or result.get('Success') is not True or str(result.get('ErrorCode')) != '0' or result.get('TerminalKey') != self.terminal:
            raise BankError('bank rejected request or identity mismatch')
        return result

    def get_state(self, payment):
        result = self._request('GetState', {'PaymentId': payment})
        if str(result.get('PaymentId')) != str(payment):
            raise BankError('payment identity mismatch')
        return result

    def init_payment(self, order, price, *, notification_url, success_url, fail_url, receipt):
        if not order.startswith('ss_') or type(price) is not int or price != 49900:
            raise ValueError('unexpected product order or price')
        # Receipt generation is a separate, owner-approved integration; never fabricate it.
        if not isinstance(receipt, dict) or not receipt:
            raise ValueError('explicit receipt configuration required')
        urls = [urlparse(u) for u in (notification_url, success_url, fail_url)]
        if any(u.scheme != 'https' or not u.hostname or u.username or u.password for u in urls) or len({u.netloc for u in urls}) != 1:
            raise ValueError('same-origin HTTPS callback URLs required')
        result = self._request('Init', {'OrderId': order, 'Amount': price,
            'Description': 'Shift Switcher Pro 1.x, 2 Mac', 'NotificationURL': notification_url,
            'SuccessURL': success_url, 'FailURL': fail_url, 'Receipt': receipt})
        payment = str(result.get('PaymentId', ''))
        target = urlparse(result.get('PaymentURL', ''))
        if not payment.isascii() or not payment.isdigit() or amount(result.get('Amount')) != price or result.get('OrderId') != order:
            raise BankError('Init identity or amount mismatch')
        if target.scheme != 'https' or target.hostname not in {'securepay.tinkoff.ru', 'securepay.tbank.ru'} or target.username or target.password:
            raise BankError('unexpected bank payment URL')
        return result
