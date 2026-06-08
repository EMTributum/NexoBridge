#!/usr/bin/env python3
"""
Standalone diagnostic sender for NexoBridge KSeF tests.

Put any .epp file next to this script, then run:

    python test_ksef_epp.py --database-name Nexo_TEST --username "Tomasz Bloch"

The script sends the EPP to NexoBridge using the same JSON contract as the
classifier and adds one synthetic KSeF number in invoicesMetadata.
"""

from __future__ import annotations

import argparse
import base64
import csv
import getpass
import json
import os
import re
import sys
import urllib.error
import urllib.request
import uuid
from datetime import date
from pathlib import Path


DEFAULT_BRIDGE_URL = "http://192.168.1.10:5000"
DEFAULT_KSEF_NUMBER = "12"

EMPTY_KSEF_MARKERS = {"", "BFK", "DI", "OFF", "BRAK", "NONE", "NULL", "NIE DOTYCZY"}
DOCUMENT_TYPES = {
    "FS",
    "FZ",
    "FV",
    "PA",
    "RACH",
    "KFS",
    "KFZ",
    "KOR",
}


def load_dotenv(path: Path) -> None:
    if not path.exists():
        return

    for raw_line in path.read_text(encoding="utf-8", errors="ignore").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue

        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")

        if key and key not in os.environ:
            os.environ[key] = value


def load_known_env_files(script_dir: Path) -> None:
    candidates = [
        Path.cwd() / ".env",
        script_dir / ".env",
        script_dir / "NexoBridge" / ".env",
        script_dir.parent / "KlasyfikatorFaktur" / ".env",
        script_dir.parent / "NexoBridge" / "NexoBridge" / ".env",
    ]

    for path in candidates:
        load_dotenv(path)


def first_env(*names: str) -> str | None:
    for name in names:
        value = os.getenv(name)
        if value:
            return value
    return None


def prompt_if_missing(value: str | None, label: str, secret: bool = False) -> str:
    if value:
        return value

    if secret:
        value = getpass.getpass(f"{label}: ").strip()
    else:
        value = input(f"{label}: ").strip()

    if not value:
        raise SystemExit(f"Missing required value: {label}")

    return value


def find_epp_file(script_dir: Path, explicit_path: str | None) -> Path:
    if explicit_path:
        path = Path(explicit_path).expanduser().resolve()
        if not path.exists():
            raise SystemExit(f"EPP file does not exist: {path}")
        return path

    files = sorted(list(script_dir.glob("*.epp")) + list(script_dir.glob("*.EPP")))
    if not files:
        raise SystemExit(
            f"No .epp file found next to script: {script_dir}. "
            "Pass --epp C:\\path\\file.epp or copy an EPP file next to this script."
        )

    if len(files) > 1:
        print(f"Found {len(files)} EPP files next to script, using first one: {files[0].name}")

    return files[0]


def read_text_loose(path: Path) -> str:
    raw = path.read_bytes()
    for encoding in ("utf-8-sig", "cp1250", "iso-8859-2", "latin-1"):
        try:
            return raw.decode(encoding)
        except UnicodeDecodeError:
            continue

    return raw.decode("latin-1", errors="replace")


def normalize_digits(value: str | None) -> str:
    return "".join(ch for ch in str(value or "") if ch.isdigit())


def looks_like_invoice_number(value: str | None) -> bool:
    if not value:
        return False

    text = str(value).strip()
    if len(text) > 80:
        return False

    digits = normalize_digits(text)
    if len(digits) == 10 and text.replace("PL", "").replace("-", "").replace(" ", "").isdigit():
        return False

    has_digit = any(ch.isdigit() for ch in text)
    has_invoice_signal = "/" in text or "\\" in text or re.search(r"\b(FV|FS|FZ|VAT|FA|KOR)\b", text, re.I)
    return has_digit and has_invoice_signal


def pick_invoice_number(row: list[str]) -> str | None:
    for idx in (4, 6, 3, 5):
        if idx < len(row) and looks_like_invoice_number(row[idx]):
            return row[idx].strip()

    for cell in row:
        if looks_like_invoice_number(cell):
            return cell.strip()

    return None


def pick_vendor_nip(row: list[str]) -> str | None:
    candidates = []
    for cell in row:
        digits = normalize_digits(cell)
        if len(digits) == 10:
            candidates.append(digits)

    return candidates[-1] if candidates else None


def parse_first_invoice_from_epp(path: Path) -> tuple[str | None, str | None]:
    text = read_text_loose(path)

    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("[") or "," not in line:
            continue

        try:
            row = next(csv.reader([line]))
        except csv.Error:
            continue

        if not row:
            continue

        doc_type = row[0].strip().strip('"').upper()
        if doc_type not in DOCUMENT_TYPES:
            continue

        invoice_number = pick_invoice_number(row)
        vendor_nip = pick_vendor_nip(row)
        if invoice_number and vendor_nip:
            return invoice_number, vendor_nip

    return None, None


def infer_period_from_epp(path: Path) -> tuple[int, int]:
    text = read_text_loose(path)

    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("[") or "," not in line:
            continue

        try:
            row = next(csv.reader([line]))
        except csv.Error:
            continue

        if not row:
            continue

        doc_type = row[0].strip().strip('"').upper()
        if doc_type not in DOCUMENT_TYPES:
            continue

        for cell in row:
            match = re.fullmatch(r"(20\d{2})(0[1-9]|1[0-2])\d{2}000000", str(cell).strip())
            if match:
                return int(match.group(2)), int(match.group(1))

    match = re.search(r"(20\d{2})(0[1-9]|1[0-2])\d{2}000000", text)
    if match:
        return int(match.group(2)), int(match.group(1))

    today = date.today()
    return today.month, today.year


