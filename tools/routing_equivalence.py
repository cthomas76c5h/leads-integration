"""Would the default routing rule send each firm's CRMs what they get today?

    python tools/routing_equivalence.py [--db backend/data/lead-integrations.db]

Today (the legacy system, docs/lead-delivery/production-today.md): a lead goes to a firm's CRMs
only if its call type is one of the firm's *active* contact types; a firm with no
active types sends nothing to any CRM. Email and the portal take everything, today
and under the default, so only CRM destinations can differ.

The default rule (answers.md, routing): CRMs take new matters. Categories the
taxonomy marks `review` (general, jail-custody, staff-routing) are the firm's choice.

For every firm with a legacy CRM connection, each call type is compared, and the
firm's actual behaviour is expressed as a category rule where it can be:

  default           exactly new matters, every practice area
  default+choices   new matters, plus or minus whole `review` categories
  practice-areas    new matters limited to some practice areas
  custom            whole categories the default skips (existing clients, courts ...)
  inexpressible     one category (and practice area) split between active and not:
                    category routing cannot say it without a per-label exception
  nothing-today     no active types: the CRM receives nothing today

Writes:
  backend/data/routing-equivalence.tsv     per firm and call type (git-ignored: firm
                                           names, and call types hold staff names)
  docs/lead-delivery/routing-equivalence.json   aggregate counts only
"""

import argparse
import collections
import json
import sqlite3
import sys
from pathlib import Path

from call_categories import DOCS, ROOT, Taxonomy

OUT_JSON = ROOT / "docs" / "lead-delivery" / "routing-equivalence.json"
OUT_TSV = ROOT / "backend" / "data" / "routing-equivalence.tsv"
DEFAULT_SEND = {"new-matter"}


def legacy_key(name: str) -> str:
    # The legacy system trims " ,.;:-*" from the ends and lowercases before matching.
    return (name or "").strip(" ,.;:-*").lower()


def load(db: Path):
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    crms = collections.defaultdict(list)
    for fid, kind, enabled in c.execute("SELECT firm_id, kind, enabled FROM connections WHERE source = 'legacy'"):
        crms[fid].append((kind, bool(enabled)))
    firms = []
    for fid, name, active, cts in c.execute("SELECT id, name, active, contact_types FROM firms"):
        if fid not in crms:
            continue
        types = {}
        for t in json.loads(cts or "[]"):
            raw = (t.get("name") or t.get("Name") or "").strip()
            if not raw:
                continue
            k = legacy_key(raw)
            prev = types.get(k)
            types[k] = (prev[0] if prev else raw, bool(t.get("active", t.get("Active"))) or (prev[1] if prev else False))
        firms.append({"id": fid, "name": name, "active": bool(active), "crms": crms[fid], "types": list(types.values())})
    return firms


