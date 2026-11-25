#!/usr/bin/env python3
"""Package already-published Windows preview binaries; never claims native UI QA."""
import hashlib
import json
import subprocess
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parent.parent
commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
dirty = bool(subprocess.check_output(['git', 'status', '--porcelain', '--', 'windows', 'scripts/package-windows-preview.py'], cwd=root, text=True).strip())
if dirty:
    raise SystemExit('Commit Windows source before packaging so the manifest names its exact revision')
runtime = '10.0.15'
packages = Path.home() / '.nuget/packages'
result = {'source_commit': commit, 'runtime': runtime, 'native_ui_acceptance': 'pending', 'artifacts': []}
for rid, machine in [('win-x64', 0x8664), ('win-arm64', 0xaa64)]:
    source = root / 'release/windows' / rid
    executable = source / 'ShiftSwitcher.exe'
    data = executable.read_bytes()
    pe = int.from_bytes(data[0x3c:0x40], 'little')
    if data[:2] != b'MZ' or data[pe:pe+4] != b'PE\0\0' or int.from_bytes(data[pe+4:pe+6], 'little') != machine:
        raise SystemExit(f'{rid}: expected architecture does not match PE header')
    deps = root / 'windows/ShiftSwitcher.Windows/bin/Release/net10.0-windows' / rid / 'ShiftSwitcher.deps.json'
    libraries = json.loads(deps.read_text())['libraries']
    for framework in ['Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App']:
        if f'runtimepack.{framework}.Runtime.{rid}/{runtime}' not in libraries:
            raise SystemExit(f'{rid}: unexpected runtime framework version')
    binary_hash = hashlib.sha256(data).hexdigest()
    manifest = {'product': 'Shift Switcher Windows Preview', 'version': '0.1.0-preview.1', 'rid': rid,
                'source_commit': commit, 'runtime': runtime, 'sha256': binary_hash, 'native_ui_acceptance': 'pending'}
    destination = root / 'release' / f'Shift-Switcher-Windows-0.1.0-preview.1-{rid}.zip'
    with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        archive.writestr('ShiftSwitcher.exe', data)
        archive.write(root / 'windows/README.md', 'README.md')
        archive.write(root / 'windows/ACCEPTANCE.md', 'ACCEPTANCE.md')
        archive.writestr('manifest.json', json.dumps(manifest, indent=2) + '\n')
        for package in ['microsoft.netcore.app.runtime.' + rid, 'microsoft.windowsdesktop.app.runtime.' + rid]:
            folder = packages / package / runtime
            for name in ['LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT']:
                if (folder / name).is_file(): archive.write(folder / name, f'licenses/{package}/{name}')
    result['artifacts'].append({'rid': rid, 'binary_sha256': binary_hash, 'archive': str(destination.relative_to(root)),
                                'archive_sha256': hashlib.sha256(destination.read_bytes()).hexdigest()})
receipt = root / 'release/windows-preview-manifest.json'
receipt.write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps(result, indent=2))
