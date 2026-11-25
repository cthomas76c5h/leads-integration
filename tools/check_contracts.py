"""Check the language-neutral scenario and receiver contract files (docs/contracts/).

Validates every file against its JSON Schema, then checks what a schema cannot:
every id a scenario refers to exists, every receiver mode exists, every event
name is in events.json, and every category and practice area is in the
taxonomy. The intake lead contract (docs/contracts/intake-lead/) is an exact copy of
intake's schema: compared with ../intake-policy-lab when that repository is next to this one,
and its examples validated against it. Exits 1 on any problem.

    python tools/check_contracts.py
"""

import json
import sys
from pathlib import Path

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parent.parent
CONTRACTS = ROOT / "docs" / "contracts"
TAXONOMY = ROOT / "docs" / "call-categories" / "taxonomy.json"
INTAKE_LEAD = CONTRACTS / "intake-lead"
# Intake owns the lead contract; this repository keeps copies it must not change.
INTAKE_DOCS = ROOT.parent / "intake-policy-lab" / "docs"
# The center owns usage/1 (C6) and publishes an example of its current version; this is a copy.
USAGE_EXAMPLE = CONTRACTS / "usage" / "usage-1.example.json"
CENTER_USAGE_EXAMPLE = ROOT.parent / "call-center-lab" / "docs" / "contracts" / "usage" / "usage-1.example.json"
LEAD_SCHEMAS = {  # contract -> (our copy, intake's original)
    "intake-lead/draft-2": ("draft-2.schema.json", "lead-contract.schema.json"),
    "intake-lead/draft-3": ("draft-3.schema.json", "lead-contract.draft-3.schema.json"),
}


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def schema_problems(validator: Draft202012Validator, doc) -> list[str]:
    return [f"{'/'.join(map(str, e.absolute_path)) or '(root)'}: {e.message}" for e in validator.iter_errors(doc)]


def scenario_problems(s: dict, modes: set[str], categories: set[str], areas: set[str], events: set[str]) -> list[str]:
    problems = []
    firms = {f["id"] for f in s["given"]["firms"]}
    destinations = {d["id"]: d for d in s["given"]["destinations"]}
    leads = set()

    for d in destinations.values():
        if d["firm"] not in firms:
            problems.append(f"destination {d['id']}: no firm {d['firm']}")
        for c in d.get("routing", {}).get("categories", []):
            if c not in categories:
                problems.append(f"destination {d['id']}: no category {c} in the taxonomy")

    for step in s["when"]:
        if "lead" in step:
            lead = step["lead"]
            leads.add(lead["id"])
            if lead["firm"] not in firms:
                problems.append(f"lead {lead['id']}: no firm {lead['firm']}")
            if lead["category"] not in categories:
                problems.append(f"lead {lead['id']}: no category {lead['category']} in the taxonomy")
            if "practiceArea" in lead and lead["practiceArea"] not in areas:
                problems.append(f"lead {lead['id']}: no practice area {lead['practiceArea']} in the taxonomy")
            if "only" in lead and lead["only"] not in destinations:
                problems.append(f"lead {lead['id']}: no destination {lead['only']}")
            if "contract" in lead and not (INTAKE_LEAD / "examples" / f"{lead['contract']['example']}.json").exists():
                problems.append(f"lead {lead['id']}: no contract example {lead['contract']['example']}")
        if "receiver" in step:
            r = step["receiver"]
            if r["destination"] not in destinations:
                problems.append(f"receiver at {step['at']}: no destination {r['destination']}")
            mode = r["answers"].get("mode")
            if mode is not None and mode not in modes:
                problems.append(f"receiver at {step['at']}: no mode {mode} in receivers/modes.json")
        if "action" in step:
            a = step["action"]
            if "destination" in a and a["destination"] not in destinations:
                problems.append(f"action at {step['at']}: no destination {a['destination']}")
            if "lead" in a and a["lead"] not in leads:
                problems.append(f"action at {step['at']}: no earlier lead {a['lead']}")
            for c in [a["category"]] if "category" in a else []:
                if c not in categories:
                    problems.append(f"action at {step['at']}: no category {c} in the taxonomy")
            for c in a.get("routing", {}).get("categories", []):
                if c not in categories:
                    problems.append(f"action at {step['at']}: no category {c} in the taxonomy")
        if "fault" in step:
            f = step["fault"]
            if "lead" in f and f["lead"] not in leads:
                problems.append(f"fault at {step['at']}: no earlier lead {f['lead']}")
            if "destination" in f and f["destination"] not in destinations:
                problems.append(f"fault at {step['at']}: no destination {f['destination']}")
        if "access" in step:
            acc = step["access"]
            if acc["firm"] not in firms:
                problems.append(f"access at {step['at']}: no firm {acc['firm']}")
            if acc["lead"] not in leads:
                problems.append(f"access at {step['at']}: no earlier lead {acc['lead']}")
        if "cycleCloses" in step and step["cycleCloses"]["firm"] not in firms:
            problems.append(f"cycle close at {step['at']}: no firm {step['cycleCloses']['firm']}")

    for e in s["then"]:
        refs = []
        if "destination" in e:
            refs.append(("destination", e["destination"]["id"]))
        for key in ("delivery", "noDelivery"):
            if key in e:
                refs += [("lead", e[key]["lead"]), ("destination", e[key]["destination"])]
        if "handOff" in e:
            refs.append(("lead", e["handOff"]["lead"]))
            if "by" in e["handOff"]:
                refs.append(("destination", e["handOff"]["by"]))
        for key in ("outcome", "capture", "billing"):
            if key in e:
                refs.append(("lead", e[key]["lead"]))
        if "access" in e:
            refs += [("lead", e["access"]["lead"]), ("firm", e["access"]["firm"])]
        if "classification" in e:
            c = e["classification"]
            refs.append(("lead", c["lead"]))
            refs += [("destination", d) for d in c.get("offered", [])]
            if c["category"] not in categories:
                problems.append(f"expectation at {e['at']}: no category {c['category']} in the taxonomy")
        for key in ("event", "noEvent", "noAttempt"):
            if key in e:
                ev = e[key]
                refs += [(what, ev[what]) for what in ("lead", "destination", "firm") if what in ev]
                if "name" in ev and ev["name"] not in events:
                    problems.append(f"expectation at {e['at']}: no event {ev['name']} in events.json")
        for what, ref in refs:
            known = {"lead": leads, "destination": destinations, "firm": firms}[what]
            if ref not in known:
                problems.append(f"expectation at {e['at']}: no {what} {ref}")
    return problems


