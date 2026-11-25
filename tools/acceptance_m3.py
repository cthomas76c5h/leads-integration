"""Milestone 3 acceptance, across repositories: intake's lead builder, run as the center will run it
(a JSON Lines worker process), publishes draft-3 leads under canonical firm ids to a running
delivery API, twice each, as an outbox that lost its answers would.

    python tools/acceptance_m3.py --api http://localhost:5097

Needs ../intake-policy-lab (read only: its conformance requests and its worker) and Node 22.18+.
Against a fresh delivery database it:
  1. provisions every canonical firm in intake's conformance/firms.json, each with a webhook
     destination at delivery's fake receiver;
  2. starts intake's worker and sends it every conformance request; checks each answer equals the
     suite's recorded one (the worker is intake's reference);
  3. publishes each lead (attempt 1), then again with a later time (attempt 2);
  4. waits for delivery's worker, and checks: every lead recorded once, every retry a duplicate
     and never a conflict, every lead delivered once to its own firm.
Exit status 0 only if all of it holds. Standard library only.
"""

import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
INTAKE = ROOT.parent / "intake-policy-lab"


def call(api, method, path, body=None, headers=None):
    data = None if body is None else (body if isinstance(body, bytes) else json.dumps(body).encode())
    req = urllib.request.Request(api + path, data=data, method=method, headers={"Content-Type": "application/json", **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            return res.status, json.loads(res.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"null")


def now_iso():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--api", default="http://localhost:5080")
    ap.add_argument("--node", default="node")
    args = ap.parse_args()
    api = args.api.rstrip("/")
    problems = []

    # 1. Canonical firms, each with its own destination.
    firms = json.loads((INTAKE / "conformance" / "firms.json").read_text(encoding="utf-8"))["firms"]
    local = {}
    for fixture, canonical in firms.items():
        status, firm = call(api, "PUT", f"/api/provisioning/firms/{canonical}", {"name": f"Acceptance firm {canonical}"})
        if status not in (200, 201):
            print(f"provisioning {canonical}: {status} {firm}")
            return 1
        local[canonical] = firm["firmId"]
        call(api, "POST", f"/api/firms/{firm['firmId']}/connections", {"kind": "webhook", "name": f"webhook {canonical}"})
    print(f"provisioned {len(local)} firms under canonical ids")

    # 2. intake's worker builds every request.
    rows = [json.loads(l) for l in (INTAKE / "conformance" / "intake-lead-draft-3.jsonl").read_text(encoding="utf-8").splitlines() if l]
    worker = subprocess.run([args.node, str(INTAKE / "src" / "lead-worker.ts")], input="\n".join(json.dumps(r["request"]) for r in rows) + "\n",
                            capture_output=True, text=True, encoding="utf-8", timeout=120)
    answers = [json.loads(l) for l in worker.stdout.splitlines() if l]
    if worker.returncode != 0 or len(answers) != len(rows):
        print(f"worker: exit {worker.returncode}, {len(answers)} answers for {len(rows)} requests\n{worker.stderr}")
        return 1
    for r, a in zip(rows, answers):
        if a != r["answer"]:
            problems.append(f"{r['name']}: the worker's answer differs from the suite's")
    print(f"worker built {len(answers)} leads, {sum(1 for a in answers if a['ok'])} ok")

    # 3. Publish each lead twice: attempt 1, then attempt 2 later, as an outbox that lost its answer.
    events = {}
    for r, a in zip(rows, answers):
        if not a["ok"]:
            continue
        body = json.dumps(a["lead"]).encode()
        for attempt in (1, 2):
            status, res = call(api, "POST", "/api/ingest/intake-lead/draft-3", body,
                               {"Lead-Published-At": now_iso(), "Lead-Publication-Attempt": str(attempt)})
            expected = 201 if attempt == 1 else 200
            if status != expected:
                problems.append(f"{r['name']} attempt {attempt}: {status} {res}")
                continue
            if attempt == 1:
                events[r["name"]] = (res["event"]["id"], local[a["lead"]["firm"]["id"]])
                if res["event"]["firmId"] != local[a["lead"]["firm"]["id"]]:
                    problems.append(f"{r['name']}: recorded for the wrong firm")
            elif not res["duplicate"] or res["event"]["id"] != events[r["name"]][0]:
                problems.append(f"{r['name']}: the retry was not the lead already recorded")
    print(f"published {len(events)} leads, each twice")

    # 4. Delivery's worker sends them; each is delivered once, to its own firm.
    deadline = time.time() + 120
    pending = dict(events)
    while pending and time.time() < deadline:
        for name, (event_id, _) in list(pending.items()):
            _, detail = call(api, "GET", f"/api/leads/{event_id}")
            statuses = [d["status"] for d in detail["deliveries"]]
            if statuses and all(s == "delivered" for s in statuses):
                if len(statuses) != 1:
                    problems.append(f"{name}: {len(statuses)} deliveries, expected 1")
                del pending[name]
        time.sleep(1)
    for name in pending:
        problems.append(f"{name}: not delivered within two minutes")
    _, received = call(api, "GET", "/api/received?limit=10000")
    if len(received) != len(events):
        problems.append(f"the receiver saw {len(received)} requests for {len(events)} leads")

    for p in problems:
        print("PROBLEM", p)
    print(f"{len(events) - len(pending)} of {len(events)} leads delivered once; {len(problems)} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
