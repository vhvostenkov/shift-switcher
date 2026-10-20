#!/usr/bin/env python3
"""Package an already-built app; preserve executable permissions and signature."""
import argparse
import hashlib
import plistlib
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--app', type=Path, default=root / 'build/Shift Switcher.app')
args = parser.parse_args()
app = args.app.resolve()
with (app / 'Contents/Info.plist').open('rb') as stream:
    version = plistlib.load(stream)['CFBundleShortVersionString']
output = root / 'release'
output.mkdir(exist_ok=True)
archive = output / f'Shift-Switcher-{version}-macOS-arm64.zip'
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as bundle:
    for path in sorted(app.rglob('*')):
        if path.is_file():
            bundle.write(path, str(path.relative_to(app.parent)))
    bundle.write(root / 'docs/Установка.txt', 'Установка.txt')
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
(output / 'SHA256SUMS.txt').write_text(f'{digest}  {archive.name}\n')
print(f'{digest}  {archive}')
