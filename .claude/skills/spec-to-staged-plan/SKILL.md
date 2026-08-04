---
name: spec-to-staged-plan
description: >-
  **METHODOLOGY SKILL** - Turn a rough spec/design drafted by another AI (or a human, from a
  design conversation elsewhere) into a verified, staged, implementable plan for this codebase.
  USE FOR: "here's what [other AI] suggested, make it implementation-ready", a pasted design doc
  or markdown spec that was written without real access to the repo, a rough idea from an
  external design conversation that needs turning into concrete stages before coding starts,
  any spec that invents API signatures, library behavior, or "there's no X available" claims
  without having read the actual source. DO NOT USE FOR: a spec already written against this
  codebase by someone who read the real code, a single small well-scoped change with one obvious
  implementation, greenfield design with no existing codebase to verify against.
license: MIT
metadata:
  author: user-derived
  version: "1.0.0"
---

# Spec-to-staged-plan

Use this when the input is an idea or design document produced *without* real access to this
codebase — another AI's suggestion, a design conversation from elsewhere, a rough sketch the user
wrote themselves. Such a spec is a **hypothesis about what would work**, not a design that's
already been checked against reality. Concrete-looking details in it (method signatures, "this
library already does X," "there's no better control available") are exactly as reliable as any
other unverified claim — which is to say, not reliable at all until checked. The job is to turn
that hypothesis into a plan whose every load-bearing technical claim has been verified against the
real source, staged so each piece is independently shippable, testable, and committable.

## 1. Research the real codebase before touching the spec's claims

Identify every subsystem the spec touches and build an accurate picture of how it *actually* works
today — not how the spec assumes it works. For a spec spanning multiple independent areas (e.g. a
server pipeline and a client pipeline), spawn parallel research agents, one per area, each with a
self-contained brief: what to find, which files to check, what questions to answer, explicitly
told this is read-only research. Run them in parallel (single message, multiple tool calls) and
let them complete before drafting anything.

What to extract per area:
- The actual current data flow / call path, with real file paths and line numbers.
- Anything the spec's motivating scenario depends on that might already exist under a different
  name, or might not exist at all.
- Existing test/fake patterns already in the repo, so new stages reuse them instead of inventing
  parallel infrastructure.
- Any prior design notes in the repo (docs, `new-features/*.md`, architecture comments) that
  already discuss this exact problem — a deferred decision, a documented constraint, a past
  rejection of an approach the spec is about to re-suggest.

## 2. Verify every load-bearing technical claim directly — don't trust the spec, don't trust memory either

Before a claim from the foreign spec (or your own recollection of a library/API) is allowed into
the plan, check it against the real thing:

- **A library or framework API the spec invents or assumes**: find the real installed
  version and inspect it. Don't guess type/method names — reflect on them. If a bare
  `Assembly.LoadFrom` on a standalone package DLL fails with missing-dependency errors (common for
  UI framework packages with many transitive references), load it from the **application's own
  build output directory** instead (e.g. `bin/Debug/.../App.dll`'s folder) — that directory has
  every transitive dependency already resolved side-by-side, unlike a bare NuGet cache path.
- **A vendored or third-party source library already in the repo**: read its actual header/source
  files for the exact method signatures, not the spec's paraphrase of them. A spec written without
  file access will confidently invent plausible-looking signatures that don't match — pure virtual
  methods left unoverridden, wrong parameter types, made-up overloads. These are exactly the bugs
  that only surface at compile time or, worse, as silent runtime corruption — catch them at
  planning time instead by reading the real header.
- **A "there's no X available" or "this always works this way" claim**: verify by checking the
  actual types/behavior (reflection, grep, reading the real implementation), not by trusting the
  spec's confidence or your own prior. A claim can be *wrong in a way that changes the whole
  design* (e.g. the spec assumes a resumable exception-based retry path exists in a decoder when
  the real library's exception handling is actually only used for unrecoverable errors elsewhere in
  the same file — discovered by reading real usage, not the header alone).
- **A safer/better primitive the spec didn't know about**: while verifying, look for real existing
  capabilities that solve the same problem more safely than the spec's invented mechanism (e.g. a
  library method that already exposes exact byte-length-per-chunk information, making a
  byte-threshold-gated design possible instead of the spec's unsafe "try it and catch the
  exception" guess). Finding one of these is usually the single highest-value output of this step.

## 3. Reconcile explicitly — name what's wrong and what replaces it

Write down, concretely: which specific claims in the foreign spec are wrong (cite the real
signature/behavior next to the spec's guess), why they're wrong (compile failure, unsafe
assumption, doesn't exist), and what verified real capability replaces each one. This becomes the
plan's justification for diverging from the input spec — the user asked for the *rough idea* to be
made implementable, not for the rough idea to be implemented as-is once it turns out to be unsafe
or infeasible.

Also separate the spec's actual goal from its motivating scenario: a spec that says "this matters
because we might have 50,000 of these someday" is asking you to make today's design not choke on
that scale later, not asking you to build the 50,000-item feature now. Keep scope tied to what
hurts *today* plus reasonable scale-proofing; don't let a future scenario balloon current scope.

## 4. Design the stages

Principles, in priority order:

1. **Sequence by risk and independence, not by the spec's narrative order.** Cheap, obviously-safe
   fixes found incidentally during research (a race condition, a missing cancellation, a dead code
   path) ship first, standalone — smaller diff, de-risks later stages' testing, doesn't wait on
   anything.
2. **Every stage is one commit with its own tests**, and should leave the app in a state no worse
   than before it. A stage that changes shared, already-in-use behavior (an API response format, a
   default control's behavior) needs an explicit non-breaking gate — a feature flag, content
   negotiation, an opt-in parameter nothing existing sends yet — specifically so it can land safely
   before its consumer exists, rather than requiring several stages to be merged atomically as one
   unreviewable unit.
3. **Name hidden couplings between stages explicitly.** A later stage's assumption (e.g. "the
   container showing this data item never changes under it") may be *newly untrue* once an earlier
   stage lands (e.g. once containers start getting recycled) — call out where a new interaction
   between two stages is genuinely untested territory in this codebase and needs its own regression
   test, rather than assuming a library/framework "just handles it."
4. **Reuse the repo's real existing test/fake patterns**, found in step 1 — name the specific files
   to extend, and flag where you're about to assume shared fixture infrastructure exists that
   turns out to only exist as a private per-file fake (verify this too, don't assume).
5. **Don't over-fragment.** A stage that's naturally one reviewable diff shouldn't be split just
   for stage-count's sake; a stage that bundles two unrelated fixes should be split.

## 5. Pressure-test before presenting

Hand the drafted stage list, plus every verified fact from steps 1-3 (file paths, real signatures,
what's confirmed vs. assumed), to a planning subagent whose job is to critique, not rediscover.
Explicitly tell it to trust the supplied facts and spend its effort on: sequencing errors, stages
too large for one commit, hidden cross-stage dependencies, under-specified "sounds right in
principle" design decisions (e.g. a selection heuristic that needs an explicit tie-breaking rule
before it's actually implementable), and gaps against the actual current scale/usage of the
feature being changed. Fold real findings back into the plan before presenting it.

## 6. Present, expect pushback, iterate

Present the plan (e.g. via this environment's plan-mode flow: a Context section explaining the
*why*, then the stage list, then a verification section) and treat pushback as a normal part of
the loop, not a failure of the plan. The user will often supply a concrete real-world scenario the
design didn't cover (e.g. "what happens when the user scrolls fast through the whole list, not
just scrolls-then-stops") — this is exactly the kind of gap that's cheap to fix at plan time and
expensive to fix after the code exists. Revise the affected stage(s) precisely (don't rewrite the
whole plan), re-explain what changed and why in the plan file itself, and re-present.

## The loop, end to end

1. Read the foreign spec once for its *intent*, not its details.
2. Parallel-research every codebase area it touches (real agents, real files, real line numbers).
3. Verify every concrete technical claim the spec makes against real source/reflection — expect to
   find some wrong, and look for real safer alternatives while you're in there.
4. Write down the reconciliation: spec claim → why it's wrong → verified replacement.
5. Draft stages: cheap/safe fixes first, non-breaking gates on anything that changes shared
   behavior, explicit call-outs for new cross-stage interactions, tests and commit boundaries per
   stage.
6. Pressure-test the draft with a planning subagent fed your verified facts, not asked to
   re-derive them.
7. Present via the plan-mode flow; when the user pushes back with a scenario you missed, fix that
   specific stage and re-present rather than starting over.
