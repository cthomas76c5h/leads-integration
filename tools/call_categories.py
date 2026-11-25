"""Map every contact-type name in the lab database to the call-category taxonomy.

    python tools/call_categories.py [--db backend/data/lead-integrations.db]

Reads docs/call-categories/taxonomy.json and the imported firms' contact types.
Writes:
  backend/data/contact-type-mapping.tsv     every raw name -> category (git-ignored:
                                            the long tail holds staff names)
  docs/call-categories/coverage.json        aggregate counts only, plus example names
                                            used by 5+ firms with no person's name in them
Then checks docs/call-categories/reference-leads.json against the rules and exits 1
if any reference lead maps differently from what it expects.
"""

import argparse
import collections
import json
import re
import sqlite3
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs" / "call-categories"
EXAMPLE_MIN_FIRMS = 5


def norm(s: str) -> str:
    return " " + re.sub(r"\s+", " ", re.sub(r"[^a-z0-9]+", " ", (s or "").lower())).strip() + " "


def key(s: str) -> str:
    return re.sub(r"[^a-z0-9]", "", (s or "").lower())


class Taxonomy:
    def __init__(self, path: Path):
        t = json.loads(path.read_text(encoding="utf-8"))
        self.raw = t
        self.modifiers = [(m["id"], re.compile(m["pattern"])) for m in t["modifiers"]]
        self.areas = [(a["id"], re.compile(a["pattern"])) for a in t["practiceAreas"]]
        self.categories = []
        for c in t["categories"]:
            p = c["pattern"]
            special = p if p in ("MODIFIERS_ONLY", "NAME_ONLY") else None
            rx = None if special else re.compile(p)
            self.categories.append((c["id"], rx, special, bool(c.get("practiceAreaFallback"))))
        self.fallback_id = next((cid for cid, _, _, fb in self.categories if fb), None)
        self.filler = set(t["filler"].split())
        self.all_rx = [rx for _, rx in self.modifiers] + [rx for _, rx in self.areas] + [rx for _, rx, _, _ in self.categories if rx]

    def name_tokens(self, raw: str) -> list[str]:
        """Words left once every rule's matches and the filler words are removed,
        written like a name (Title case, 3+ letters): 'General Calls Andrea'."""
        rest = norm(raw)
        for rx in self.all_rx:
            rest = rx.sub(" ", rest)
        left = {w for w in rest.split() if w not in self.filler and not w.isdigit()}
        words = re.findall(r"[A-Za-z]+", raw or "")
        if len((raw or "").strip()) >= 24 and words:
            # The source system cuts names at 25 characters: the last word is often a stub ("Accide").
            left.discard(words[-1].lower())
        return [w for w in words if w.lower() in left and len(w) >= 3 and w[0].isupper() and not w.isupper()]

    def area(self, text: str) -> str | None:
        for aid, rx in self.areas:
            if rx.search(text):
                return aid
        return None

    def classify(self, raw: str) -> dict:
        r = self._classify(raw)
        prefix = self.raw.get("systemPrefix")
        if prefix and (raw or "").strip().startswith(prefix) and r["category"] in ("staff-routing", "unmapped"):
            return {"category": "system", "practiceArea": None, "modifiers": [m for m in r["modifiers"] if m != "named-person"]}
        return r

    def _classify(self, raw: str) -> dict:
        text = norm(raw)
        mods = [mid for mid, rx in self.modifiers if rx.search(text)]
        if "no-patch" in mods and "patch" in mods:
            mods.remove("patch")
        names = self.name_tokens(raw)
        handling = {"after-hours", "business-hours", "patch", "no-patch", "away"}
        # Handling words ("AH", "patch", "New Years") say how, not what: drop them before
        # matching categories. Language, channel and custody words stay: they carry meaning.
        stripped = text
        for mid, rx in self.modifiers:
            if mid in handling:
                stripped = rx.sub(" ", stripped)
        area = self.area(stripped)
        handling = {"after-hours", "business-hours", "patch", "no-patch", "away"}
        for cid, rx, special, _ in self.categories:
            if cid == "general" and rx.search(stripped):
                return self._result(cid, None, mods, names)
            if cid == "general" and self.fallback_id and area and area != "other":
                # A name that only names a practice area ("Personal Injury") is a new matter.
                return self._result(self.fallback_id, area, mods, names)
            if special == "MODIFIERS_ONLY":
                if set(mods) & handling and not set(mods) - handling - {"in-custody"} and not names:
                    return self._result(cid, None, mods, names)
                continue
            if special == "NAME_ONLY":
                if names:
                    return self._result(cid, None, mods, names)
                continue
            target = text if cid == "system" else stripped
            if rx.search(target):
                pa = (area or "unspecified") if cid == self.fallback_id else None
                return self._result(cid, pa, mods, names)
            if cid == "general" and set(mods) & {"spanish", "other-language", "webchat"}:
                # "Spanish Calls", "WebChat": a kind of caller or channel, nothing narrower.
                return self._result(cid, None, mods, names)
        return self._result("unmapped", None, mods, names)

    @staticmethod
    def _result(cid, area, mods, names):
        mods = list(mods)
        if names and cid not in ("staff-routing",):
            mods.append("named-person")
        return {"category": cid, "practiceArea": area, "modifiers": mods}


