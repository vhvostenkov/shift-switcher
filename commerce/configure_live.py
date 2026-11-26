"""Owner-only hidden input of approved LIVE credentials. No bank or SMTP requests."""
import argparse
import base64
import getpass
import json
import os
import re
from pathlib import Path

from cryptography.fernet import Fernet
from cryptography.hazmat.primitives import serialization


def prepare(base, signing_key, public_keys, terminal, password, *, fresh_confirmed, retain_existing_approved=False):
    if fresh_confirmed is not True and retain_existing_approved is not True:
        raise ValueError('Explicit owner approval of fresh or retained credentials required')
    if not re.fullmatch(r'[A-Za-z0-9_-]{4,80}',terminal) or terminal.endswith('DEMO'):
        raise ValueError('A working API TerminalKey is required, not a DEMO key or merchant ID')
    if not 8<=len(password)<=256 or any(c.isspace() or ord(c)<32 for c in password):
        raise ValueError('Invalid working password format')
    signer=serialization.load_pem_private_key(signing_key.encode(),password=None)
    raw=signer.public_key().public_bytes(serialization.Encoding.Raw,serialization.PublicFormat.Raw)
    if base64.b64encode(raw).decode()!=public_keys.get('LIVE:ss-live-1'):
        raise ValueError('Signing key does not match the shipped LIVE trust key')
    # Routing secrets are retained; DEMO encryption and signing material are not reused.
    config={key:base[key] for key in ['bind','port','edge_key','ca_file','smtp_config'] if key in base}
    config.update(environment='LIVE',terminal=terminal,password=password,signing_key=signing_key,
        key_id='ss-live-1',encryption_key=Fernet.generate_key().decode(),origin='https://vhvost.com/pay',
        checkout_url='https://vhvost.com/pay/',revision='owner-configured-closed-live',
        bank_approved=True,fresh_credentials_verified=fresh_confirmed is True,
        existing_credentials_owner_approved=retain_existing_approved is True and fresh_confirmed is not True,
        credentials_source='owner_confirmed_fresh_hidden_input' if fresh_confirmed else 'owner_explicitly_retained_existing_credentials',
        checkout_enabled=False,control_checkout_approved=False,legal_terms_approved=False,
        receipt_delivery_verified=False,live_checkout_approved=False,smtp_enabled=False)
    return config


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-config',required=True);parser.add_argument('--signing-key',required=True)
    parser.add_argument('--public-keys',required=True);parser.add_argument('--output',required=True)
    parser.add_argument('--retain-existing-owner-approved',action='store_true',help='Only after an explicit owner decision to keep the current terminal password')
    args=parser.parse_args()
    base_path,key_path,output=map(Path,[args.base_config,args.signing_key,args.output])
    for path in [base_path,key_path]:
        if path.stat().st_mode & 0o077:raise SystemExit('Private input files (0600) required')
    if output.exists():raise SystemExit('Output already exists; reconcile it before repeating')
    if not output.parent.is_dir() or output.parent.stat().st_mode & 0o077:
        raise SystemExit('Use an existing private directory (0700)')
    print('Only for the owner-approved active Shift Switcher shop. Sales and mail remain OFF.')
    print('Use the working API TerminalKey and terminal password, never the bank login password.')
    terminal=getpass.getpass('Working API TerminalKey (hidden): ')
    password=getpass.getpass('Working terminal password (hidden): ')
    confirmed='' if args.retain_existing_owner_approved else input('Have you replaced the previously exposed terminal password and copied these values from the working terminal? [yes/no]: ')
    config=prepare(json.loads(base_path.read_text()),key_path.read_text(),json.loads(Path(args.public_keys).read_text()),terminal,password,fresh_confirmed=confirmed=='yes',retain_existing_approved=args.retain_existing_owner_approved)
    with os.fdopen(os.open(output,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600),'w') as stream:
        json.dump(config,stream);stream.write('\n');stream.flush();os.fsync(stream.fileno())
    print('Private LIVE configuration saved. No API authentication, order, payment, receipt or email was performed.')


if __name__=='__main__':main()
