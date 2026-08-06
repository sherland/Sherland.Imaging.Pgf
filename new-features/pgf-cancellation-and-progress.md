# PGF codec: cancellation + progress reporting — PRD

**Status: done, all 5 stages shipped** (see "Progress log" below for the full record). Closed a gap
documented in [`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md), which now lists this under "Supported"
instead of "Explicitly out of scope".

## Context

The native `libpgf` codec's `Read`/`Write`/`GetBitmap`/`RgbToYuv`/`ImportBitmap`/`ImportYUV` all
accept an optional `CallbackPtr cb` + `void *data` pair, invoked periodically with a completion
fraction; returning `true` from the callback requests early abort. `Sherland.Imaging.Pgf` — this
codebase's from-scratch managed port of the same codec (see
[`new-features/managed-pgf-codec.md`](managed-pgf-codec.md) for the full port history) — has no
equivalent at all. `PgfImageDecoder.TryDecode`/`PgfImageEncoder.TryEncode`/`PgfProgressiveDecoder.
TryDecodeLevel` run to completion or fail, with no way for a caller to observe progress or ask for
early termination mid-operation.

**Being honest about how much this matters today**: at this app's actual scale (digiKam
thumbnails, ~128-512px), Stage 10 of `managed-pgf-codec.md` measured full single-shot decode/encode
at roughly 0.3-11ms depending on size/quality — short enough that neither progress reporting nor
cancellation has an obvious user-visible payoff for *today's* real call sites
(`PictTag.Api.Thumbnails.ThumbnailService`, `DesktopProgressiveBitmapLoader`/
`BrowserProgressiveBitmapLoader`, all of which already get a natural, coarser-grained cancellation
point for free — the progressive decode loop calls `TryDecodeLevel` once per level and can simply
stop calling it). This PRD is motivated by making `Sherland.Imaging.Pgf` a complete, idiomatic library
rather than by an urgent product need — the same category of "close a documented, deliberate gap"
motivation as `managed-pgf-codec.md`'s own Stage 1 (test/oracle infrastructure) rather than a
user-facing bug fix. The real payoff is a single-shot `TryDecode`/`TryEncode` call that currently has
**no internal break point at all** once started, unlike the progressive API's own natural one — and
a hedge against a future, larger, non-thumbnail use of this library where a multi-level operation
could plausibly run long enough to matter (a slower device, a much larger source image, or the
browser's Mono interpreter, confirmed meaningfully slower than Desktop CoreCLR in Stage 10's own
numbers).

## Why this needs to be grounded in the real algorithm

- **`CallbackPtr`** (`PGFplatform.h:181,426`): `typedef bool (*CallbackPtr)(double percent, bool
  escapeAllowed, void *data)`. Returning `true` requests abort.
- **Abort mechanism**: `#define EscapePressed 0x2003 // user break by ESC` (`PGFplatform.h:522`,
  the non-big-endian branch actually used here), and `#define ReturnWithError(err) throw
  IOException(err)` (`PGFplatform.h:500`) — so requesting abort throws `IOException(EscapePressed)`,
  which propagates up through the call stack uncaught until `shim.cpp`'s outer `catch (...)`
  boundary, which returns `false` with no distinction from any other error. There is no dedicated
  "cancelled, not failed" signal in the native API's own return shape.
