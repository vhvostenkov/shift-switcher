import contextlib
import io
import json
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from cryptography.fernet import Fernet
from commerce.backup import main


class BackupTests(unittest.TestCase):
    def test_encrypted_restore_preserves_free_grants_as_well_as_paid_orders(self):
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory);key=Fernet.generate_key();config=folder/'config.json'
            config.write_text(json.dumps({'encryption_key':key.decode()}));config.chmod(0o600)
            with contextlib.closing(sqlite3.connect(folder/'orders.sqlite',isolation_level=None)) as db:
                db.executescript("CREATE TABLE orders(id TEXT); CREATE TABLE benefit_grants(id TEXT); INSERT INTO benefit_grants VALUES('test-free-grant');")
            output=io.StringIO()
            with patch.object(sys,'argv',['backup','--config',str(config),'--data',str(folder),'--output',str(folder/'backups')]),contextlib.redirect_stdout(output):main()
            result=json.loads(output.getvalue());self.assertTrue(result['all_tables_match'])
            archive=folder/'backups'/result['file'];self.assertEqual(archive.stat().st_mode&0o777,0o600)
            with contextlib.closing(sqlite3.connect(':memory:')) as restored:
                restored.deserialize(Fernet(key).decrypt(archive.read_bytes()))
                self.assertEqual(restored.execute('SELECT id FROM benefit_grants').fetchone()[0],'test-free-grant')


if __name__=='__main__':unittest.main()