def judge(firm, tax: Taxonomy, crm_of: dict):
    rows = []
    for raw, active in firm["types"]:
        m = tax.classify(raw)
        cat, pa = m["category"], m["practiceArea"]
        policy = crm_of.get(cat)  # send | skip | review | never | None (unmapped)
        default = "send" if cat in DEFAULT_SEND else ("firm" if policy == "review" else "skip")
        if default == "firm":
            outcome = "firm-choice"
        elif active and default == "send":
            outcome = "same"
        elif not active and default == "skip":
            outcome = "same"
        elif active:
            outcome = "stops"  # reaches the CRM today, the default would not send it
        else:
            outcome = "starts"  # not sent today, the default would send it
        rows.append({"name": raw, "active": active, "category": cat, "practiceArea": pa or "", "default": default, "outcome": outcome})

    if not any(r["active"] for r in rows):
        return "nothing-today", rows, {}

    by_cat = collections.defaultdict(set)
    by_area = collections.defaultdict(set)
    for r in rows:
        by_cat[r["category"]].add(r["active"])
        if r["category"] == "new-matter":
            by_area[r["practiceArea"]].add(r["active"])
    split = sorted(c for c, s in by_cat.items() if len(s) == 2 and c != "new-matter")
    nm = by_cat.get("new-matter", set())
    area_split = sorted(a for a, s in by_area.items() if len(s) == 2)
    if area_split:
        split.append("new-matter")
    rule = {
        "categories": sorted(c for c, s in by_cat.items() if s == {True}),
        "practiceAreas": "all" if nm != {True, False} else sorted(a for a, s in by_area.items() if s == {True}),
        "split": split,
    }
    if nm == {True, False} and not area_split:
        rule["categories"] = sorted(set(rule["categories"]) | {"new-matter"})
    send_cats = set(rule["categories"])
    review = {c for c, p in crm_of.items() if p == "review"}
    if split:
        verdict = "inexpressible"
    elif send_cats - DEFAULT_SEND - review:
        verdict = "custom"
    elif rule["practiceAreas"] != "all" or "new-matter" not in send_cats and nm:
        verdict = "practice-areas"
    elif send_cats == DEFAULT_SEND:
        verdict = "default"
    else:
        verdict = "default+choices"
    return verdict, rows, rule


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=str(ROOT / "backend" / "data" / "lead-integrations.db"))
    args = ap.parse_args()
    tax = Taxonomy(DOCS / "taxonomy.json")
    crm_of = {c["id"]: c["crm"] for c in tax.raw["categories"]}
    firms = load(Path(args.db))

    groups = {"enabledCrm": lambda f: any(e for _, e in f["crms"]), "anyCrm": lambda f: True}
    report = {
        "about": "Firms with a legacy CRM connection. Counts are call types and firms, not leads: "
        "the import holds no lead volume. Production feed needed to weigh by leads.",
        "generatedFrom": "imported firms' contact types (legacy) and CRM connections",
        "defaultRule": "CRMs take new matters, every practice area; review categories are the firm's choice",
    }
    with OUT_TSV.open("w", encoding="utf-8", newline="") as fh:
        fh.write("firm\tfirm_active\tcrm_enabled\tverdict\tcall_type\tactive\tcategory\tpractice_area\tdefault\toutcome\n")
        judged = []
        for f in firms:
            verdict, rows, rule = judge(f, tax, crm_of)
            judged.append((f, verdict, rows, rule))
            en = groups["enabledCrm"](f)
            for r in rows:
                fh.write(f"{f['name']}\t{int(f['active'])}\t{int(en)}\t{verdict}\t{r['name']}\t{int(r['active'])}\t"
                         f"{r['category']}\t{r['practiceArea']}\t{r['default']}\t{r['outcome']}\n")

    for gname, pred in groups.items():
        sel = [j for j in judged if j[0]["active"] and pred(j[0])]
        verdicts = collections.Counter(v for _, v, _, _ in sel)
        outcomes = collections.Counter()
        stops = collections.Counter()
        starts = collections.Counter()
        choices = collections.Counter()
        splits = collections.Counter()
        for f, v, rows, rule in sel:
            if v == "nothing-today":
                continue
            for r in rows:
                outcomes[r["outcome"]] += 1
                if r["outcome"] == "stops":
                    stops[r["category"]] += 1
                if r["outcome"] == "starts":
                    starts[r["practiceArea"] or r["category"]] += 1
            for c in rule.get("categories", []):
                if crm_of.get(c) == "review":
                    choices[c] += 1
            for c in rule.get("split", []):
                splits[c] += 1
        report[gname] = {
            "activeFirms": len(sel),
            "byVerdict": dict(verdicts.most_common()),
            "callTypesByOutcome": dict(outcomes.most_common()),
            "stopsByCategory": dict(stops.most_common()),
            "startsByPracticeArea": dict(starts.most_common()),
            "firmsChoosingReviewCategory": dict(choices.most_common()),
            "firmsWithSplitCategory": dict(splits.most_common()),
        }
    OUT_JSON.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    for gname in groups:
        g = report[gname]
        print(f"{gname}: {g['activeFirms']} active firms")
        for v, n in g["byVerdict"].items():
            print(f"  {v:16} {n:5}")
        print(f"  call types: {g['callTypesByOutcome']}")
        print(f"  stop reaching the CRM, by category: {g['stopsByCategory']}")
    print(f"-> {OUT_JSON.relative_to(ROOT)}, {OUT_TSV.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
