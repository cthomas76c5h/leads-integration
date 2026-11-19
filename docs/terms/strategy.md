# Terms of service: how firms accept them, and how they change

How Answering AI's terms of service should work for law firms: accepted once,
changed without friction, and provable when it matters. It follows the
[billing strategy](../billing/strategy.md) (price changes arrive through the terms)
and runs in the lab: **Terms** (the admin page) and **Firm portal** (what a firm's
users see).

## 1. The story so far: problems and what solved them

| Problem | What it caused | Solution |
|---|---|---|
| Any update to the terms forced every user to accept again before signing in | Even a grammar fix locked everyone out; firms pushed back and fell back to text and email | **A new version asks for acceptance only when its publisher says so.** Off by default |
| Editorial changes and real changes were treated the same | No way to fix wording without disrupting every firm | **Each version states its kind of change**: editorial, pricing or material, and the kind suggests who must accept |
| Everyone was asked, whatever the change concerned | Members asked to accept pricing terms they do not pay | **Scope: firm admins only, or every user.** A pricing change asks the admins; a change to what users may do asks everyone |
| Terms lived in three places (the app, the website, the portal), each with its own logic | Inconsistent behaviour, duplicated work | **One global version, in the portal.** The app and website show it as static text |
| A new clause (promotions) was ready, but publishing it would have locked everyone out, because the scoped-acceptance change had not shipped | A content change waited on a software release | **Publishing is content, not code.** Once scoped acceptance exists, a new clause ships as a version the same day |
| The source document came from counsel as a Word file | Copy-and-paste errors, lost formatting | **Import the Word document** (or a Google Doc exported as Word), with a preview before publishing |
| Acceptance was a lock, not a record | No answer to "who agreed to what, and when?" | **Every acceptance is recorded**: version, firm, user, role, time. Append-only |
| Users learned of changes by being blocked | Confusion and support calls | **An in-portal notice** says what changed in plain words, for every version, whether or not it asks for acceptance |

## 2. Principles

1. **Accept once at the start.** Every user accepts the current terms at first sign-in; the portal waits until they do.
2. **Ask again only when it matters, and only whom it concerns.** Editorial: nobody. Pricing: firm admins. Material: every user. The publisher can override, and the choice is recorded with the version.
3. **Never surprise.** Every version carries a plain summary of what changed. A version that asks for acceptance can set an effective date; until then users see a notice with the date, and only from that date does the portal wait for them.
4. **One document, one place.** One global version for every firm and division; the portal is the source; everything else displays it.
5. **Prove it.** Each acceptance is a record (version, firm, user, role, time) that is never edited. Each version keeps its full text, its kind, who it asked, and when it was published and took effect.
6. **Offers carry their own terms.** Promotions, pilots and discounts are stated when a firm takes them up and end on a stated date, so the main terms do not change for each offer.

## 3. The model (built in the lab)

| Part | What it holds |
|---|---|
| **Version** | Number, title, summary of what changed, full text, kind (editorial, pricing, material), who must accept (nobody, admins, all), published at, takes effect at, source (Word or text) |
| **Acceptance** | Version, firm, user, role (admin or member), time; append-only |
| **What a user sees on signing in** | *Block*: first sign-in, or a version that asks this user and has taken effect. *Notice*: a version that asks this user but takes effect later (with the date), or one that asks nobody (no action needed). *Nothing*: all accepted |
| **Coverage** | For each version that asks for acceptance: how many of the users it asked have accepted |

The lab's history replays the real one as versions: the first terms (everyone),
a grammar fix (notice only), the promotions clause (firm admins), and the
price-book change from the first of next month (firm admins, with an effective
date). Portal users are synthetic: one admin and one member per firm, plus a new
member who has never signed in.

## 4. How law firms accept

- **The firm admin accepts for the firm** on commercial terms (pricing, billing). Every user accepts the terms that govern their own use.
- **Clear consent**: the full text is shown, a box is ticked ("I have read and accept these terms on behalf of my firm"), and the button says what it does. No pre-ticked boxes, no acceptance by continuing to browse.
- **Notice before change**: a change that needs acceptance is announced in the portal with its effective date, so firms can review it with their own counsel before it applies.
- **Records on request**: a firm, or our counsel, can see exactly which version each user accepted and when.

## 5. How a change is published

1. Counsel edits the Word document; the operator imports it and previews it.
2. The operator writes the summary of what changed and picks the kind of change; who must accept follows from the kind and can be overridden.
3. For pricing or material changes, an effective date gives firms time (for example the first of next month, with the price book version that starts then).
4. Publish. Users see the notice at their next sign-in; coverage shows who has accepted; on the effective date, those who have not are asked before continuing.

## 6. Our answers

Every terms decision (when to ask again, who accepts what, when the portal
waits, promotions, one version, notifications, the record) is in
[answers.md](../lead-delivery/answers.md#terms-of-service).
