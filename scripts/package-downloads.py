#!/usr/bin/env python3
"""Package exact tested binaries behind stable, version-free download names."""
import argparse
import hashlib
import json
import shutil
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--windows-exe', type=Path, required=True)
parser.add_argument('--windows-receipt', type=Path, required=True)
parser.add_argument('--mac-archive', type=Path, required=True)
parser.add_argument('--mac-receipt', type=Path, required=True)
parser.add_argument('--commercial-windows', action='store_true', help='Include Pro instructions and the Ed25519 dependency licence for the verified commercial build')
args = parser.parse_args()
windows = json.loads(args.windows_receipt.read_text())
mac = json.loads(args.mac_receipt.read_text())
binary = args.windows_exe.read_bytes()
assert hashlib.sha256(binary).hexdigest() == windows['installed_exe_sha256'], 'Windows EXE differs from native receipt'
pe = int.from_bytes(binary[0x3c:0x40], 'little')
assert binary[:2] == b'MZ' and binary[pe:pe+4] == b'PE\0\0' and binary[pe+4:pe+6] == b'd\x86', 'Windows x64 PE required'
with zipfile.ZipFile(args.mac_archive) as bundle:
    names = [name for name in bundle.namelist() if '/Contents/MacOS/' in name and not name.endswith('/')]
    assert len(names) == 1 and hashlib.sha256(bundle.read(names[0])).hexdigest() == mac['binary_sha256'], 'Mac binary differs from native receipt'
destination = root / 'release/downloads'
destination.mkdir(parents=True, exist_ok=True)
win_zip = destination / 'Shift-Switcher-Windows.zip'
with zipfile.ZipFile(win_zip, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
    bundle.writestr('ShiftSwitcher.exe', binary)
    bundle.write(root / ('docs/Windows-Pro-Установка.txt' if args.commercial_windows else 'docs/Windows-Установка.txt'), 'Установка.txt')
    if args.commercial_windows:
        notice = Path.home() / '.nuget/packages/bouncycastle.cryptography/2.7.0/LICENSE.md'
        assert notice.is_file(), 'Missing Bouncy Castle notice'
        bundle.write(notice, 'licenses/bouncycastle.cryptography/LICENSE.md')
    for name in ['microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64']:
        folder = Path.home() / '.nuget/packages' / name / '10.0.15'
        notices = [p for p in folder.iterdir() if p.name.upper() in {'LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'}]
        assert notices, f'Missing runtime notices: {name}'
        for notice in notices:
            bundle.write(notice, f'licenses/{name}/{notice.name}')
mac_zip = destination / 'Shift-Switcher-Mac.zip'
shutil.copyfile(args.mac_archive, mac_zip)
manifest = {'schema': 1, 'channel': 'release-candidate' if args.commercial_windows else 'preview', 'downloads': {}}
for platform, archive, binary_hash, source in [
    ('windows', win_zip, windows['installed_exe_sha256'], windows['source_commit']),
    ('mac', mac_zip, mac['binary_sha256'], mac['source_commit'])]:
    manifest['downloads'][platform] = dict(filename=archive.name, bytes=archive.stat().st_size,
        sha256=hashlib.sha256(archive.read_bytes()).hexdigest(), binary_sha256=binary_hash, source_commit=source)
(destination / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps(manifest, indent=2))
