"""Run the contract scenarios (docs/contracts/scenarios/) against the lab.

Each scenario gets a fresh API process with its own temporary database and test
control on (docs/api.md, "Test control"). The runner sets the clock, builds the
scenario's firms and destinations through the public routes, plays the `when`
timeline in time order, and checks every `then` expectation from what the API
reports.

    python tools/run_scenarios.py                   # every scenario
    python tools/run_scenarios.py auth-expired      # by name (several allowed)
    python tools/run_scenarios.py --model lab       # only the ones the lab should pass
    python tools/run_scenarios.py --report out.json # results as JSON too

A scenario passes only when every assertion is supported and holds:
pass = all assertions supported AND all satisfied. Each scenario's line shows
assertions passed/failed/unsupported (unsupported includes those never reached
because an earlier step was unsupported), and the run ends with the totals.

A scenario marked `lab` must pass; one marked `proposed` is expected to fail or
be unsupported until its rule is built. The exit code is 1 if any `lab`
scenario does not pass. A `proposed` scenario that passes is reported, so it can
be promoted.

The scenario format is neutral; `LabAdapter` is the one place lab-specific
translation lives (a category becomes a reference lead's call type, lab statuses
become the model's states). Another implementation would get its own adapter.
Anything a scenario needs that the lab cannot do is reported as unsupported,
never skipped silently.
"""

import argparse
import glob
import json
import os
import re
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCENARIOS = ROOT / "docs" / "contracts" / "scenarios"
BUILD = ROOT / ".scenariobuild"

OFFSET = re.compile(r"^\+(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$")


def offset_ms(text: str) -> int:
    m = OFFSET.match(text)
    if not m:
        raise ValueError(f"bad offset {text}")
    d, h, mi, s = (int(x or 0) for x in m.groups())
    return ((d * 24 + h) * 60 + mi) * 60_000 + s * 1000


class Unsupported(Exception):
    """The lab cannot do or report something the scenario needs."""


# ----- The API ---------------------------------------------------------------


class Api:
    def __init__(self, base: str):
        self.base = base

    def call(self, method: str, path: str, body=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(self.base + path, data=data, method=method)
        if data is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=120) as res:
                text = res.read().decode("utf-8")
                return res.status, (json.loads(text) if text else None)
        except urllib.error.HTTPError as e:
            text = e.read().decode("utf-8")
            try:
                return e.code, json.loads(text)
            except ValueError:
                return e.code, {"error": text}

    def page(self, method: str, url_path: str):
        """A page meant for a person (HTML), as a browser would ask for it: the status only."""
        req = urllib.request.Request(self.base + url_path, data=b"" if method == "POST" else None, method=method)
        try:
            with urllib.request.urlopen(req, timeout=60) as res:
                return res.status
        except urllib.error.HTTPError as e:
            return e.code

    def ok(self, method: str, path: str, body=None):
        status, payload = self.call(method, path, body)
        if status >= 300:
            raise RuntimeError(f"{method} {path} -> {status}: {payload}")
        return payload


# ----- The lab adapter -------------------------------------------------------

# Lab delivery status -> the model's delivery state (docs/lead-delivery/README.md §4).
OUTCOMES = {"expired": "expired", "none": "no-destination", "pending": "in-progress",
            "refused": "needs-attention", "skipped": "not-sent", "delivered": "accepted", "held": "held"}
FAILURE_CLASSES = {"transient", "lead", "configuration", "connection", "ours"}


