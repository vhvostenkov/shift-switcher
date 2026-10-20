"""Run manually in a local terminal. Never paste credentials into a chat."""
import getpass
import json
import os
from pathlib import Path


def main():
    folder = Path.home() / '.config' / 'shift-switcher-commerce'
    target = folder / 'tbank-demo.json'
    if target.exists():
        raise SystemExit('DEMO configuration already exists; it was not overwritten.')
    terminal = getpass.getpass('DEMO TerminalKey (hidden): ').strip()
    password = getpass.getpass('DEMO password (hidden): ')
    if not terminal.endswith('DEMO') or not password:
        raise SystemExit('Only a DEMO terminal and non-empty password are accepted.')
    folder.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(folder, 0o700)
    fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w') as stream:
        json.dump({'terminal': terminal, 'password': password}, stream)
    print('DEMO configuration saved locally with mode 0600. No bank request was made.')


if __name__ == '__main__':
    main()
