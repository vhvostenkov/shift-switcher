"""Owner-only operations over SSH. Never expose this CLI as a public HTTP API."""
import argparse
import json
import os
import sqlite3
import time
from pathlib import Path
from urllib.parse import urlsplit
from .delivery import ensure_tables, enqueue


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--data',required=True);parser.add_argument('--config',required=True)
    sub=parser.add_subparsers(dest='action',required=True)
    sub.add_parser('receipts')
    control=sub.add_parser('prepare-control-checkout')
    control.add_argument('--email',required=True); control.add_argument('--product',default='shift-switcher-pro-v1')
    control.add_argument('--output',required=True)
    attach=sub.add_parser('attach-receipt');attach.add_argument('--order',required=True);attach.add_argument('--url',required=True)
    args=parser.parse_args()
    if args.action=='prepare-control-checkout':
        from .service import Commerce
        from urllib.parse import urlencode
        config_path=Path(args.config)
        if config_path.stat().st_mode & 0o077: raise SystemExit('Private configuration required')
        config=json.loads(config_path.read_text())
        checkout=config.get('checkout_url','')
        url=urlsplit(checkout)
        if url.scheme!='https' or url.netloc!='vhvost.com' or url.path!='/pay/' or url.query or url.fragment:
            raise SystemExit('Expected the owned HTTPS checkout URL')
        # Reserve a private output file before creating anything. Never print the token.
        fd=os.open(args.output,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
        with os.fdopen(fd,'w') as output:
            service=Commerce(args.data,config)
            token,order=service.prepare_control_checkout(args.email,args.product)
            json.dump(dict(order=order['order'],url=checkout+'?'+urlencode({'product':args.product})+'#control='+token,expires_in=3600),output)
            output.write('\n');output.flush();os.fsync(output.fileno())
        print('Private control checkout saved. No bank initialization or payment performed.');return
    db=sqlite3.connect(Path(args.data)/'orders.sqlite');db.row_factory=sqlite3.Row
    try:
        if args.action=='receipts':
            rows=db.execute("SELECT orders.id,orders.amount,orders.status,sessions.email,receipt_tasks.status receipt_status,receipt_tasks.url FROM receipt_tasks JOIN orders ON orders.id=receipt_tasks.order_id JOIN sessions ON sessions.order_id=orders.id ORDER BY receipt_tasks.updated").fetchall()
            from cryptography.fernet import Fernet
            cipher=Fernet(json.loads(Path(args.config).read_text())['encryption_key'].encode())
            result=[dict(r) for r in rows]
            for row in result:row['email']=cipher.decrypt(row['email'].encode()).decode()
            print(json.dumps(result,ensure_ascii=False,indent=2));return
        url=urlsplit(args.url)
        if url.scheme!='https' or url.hostname!='lknpd.nalog.ru' or not url.path.startswith('/api/v1/receipt/') or url.username or url.password or url.query or url.fragment:
            raise SystemExit('Only a receipt link from My Tax is accepted. Operator must verify its actual contents.')
        with db:
            row=db.execute('SELECT orders.status,receipt_tasks.status receipt_status FROM orders JOIN receipt_tasks ON orders.id=receipt_tasks.order_id WHERE orders.id=?',(args.order,)).fetchone()
            if not row or row['status']!='CONFIRMED' or row['receipt_status'].startswith('DEMO'):
                raise SystemExit('Not a confirmed LIVE order awaiting a fiscal receipt.')
            db.execute("UPDATE receipt_tasks SET status='ISSUED',url=?,updated=? WHERE order_id=?",(args.url,int(time.time()),args.order))
            db.execute("INSERT INTO events(at,order_id,kind,detail) VALUES(?,?,'receipt_attached','owner verified')",(int(time.time()),args.order))
            ensure_tables(db);enqueue(db,args.order,'receipt')
        print('Receipt attached. Email delivery is a separate verified operation.')
    finally:db.close()


if __name__=='__main__':main()