class LabAdapter:
    """Drives the lab through its public routes and test control."""

    def __init__(self, api: Api, scenario: dict, t0: int):
        self.api = api
        self.scenario = scenario
        self.t0 = t0
        self.firms = {}         # scenario firm id -> lab firm id
        self.dests = {}         # scenario destination id -> lab connection id
        self.dest_kind = {}
        self.leads = {}         # scenario lead id -> lab event id
        self.sent = {}  # scenario lead id -> (firm id, intake body, conversation): what the channel keeps
        self.told = {}  # what intake answered the channel when it repeated a lead's event
        self.pending_mapping = {}
        self.refs = None

    # -- set up --

    def setup(self):
        self.api.ok("POST", "/api/test/clock", {"at": self.t0})
        given = self.scenario["given"]
        for f in given["firms"]:
            self.firms[f["id"]] = self.api.ok("POST", "/api/firms", {"name": f["name"]})["id"]
        for d in given["destinations"]:
            firm = self.firms[d["firm"]]
            url = f"{self.api.base}/receiver/crm/{d['kind']}?dest={d['id']}"
            c = self.api.ok("POST", f"/api/firms/{firm}/connections", {"kind": d["kind"], "name": d["id"], "deliveryUrl": url})
            self.dests[d["id"]] = c["id"]
            self.dest_kind[d["id"]] = d["kind"]
            # What a working setup of these kinds has; fictional, like every lead here.
            settings = {"email": {"recipients": "intake@example.com"}, "sms": {"to": "+15555550199"}}.get(d["kind"], {})
            if d.get("role") == "fallback":
                settings["role"] = "fallback"
            routing = d.get("routing", {})
            if routing.get("chatLeads") is False:
                settings["sendWebchats"] = "false"
            if routing.get("categories"):
                settings["routeCategories"] = ",".join(routing["categories"])
            if routing.get("practiceAreas"):
                settings["routePracticeAreas"] = ",".join(routing["practiceAreas"])
            if d.get("duplicates"):
                settings["duplicates"] = d["duplicates"]
            body = {}
            if settings:
                body["settings"] = settings
            if "mapping" in d:
                body["mapping"] = d["mapping"]
            if body:
                self.api.ok("PUT", f"/api/test/connections/{c['id']}", body)
            state = d.get("state", "live")
            if state == "paused":
                self.api.ok("PATCH", f"/api/connections/{c['id']}", {"enabled": False})
            elif state != "live":
                raise Unsupported(f"starting a destination as {state}")

    def _refs(self):
        if self.refs is None:
            self.refs = self.api.ok("GET", "/api/reference-leads")
        return self.refs

    def _reference(self, category, practice_area):
        for r in self._refs():
            e = r["expect"]
            if e["category"] == category and (practice_area is None or e.get("practiceArea") == practice_area):
                return r
        if practice_area is not None:
            return self._reference(category, None)
        raise Unsupported(f"no reference lead for category {category}")

    # -- steps --

    def step(self, step: dict):
        if "lead" in step:
            self._lead(step["lead"])
        elif "receiver" in step:
            self._receiver(step["receiver"])
        elif "action" in step:
            self._action(step["action"])
        elif "fault" in step:
            self._fault(step["fault"])
        elif "cycleCloses" in step:
            self.api.ok("POST", f"/api/test/firms/{self.firms[step['cycleCloses']['firm']]}/close-cycle")
        else:
            kind = next(k for k in step if k != "at")
            detail = step[kind].get("kind", "") if isinstance(step[kind], dict) else ""
            raise Unsupported(f"step '{kind}' {detail}".strip())

    def _lead(self, lead):
        if lead.get("test"):
            only = lead.get("only")
            if not only:
                raise Unsupported("a test lead without a destination under test")
            d = self.api.ok("POST", f"/api/connections/{self.dests[only]}/test")
            self.leads[lead["id"]] = d["eventId"]
            return
        ref = self._reference(lead["category"], lead.get("practiceArea"))
        chat = lead.get("channel") == "chat"
        if lead.get("hangUp"):
            body = {"variant": "sparse", "callType": ref["lead"]["callType"], "webchat": chat}
        elif chat:
            body = {"variant": "full", "callType": ref["lead"]["callType"], "webchat": True}
        else:
            body = {"referenceLead": ref["id"]}
        if lead.get("conversation"):
            body["conversationId"] = lead["conversation"]
        self.sent[lead["id"]] = (self.firms[lead["firm"]], body, lead.get("conversation"))
        self._intake(lead["id"])

    def _intake(self, lead_id):
        """The channel sends a lead's event; intake records it, or says it did not, and the channel keeps it."""
        firm_id, body, _ = self.sent[lead_id]
        status, res = self.api.call("POST", f"/api/firms/{firm_id}/leads", body)
        if status == 503:
            self.told[lead_id] = "not-recorded"
            return
        if status >= 300:
            raise RuntimeError(f"intake -> {status}: {res}")
        self.told[lead_id] = "recorded"
        self.leads[lead_id] = res["event"]["id"]

    def _fault(self, f):
        if f["kind"] == "intake-event-repeated" and f["lead"] not in self.leads:
            # Intake said it was not recorded: the channel kept its copy and sends it again.
            self._intake(f["lead"])
            return
        body = {"kind": f["kind"]}
        if "lead" in f:
            body["eventId"] = self.leads[f["lead"]]
        if "destination" in f:
            body["connectionId"] = self.dests[f["destination"]]
        status, payload = self.api.call("POST", "/api/test/faults", body)
        if status == 422:
            raise Unsupported(f"step 'fault' {f['kind']}")
        if status >= 300:
            raise RuntimeError(f"fault -> {status}: {payload}")
        if f["kind"] == "intake-event-repeated":
            self.told[f["lead"]] = payload.get("told")

    def _receiver(self, r):
        answers = dict(r["answers"])
        status, payload = self.api.call("PUT", f"/api/test/receivers/{r['destination']}", answers)
        if status == 422:
            raise Unsupported(f"receiver answer {answers}: {payload.get('error')}")
        if status >= 300:
            raise RuntimeError(f"receiver -> {status}: {payload}")

    def _newest(self, lead, dest):
        if lead not in self.leads:
            return []  # never recorded: it has no deliveries
        ds = [d for d in self._lead_detail(lead)["deliveries"] if d["connectionId"] == self.dests[dest]]
        return ds  # newest first

    def _action(self, a):
        do = a["do"]
        dest = a.get("destination")
        cid = self.dests.get(dest) if dest else None
        if do == "resend":
            ds = self._newest(a["lead"], dest)
            if not ds:
                raise RuntimeError(f"resend: no delivery of {a['lead']} to {dest}")
            who = {"by": f"scenario {a['by']}", "role": a["by"], "why": a.get("why")}
            status, payload = self.api.call("POST", f"/api/deliveries/{ds[0]['id']}/replay", who)
            if a.get("refused"):
                if status != 403:
                    raise RuntimeError(f"resend by {a['by']} was not refused: {status}")
            elif status >= 300:
                raise RuntimeError(f"resend -> {status}: {payload}")
        elif do in ("reactivate", "reconnect"):
            # The lab has no sign-in flow: the firm reconnecting is a reactivation once its receiver answers again.
            self.api.ok("PATCH", f"/api/connections/{cid}", {"reactivate": True})
        elif do == "pause":
            self.api.ok("PATCH", f"/api/connections/{cid}", {"enabled": False})
        elif do == "resume":
            self.api.ok("PATCH", f"/api/connections/{cid}", {"enabled": True})
        elif do == "acknowledge":
            # A person at the firm follows the link in the email or SMS they received, then confirms.
            if a.get("via", "link") != "link":
                raise Unsupported(f"acknowledge via {a['via']}")
            event_id = self.leads[a["lead"]]
            marker = f"/ack/{event_id}?"
            links = []
            for r in self.api.ok("GET", "/api/received") or []:
                try:
                    url = json.loads(r.get("body") or "{}").get("ackUrl")
                except ValueError:
                    url = None
                if url and marker in url:
                    links.append(url)
            if not links:
                raise RuntimeError(f"acknowledge: no message for {a['lead']} carried an acknowledgement link")
            path = links[0][links[0].index("/ack/"):]
            if self.api.page("GET", path) != 200 or self.api.page("POST", path) != 200:
                raise RuntimeError(f"acknowledge: the link for {a['lead']} was refused")
        elif do == "send-missed":
            self.api.ok("POST", f"/api/connections/{cid}/send-missed")
        elif do == "change-mapping":
            # The lab's real path is a setup revision; the scenario folds test and confirm into approve.
            self.pending_mapping[dest] = a["mapping"]
        elif do == "correct-classification":
            self.api.ok("POST", f"/api/leads/{self.leads[a['lead']]}/classification",
                        {"category": a["category"], "practiceArea": a.get("practiceArea"), "source": a["by"], "by": f"scenario {a['by']}"})
        elif do == "change-routing":
            self.api.ok("POST", f"/api/connections/{cid}/routing",
                        {"categories": a["routing"].get("categories"), "practiceAreas": a["routing"].get("practiceAreas"), "by": a["by"]})
        elif do == "approve" and dest not in self.pending_mapping:
            self.api.ok("POST", f"/api/connections/{cid}/routing/approve", {"by": a["by"], "applyToWaiting": bool(a.get("applyToWaiting"))})
        elif do == "approve" and dest in self.pending_mapping and not a.get("applyToWaiting"):
            self.api.ok("PUT", f"/api/test/connections/{cid}", {"mapping": self.pending_mapping.pop(dest)})
        else:
            raise Unsupported(f"action '{do}'")

    # -- reading state --

    def _lead_detail(self, lead):
        if lead not in self.leads:
            raise RuntimeError(f"no lead {lead} recorded")
        return self.api.ok("GET", f"/api/leads/{self.leads[lead]}")

    def _connection(self, dest):
        for c in self.api.ok("GET", "/api/connections"):
            if c["id"] == self.dests[dest]:
                return c
        raise RuntimeError(f"no connection for {dest}")

    def _events(self, x, after, before, inclusive=True):
        """Domain events matching an expectation's name, lead, destination and firm, after `after` and up to
        `before` (a prohibited effect's interval excludes its end: "before +5m" means before it)."""
        rows = self.api.ok("GET", "/api/test/events")
        want = {"eventId": self.leads.get(x["lead"]) if "lead" in x else None,
                "connectionId": self.dests.get(x["destination"]) if "destination" in x else None,
                "firmId": self.firms.get(x["firm"]) if "firm" in x else None}
        return [r for r in rows if r["name"] == x["name"]
                and all(v is None or r[k] == v for k, v in want.items())
                and (after is None or r["at"] > after) and (r["at"] <= before if inclusive else r["at"] < before)]

    @staticmethod
    def _attempt_outcome(a):
        """How an attempt ended (RFC-008 §4.2): answered, not sent, unknown, or our fault."""
        outcome = a.get("outcome") or ""
        if outcome.startswith("unknown") or outcome == "started":
            return "unknown"
        if a.get("status") is not None:
            return "answered"
        if (a.get("error") or "").startswith("address refused"):
            return "not-sent"
        return "unknown" if "no answer" in (a.get("error") or "") else "not-sent"

    @staticmethod
    def _delivery_state(d):
        status, attempts = d["status"], d.get("attempts") or []
        if d.get("stage") == "held":
            return "held"
        if status == "pending":
            return "queued" if not attempts else "retrying"
        if status == "failed":
            return "gave-up" if (d.get("reason") or "").startswith("gave up") else "refused"
        return {"delivering": "sending", "delivered": "accepted", "skipped": "not-sent", "cancelled": "cancelled"}.get(status, status)

    def expect(self, e: dict):
        """(True, '') when it holds, (False, why) when not; Unsupported when the lab cannot say."""
        kind = next(k for k in e if k != "at")
        x = e[kind]
        if kind == "delivery":
            ds = self._newest(x["lead"], x["destination"])
            if "count" in x and len(ds) != x["count"]:
                return False, f"{len(ds)} deliveries, expected {x['count']}"
            if not ds:
                return False, f"no delivery of {x['lead']} to {x['destination']}"
            d = ds[0]
            problems = []
            state = self._delivery_state(d)
            if "state" in x and state != x["state"]:
                problems.append(f"state {state} (lab {d['status']}: {d.get('reason')}), expected {x['state']}")
            if "failureClass" in x:
                cls = (d.get("diagnosis") or {}).get("class")
                cls = cls if cls in FAILURE_CLASSES else None
                if cls != x["failureClass"]:
                    problems.append(f"failure class {cls}, expected {x['failureClass']}")
            if "revision" in x:
                got = (d.get("decision") or {}).get("revision")
                if got != x["revision"]:
                    problems.append(f"revision {got}, expected {x['revision']}")
            if "evidence" in x and d.get("evidence") != x["evidence"]:
                problems.append(f"evidence {d.get('evidence')}, expected {x['evidence']}")
            if "verifyOverdue" in x and bool(d.get("verifyOverdue")) != x["verifyOverdue"]:
                problems.append(f"verify overdue {d.get('verifyOverdue')}, expected {x['verifyOverdue']}")
            if "outcomes" in x:
                got = [self._attempt_outcome(a) for a in d.get("attempts") or []]
                if got != x["outcomes"]:
                    problems.append(f"outcomes {got}, expected {x['outcomes']}")
            if "attempts" in x and len(d.get("attempts") or []) != x["attempts"]:
                problems.append(f"{len(d.get('attempts') or [])} attempts, expected {x['attempts']}")
            if "late" in x:
                # Late: accepted after the lead's window to act closed (its hand-off's due time).
                h = self._lead_detail(x["lead"]).get("handOff")
                accepted = [a["at"] for a in d.get("attempts") or [] if a["outcome"] == "delivered"]
                if not h or not accepted:
                    raise Unsupported("delivery late, without a hand-off or an accepted attempt")
                late = accepted[0] > h["dueAt"]
                if late != x["late"]:
                    problems.append(f"late {late}, expected {x['late']}")
            if "reason" in x:
                words = x["reason"] if isinstance(x["reason"], str) else " ".join(x["reason"])
                if words.lower() not in (d.get("reason") or "").lower():
                    problems.append(f"reason {d.get('reason')!r} lacks {words!r}")
            return (not problems), "; ".join(problems)
        if kind == "event":
            n = len(self._events(x, None, self.t0 + offset_ms(e["at"])))
            if "count" in x:
                return n == x["count"], f"{n} {x['name']} events, expected {x['count']}"
            return n > 0, f"no {x['name']} event"
        if kind == "noEvent":
            after = self.t0 + offset_ms(x["after"]) if "after" in x else None
            before = self.t0 + offset_ms(x.get("before", e["at"]))
            n = len(self._events(x, after, before, inclusive=False))
            return n == 0, f"{n} {x['name']} events in the interval"
        if kind == "noAttempt":
            after = self.t0 + offset_ms(x["after"]) if "after" in x else None
            before = self.t0 + offset_ms(x.get("before", e["at"]))
            leads = [x["lead"]] if "lead" in x else list(self.leads)
            hits = [a for lead in leads for d in self._newest(lead, x["destination"]) for a in d.get("attempts") or []
                    if (after is None or a["at"] > after) and a["at"] < before]
            return not hits, f"{len(hits)} attempts in the interval"
        if kind == "noDelivery":
            ds = self._newest(x["lead"], x["destination"]) if x["lead"] in self.leads else []
            return (not ds), f"{len(ds)} deliveries exist"
        if kind == "destination":
            c = self._connection(x["id"])
            if c["status"] == "needs-reconnection":
                state = "needs-reconnection"
            elif not c["enabled"]:
                state = "paused"
            elif c["status"] == "disabled":
                state = "switched-off"
            else:
                state = "live"
            problems = []
            if "state" in x and state != x["state"]:
                problems.append(f"state {state} ({c.get('disabledReason')}), expected {x['state']}")
            if "failureStreak" in x and c["consecutiveFailures"] != x["failureStreak"]:
                problems.append(f"failure streak {c['consecutiveFailures']}, expected {x['failureStreak']}")
            return (not problems), "; ".join(problems)
        if kind == "handOff":
            if x.get("evidence") == "verified":
                raise Unsupported("hand-off evidence: verified")
            h = self._lead_detail(x["lead"]).get("handOff")
            if not h:
                return False, "no hand-off (a test lead?)"
            state = {"waiting": "pending", "expired": "expired"}.get(h["state"], "reached-by-fallback" if h["viaFallback"] else "reached")
            problems = []
            if state != x["state"]:
                problems.append(f"hand-off {state}, expected {x['state']}")
            if "by" in x and h.get("by") not in (x["by"], self.dests.get(x["by"])):
                problems.append(f"by {h.get('by')}, expected {x['by']}")
            if "evidence" in x and h.get("evidence") != x["evidence"]:
                problems.append(f"evidence {h.get('evidence')}, expected {x['evidence']}")
            if "escalated" in x and bool(h.get("escalated")) != x["escalated"]:
                problems.append(f"escalated {h.get('escalated')}, expected {x['escalated']}")
            return (not problems), "; ".join(problems)
        if kind == "capture":
            if x["lead"] not in self.leads:
                problems = [] if x["is"] == "not-recorded" else ["not recorded"]
                if "channelTold" in x and self.told.get(x["lead"]) != x["channelTold"]:
                    problems.append(f"the channel was told {self.told.get(x['lead'])}, expected {x['channelTold']}")
                firm_id, _, conv = self.sent.get(x["lead"], (None, None, None))
                if "count" in x:
                    if not conv:
                        raise Unsupported("capture count: the lead has no conversation id")
                    items = self.api.ok("GET", f"/api/leads?firmId={firm_id}&limit=500")["items"]
                    n = sum(1 for i in items if i.get("conversationId") == conv)
                    if n != x["count"]:
                        problems.append(f"{n} leads for conversation {conv}, expected {x['count']}")
                return (not problems), "; ".join(problems)
            detail = self._lead_detail(x["lead"])
            problems = []
            if x["is"] != "recorded":
                problems.append("recorded, expected not recorded")
            if "count" in x:
                conv = detail.get("conversationId")
                if conv is None:
                    raise Unsupported("capture count: the lead has no conversation id")
                items = self.api.ok("GET", f"/api/leads?firmId={detail['firmId']}&limit=500")["items"]
                n = sum(1 for i in items if i.get("conversationId") == conv)
                if n != x["count"]:
                    problems.append(f"{n} leads for conversation {conv}, expected {x['count']}")
            if "channelTold" in x and self.told.get(x["lead"]) != x["channelTold"]:
                problems.append(f"the channel was told {self.told.get(x['lead'])}, expected {x['channelTold']}")
            return (not problems), "; ".join(problems)
        if kind == "classification":
            v = self.api.ok("GET", f"/api/leads/{self.leads[x['lead']]}/classification")
            problems = []
            if v["category"] != x["category"]:
                problems.append(f"category {v['category']}, expected {x['category']}")
            if "records" in x and len(v["records"]) != x["records"]:
                problems.append(f"{len(v['records'])} records, expected {x['records']}")
            if "offered" in x:
                want = sorted(self.dests[d] for d in x["offered"])
                if sorted(v["offered"]) != want:
                    problems.append(f"offered {v['offered']}, expected {want}")
            return (not problems), "; ".join(problems)
        if kind == "billing":
            v = self.api.ok("GET", f"/api/leads/{self.leads[x['lead']]}/billing")
            return v["is"] == x["is"], f"billing {v['is']}, expected {x['is']}"
        if kind == "outcome":
            got = OUTCOMES.get(self._lead_detail(x["lead"])["outcome"], "?")
            return got == x["is"], f"outcome {got}, expected {x['is']}"
        raise Unsupported(f"expectation '{kind}'")


