"""Create one DEMO order and independently read its state; no paid entitlement.

Run explicitly, never during unit tests. Callbacks are placeholders until a
dedicated server exists. A successful Init is not a payment/webhook/receipt test.
"""
import json
import os
import uuid
from pathlib import Path

from .tbank import TBankDemo


def main():
    folder = Path.home() / '.config' / 'shift-switcher-commerce'
    config_path = folder / 'tbank-demo.json'
    if config_path.stat().st_mode & 0o077:
        raise SystemExit('DEMO configuration must be private (0600).')
    config = json.loads(config_path.read_text())
    bank = TBankDemo(config['terminal'], config['password'], ca_file=str(folder / 'tbank-ca.pem'))
    order = 'ss_' + uuid.uuid4().hex
    result = bank.init_connectivity_probe(order,
        notification_url='https://vhvost.com/shift-switcher/demo/notification',
        success_url='https://vhvost.com/shift-switcher/?demo=success',
        fail_url='https://vhvost.com/shift-switcher/?demo=failed')
    target = folder / f'demo-probe-{order}.json'
    fd = os.open(target, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(fd, 'w') as stream:
        json.dump({'order': order, 'payment': str(result['PaymentId']), 'url': result['PaymentURL'], 'amount': 49900}, stream)
    state = bank.get_state(str(result['PaymentId']))
    print(json.dumps({'environment': 'DEMO', 'init_verified': True,
        'get_state_verified': True, 'status': state.get('Status'),
        'amount_matches': state.get('Amount') == 49900,
        'private_record': str(target), 'tls_verified': True,
        'payment_completed': False, 'webhook_verified': False, 'receipt_verified': False}, ensure_ascii=False))


if __name__ == '__main__':
    main()