def main() -> int:
    taxonomy = load(TAXONOMY)
    categories = {c["id"] for c in taxonomy["categories"]}
    areas = {a["id"] for a in taxonomy["practiceAreas"]}
    modes = set(load(CONTRACTS / "receivers" / "modes.json")["modes"])
    events = set(load(CONTRACTS / "events.json")["events"])

    scenario_validator = Draft202012Validator(load(CONTRACTS / "scenario.schema.json"))
    receiver_validator = Draft202012Validator(load(CONTRACTS / "receiver.schema.json"))

    failed = False
    checked = 0
    for path in sorted((CONTRACTS / "receivers").glob("*.json")):
        if path.name == "modes.json":
            continue
        doc = load(path)
        problems = schema_problems(receiver_validator, doc)
        if not problems and doc["kind"] != path.stem:
            problems.append(f"kind {doc['kind']} does not match the file name")
        checked += 1
        for p in problems:
            failed = True
            print(f"{path.relative_to(ROOT)}: {p}")

    for path in sorted((CONTRACTS / "scenarios").glob("*.json")):
        doc = load(path)
        problems = schema_problems(scenario_validator, doc)
        if not problems:
            problems = scenario_problems(doc, modes, categories, areas, events)
            if doc["name"] != path.stem:
                problems.append(f"name {doc['name']} does not match the file name")
        checked += 1
        for p in problems:
            failed = True
            print(f"{path.relative_to(ROOT)}: {p}")

    lead_validators = {}
    for contract, (ours, theirs) in LEAD_SCHEMAS.items():
        path = INTAKE_LEAD / ours
        checked += 1
        source = INTAKE_DOCS / theirs
        if source.exists() and source.read_bytes() != path.read_bytes():
            failed = True
            print(f"{path.relative_to(ROOT)}: differs from intake's {theirs}; copy it again, never edit it here")
        lead_validators[contract] = Draft202012Validator(load(path))
    for path in sorted((INTAKE_LEAD / "examples").glob("*.json")):
        doc = load(path)
        validator = lead_validators.get(doc.get("contract"))
        problems = [f"contract {doc.get('contract')!r} is not one this repository takes"] if validator is None else schema_problems(validator, doc)
        category = doc.get("classification", {}).get("category")
        area = doc.get("classification", {}).get("practiceArea")
        if category is not None and category not in categories:
            problems.append(f"category {category} is not in the taxonomy")
        if area is not None and area not in areas:
            problems.append(f"practice area {area} is not in the taxonomy")
        checked += 1
        for p in problems:
            failed = True
            print(f"{path.relative_to(ROOT)}: {p}")

    checked += 1
    usage = load(USAGE_EXAMPLE)
    if usage.get("contract") != "usage/1":
        failed = True
        print(f"{USAGE_EXAMPLE.relative_to(ROOT)}: contract is {usage.get('contract')!r}, not usage/1")
    if CENTER_USAGE_EXAMPLE.exists() and CENTER_USAGE_EXAMPLE.read_bytes() != USAGE_EXAMPLE.read_bytes():
        failed = True
        print(f"{USAGE_EXAMPLE.relative_to(ROOT)}: differs from the center's example; copy it again, never edit it here")

    print(f"{checked} contract files checked, {'problems found' if failed else 'all valid'}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