# ----- Running ---------------------------------------------------------------


def build() -> Path:
    subprocess.run(["dotnet", "build", str(ROOT / "backend" / "src" / "Api"), "--artifacts-path", str(BUILD), "-v", "q", "-nologo"],
                   check=True, stdout=subprocess.DEVNULL)
    dlls = [p for p in glob.glob(str(BUILD / "bin" / "**" / "LeadIntegrations.Api.dll"), recursive=True)]
    if not dlls:
        sys.exit("build produced no LeadIntegrations.Api.dll")
    return Path(max(dlls, key=os.path.getmtime))


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def start(dll: Path, workdir: Path):
    port = free_port()
    env = dict(os.environ,
               PORT=str(port), DB_PATH=str(workdir / "scenario.db"), TEST_CONTROL="true",
               WORKER_ENABLED="false", TIME_SCALE="1", DELIVERY_TIMEOUT_MS="1500",
               ALLOW_LOCAL_TARGETS="true", FRONTEND_DIST=str(workdir / "no-frontend"))
    log = open(workdir / "api.log", "w", encoding="utf-8")
    proc = subprocess.Popen(["dotnet", str(dll)], env=env, stdout=log, stderr=subprocess.STDOUT, cwd=str(workdir))
    api = Api(f"http://localhost:{port}")
    deadline = time.time() + 60
    while time.time() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(f"API exited: see {workdir / 'api.log'}")
        try:
            if api.call("GET", "/api/health")[0] == 200:
                return proc, api, log
        except (urllib.error.URLError, ConnectionError, OSError):
            pass
        time.sleep(0.3)
    proc.kill()
    raise RuntimeError("API did not start")


