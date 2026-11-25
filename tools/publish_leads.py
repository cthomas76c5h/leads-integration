"""Publish intake-lead/draft-2 leads to delivery: the file-based seam of milestone 1.

    python tools/publish_leads.py ../call-center-lab/out/leads
    python tools/publish_leads.py --alias injury-firm=firm_ab12 --alias general-firm=firm_ab12 ../call-center-lab/out/leads
    python tools/publish_leads.py --api http://localhost:5080 lead.json other.json

Each file is posted, unchanged, to POST /api/ingest/intake-lead/draft-2 (docs/api.md), in name
order. The files are only read: the producing repository (the call center's simulator writes
out/leads/) is never written. --alias SENDER=FIRM maps a sender's firm id to a firm here first
(PUT /api/firm-aliases/SENDER), until firm ids are shared (docs/contracts/intake-lead/README.md).

Prints one line per lead. Exit status: 0 when every lead was recorded or was already recorded;
1 when any was refused (409, 422) or not recorded (503, no answer), so a script can stop.
Standard library only.
"""

import argparse
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROUTE = "/api/ingest/intake-lead/draft-2"


def call(method: str, url: str, body: bytes) -> tuple[int, dict]:
    req = urllib.request.Request(url, data=body, method=method, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            return res.status, json.loads(res.read() or b"{}")
    except urllib.error.HTTPError as e:
        text = e.read()
        try:
            return e.code, json.loads(text or b"{}")
        except json.JSONDecodeError:
            return e.code, {"error": text.decode("utf-8", "replace")[:300]}


def files_from(paths: list[str]) -> list[Path]:
    found: list[Path] = []
    for p in map(Path, paths):
        found.extend(sorted(p.glob("*.json")) if p.is_dir() else [p])
    return found


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+", help="lead files, or folders of *.json leads")
    ap.add_argument("--api", default="http://localhost:5080", help="the delivery API (default %(default)s)")
    ap.add_argument("--alias", action="append", default=[], metavar="SENDER=FIRM",
                    help="map a sender's firm id to a firm here before publishing (repeatable)")
    args = ap.parse_args()
    api = args.api.rstrip("/")

    for pair in args.alias:
        sender, _, firm = pair.partition("=")
        if not sender or not firm:
            print(f"--alias {pair!r}: expected SENDER=FIRM", file=sys.stderr)
            return 2
        status, body = call("PUT", f"{api}/api/firm-aliases/{sender}", json.dumps({"firmId": firm}).encode())
        if status != 200:
            print(f"alias {sender} -> {firm}: {status} {body.get('error', '')}", file=sys.stderr)
            return 1
        print(f"alias    {sender} -> {firm}")

    files = files_from(args.paths)
    if not files:
        print("no lead files found", file=sys.stderr)
        return 2

    failed = 0
    for f in files:
        try:
            status, body = call("POST", api + ROUTE, f.read_bytes())
        except urllib.error.URLError as e:
            print(f"{f.name}: no answer from {api} ({e.reason}): not recorded, send it again")
            failed += 1
            continue
        if status in (200, 201):
            event = body["event"]["id"]
            deliveries = body.get("deliveries", [])
            planned = sum(1 for d in deliveries if d.get("status") == "pending")
            word = "already  " if body.get("duplicate") else "recorded "
            print(f"{word}{f.name}: lead {body.get('leadId')} -> {event}, {len(deliveries)} destination(s), {planned} to send")
        else:
            label = {409: "conflict", 422: "refused ", 503: "not recorded, send it again"}.get(status, f"HTTP {status}")
            print(f"{label} {f.name}: {body.get('error', '')}")
            failed += 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
