"""Check the language-neutral scenario and receiver contract files (docs/contracts/).

Validates every file against its JSON Schema, then checks what a schema cannot:
every id a scenario refers to exists, every receiver mode exists, and every
category and practice area is in the taxonomy. Exits 1 on any problem.

    python tools/check_contracts.py
"""

import json
import sys
from pathlib import Path

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parent.parent
CONTRACTS = ROOT / "docs" / "contracts"
TAXONOMY = ROOT / "docs" / "call-categories" / "taxonomy.json"


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def schema_problems(validator: Draft202012Validator, doc) -> list[str]:
    return [f"{'/'.join(map(str, e.absolute_path)) or '(root)'}: {e.message}" for e in validator.iter_errors(doc)]


def scenario_problems(s: dict, modes: set[str], categories: set[str], areas: set[str]) -> list[str]:
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
        if "outcome" in e:
            refs.append(("lead", e["outcome"]["lead"]))
        for what, ref in refs:
            known = leads if what == "lead" else destinations
            if ref not in known:
                problems.append(f"expectation at {e['at']}: no {what} {ref}")
    return problems


def main() -> int:
    taxonomy = load(TAXONOMY)
    categories = {c["id"] for c in taxonomy["categories"]}
    areas = {a["id"] for a in taxonomy["practiceAreas"]}
    modes = set(load(CONTRACTS / "receivers" / "modes.json")["modes"])

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
            problems = scenario_problems(doc, modes, categories, areas)
            if doc["name"] != path.stem:
                problems.append(f"name {doc['name']} does not match the file name")
        checked += 1
        for p in problems:
            failed = True
            print(f"{path.relative_to(ROOT)}: {p}")

    print(f"{checked} contract files checked, {'problems found' if failed else 'all valid'}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