def run_one(dll: Path, path: Path) -> dict:
    scenario = json.loads(path.read_text(encoding="utf-8"))
    t0 = int(datetime.fromisoformat(scenario["given"]["clock"]).timestamp() * 1000)
    # Every expectation is counted, including those never reached because a step was unsupported:
    # missing capability never leaves the denominator.
    counts = {"passed": 0, "failed": 0, "unsupported": 0, "notReached": len(scenario["then"])}
    result = {"name": scenario["name"], "model": scenario["model"], "slice": scenario["slice"], "result": None, "details": [],
              "assertions": counts}
    with tempfile.TemporaryDirectory(prefix=f"scenario-{scenario['name']}-") as tmp:
        proc, api, log = start(dll, Path(tmp))
        try:
            failures, unsupported = [], []
            lab = LabAdapter(api, scenario, t0)
            lab.setup()
            timeline = sorted(
                [(offset_ms(s["at"]), 0, i, s) for i, s in enumerate(scenario["when"])]
                + [(offset_ms(e["at"]), 1, i, e) for i, e in enumerate(scenario["then"])],
                key=lambda x: (x[0], x[1], x[2]))
            clock = None
            for k, (at, is_expectation, _, item) in enumerate(timeline):
                if at != clock:
                    api.ok("POST", "/api/test/advance", {"to": t0 + at})
                    clock = at
                if not is_expectation:
                    lab.step(item)
                    # Steps that share a time happen together (a lead and the fault that meets it):
                    # the worker runs after the last of them.
                    nxt = timeline[k + 1] if k + 1 < len(timeline) else None
                    if not (nxt and nxt[0] == at and nxt[1] == 0):
                        api.ok("POST", "/api/test/advance", {"to": t0 + at})
                    continue
                counts["notReached"] -= 1
                try:
                    holds, why = lab.expect(item)
                    if holds:
                        counts["passed"] += 1
                    else:
                        counts["failed"] += 1
                        failures.append(f"{item['at']} {next(k for k in item if k != 'at')}: {why}")
                except Unsupported as u:
                    counts["unsupported"] += 1
                    unsupported.append(f"{item['at']} {u}")
            if failures:
                result["result"], result["details"] = "FAIL", failures + [f"(unsupported) {u}" for u in unsupported]
            elif unsupported:
                result["result"], result["details"] = "UNSUPPORTED", unsupported
            else:
                result["result"] = "PASS"
        except Unsupported as u:
            # Stopped at an unsupported step; assertions already failed still make it a FAIL.
            stop = [f"cannot run further: {u}"] + [f"(unsupported) {x}" for x in unsupported]
            result["result"], result["details"] = ("FAIL", failures + stop) if failures else ("UNSUPPORTED", stop)
        except Exception as ex:  # a runner or API error, not a scenario verdict
            result["result"], result["details"] = "ERROR", [str(ex)]
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=15)
            except subprocess.TimeoutExpired:
                proc.kill()
            log.close()
    return result


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("names", nargs="*", help="scenario names (default: all)")
    ap.add_argument("--model", choices=["lab", "proposed"], help="only scenarios with this model")
    ap.add_argument("--report", help="also write the results as JSON here")
    args = ap.parse_args()

    paths = sorted(SCENARIOS.glob("*.json"))
    if args.names:
        paths = [p for p in paths if p.stem in set(args.names)]
    if args.model:
        paths = [p for p in paths if json.loads(p.read_text(encoding="utf-8"))["model"] == args.model]
    if not paths:
        sys.exit("no scenarios match")

    print(f"building the API into {BUILD.name}/ ...", flush=True)
    dll = build()
    results = []
    for p in paths:
        r = run_one(dll, p)
        results.append(r)
        expected = "expected" if (r["model"] == "lab") == (r["result"] == "PASS") else "UNEXPECTED"
        c = r["assertions"]
        tally = f"{c['passed']}/{c['failed']}/{c['unsupported'] + c['notReached']}"
        print(f"{r['result']:<11} {r['model']:<8} {r['name']:<30} {tally:<9} ({expected})", flush=True)
        for d in r["details"]:
            print(f"            {d}")

    lab_bad = [r for r in results if r["model"] == "lab" and r["result"] != "PASS"]
    promoted = [r for r in results if r["model"] == "proposed" and r["result"] == "PASS"]
    counts = {k: sum(1 for r in results if r["result"] == k) for k in ("PASS", "FAIL", "UNSUPPORTED", "ERROR")}
    print(f"\n{len(results)} scenarios: " + ", ".join(f"{v} {k.lower()}" for k, v in counts.items() if v))
    a = {k: sum(r["assertions"][k] for r in results) for k in ("passed", "failed", "unsupported", "notReached")}
    total = sum(a.values())
    print(f"{total} assertions: {a['passed']} passed, {a['failed']} failed, {a['unsupported']} unsupported, "
          f"{a['notReached']} not reached (a step before them was unsupported)")
    if lab_bad:
        print("lab scenarios not passing: " + ", ".join(r["name"] for r in lab_bad))
    if promoted:
        print("proposed scenarios now passing (promote to lab?): " + ", ".join(r["name"] for r in promoted))
    if args.report:
        Path(args.report).write_text(json.dumps(results, indent=2), encoding="utf-8")
    return 1 if lab_bad else 0


if __name__ == "__main__":
    sys.exit(main())