- **Where the callback fires, and the percent formula** (`PGFimage.cpp`):
  - `Read(level, cb, data)`'s per-level loop (`PGFimage.cpp:415-476`): fires once per level actually
    decoded (`while (m_currentLevel > level)`), `percent *= 4` each iteration (each level covers 4x
    the previous level's linear coverage in the wavelet pyramid) starting from
    `pow(0.25, levelDiff)` in the default `PM_Relative` mode, or from the running `m_percent` in
    `PM_Absolute` mode.
  - `CPGFImage::WriteImage`'s per-level loop (`PGFimage.cpp:1176-1194`) mirrors this exactly on
    encode, via `WriteLevel()`.
  - `GetBitmap`/`RgbToYuv` and their per-mode branches (`PGFimage.cpp`, e.g. lines 1843-1886,
    2206-2271) fire once per copied/converted image *row*, via `dP = 1.0/h` accumulated per row —
    a much finer grain than the per-level callback, and one this PRD should confirm is actually
    worth matching (see "Open questions").
  - `ImportBitmap`/`ImportYUV`'s color-conversion loops mirror the `GetBitmap`/`RgbToYuv` per-row
    cadence on the encode side.
- **`ProgressMode`** (`PGFimage.h:292-296`): `PM_Relative` (default) treats each `Read`/`Write` call
  as its own 0-100% span (only meaningful for *that* call's own level range); `PM_Absolute` treats
  the callback's percent as a share of the *whole image's* total levels, so a partial progressive
  call reports proportionally less than 100%. This distinction exists because the native API serves
  callers doing arbitrary partial reads across many separate calls on the same handle — this port's
  own `PgfProgressiveDecoder` already has exactly that shape, so which semantic (if either, unmodified)
  actually fits this port's callers needs a real decision, not an assumption (see "Open questions").

## Goals

1. `PgfImageDecoder.TryDecode`/`PgfImageEncoder.TryEncode` gain a way to observe progress
   (`IProgress<double>`, reporting a monotonically non-decreasing fraction in `[0, 1]` — the
   idiomatic .NET progress-reporting primitive, not a raw callback delegate) and to request
   cooperative cancellation (`CancellationToken` — the idiomatic .NET primitive, not a
   bool-returning callback).
2. `PgfProgressiveDecoder.TryDecodeLevel` gains the same, for symmetry and for the (currently
   thin) case where a single level's own decode work is long enough to want mid-level abort.
3. Cancellation is checked **between levels**, not mid-row/mid-macroblock — matching the grain the
   real percent formula above is already built around, and matching this app's own real timing
   profile (per-level work is the smallest unit worth interrupting between; per-row/per-macroblock
   granularity would add real overhead — an `IsCancellationRequested` check, however cheap, is not
   free at a scale of thousands of calls per decode — for a benefit no real caller has asked for).
4. A cancelled operation surfaces as `OperationCanceledException`, matching every other
   `CancellationToken`-consuming .NET API's own convention — a deliberate, documented departure from
   this codec's usual "fail closed, return `false`, never throw for an anticipated condition"
   philosophy (Tier 5 in `managed-pgf-codec.md`), because cancellation is a caller-*requested*
   outcome a caller reasonably expects to catch by exception type, not a malformed-input failure
   mode indistinguishable from any other `false`.
5. Wire the new capability through to real call sites where it's cheap and correct to do so
   (`PictTag.Data.PgfDecoding.PgfDecoder`'s facade, at minimum) without inventing a use for it that
   doesn't exist yet in the UI layer.

## Non-goals

- **Per-row/per-pixel progress granularity matching `GetBitmap`/`RgbToYuv`'s native cadence exactly**
  — see Goal 3's reasoning. If a future need for finer-grained progress emerges, revisit; don't build
  it speculatively.
- **A dedicated "was cancelled" vs. "genuinely failed" distinction in the `TryDecode`/`TryEncode`
  boolean-return shape.** `OperationCanceledException` already is that distinction, via exception
  type — don't also thread a redundant enum/flag through the `out result` shape.
- **Replicating `ProgressMode`'s `PM_Relative`/`PM_Absolute` split as public API surface** unless
  Stage 1's investigation finds a real reason this port's callers need both. Default to whichever one
  turns out simpler and matches this port's actual callers' expectations (see "Open questions") —
  don't expose a knob nobody has asked for.
- **Encode progress/cancellation mattering for a real production caller.** `PgfImageEncoder` has no
  production call site today (`managed-pgf-codec.md`'s own Non-goals) — this PRD ports the
  capability for completeness and symmetry with decode, not because an encode caller is waiting on
  it.

## Proposed architecture

- New shared type (or reuse of the existing `PgfDecodeSession`/orchestration layer): thread an
  optional `IProgress<double>?` and `CancellationToken` through `PgfDecodeSession.DecodeOneLevel`'s
  caller loop (`PgfImageDecoder.TryDecode`'s level loop, `PgfProgressiveDecoder.TryDecodeLevel`'s
  `while (currentLevel > level)` loop) and the mirror-image encode loop in `PgfImageEncoder.
  TryEncode`. Check `cancellationToken.ThrowIfCancellationRequested()` at the top of each level
  iteration (before doing that level's work — an already-decoded level shouldn't be thrown away by a
  cancellation that arrives just as it finishes); call `progress?.Report(...)` after each level
  completes.
- Progress fraction: default to something like `(totalLevels - currentLevel) / (double)totalLevels`
  for single-shot `TryDecode`/`TryEncode` (a plain, always-meaningful 0→1 sweep over the whole
  operation) — confirm during Stage 1 whether this or the native's own `pow(0.25, levelDiff)`-based
  curve (which front-loads perceived progress toward the coarser, cheaper levels) is the more useful
  shape for a caller, rather than assuming either.
- Overload strategy: add new overloads taking `IProgress<double>?`/`CancellationToken` with default
  values (`null`/`CancellationToken.None`) alongside the existing methods, so every current call site
  (`PictTag.Data.PgfDecoding.PgfDecoder`, `BrowserProgressiveBitmapLoader`) keeps compiling unchanged
  and opts in only if it chooses to.
- `PgfMacroBlock`/`PgfEncodeMacroBlock`/`PgfSubband`/`PgfWaveletTransform` are untouched — this
  feature lives entirely in the orchestration layer (`PgfDecodeSession`, `PgfImageDecoder`,
  `PgfImageEncoder`, `PgfProgressiveDecoder`), not the entropy coder or wavelet transform, since the
  native callback never fires at a finer grain than "one level" in the paths this port actually
  exercises.

## Test rig

- **Progress reporting**: a fake `IProgress<double>` recording every reported value; assert the
  sequence is non-decreasing, ends at (or converges to) `1.0`, and reports exactly once per level
  actually decoded/encoded (count matches `Levels`/`NLevels`) for a range of fixture sizes/level
  counts, mirroring `managed-pgf-codec.md`'s existing `TestBitmaps`/`EdgeCaseDimensions` fixtures.
- **Cancellation**:
  - A pre-cancelled token (`new CancellationToken(true)`) passed to `TryDecode`/`TryEncode`/
    `TryDecodeLevel` throws `OperationCanceledException` before any level's work happens (assert via
    a counting `IProgress<double>` that saw zero reports).
  - A token cancelled *during* a multi-level operation (e.g. via a custom `IProgress<double>` that
    cancels a linked `CancellationTokenSource` after the Nth report) throws after completing exactly
    the levels already in flight, not mid-level — assert the exact level count completed matches
    expectation for a few different cancel-after-N values.
  - Cancellation must never corrupt or leave partially-consumed shared state that would break a
    *subsequent*, unrelated `TryDecode` call on the same input (a fresh call, not resuming the
    cancelled one) — the existing `PgfDecodeSession`/`PgfDecoderCore` lifecycle is already
    call-scoped for single-shot decode, so this should hold by construction; assert it directly
    anyway rather than assume.
- **Regression**: the full existing 567-test `Sherland.Imaging.Pgf.Tests` suite must stay green
  throughout — the new optional parameters must not change any existing behavior when unused.

## Stage sequence

1. **Confirm the real percent-curve/`ProgressMode` question empirically** (see "Open questions")
   before committing to a shape — read `WriteImage`'s and `Read`'s exact formulas fresh (cited
   above), decide `PM_Relative`-equivalent vs. `PM_Absolute`-equivalent vs. a simpler linear sweep,
   and document the decision and why here in the Progress log.
2. **Thread `IProgress<double>?`/`CancellationToken` through `PgfImageDecoder.TryDecode` and
   `PgfProgressiveDecoder.TryDecodeLevel`** (decode first — the more real-world-relevant direction,
   matching `managed-pgf-codec.md`'s own "prioritize decode if time-boxing is needed" precedent).
   Exit test: the cancellation/progress test rig above, decode side only.
3. **Same for `PgfImageEncoder.TryEncode`.** Exit test: same rig, encode side.
4. **Wire `PictTag.Data.PgfDecoding.PgfDecoder`'s facade** to accept and pass through the new
   parameters (new overloads, matching the mechanical-facade-swap precedent from
   `managed-pgf-codec.md`'s Stage 12) — confirm whether `PictTag.Api.Thumbnails.ThumbnailService` or
   either UI loader has any real reason to consume it yet, or whether this stays available-but-unused
   library surface for now (that's fine — matches this port's own "library first" framing).
5. **Documentation**: update `docs/PGF-CODEC.md`'s "Explicitly out of scope" list (move this item to
   "Supported"), and this PRD's own Progress log.

## Acceptance criteria / Definition of Done

- `PgfImageDecoder.TryDecode`, `PgfImageEncoder.TryEncode`, and `PgfProgressiveDecoder.
  TryDecodeLevel` all accept optional `IProgress<double>?`/`CancellationToken` parameters with
  backward-compatible defaults.
- Progress reports are monotonically non-decreasing and reach `1.0` on a completed operation, for
  every fixture/level-count combination tested.
- Cancellation throws `OperationCanceledException` promptly (within one level's worth of work) and
  leaves no corrupted shared state affecting subsequent unrelated calls.
- The full existing `Sherland.Imaging.Pgf.Tests` suite (567 tests as of this PRD's writing) stays green
  with the new parameters unused at their default values.
- `docs/PGF-CODEC.md` updated to move this item from "out of scope" to "supported."

## Open questions

- **`PM_Relative` vs. `PM_Absolute` vs. a plain linear sweep** — which progress-curve shape actually
  serves this port's real callers best is not obvious from the native API's own two-mode split (built
  for a different, more general calling pattern). Resolve empirically in Stage 1, not by assumption.
- **Does per-row cancellation/progress inside color conversion ever turn out to matter** — e.g. if
  this library is ever used for much larger (non-thumbnail) images where a single level's color
  conversion pass is no longer negligible next to the level-granularity checks. Revisit if that need
  materializes; the "Non-goals" section's reasoning for skipping it now is based on today's real
  scale, not a permanent architectural limit.
- **Whether any real UI call site actually wants this yet.** If Stage 4 finds no real caller with an
  actual use for progress/cancellation today, that's fine — leave the capability as tested,
  documented library surface rather than force a speculative consumer into the UI layer.

## Progress log

**Stage 1 — done.** Resolved both open questions before writing any of the threading code:

- **Progress curve: area-weighted, mirroring the native's `pow(0.25, levelDiff)` shape, not a plain
  per-level-count linear sweep.** A count-based fraction (`levelsCompleted / totalLevels`) would
  misreport "almost done" after finishing only the cheap, coarse levels — in a 2D wavelet pyramid
  each level covers 4x the previous level's linear coverage, so the *last* level decoded (finest,
  full resolution) dominates the real work, not the level count. New internal
  `PgfProgressCurve.FractionAfter(levelsCompleted, levelsInThisCall)` computes
  `(4^levelsCompleted - 1) / (4^levelsInThisCall - 1)` — monotonically increasing, reaches exactly
  `1.0` when `levelsCompleted == levelsInThisCall`, and doesn't need to bit-match the native formula
  (this port's own "decode-correctness, not bitstream-identity" bar, `managed-pgf-codec.md`) to be a
  meaningfully more honest signal than linear-by-count.
- **`PM_Relative`-equivalent (per-call sweep), not `PM_Absolute` (whole-image share), and not exposed
  as public API** — matches the Non-goals' instruction to default to whichever is simpler. For
  `TryDecode`/`TryEncode` (always the full level range) the two coincide anyway; it only matters for
  `TryDecodeLevel`'s partial-range calls, where per-call is simpler (no cross-call state to track) and
  already matches the native's own default mode.

**Stage 2 — done.** `PgfImageDecoder.TryDecode<TResult>` and `PgfProgressiveDecoder.
TryDecodeLevel<TResult>` both gained `IProgress<double>? progress = null, CancellationToken
cancellationToken = default` as trailing optional parameters (not separate overloads — optional
parameters on the existing signature already keep every current call site compiling and behaving
unchanged, with no logic duplication). `cancellationToken.ThrowIfCancellationRequested()` runs at the
top of each level-loop iteration, before that level's `DecodeOneLevel` call; `progress?.Report(...)`
runs after it completes. `PgfDecodeSession.DecodeOneLevel` itself is untouched, exactly as the
architecture note specified — both loops live entirely in the caller. 43 new tests in the new
`PgfProgressAndCancellationTests.cs` (monotonic-non-decreasing-ends-at-1.0 across every
`TestBitmaps.EdgeCaseDimensions()`/quality combination, pre-cancelled-token, cancel-mid-operation at
both an early and a late level with an exact-count assertion, same-level re-request reporting zero
times, and a fresh-unrelated-call-after-cancellation check). Full suite: 610/610 green (567 + 43).
Nothing broke — no surprises here, the existing loop shapes already matched the architecture note
exactly.

**Stage 3 — done.** Same treatment for `PgfImageEncoder.TryEncode`, instrumented at the same grain as
decode: once per level in the entropy-encode loop (`WriteImage`'s mirror), not in the earlier
forward-transform pass (which is where the native callback never fires, per the PRD's own "Where the
callback fires" citations). 21 more tests added to the same test file (encode-side progress/
cancellation, using this port's own encoder — Stage 8 of `managed-pgf-codec.md` — to produce fixtures
rather than the native oracle, since `PgfImageEncoder` has no cross-implementation correctness claim
to prove here). Full suite: 631/631 green (567 + 64 in the new file).

**Stage 4 — done.** `PictTag.Data.PgfDecoding.PgfDecoder.TryDecode` and `ProgressivePgfDecoder.
TryDecodeLevel` both gained the same trailing optional parameters, passed straight through to
`Sherland.Imaging.Pgf`. Checked both real UI call sites before wiring anything further:
`DesktopProgressiveBitmapLoader.DecodeProgressivePgf` and `BrowserProgressiveBitmapLoader.
DecodeProgressivePgf` **already** call `cancellationToken.ThrowIfCancellationRequested()` once per
level in their own outer loop, around each `TryDecodeLevel` call — exactly the natural,
coarser-grained cancellation point this PRD's Context section predicted they'd have "for free".
Wiring the new inner per-level token into these call sites on top of that would be redundant, not
"cheap and correct," so neither loader was changed. `PictTag.Api.Thumbnails.ThumbnailService` has no
`CancellationToken` threaded into its decode call at all today and no real product need for one
(sub-11ms decodes, per this PRD's own Context section) — left unwired. This capability is therefore
shipped as tested, documented, available-but-unused library surface at the facade layer, exactly the
outcome the PRD's own "Open questions"/Stage 4 description called a legitimate result.
`PictTag.Data.Tests` (the facade's pre-existing regression suite): 15/15 still green, unchanged.
Full solution build (`PictTag.slnx`, including the Browser/WASM head) confirmed clean.

**Stage 5 — done.** `docs/PGF-CODEC.md`: moved this item from "Explicitly out of scope" to
"Supported" with a summary of the shape and the Stage 4 decision to leave UI call sites unwired;
updated the "if a real need shows up" footer's PRD count from four to three. This PRD's own Status
line and this Progress log updated to match.

**Final state**: `Sherland.Imaging.Pgf.Tests` — 631/631 passing (567 original + 64 new). Full solution
build green. No production call site's behavior changed (every new parameter is optional and unused
at its default), matching the Acceptance Criteria's explicit bar.
