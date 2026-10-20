"""Encrypted SQLite snapshot and self-contained restore verification."""
import argparse
import json
import sqlite3
import time
from pathlib import Path
from cryptography.fernet import Fernet


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--config',required=True);parser.add_argument('--data',required=True);parser.add_argument('--output',required=True)
    args=parser.parse_args();config=json.loads(Path(args.config).read_text());cipher=Fernet(config['encryption_key'].encode())
    with sqlite3.connect(Path(args.data)/'orders.sqlite') as source, sqlite3.connect(':memory:') as snapshot:
        source.backup(snapshot);raw=snapshot.serialize()
        original=snapshot.execute('SELECT COUNT(*) FROM orders').fetchone()[0]
    encrypted=cipher.encrypt(raw)
    directory=Path(args.output);directory.mkdir(mode=0o700,parents=True,exist_ok=True)
    target=directory/('orders-'+str(time.time_ns())+'.fernet')
    with target.open('xb') as stream:stream.write(encrypted)
    target.chmod(0o600)
    with sqlite3.connect(':memory:') as restored:
        restored.deserialize(cipher.decrypt(target.read_bytes()))
        assert restored.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
        assert restored.execute('SELECT COUNT(*) FROM orders').fetchone()[0]==original
    print(json.dumps({'backup_created':True,'restore_integrity':'ok','orders_match':True,'file':target.name}))


if __name__=='__main__':main()