def load_firms(db: Path):
    c = sqlite3.connect(db)
    firms = []
    crm_firms = {r[0] for r in c.execute("SELECT DISTINCT firm_id FROM connections WHERE source = 'legacy'")}
    for fid, active, cts in c.execute("SELECT id, active, contact_types FROM firms"):
        types = []
        for t in json.loads(cts or "[]"):
            name = (t.get("name") or t.get("Name") or "").strip()
            act = bool(t.get("active", t.get("Active")))
            if name:
                types.append((name, act))
        firms.append({"id": fid, "active": bool(active), "crm": fid in crm_firms, "types": types})
    return firms


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=str(ROOT / "backend" / "data" / "lead-integrations.db"))
    args = ap.parse_args()
    tax = Taxonomy(DOCS / "taxonomy.json")
    firms = load_firms(Path(args.db))

    # Distinct names by normalized key; the most common spelling represents the key.
    spellings = collections.defaultdict(collections.Counter)
    firm_count = collections.Counter()
    active_count = collections.Counter()
    for f in firms:
        seen = set()
        for name, act in f["types"]:
            k = key(name)
            spellings[k][name] += 1
            if k not in seen:
                seen.add(k)
                firm_count[k] += 1
                if act:
                    active_count[k] += 1
    rep = {k: s.most_common(1)[0][0] for k, s in spellings.items()}
    mapped = {k: tax.classify(rep[k]) for k in rep}

    out_tsv = ROOT / "backend" / "data" / "contact-type-mapping.tsv"
    with out_tsv.open("w", encoding="utf-8", newline="") as fh:
        fh.write("firms\tactive_in\tname\tcategory\tpractice_area\tmodifiers\n")
        for k, n in firm_count.most_common():
            m = mapped[k]
            fh.write(f"{n}\t{active_count[k]}\t{rep[k]}\t{m['category']}\t{m['practiceArea'] or ''}\t{','.join(m['modifiers'])}\n")

    total_occ = sum(firm_count.values())
    by_cat = collections.Counter()
    by_cat_names = collections.Counter()
    by_area = collections.Counter()
    by_mod = collections.Counter()
    examples = collections.defaultdict(list)
    system_rx = next(rx for cid, rx, _, _ in tax.categories if cid == "system")
    for k, n in firm_count.most_common():
        m = mapped[k]
        by_cat[m["category"]] += n
        by_cat_names[m["category"]] += 1
        if m["practiceArea"]:
            by_area[m["practiceArea"]] += n
        for mod in m["modifiers"]:
            by_mod[mod] += n
        # Examples are committed: no person's name, and for system entries only the ones the
        # system pattern names (admin, no charge, pop-up), never phone-number entries (they name carriers).
        number_entry = m["category"] == "system" and (re.search(r"\b(did|sip)\b", norm(rep[k])) or not system_rx.search(norm(rep[k])))
        if n >= EXAMPLE_MIN_FIRMS and "named-person" not in m["modifiers"] and m["category"] != "staff-routing" and not number_entry:
            bucket = m["practiceArea"] if m["category"] == "new-matter" else m["category"]
            if len(examples[bucket]) < 6:
                examples[bucket].append({"name": rep[k], "firms": n})

    # Which categories firms with a CRM integration keep active: what reaches the CRM today.
    crm_active = collections.Counter()
    crm_firms_with_types = 0
    only_general = 0
    for f in firms:
        if not (f["crm"] and f["active"]):
            continue
        cats = {tax.classify(n)["category"] for n, a in f["types"] if a}
        if not cats:
            continue
        crm_firms_with_types += 1
        for c in cats:
            crm_active[c] += 1
        if cats & {"new-matter"} == set() and "general" in cats:
            only_general += 1

    # The legacy system marks a contact type active when that kind of call counts as a lead. The rules
    # never see that flag, so agreement with it is an independent check on new-matter.
    agree = collections.Counter()
    for f in firms:
        if not f["active"]:
            continue
        for n, a in f["types"]:
            is_new = tax.classify(n)["category"] == "new-matter"
            agree[("active" if a else "inactive", "new-matter" if is_new else "other")] += 1
    active_total = agree[("active", "new-matter")] + agree[("active", "other")]
    new_total = agree[("active", "new-matter")] + agree[("inactive", "new-matter")]

    repeated = [k for k, n in firm_count.items() if n >= 2]
    coverage = {
        "generatedFrom": "imported firms' contact types (the legacy sources)",
        "firmsWithContactTypes": sum(1 for f in firms if f["types"]),
        "distinctNames": len(firm_count),
        "firmOccurrences": total_occ,
        "namesUsedByOneFirm": sum(1 for n in firm_count.values() if n == 1),
        "byCategory": {c: {"occurrences": by_cat[c], "share": round(by_cat[c] / total_occ, 4), "distinctNames": by_cat_names[c]} for c, _ in by_cat.most_common()},
        "newMatterByPracticeArea": dict(by_area.most_common()),
        "byModifier": dict(by_mod.most_common()),
        "unmappedRepeated": sum(1 for k in repeated if mapped[k]["category"] == "unmapped"),
        "agreementWithLeadFlag": {
            "about": "Active firms only. 'active' = the legacy system counts this contact type as a lead.",
            "activeTypes": active_total,
            "activeTypesClassifiedNewMatter": agree[("active", "new-matter")],
            "newMatterTypes": new_total,
            "newMatterTypesActive": agree[("active", "new-matter")],
            "otherTypesActive": agree[("active", "other")],
        },
        "activeCategoriesAtFirmsWithCrm": {"firms": crm_firms_with_types, "firmsWithCategory": dict(crm_active.most_common()), "firmsWithGeneralButNoNewMatter": only_general},
        "examples": {b: examples[b] for b in sorted(examples)},
    }
    (DOCS / "coverage.json").write_text(json.dumps(coverage, indent=2) + "\n", encoding="utf-8")

    print(f"{len(firm_count)} names, {total_occ} firm occurrences -> {out_tsv.relative_to(ROOT)}")
    for c, v in coverage["byCategory"].items():
        print(f"  {c:20} {v['occurrences']:6} occ  {v['share']*100:5.1f}%  {v['distinctNames']:5} names")

    # Reference leads must map the way they say they do.
    refs = json.loads((DOCS / "reference-leads.json").read_text(encoding="utf-8"))
    crm_of = {c["id"]: c["crm"] for c in tax.raw["categories"]}
    bad = 0
    for r in refs["leads"]:
        got = tax.classify(r["lead"]["callType"])
        got["crm"] = crm_of.get(got["category"])
        exp = r["expect"]
        ok = (
            got["category"] == exp["category"]
            and got["practiceArea"] == exp.get("practiceArea")
            and set(exp.get("modifiers", [])) == set(got["modifiers"])
            and got["crm"] == exp.get("crm")
        )
        if not ok:
            bad += 1
            print(f"  MISMATCH {r['id']}: {r['lead']['callType']!r} expected {exp} got {got}")
    print(f"reference leads: {len(refs['leads']) - bad}/{len(refs['leads'])} map as expected")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
