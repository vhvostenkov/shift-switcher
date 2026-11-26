"""Offline issuer for an explicitly approved, device-bound owner license.

No order, payment or refund state is changed. Private signing config stays local.
"""
import argparse
import base64
import json
import os
import time
import uuid
from pathlib import Path
from cryptography.hazmat.primitives import serialization


def issue(config, device, days=365, now=None, *, allow_live=False):
    environment = config.get('environment', 'DEMO')
    if environment not in {'DEMO', 'LIVE'} or (environment == 'LIVE' and not allow_live):
        raise ValueError('LIVE owner issuance requires explicit operator opt-in')
    if len(device) != 64 or any(c not in '0123456789abcdef' for c in device):
        raise ValueError('installation SHA-256 required')
    if not 1 <= days <= 365:
        raise ValueError('owner term must be 1..365 days')
    signer = serialization.load_pem_private_key(config['signing_key'].encode(), password=None)
    now = int(time.time()) if now is None else now
    payload = dict(schema=1, kind='owner', product='shift-switcher-pro-v1',
                   license='owner_' + uuid.uuid4().hex, device=device,
                   issued_at=now, valid_until=now + days * 86400,
                   key_id=config['key_id'], environment=environment)
    def encode(data):
        return base64.urlsafe_b64encode(data).decode().rstrip('=')
    message = 'ss1.' + encode(json.dumps(payload, sort_keys=True, separators=(',', ':')).encode())
    return dict(schema=1, product=payload['product'], environment=environment,
                certificate=message + '.' + encode(signer.sign(message.encode())))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True)
    parser.add_argument('--device', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--allow-live-owner', action='store_true', help='Only for an owner-approved free grant to this exact device; no paid order is fabricated')
    args = parser.parse_args()
    output = Path(args.output)
    result = issue(json.loads(Path(args.config).read_text()), args.device, allow_live=args.allow_live_owner)
    # Refuse overwrites, keep the entitlement outside Git with private permissions.
    fd = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w') as stream:
        json.dump(result, stream); stream.write('\n'); stream.flush(); os.fsync(stream.fileno())
    print('Signed owner certificate saved privately; no bank operation performed.')


if __name__ == '__main__':
    main()
