"""Transactional product mail queue. DEMO never sends mail; sending is opt-in."""
import argparse
import json
import smtplib
import sqlite3
import ssl
import time
from contextlib import closing
from email.message import EmailMessage
from email.utils import formatdate
from pathlib import Path

from cryptography.fernet import Fernet
from .catalog import catalog_from


def ensure_tables(db):
    db.execute('''CREATE TABLE IF NOT EXISTS mail_jobs (
        order_id TEXT NOT NULL REFERENCES orders(id), kind TEXT NOT NULL,
        status TEXT NOT NULL, updated INTEGER NOT NULL,
        PRIMARY KEY(order_id,kind)
    )''')


def enqueue(db,order,kind):
    if kind not in {'license','receipt'}: raise ValueError('invalid mail kind')
    db.execute("INSERT OR IGNORE INTO mail_jobs VALUES(?,?,'PENDING',?)",(order,kind,int(time.time())))


def smtp_send(config,message,recipient):
    context=ssl.create_default_context()
    with smtplib.SMTP_SSL(config['smtp_host'],465,timeout=20,context=context) as client:
        client.login(config['smtp_user'],config['smtp_password'])
        client.send_message(message,from_addr=config['smtp_from'],to_addrs=[recipient])


def deliver_one(folder,config,*,sender=None):
    if config.get('environment','DEMO')!='LIVE' or config.get('smtp_enabled') is not True:
        return 'DISABLED'
    with closing(sqlite3.connect(Path(folder)/'orders.sqlite',timeout=10,isolation_level=None)) as db:
        db.row_factory=sqlite3.Row
        # Refuse accidental delivery from a DEMO database even with wrong config.
        tag=db.execute("SELECT value FROM ledger_metadata WHERE key='environment'").fetchone()
        if not tag or tag[0]!='LIVE': raise ValueError('mail database environment mismatch')
        ensure_tables(db);db.execute('BEGIN IMMEDIATE')
        job=db.execute("SELECT * FROM mail_jobs WHERE status='PENDING' ORDER BY updated LIMIT 1").fetchone()
        if not job:db.commit();return 'EMPTY'
        order=db.execute('SELECT * FROM orders WHERE id=?',(job['order_id'],)).fetchone()
        if not order or order['status']!='CONFIRMED':
            db.execute("UPDATE mail_jobs SET status='CANCELED',updated=? WHERE order_id=? AND kind=?",(int(time.time()),job['order_id'],job['kind']))
            db.commit();return 'CANCELED'
        db.execute("UPDATE mail_jobs SET status='SENDING',updated=? WHERE order_id=? AND kind=?",(int(time.time()),job['order_id'],job['kind']))
        db.commit()
        try:
            session=db.execute('SELECT * FROM sessions WHERE order_id=?',(order['id'],)).fetchone()
            cipher=Fernet(config['encryption_key'].encode())
            recipient=cipher.decrypt(session['email'].encode()).decode()
            product=catalog_from(config)[order['product']]
            message=EmailMessage()
            message['From']=config['smtp_from'];message['To']=recipient
            message['Date']=formatdate(usegmt=True)
            message['Message-ID']='<'+order['id']+'.'+job['kind']+'@vhvost.com>'
            message['Subject']='VHVost — '+product['name']+(': лицензия' if job['kind']=='license' else ': чек')
            if job['kind']=='license':
                key=cipher.decrypt(session['encrypted_key'].encode()).decode()
                message.set_content('Спасибо за покупку '+product['name']+'.\nЗаказ: '+order['id']+'\n\nКлюч лицензии: '+key+'\n\nСкачивание: https://vhvost.com'+product['download']+'\nСохраните приложенный файл лицензии и откройте его в приложении.\nЧек придёт отдельным письмом.\nПоддержка: vhvostenkov@gmail.com\n')
                payload=dict(schema=1,product=order['product'],environment='LIVE',license_key=key)
                filename='Shift-Switcher.sslicense' if order['product']=='shift-switcher-pro-v1' else order['product']+'.json'
                message.add_attachment(json.dumps(payload).encode(),maintype='application',subtype='json',filename=filename)
            else:
                receipt=db.execute("SELECT url FROM receipt_tasks WHERE order_id=? AND status='ISSUED'",(order['id'],)).fetchone()
                if not receipt or not receipt[0]:raise ValueError('actual receipt is not attached')
                message.set_content('Чек по заказу '+order['id']+' на '+product['name']+':\n'+receipt[0]+'\n\nПоддержка: vhvostenkov@gmail.com\n')
            (sender or (lambda m,r:smtp_send(config,m,r)))(message,recipient)
        except Exception:
            # A disconnect after DATA can mean accepted mail. Never auto-repeat it.
            db.execute("UPDATE mail_jobs SET status='UNCERTAIN',updated=? WHERE order_id=? AND kind=?",(int(time.time()),job['order_id'],job['kind']))
            return 'UNCERTAIN'
        db.execute("UPDATE mail_jobs SET status='SENT',updated=? WHERE order_id=? AND kind=?",(int(time.time()),job['order_id'],job['kind']))
        return 'SENT'


def load_config(path):
    config=json.loads(Path(path).read_text())
    if config.get('smtp_config'):
        mail=json.loads(Path(config['smtp_config']).read_text())
        # A mail file cannot enable LIVE or override bank/readiness settings.
        for key in ['smtp_host','smtp_user','smtp_password','smtp_from']:
            config[key]=mail[key]
    return config


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--data',required=True);parser.add_argument('--config',required=True)
    parser.add_argument('--send',action='store_true');args=parser.parse_args()
    config=load_config(args.config)
    if not args.send:
        with closing(sqlite3.connect(Path(args.data)/'orders.sqlite')) as db:
            ensure_tables(db);print(json.dumps(dict(db.execute('SELECT status,COUNT(*) FROM mail_jobs GROUP BY status'))))
        return
    for _ in range(10):
        status=deliver_one(args.data,config);print(status)
        if status in {'EMPTY','DISABLED','UNCERTAIN'}:break


if __name__=='__main__':main()
