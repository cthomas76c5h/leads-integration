"""Capture the vision meeting screenshots from a running lab.

Usage (from the repo root, with the API running on :5080). In backend/, first:
    dotnet run --project src/Import -- demo-names   # fictional firm names: no real firm in a screenshot
    dotnet run --project src/Import -- simulate

    python tools/capture_vision_deck.py            # writes docs/vision-meeting/*.png
    python tools/capture_vision_deck.py --out DIR

Needs Python Playwright with Chromium (`pip install playwright && playwright install chromium`).
The order follows the meeting story in docs/vision-meeting/README.md. It edits one
mapping (a draft revision) for screen 15; rerun `simulate` to reset it.
"""
import argparse
import json
import sys
import time
import urllib.request

from playwright.sync_api import sync_playwright

B = "http://localhost:5080"


def get(path):
    return json.load(urllib.request.urlopen(B + path))


def scenario_firms():
    """The firms the simulator chose for each story, from the data itself."""
    expired = get("/api/leads?outcome=expired&limit=200")["items"]
    late = [x for x in expired if x["handOff"]["at"]]  # reached the firm, too late
    fallback = None
    for x in get("/api/leads?limit=500")["items"]:
        if x["handOff"] and x["handOff"]["viaFallback"]:
            fallback = x
            break
    return late, fallback


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="docs/vision-meeting")
    out = ap.parse_args().out

    for _ in range(90):
        try:
            urllib.request.urlopen(B + "/api/health", timeout=2)
            break
        except Exception:
            time.sleep(1)

    late, fallback = scenario_firms()
    if not late or not fallback:
        sys.exit("no expired or fallback lead: run `dotnet run --project src/Import -- simulate` first")
    # The showcase firm, by role rather than name: the one on the Insight tier (most destinations).
    showcase = next(r["firmId"] for r in get("/api/billing/accounts") if r["tier"] == "insight")
    # The firm whose new matters name the most practice areas: the categories' value at a glance.
    areas = {}
    for x in get("/api/leads?category=new-matter&limit=500")["items"]:
        if x["mapping"]["practiceArea"] not in (None, "other"):
            areas.setdefault(x["firmId"], set()).add(x["mapping"]["practiceArea"])
    varied = max(areas, key=lambda f: len(areas[f]))
    attention = get("/api/deliveries?status=attention&test=false&limit=500")
    failed = get("/api/deliveries?status=failed&test=false&limit=500")
    hook = next(d for d in attention if d["kind"] == "webhook")
    smokeball = next(d for d in attention if d["kind"] == "smokeball")
    docket = next(d for d in attention if d["kind"] == "lead-docket" and d["diagnosis"]["class"] == "connection")
    hang_up = next(d for d in failed if d["diagnosis"] and d["diagnosis"]["class"] == "lead")

    errors = []
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={"width": 1440, "height": 900}, device_scale_factor=2)
        page.on("pageerror", lambda e: errors.append(str(e)))
        page.on("console", lambda m: m.type == "error" and errors.append(m.text))

        def open_(url, wait=1800):
            page.goto(B + "/" + url)
            page.wait_for_timeout(wait)

        def at(text, tag="h2"):
            el = page.locator(tag, has_text=text).first
            el.scroll_into_view_if_needed()
            page.evaluate("window.scrollBy(0, -90)")
            page.wait_for_timeout(400)

        def shot(name, full=False):
            page.screenshot(path=f"{out}/{name}.png", full_page=full)
            print("saved", name)

        # A. The view from outside
        open_("#/vision")
        shot("01-vision-thesis-and-live-numbers")
        at("What day-to-day operations hides")
        shot("02-vision-what-operations-hides")
        at("Three moves")
        shot("03-vision-moves-direction-roadmap")

        # B. Call categories: the missing language
        open_("#/categories")
        at("One field, four jobs")
        shot("04-categories-one-field-four-jobs")
        open_(f"#/report?firm={varied}", 2500)
        shot("05-firm-report-week-in-calls")
        open_(f"#/report?firm={showcase}", 2500)
        at("Proof of delivery")
        shot("06-firm-report-proof-of-delivery")

        # C. Speed: a lead's value expires
        open_("#/leads?outcome=expired", 2200)
        shot("07-leads-expired")
        open_(f"#/leads?lead={fallback['id']}&firm={fallback['firmId']}", 2200)
        shot("08-lead-reached-by-fallback")
        open_(f"#/leads?lead={late[0]['id']}&firm={late[0]['firmId']}&outcome=expired", 2200)
        shot("09-lead-expired-past-window")
        open_(f"#/report?firm={fallback['firmId']}", 2500)
        shot("10-firm-report-fallback-in-time")

        # D. Silent loss made visible
        open_("#/destinations", 2200)
        shot("11-destinations-stopped-and-missed")
        open_("#/deliveries", 2200)
        shot("12-delivery-center-expired-first")

        # E. Diagnose and fix where you found it
        open_(f"#/deliveries?d={hang_up['id']}", 2200)
        shot("13-drawer-lead-problem")
        open_(f"#/deliveries?d={hook['id']}", 2200)
        shot("14-drawer-setup-problem-edit-mapping")
        page.locator("aside.drawer-card").get_by_role("button", name="Edit mapping").click()
        page.wait_for_selector("dialog[open] .maprow")
        dlg = page.locator("dialog[open]")
        dlg.get_by_role("button", name="Add field").click()
        row = dlg.locator(".maprow").last
        row.locator("select").select_option("phone")
        row.locator("input").fill("Phone")
        dlg.get_by_role("button", name="Save mapping").click()
        page.wait_for_timeout(2200)
        shot("15-edit-mapping-draft-revision")
        page.keyboard.press("Escape")
        open_(f"#/deliveries?d={smokeball['id']}", 2200)
        page.locator("aside.drawer-card").get_by_role("button", name="See what we send").click()
        page.wait_for_selector("dialog[open] pre")
        page.wait_for_timeout(600)
        shot("16-see-what-we-send")
        page.keyboard.press("Escape")
        open_(f"#/deliveries?d={docket['id']}", 2200)
        shot("17-drawer-connection-problem")

        # F. One firm's whole picture
        open_(f"#/flow?firm={showcase}", 2500)
        page.locator(".firm-picker input").click()
        page.wait_for_timeout(1200)
        shot("18-delivery-graph-and-firm-picker")
        page.keyboard.press("Escape")

        # G. Billing, working on the same week
        open_("#/vision")
        at("Billing: priced on value")
        shot("19-vision-billing")
        upgraded = fallback["firmId"]  # the simulator moved this firm up a tier three days ago
        open_(f"#/portal?firm={upgraded}&role=admin", 2500)
        shot("20-portal-billing-statement")
        rows = get("/api/billing/accounts")
        locked = next(r["firmId"] for r in rows if r["cycleDay"] == 15 and r["ready"]
                      and get(f"/api/firms/{r['firmId']}/statement?cycle=previous")["conversations"] > 0)
        open_(f"#/portal?firm={locked}&role=admin&cycle=previous", 2500)
        shot("21-portal-previous-cycle-locked")
        open_(f"#/portal?firm={upgraded}&role=admin", 2500)
        at("Change plan")
        page.locator("select").last.select_option(index=12)
        page.wait_for_timeout(500)
        shot("22-portal-change-plan")
        open_("#/billing", 2500)
        shot("23-billing-operations")
        at("Price book")
        shot("24-billing-price-book")

        # H. Terms of service
        open_("#/terms", 2200)
        shot("25-terms-versions-and-coverage")
        at("Publish a new version")
        form = page.locator("section", has_text="Publish a new version").last
        form.locator("select").first.select_option("pricing")
        form.locator("input[type=date]").fill("2026-12-01")
        form.locator("input").last.fill("Overage per conversation from $3.25 to $3.10 on 100-conversation plans")
        page.wait_for_timeout(500)
        shot("26-terms-publish-with-preview")
        open_(f"#/portal?firm={upgraded}&role=new", 2500)
        shot("27-portal-first-sign-in")

        # I. Our answers
        open_("#/vision")
        at("Our answers")
        shot("28-vision-our-answers")

        browser.close()
    if errors:
        sys.exit("console errors: " + "; ".join(errors))


if __name__ == "__main__":
    main()
