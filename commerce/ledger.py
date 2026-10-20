"""Local transaction core. Real bank transport and public HTTP are not implemented here."""
import hashlib
import hmac
import json
import secrets
import sqlite3
import time
import uuid
from contextlib import closing
from pathlib import Path

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

from .catalog import DEFAULT_PRODUCT, catalog_from

PRODUCT = DEFAULT_PRODUCT


def bank_token(payload: dict, password: str) -> str:
    # T-Bank: root scalar values only, sorted keys; booleans are lowercase.
    values = {k: v for k, v in payload.items()
              if k not in {"Token", "Password"} and v is not None and isinstance(v, (str, bool, int, float))}
    values["Password"] = password
    text = "".join(str(v).lower() if isinstance(v, bool) else str(v)
                   for _, v in sorted(values.items()))
    return hashlib.sha256(text.encode()).hexdigest()


def amount(value) -> int:
    if type(value) is int and value >= 0:
        return value
    if isinstance(value, str) and value.isascii() and value.isdigit():
        return int(value)
    raise ValueError("invalid amount")


def b64(data: bytes) -> str:
    import base64
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


class Ledger:
    def __init__(self, path: Path, *, terminal: str, password: str,
                 signing_key: Ed25519PrivateKey, key_id: str, sandbox: bool = True, products=None):
        if not terminal or not password or not key_id:
            raise ValueError("explicit credentials and signing key ID required")
        if sandbox != terminal.endswith("DEMO"):
            raise ValueError("terminal environment mismatch")
        self.path, self.terminal, self.password = str(path), terminal, password
        self.products = catalog_from({} if products is None else {"products": products})
        self.sandbox = sandbox
        self.signing_key, self.key_id = signing_key, key_id
        with closing(self.connect()) as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS orders (
                    id TEXT PRIMARY KEY, product TEXT NOT NULL, amount INTEGER NOT NULL,
                    payment_id TEXT UNIQUE, status TEXT NOT NULL, secret_hash TEXT UNIQUE NOT NULL
                );
                CREATE TABLE IF NOT EXISTS activations (
                    order_id TEXT NOT NULL REFERENCES orders(id), device TEXT NOT NULL,
                    PRIMARY KEY(order_id, device)
                );
            """)

    def connect(self):
        db = sqlite3.connect(self.path, timeout=10, isolation_level=None)
        db.row_factory = sqlite3.Row
        db.execute("PRAGMA foreign_keys=ON")
        return db

    def create_order(self, product=PRODUCT) -> tuple[str, str, int]:
        # Price is server-owned. A client cannot pass an amount or an early-bird flag.
        if product not in self.products: raise ValueError("unknown product")
        order = "ss_" + uuid.uuid4().hex
        key = secrets.token_urlsafe(32)
        with closing(self.connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            # Early slots require an explicit reservation design before production.
            # Keep a fixed price until that design exists; do not oversell a limited offer.
            price = self.products[product]["amount"]
            db.execute("INSERT INTO orders VALUES(?,?,?,NULL,'NEW',?)",
                       (order, product, price, hashlib.sha256(key.encode()).hexdigest()))
            db.commit()
        return order, key, price

    def bind_payment(self, order: str, payment: str):
        """Internal operation after a verified Init response; never a public API."""
        if not payment or not payment.isascii() or not payment.isdigit():
            raise ValueError("invalid payment ID")
        with closing(self.connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT * FROM orders WHERE id=?", (order,)).fetchone()
            if not row or row["payment_id"] not in (None, payment):
                raise ValueError("payment binding mismatch")
            db.execute("UPDATE orders SET payment_id=? WHERE id=?", (payment, order))
            db.commit()

    def notification(self, payload: dict, get_state) -> str:
        """get_state must be an authenticated real GetState adapter outside tests."""
        supplied = payload.get("Token")
        if payload.get("TerminalKey") != self.terminal or not isinstance(supplied, str) or not hmac.compare_digest(supplied, bank_token(payload, self.password)):
            raise ValueError("untrusted notification")
        failed = payload.get("Status") in {"REJECTED", "DEADLINE_EXPIRED", "CANCELED"}
        if not failed and (payload.get("Success") is not True or str(payload.get("ErrorCode")) != "0"):
            raise ValueError("unsuccessful notification")
        order, payment = payload.get("OrderId"), str(payload.get("PaymentId", ""))
        with closing(self.connect()) as db:
            row = db.execute("SELECT * FROM orders WHERE id=?", (order,)).fetchone()
        if not row or not row["payment_id"] or payment != row["payment_id"]:
            raise ValueError("unknown or mismatched payment")
        if not failed and payload.get("Status") not in {"CONFIRMED", "REFUNDED", "PARTIAL_REFUNDED", "REVERSED", "PARTIAL_REVERSED"}:
            return "OK"  # AUTHORIZED is not paid.
        state = get_state(payment)
        if state.get("Success") is not True or str(state.get("PaymentId")) != payment or state.get("TerminalKey") != self.terminal or str(state.get("ErrorCode")) != "0":
            raise ValueError("bank state could not be verified")
        status = state.get("Status")
        if status == "CONFIRMED":
            if payload.get("Status") != "CONFIRMED" or amount(payload.get("Amount")) != row["amount"] or amount(state.get("Amount")) != row["amount"]:
                raise ValueError("paid amount/status mismatch")
        elif status in {"REJECTED", "DEADLINE_EXPIRED", "CANCELED"}:
            if status != payload.get("Status"):
                raise ValueError("failed status mismatch")
        elif status not in {"REFUNDED", "PARTIAL_REFUNDED", "REVERSED", "PARTIAL_REVERSED"}:
            raise ValueError("bank state not final")
        with closing(self.connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            current = db.execute("SELECT status FROM orders WHERE id=?", (order,)).fetchone()[0]
            # Refund is terminal locally: a late CONFIRMED cannot resurrect a license.
            if current == "NEW" or status in {"REFUNDED", "PARTIAL_REFUNDED", "REVERSED", "PARTIAL_REVERSED"}:
                db.execute("UPDATE orders SET status=? WHERE id=?", (status, order))
            db.commit()
        return "OK"

    def activate(self, key: str, device: str, *, now: int | None = None) -> str:
        if not isinstance(device, str) or len(device) != 64 or any(c not in "0123456789abcdef" for c in device):
            raise ValueError("device must be SHA-256 of an installation-random ID")
        now = int(time.time()) if now is None else now
        with closing(self.connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT * FROM orders WHERE secret_hash=?", (hashlib.sha256(key.encode()).hexdigest(),)).fetchone()
            if not row or row["status"] != "CONFIRMED":
                raise ValueError("license not paid or revoked")
            devices = {r[0] for r in db.execute("SELECT device FROM activations WHERE order_id=?", (row["id"],))}
            if device not in devices and len(devices) >= self.products[row["product"]]["devices"]:
                raise ValueError("device limit reached")
            db.execute("INSERT OR IGNORE INTO activations VALUES(?,?)", (row["id"], device))
            db.commit()
        payload = {"schema": 1, "product": row["product"], "license": row["id"], "device": device,
                   "issued_at": now, "valid_until": now + 30 * 86400, "key_id": self.key_id,
                   "environment": "DEMO" if self.sandbox else "LIVE"}
        encoded = b64(json.dumps(payload, sort_keys=True, separators=(",", ":")).encode())
        message = "ss1." + encoded
        return message + "." + b64(self.signing_key.sign(message.encode()))

    def release_device(self, key: str, device: str):
        with closing(self.connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT id FROM orders WHERE secret_hash=?", (hashlib.sha256(key.encode()).hexdigest(),)).fetchone()
            if not row:
                raise ValueError("unknown license")
            db.execute("DELETE FROM activations WHERE order_id=? AND device=?", (row[0], device))
            db.commit()
