"""Owner-only operations over SSH. Never expose this CLI as a public HTTP API."""
import argparse
import json
import sqlite3
import time
from pathlib import Path
from urllib.parse import urlsplit


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--data',required=True)
    sub=parser.add_subparsers(dest='action',required=True)
    sub.add_parser('receipts')
    attach=sub.add_parser('attach-receipt');attach.add_argument('--order',required=True);attach.add_argument('--url',required=True)
    args=parser.parse_args()
    db=sqlite3.connect(Path(args.data)/'orders.sqlite');db.row_factory=sqlite3.Row
    try:
        if args.action=='receipts':
            rows=db.execute("SELECT orders.id,orders.amount,orders.status,sessions.email,receipt_tasks.status receipt_status,receipt_tasks.url FROM receipt_tasks JOIN orders ON orders.id=receipt_tasks.order_id JOIN sessions ON sessions.order_id=orders.id ORDER BY receipt_tasks.updated").fetchall()
            print(json.dumps([dict(r) for r in rows],ensure_ascii=False,indent=2));return
        url=urlsplit(args.url)
        if url.scheme!='https' or url.hostname!='lknpd.nalog.ru' or not url.path.startswith('/api/v1/receipt/') or url.username or url.password or url.query or url.fragment:
            raise SystemExit('Only a receipt link from My Tax is accepted. Operator must verify its actual contents.')
        with db:
            row=db.execute('SELECT orders.status,receipt_tasks.status receipt_status FROM orders JOIN receipt_tasks ON orders.id=receipt_tasks.order_id WHERE orders.id=?',(args.order,)).fetchone()
            if not row or row['status']!='CONFIRMED' or row['receipt_status'].startswith('DEMO'):
                raise SystemExit('Not a confirmed LIVE order awaiting a fiscal receipt.')
            db.execute("UPDATE receipt_tasks SET status='ISSUED',url=?,updated=? WHERE order_id=?",(args.url,int(time.time()),args.order))
            db.execute("INSERT INTO events(at,order_id,kind,detail) VALUES(?,?,'receipt_attached','owner verified')",(int(time.time()),args.order))
        print('Receipt attached. Email delivery is a separate verified operation.')
    finally:db.close()


if __name__=='__main__':main()
