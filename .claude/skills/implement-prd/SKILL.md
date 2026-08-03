---
name: implement-prd
description: >-
  Implement one of this repo's own staged PRDs (`new-features/*.md`, e.g.
  `managed-pgf-codec.md`, `pgf-cancellation-and-progress.md`, `pgf-roi-support.md`) end to end:
  work through its own "Stage sequence", resolve its "Open questions" empirically, keep the full
  test suite green at every stage, and update both the PRD's own "Progress log" and any
  cross-referencing docs/*.md pages at the end. USE FOR: "/implement-prd <file-reference>",
  "implement new-features/X.md", "work through this PRD", continuing a partially-done PRD found
  in this repo. DO NOT USE FOR: a spec pasted from outside this repo that hasn't been verified
  against the real codebase yet (use the `spec-to-staged-plan` skill first to turn it into a
  proper staged PRD in this repo's own style) or a one-off bug fix with no PRD document.
---

# Implement a PRD

This repo's `new-features/*.md` files are staged PRDs written *with real repo access* — they cite
real file paths, line numbers, and existing API shapes, and already contain a stage sequence, test
rig, acceptance criteria, and an empty "Progress log" section to fill in as work happens. This
skill is the process that produced `pgf-cancellation-and-progress.md`'s implementation (all 5
stages, 631/631 green, docs updated) in one sitting — follow it the same way for the next one.

If the input isn't already a PRD in this shape (e.g. a spec pasted from an external AI
conversation, or a design with invented API signatures), stop and use the `spec-to-staged-plan`
skill first to turn it into one. This skill assumes the grounding work is already done.

## 0. Read and verify before touching anything

1. Read the whole PRD file. Note: Context, Goals, Non-goals, Proposed architecture, Test rig,
   Stage sequence, Acceptance criteria, Open questions, Progress log.
2. This repo holds itself to "verify, don't recall" (see `CLAUDE.md`'s "Repo-wide conventions"
   section) — the PRD's own citations are claims made *at the time it was written*, not guarantees.
   Before relying on any cited file/line/API/test count, open the real file and confirm it still
   matches. If it doesn't (renamed, moved, refactored since), that's real information — update your
   plan around the current code, don't implement against a stale citation.
3. Grep for every other doc that references this PRD by filename (typically a `docs/*.md`
   "current-state reference" page's "out of scope"/"planned" list, and other PRDs' own cross-links)
   — these are the files Stage "Documentation" will need to update later, so know them up front.

## 1. Plan the stages — don't invent your own breakdown

Use `TodoWrite` with one todo per item in the PRD's own **"Stage sequence"** section, verbatim or
near-verbatim. These PRDs are written stage-by-stage on purpose (each with its own "exit test") —
resist the urge to reorganize into a different shape. If the PRD has an **"Open questions"**
section, resolve each one for real (read the cited source, reason from the actual algorithm/code,
or run a small experiment) at the stage that needs it — usually Stage 1 — and write the decision
down in prose immediately (in a doc comment and/or the Progress log draft), not just in your own
head. A resolved open question should read as a real decision with a reason, e.g. "mirrors the
native's area-weighted curve rather than a linear one because X would misreport progress" — not
"chose the simpler option."

## 2. Implement one stage at a time

For each stage:

1. Implement exactly what that stage's own description + the PRD's "Proposed architecture" section
   says. If the architecture section turns out to be wrong about something in the real code (rare,
   but check), fix your understanding from the code and note the discrepancy — don't silently
   implement the doc's version over the code's real shape.
2. Match the surrounding code's own doc-comment density and style exactly — this codebase's doc
   comments routinely cite the motivating PRD by filename (e.g. `pgf-cancellation-and-progress.md
   Goal 3`), explain *why* a shape was chosen, and reference exact source lines when porting a
   native algorithm. A new method with no doc comment, or one that only restates what the code
   obviously does, is under the bar here.
3. Write the stage's own tests from the PRD's **"Test rig"** section (or extend an existing test
   file in the same style as the PRD names, e.g. `TestBitmaps`/`EdgeCaseDimensions` fixtures) —
   don't defer all testing to the end.
4. Build, then run just the new/affected test class first
   (`dotnet test <project> -- --filter-class "*.ClassName"` — this repo's test projects use xUnit
   v3 on the Microsoft.Testing.Platform runner, **not** VSTest, so the filter syntax is
   `--filter-class` after a `--` separator, never plain `--filter`).
5. Run the **full** test suite for every affected project (per `CLAUDE.md`'s "Tests" section, e.g.
   `dotnet test source/PictTag.PgfCodec.Tests`, plus `PictTag.Data.Tests` if a facade changed) as a
   regression gate before moving to the next stage. Don't accumulate multiple stages' changes
   before running the full suite once — that's how a regression gets misattributed later.
6. When a stage says "wire this into real call site X if it's cheap and correct" (a common last
   implementation stage in these PRDs): actually read that call site's current code first. If it
   already has its own adequate mechanism (e.g. an outer loop already checks cancellation once per
   unit of work), adding the new lower-level hook on top is redundant, not "cheap and correct" —
   leave it unwired and say so plainly in the Progress log. Don't invent a speculative consumer just
   to prove the stage did something; "available but unused library surface" is a legitimate,
   PRD-anticipated outcome, not an incomplete job. Conversely, don't skip a call site that has a
   genuine gap just because wiring it takes a few more lines.
7. Once this stage's implementation is done and the full regression gate (step 5) is green, **commit
   this stage** — see "Commit after every stage" below. Do this before starting the next stage, not
   in one batch at the end.

## 3. When to keep going vs. stop and ask

Default to continuing through the full stage sequence without pausing for approval — these PRDs
are written to be executed, and re-confirming each stage adds friction without adding information.
Stop and use `AskUserQuestion` (or just flag it in text and wait) only when:

- **A stage's premise turns out to be factually wrong** once checked against the real code (a cited
  API doesn't exist, a formula doesn't match the current implementation) — this is a "verify, don't
  recall" moment, not a rubber-stamp.
- **The only remaining path is a substantial redesign** outside the PRD's own stated scope, not
  just "more work than expected" within it.
- **A genuine product/design decision has no answer derivable from the code, tests, or the PRD's own
  reasoning** — as opposed to an "Open question" the PRD itself frames as resolvable empirically
  (those, resolve yourself and document the reasoning; don't punt ordinary judgment calls upward).

Otherwise: implement, test, move to the next stage.

## 4. Documentation, once every stage is green

This is its own PRD stage in most of these documents ("Documentation") — treat it as mandatory, not
optional polish:

1. **The PRD file itself**: flip the top "Status" line (e.g. `**Status: not started.**` →
   `**Status: done, all N stages shipped**`, pointing at the Progress log). Fill in the
   **"Progress log"** section with one entry per stage: what was built, what was found (including
   anything the PRD got wrong that you corrected), what broke and how it was fixed, and real test
   counts (before/after). This is this repo's own established convention (see
   `managed-pgf-codec.md`'s Progress log for the reference shape) — it's the authoritative
   narrative other future work and PRDs link back to, not a changelog nobody reads.
2. **Every `docs/*.md` "current-state reference" page** the PRD is closing a gap in: move the
   relevant item between its "out of scope"/"supported" (or equivalent) lists, and fix any
   footer/summary text elsewhere on that page that counts remaining PRDs or lists this one as
   "planned" (grep for the PRD's filename across `docs/` — you already found these referrers in
   step 0.3).
3. **Never create a new standalone summary document.** The PRD file plus the existing `docs/*.md`
   pages are the destination; a fresh `SUMMARY.md` or similar is exactly the kind of unrequested
   artifact this repo's own conventions (and Claude Code's general ones) avoid.

## 5. Commit after every stage

Invoking this skill is itself the standing authorization to commit as you go — **don't wait for a
separate explicit "commit" request per stage.** Commit once a stage's implementation is done and its
regression gate (section 2, step 5) is green, before starting the next stage. This matches this
repo's own historical practice (`managed-pgf-codec.md`'s `git log`: "Stage 9: ...", "Stage 10: ...",
one commit per stage as it landed) and keeps every commit bisectable to one working, tested unit of
change instead of one large diff at the end.

- **Never add a Claude/Anthropic co-author line** — this repo's `CLAUDE.md` states this explicitly
  and it applies to every commit here, no exceptions.
- Commit subject: short, references the PRD and stage (`Stage N: <what that stage did>`, matching
  this repo's own `git log` style for these PRDs). Body: what changed and why, not a restatement of
  the diff.
- Stage the exact files that stage touched (new + modified) explicitly rather than `git add -A`, and
  review `git status`/`git diff --stat` before committing.
- The Documentation stage (section 4) gets its own commit too, once the PRD's Status line/Progress
  log and any cross-referenced `docs/*.md` pages are updated — it's the closing stage, not something
  folded silently into the last code commit.
- This per-stage cadence is specific to running this skill. It doesn't change this repo's general
  rule for everything outside `/implement-prd` — still confirm before committing unrelated work, and
  never force-push, amend a previous stage's commit, or touch history that predates this skill's own
  run.

## 6. Final report to the user

Keep it short: what was implemented (one line per stage or a tight summary), final test counts
across every affected project, which docs were updated, and — if the PRD's own "Open questions" or
sibling `new-features/*.md` PRDs point at follow-up work — name them so the user can decide what's
next. Don't re-paste the PRD's own content back at the user; they already have it.

## Reusable gotchas from prior PRD implementations

- **Testing `IProgress<T>` consumers**: don't use the real `System.Progress<T>` in a test that
  depends on report ordering/timing — it posts through `SynchronizationContext` (asynchronously, off
  the calling thread, when none is captured), which makes "cancel after the Nth report" tests racy.
  Write a trivial synchronous `IProgress<T>` test double instead.
- **Adding an optional capability to an existing public method**: prefer new trailing optional
  parameters with backward-compatible defaults directly on the existing signature over hand-written
  parallel overloads, when it avoids duplicating the method body — every existing call site still
  compiles and behaves unchanged either way, but only one of the two avoids drift between two copies
  of the same logic.
- **`dotnet test` filtering** in this repo is xUnit v3/Microsoft.Testing.Platform, not VSTest:
  `dotnet test <project> -- --filter-class "*.ClassName"`, never bare `--filter`.
