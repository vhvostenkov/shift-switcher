#!/usr/bin/env python3
"""Sign a stable update feed using the offline Shift Switcher release key."""
import argparse
import base64
import hashlib
import json
import re
import subprocess
import tempfile
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('version')
parser.add_argument('mac_zip', type=Path)
parser.add_argument('windows_zip', type=Path)
parser.add_argument('output', type=Path)
parser.add_argument('--key', type=Path, default=Path.home() / '.config/shift-switcher-updates/signing.pem')
parser.add_argument('--openssl', default='openssl')
args = parser.parse_args()
if not re.fullmatch(r'(?:0|[1-9][0-9]{0,5})(\.(?:0|[1-9][0-9]{0,5})){2}', args.version):
    parser.error('expected a stable version X.Y.Z')
if args.key.stat().st_mode & 0o077:
    parser.error('signing key must be private (0600)')
public = subprocess.check_output([args.openssl, 'pkey', '-in', str(args.key), '-pubout', '-outform', 'DER'])
expected = base64.b64decode('KWJ0pQmLV+QyNTbiGsi/mU4MGt/ungVsYNfegu4Zzpc=')
# DER SubjectPublicKeyInfo prefix for a 32-byte Ed25519 public key.
if public != bytes.fromhex('302a300506032b6570032100') + expected:
    parser.error('this key does not match the one embedded in Shift Switcher')

def package(path, limit):
    size = path.stat().st_size
    if not 0 < size <= limit:
        parser.error(f'invalid ZIP size: {path}')
    with path.open('rb') as archive:
        digest = hashlib.file_digest(archive, 'sha256').hexdigest()
    return {'bytes': size, 'sha256': digest}

data = json.dumps({
    'schema': 1,
    'version': args.version,
    'mac': package(args.mac_zip, 32 * 1024 * 1024),
    'windows': package(args.windows_zip, 160 * 1024 * 1024),
}, separators=(',', ':')).encode()
with tempfile.TemporaryDirectory(prefix='shift-switcher-sign-') as folder:
    payload = Path(folder) / 'payload'
    payload.write_bytes(data)
    signature = subprocess.check_output([
        args.openssl, 'pkeyutl', '-sign', '-rawin', '-inkey', str(args.key), '-in', str(payload),
    ])
with args.output.open('x', encoding='utf-8') as stream:
    json.dump({'payload': base64.b64encode(data).decode(), 'signature': base64.b64encode(signature).decode()}, stream)
    stream.write('\n')