def normalize_ksef_number(value: str | None) -> str:
    cleaned = str(value or "").strip()
    if cleaned.upper() in EMPTY_KSEF_MARKERS:
        raise SystemExit("KSeF number is empty or technical marker. Pass a real test value, e.g. --ksef-number 12")
    return cleaned


def make_payload(args: argparse.Namespace, epp_path: Path, invoice_number: str, vendor_nip: str) -> dict:
    epp_b64 = base64.b64encode(epp_path.read_bytes()).decode("ascii")
    month = args.month
    year = args.year
    if month is None or year is None:
        inferred_month, inferred_year = infer_period_from_epp(epp_path)
        month = month or inferred_month
        year = year or inferred_year

    return {
        "jobId": args.job_id or f"ksef-test-{uuid.uuid4().hex[:8]}",
        "username": args.username,
        "password": args.password,
        "databaseName": args.database_name,
        "billingMonth": month,
        "billingYear": year,
        "importInvoices": True,
        "calculateVat": False,
        "calculatePit": False,
        "calculateAmortization": False,
        "files": [
            {
                "fileName": epp_path.name,
                "content": epp_b64,
            }
        ],
        "attachments": [],
        "invoicesMetadata": [
            {
                "invoiceNumber": invoice_number,
                "vendorNip": vendor_nip,
                "ksefNumber": normalize_ksef_number(args.ksef_number),
                "pdfFileName": None,
            }
        ],
    }


def safe_payload_for_print(payload: dict) -> dict:
    clone = json.loads(json.dumps(payload))
    clone["password"] = "***"
    for item in clone.get("files", []):
        content = item.get("content") or ""
        item["content"] = f"<base64 {len(content)} chars>"
    return clone


def post_json(url: str, payload: dict, timeout: int) -> tuple[int, str]:
    body = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", errors="replace")
    except urllib.error.URLError as exc:
        raise SystemExit(f"Cannot reach NexoBridge: {exc}") from exc


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Send local EPP to NexoBridge with one synthetic KSeF number.")
    parser.add_argument("--epp", help="Path to EPP file. Default: first *.epp next to this script.")
    parser.add_argument("--bridge-url", default=first_env("NEXO_BRIDGE_URL") or DEFAULT_BRIDGE_URL)
    parser.add_argument("--username", default=first_env("NEXO_USERNAME", "NEXO_USER", "NEXO_LOGIN"))
    parser.add_argument("--password", default=first_env("NEXO_PASSWORD", "NEXO_PASS"))
    parser.add_argument("--database-name", default=first_env("NEXO_DB_NAME", "NEXO_DATABASE_NAME"))
    parser.add_argument("--invoice-number", help="Invoice number for invoicesMetadata. If omitted, guessed from EPP.")
    parser.add_argument("--vendor-nip", help="Vendor/contractor NIP for invoicesMetadata. If omitted, guessed from EPP.")
    parser.add_argument("--ksef-number", default=DEFAULT_KSEF_NUMBER)
    parser.add_argument("--month", type=int, help="Billing month. Default: inferred from EPP or current month.")
    parser.add_argument("--year", type=int, help="Billing year. Default: inferred from EPP or current year.")
    parser.add_argument("--job-id", help="Optional fixed jobId.")
    parser.add_argument("--timeout", type=int, default=30)
    parser.add_argument("--dry-run", action="store_true", help="Print payload and do not send it.")
    return parser.parse_args()


def main() -> int:
    script_dir = Path(__file__).resolve().parent
    load_known_env_files(script_dir)

    args = parse_args()
    args.username = prompt_if_missing(args.username, "Nexo username")
    args.password = prompt_if_missing(args.password, "Nexo password", secret=True)
    args.database_name = prompt_if_missing(args.database_name, "Nexo database name")

    epp_path = find_epp_file(script_dir, args.epp)
    guessed_invoice, guessed_nip = parse_first_invoice_from_epp(epp_path)

    invoice_number = args.invoice_number or guessed_invoice
    vendor_nip = args.vendor_nip or guessed_nip

    if not invoice_number or not vendor_nip:
        raise SystemExit(
            "Could not infer invoice number or vendor NIP from EPP. "
            "Pass --invoice-number and --vendor-nip explicitly."
        )

    payload = make_payload(args, epp_path, invoice_number, vendor_nip)
    endpoint = args.bridge_url.rstrip("/") + "/api/jobs/import"

    print(f"EPP: {epp_path}")
    print(f"NexoBridge: {endpoint}")
    print(f"Database: {args.database_name}")
    print(f"Invoice metadata: invoiceNumber={invoice_number}, vendorNip={vendor_nip}, ksefNumber={args.ksef_number}")

    if args.dry_run:
        print(json.dumps(safe_payload_for_print(payload), indent=2, ensure_ascii=False))
        return 0

    status, response_text = post_json(endpoint, payload, args.timeout)
    print(f"HTTP {status}")
    print(response_text)

    return 0 if status in (200, 202) else 1


if __name__ == "__main__":
    sys.exit(main())
