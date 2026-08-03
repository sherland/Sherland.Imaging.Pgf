# PGF codec: real per-level byte lengths on encode — PRD

**Status: done, all 6 stages shipped.** Closed the gap documented in
[`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s "Not yet ported — real gaps against full C++ parity"
list ("Real per-level byte lengths on encode") — moved to "Supported". See the "Progress log" section
below for the full per-stage record.

## Context

`PictTag.PgfCodec` is planned to be published as a standalone NuGet package (see
[`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s own opening note) — a real, stated goal that changes
the bar for what counts as in-scope. The native encoder writes a real per-level byte-length table
into every PGF file's post-header area: a zero placeholder written first (so the table's own size is
already accounted for in the stream layout), patched with real accumulated values once encoding
finishes. This port's own encoder (`PgfImageEncoder.cs:15-25`'s own doc comment) always writes the
placeholder and never patches it — a deliberate scope cut, since nothing in this app's own decode
path (native or managed) ever reads level lengths back. That reasoning no longer justifies leaving
the *value* wrong, even if nothing internal reads it — a general NuGet consumer building their own
tooling against this format's real, documented structure could reasonably expect a value the format
itself defines to be correct.

**A real finding, already narrower than the doc bullet implies**: the *decode* side of this port
already correctly parses the level-length array off the wire — `PgfHeaderIO.Read`
(`PgfHeader.cs:53-211`) reads it into a real `uint[] levelLengths` return value (`PgfHeader.cs:118,
198-211`), because it has to skip past that section correctly to reach whatever follows regardless of
whether the values are ever used. `PgfDecodeSession.TryOpen` currently discards this return value
(destructured as `_`). So the real gap is almost entirely on the **encode** side (writing real
values instead of a permanent zero placeholder); decode only needs a thin "expose what's already
parsed" step, not new parsing logic or a new native oracle to prove correctness against.

## Why this needs to be grounded

- **`CEncoder::WriteLevelLength`** (`Encoder.cpp:177-196`): allocates a fresh, zeroed
  `UINT32[m_nLevels]`, assigns it to `m_levelLength`, saves the current stream position as
  `m_levelLengthPos` (the placeholder's own location, to seek back to later), writes
  `m_nLevels*WordBytes` zero bytes, then calls `SetBufferStartPos()` — the first real macroblock's
  byte-accounting baseline starts immediately *after* this placeholder table, not at file start.
- **`CEncoder::WriteMacroBlock`**'s level-length accounting (`Encoder.cpp:454-462`), gated on
  `if (m_levelLength)`: `m_levelLength[m_currLevelIndex] += (UINT32)ComputeBufferLength();` then
  `m_currLevelIndex = block->m_lastLevelIndex + 1;` — every real macroblock write credits its own
  on-disk byte count (`ComputeBufferLength()`, `Encoder.h:181`, `stream position - m_bufferStartPos`)
  to the level index *in effect when that block started*, then the index advances for the next block.
  `SetBufferStartPos()` runs again immediately after (`Encoder.cpp:465`) to reset the baseline for the
  next macroblock.
- **`CEncoder::UpdateLevelLength`** (`Encoder.cpp:202-234`): seeks back to `m_levelLengthPos`, writes
  `m_currLevelIndex*WordBytes` bytes from the now-populated `m_levelLength[]` (by the normal end of a
  `WriteImage` call, `m_currLevelIndex` has reached `m_nLevels`, so every entry gets patched), then
  restores the stream position to where it was before the seek.
- **A real, non-obvious dependency this PRD's own predecessor (`pgf-roi-support.md`) already found
  half of, but not all of**: `m_currLevelIndex` only ever advances via `block->m_lastLevelIndex`,
  which is only ever set by **`CEncoder::SetEncodedLevel`** (`Encoder.h:164`:
  `m_currentBlock->m_lastLevelIndex = m_nLevels - currentLevel - 1; m_forceWriting = true;`), called
  from `CPGFImage::WriteLevel` at `PGFimage.cpp:1117` (non-ROI) and `PGFimage.cpp:1095` (ROI, on the
  last channel's last tile, before that tile's final flush). The ROI PRD's own Stage 4 correctly found
  `SetEncodedLevel` itself unnecessary to port — but *only* because `m_forceWriting` is dead in this
  port's always-single-macroblock build (`pgf-roi-support.md`'s Stage 4 Progress log entry). **The
  `m_lastLevelIndex`/`m_currLevelIndex` bookkeeping is a separate effect of the exact same native
  call, needed here even though it wasn't needed there**: without an equivalent "advance to the next
  level's index" hook at the same call sites, `PgfEncoderCore.WriteMacroBlock`'s own
  `m_currLevelIndex` would stay at its initial value forever, and every byte written across the whole
  file would land in `levelLength[0]`. This PRD needs its own hook; it should not be assumed the ROI
  PRD's decision to skip `SetEncodedLevel` entirely extends to this concern.
- **`CPGFImage::UpdatePostHeaderSize`** (`PGFimage.cpp:1123-1136`) — confirmed to be **two
  independent** seek-and-patch operations, not one two-pass write: an `hSize` pre-header patch (only
  when `ComputeOffset() > 0`, which only fires when user data was written *after* `WriteHeader()` in
  the "uncached" pattern — this port's own `PgfHeaderIO.Write` always writes user data inline during
  the single header write, `PgfHeader.cs:214-228`, so this branch is a no-op for this port's real
  call shape) and, separately, the `WriteLevelLength` placeholder write itself. This port's own
  `PgfHeader.Write` doesn't need the `hSize`-patch logic at all — only the level-length placeholder
  and its later patch.
- **`PgfByteWriter` already supports seek-and-patch** — no writer-capability gap exists.
  `source/PictTag.PgfCodec/PgfByteWriter.cs` wraps a real `MemoryStream` with `SetPos(SeekOrigin,
  long)` (explicitly documented as existing for exactly this: seek back, write, resume forward,
  preserving `MemoryStream`'s past-end-seek semantics). The only missing piece is caller-side
  accounting (the bullet above), not writer plumbing.
- **Decode-side public accessor already exists in the native reference**: `CPGFImage::
  GetEncodedLevelLength(int level)` is public (`PGFimage.h:367`:
  `return m_levelLength[m_header.nLevels - level - 1];`), and `m_levelLength` is populated
  **unconditionally on every real `Open()`**, not lazily — `CDecoder`'s constructor
  (`Decoder.cpp:188-207`) allocates and reads the table whenever `preHeader.version > 0`, a sibling
  block to (not nested inside) the post-header-read block. `Decoder.cpp`'s own "level length
  information is optional" comment refers to *consumers* being free to ignore it, not to the read
  itself being conditional. This confirms `GetEncodedLevelLength` would return real, correct data
  today if called — a working, already-populated oracle to test a new C# decode-side accessor
  against, needing no new native shim plumbing to *read* correctly (only to *report back* for test
  comparison — see Test rig).
- **Existing C# doc comments, confirmed verbatim, both needing rewriting once this scope cut
  reverses**: `PgfImageEncoder.cs:15-25` ("Level-length bytes are always written as zero
  placeholders, never patched with real values... a deliberate scope cut, not an oversight"...) and
  `PgfHeader.cs:220-223` (same framing on the write side). `PgfEncoderCore.cs` has **zero** existing
  infrastructure for this today (no `m_levelLength`/`m_currLevelIndex`/`m_bufferStartPos`/
  `ComputeBufferLength`-equivalent fields anywhere) — this is a from-scratch addition to that class,
  not a small patch. `PgfImageEncoder.cs`'s own ROI-branch comment (`pgf-roi-support.md`'s own
  addition) already explicitly calls out that it "never tracks" this bookkeeping, corroborating.

## Goals

1. **Encode**: `PgfEncoderCore` tracks real per-macroblock byte accounting
   (`m_levelLength`/`m_currLevelIndex`/`m_bufferStartPos`/`ComputeBufferLength` equivalents) and
   `PgfImageEncoder` writes a real, patched-in level-length table instead of a permanent zero
   placeholder — for both the plain and ROI-flagged encode paths (the `m_currLevelIndex`-advancement
   hook described above is needed at both call sites `CPGFImage::WriteLevel` uses it from, matching
   `pgf-roi-support.md`'s own non-ROI/ROI branch split in `PgfImageEncoder.cs`).
2. **Decode**: expose the level-length values `PgfHeaderIO.Read` already correctly parses (currently
   discarded) via a small new public accessor (e.g. `PgfProgressiveDecoder.TryGetLevelLength(int
   level, out uint length)`, matching this port's own `TryGetLevelSize`-style API shape) — a thin
   plumbing change, not new parsing logic.
3. Verify round-trip correctness against the real native reference, using
   `CPGFImage::GetEncodedLevelLength`'s already-working, already-populated oracle (see Test rig) —
   the strongest verification shape this project's own PRDs use when a native oracle is genuinely
   available (unlike `pgf-legacy-interleaved-decode.md`'s own, weaker self-consistency-only
   situation).

## Non-goals

- **The `hSize` pre-header patch branch of `UpdatePostHeaderSize`.** Confirmed a no-op for this
  port's own call shape (user data is always written inline during the single header write, never
  after) — porting it would be speculative work for a call pattern this port's own `PgfHeaderIO.Write`
  doesn't have and has no reason to grow.
- **Any change to what level lengths mean or how they're computed** — this PRD makes the existing,
  already-correct-when-read-by-native placeholder mechanism produce real values; it doesn't change
  the file format, the wire layout, or introduce any new concept.
- **A production consumer of the new decode-side accessor inside this app.** Matches this codebase's
  own honest "for completeness, not a current internal need" framing used by `pgf-roi-support.md` and
  `pgf-cancellation-and-progress.md` for their own new API surface — no UI/service call site in this
  app has ever needed level lengths, and none is expected to; this is purely closing a public-API
  parity gap for a general NuGet consumer.

## Proposed architecture

- **`PgfEncoderCore`**: add `uint[]? levelLength`, `int currLevelIndex`, `long bufferStartPos` fields
  (mirroring the native names exactly, for direct traceability). A new `SetBufferStartPos()` (mirrors
  `CEncoder::SetBufferStartPos`, `writer.Position`) and `ComputeBufferLength()` (mirrors
  `CEncoder::ComputeBufferLength`, `writer.Position - bufferStartPos`) — check `PgfByteWriter`'s
  current API for whether `Position`/`Length` are already exposed (used elsewhere in this port) or
  need adding. `WriteMacroBlock` gains the same `if (levelLength != null) { ... }` accounting block,
  called immediately after the existing wordLen/data write, mirroring `Encoder.cpp:454-462` exactly.
  A new `AdvanceLevel(int currentLevel)` (or similarly named) method — the narrow piece of
  `SetEncodedLevel` this PRD actually needs (see "Why this needs to be grounded"'s dependency
  finding) — sets `currLevelIndex` for the *next* macroblock's accounting, called from
  `PgfImageEncoder` at the same two points `CPGFImage::WriteLevel` calls the real `SetEncodedLevel`
  from (the non-ROI per-level loop's own end, and the ROI branch's last-tile-of-last-channel point,
  `pgf-roi-support.md`'s own two `PgfImageEncoder.cs` branches).
- **`PgfImageEncoder`**: before the per-level encode loop starts, allocate a zeroed
  `uint[header.NLevels]`, write it as a placeholder (mirroring `WriteLevelLength`'s own write, at the
  same point `PgfHeaderIO.Write`'s own placeholder write already happens — verify during
  implementation whether this PRD's placeholder write *replaces* or *coexists with*
  `PgfHeaderIO.Write`'s existing zero-write loop, since that loop already exists and currently *is*
  the permanent placeholder; likely this PRD just needs to seek back and patch it after encoding,
  reusing the exact byte range `PgfHeaderIO.Write` already reserved, not add a second write). After
  the full per-level loop and `encoder.Flush()`, seek back (`PgfByteWriter.SetPos`, already
  confirmed capable) and write the real accumulated values — mirroring `UpdateLevelLength`'s
  seek-write-restore sequence exactly.
- **`PgfDecodeSession`/`PgfProgressiveDecoder`**: capture `PgfHeaderIO.Read`'s already-parsed
  `LevelLengths` return value (currently discarded) as a new property, and add the small public
  accessor described in Goal 2.

## Test rig

- **The native oracle already works — extend the shim to report it, not to compute it differently.**
  `shim.cpp`'s existing `pgf_debug_get_header_info` (`shim.cpp:602-634`) opens a file and reports
  header fields but not level lengths; extend it (or add a new export) with an
  `outLevelLengths`/`levelLengthCount` out-array, looping `img.GetEncodedLevelLength(i)` for
  `i in [0, img.Levels())` — following this file's own established `PICTTAG_EXPORT`/try-catch-bool
  pattern, and citing the owning PRD by filename the way `pgf_encode_bgra_alloc_roi`'s own comment
  does for `pgf-roi-support.md`.
- **Cross-implementation encode leg**: encode the same test image (`TestBitmaps.Gradient`, matching
  this test rig's established convention, across `TestBitmaps.EdgeCaseDimensions()` and a spread of
  quality values) via both the native encoder (`pgf_encode_bgra_alloc`, already existing) and the
  newly-fixed C# encoder; compare each level's byte length reported by the new shim export against
  each level's byte length the C# encoder actually wrote. These won't necessarily be numerically
  *identical* between implementations (different entropy-coding implementations of the same
  algorithm could in principle produce different exact byte counts per level, though this port's own
  established byte-exact-round-trip proof for the base codec suggests they likely will match exactly
  in practice) — the real correctness bar is: each implementation's own reported level lengths sum to
  that implementation's own total encoded size, and (for the C# encoder specifically) the values
  match what was *actually* written at each level boundary, verified directly by re-parsing the C#
  encoder's own output through the new decode-side accessor (Goal 2) and confirming self-consistency
  first, then cross-checking against native's reported values for the *same* input as a secondary,
  not-necessarily-bit-identical sanity check.
- **Decode-side accessor test**: open a real native-encoded file (any existing fixture) through
  `PgfProgressiveDecoder`, read back level lengths via the new accessor, and compare directly against
  the new shim export's `GetEncodedLevelLength`-backed report for the *same* file — this leg has a
  genuine, already-working independent oracle (per the Context finding above), unlike
  `pgf-legacy-interleaved-decode.md`'s self-consistency-only situation.
- **Regression**: the full existing `PictTag.PgfCodec.Tests` suite must stay green throughout,
  especially confirming the placeholder-write byte range/stream layout doesn't shift for any existing
  fixture (a level-length table sized wrong by even one entry would corrupt every subsequent byte
  offset in the file).

## Stage sequence

1. **`PgfEncoderCore` accounting fields + `AdvanceLevel` hook**, wired into `PgfImageEncoder`'s
   existing non-ROI and ROI per-level loops. Exit test: full existing regression suite stays green
   (the accounting runs but isn't patched into the output yet — still zero placeholders on disk).
2. **Patch real values into the output** (`PgfImageEncoder`'s post-encode seek-and-patch, mirroring
   `UpdateLevelLength`). Exit test: self-consistency — decode the C# encoder's own output through a
   new decode-side accessor (Stage 3) and confirm the values match what was actually accumulated.
3. **Decode-side accessor** (`PgfDecodeSession`/`PgfProgressiveDecoder`, Goal 2) — capture the
   already-parsed `LevelLengths` and expose them publicly.
4. **Native shim extension**: the `GetEncodedLevelLength`-reporting export, enabling the real
   cross-implementation legs.
5. **Full round-trip matrix**: the Test rig's cross-implementation and decode-accessor legs, across
   fixture sizes/quality levels.
6. **Documentation**: `docs/PGF-CODEC.md`'s "Not yet ported" list updated; this PRD's own Progress
   log filled in; `PgfImageEncoder.cs`/`PgfHeader.cs`'s stale "always zero placeholders" doc comments
   rewritten to match the new real behavior.

## Acceptance criteria / Definition of Done

- Real, non-zero, correct per-level byte lengths are written by the C# encoder for both the plain and
  ROI-flagged paths, verified self-consistently (encode → new decode accessor → matches actual
  accumulated bytes) and cross-checked against the native reference's own already-working
  `GetEncodedLevelLength` via the new shim export.
- A new public decode-side accessor exposes level lengths the port already parses, tested against a
  real native-encoded file's own native-reported values.
- Zero regression on the existing test suite — especially proving the on-disk byte layout for every
  existing fixture is unaffected (same total file size, same data after the level-length table) aside
  from the (previously-zero, now-real) level-length values themselves.
- `docs/PGF-CODEC.md` updated to move this item from "Not yet ported" to "Supported."

## Open questions

- **Whether the placeholder write this PRD needs is a genuinely separate write from
  `PgfHeaderIO.Write`'s existing zero-fill loop, or whether this PRD should just seek into that
  already-reserved byte range and patch it** — resolve during Stage 1/2 by reading `PgfHeaderIO.Write`
  (`PgfHeader.cs:264-269`)'s exact current zero-write loop first; very likely the latter (reuse, don't
  duplicate), but verify against the real current code rather than assume.
- **Whether cross-implementation level-length values need to match native byte-for-byte, or only
  "each implementation's own values are internally self-consistent"** — this project's own established
  base-codec proof achieved genuine byte-exact bitstream identity between implementations, so
  byte-exact level lengths are plausible too, but resolve empirically during Stage 5 rather than
  assume; if they don't match exactly, that's still fine as long as each implementation's own
  A number is provably correct for its own output (the acceptance bar this PRD's own Test rig section
  already sets as the fallback).

## Progress log

### Stage 1: `PgfEncoderCore` accounting fields + `AdvanceLevel` hook

- Added `PgfEncodeMacroBlock.LastLevelIndex` (direct port of `CMacroBlock::m_lastLevelIndex`,
  Encoder.h:92, default `-1` matching the native's own `Init(-1)`).
- Added `PgfEncoderCore.levelLength`/`currLevelIndex`/`bufferStartPos` fields, `SetBufferStartPos`/
  `ComputeBufferLength` (direct ports of the same-named native methods, Encoder.h:181,194),
  `BeginLevelLengthTracking(int levelCount)`, and `AdvanceLevel(int currentLevel)` (the
  `m_lastLevelIndex`-only half of `CEncoder::SetEncodedLevel`, Encoder.h:164 — `m_forceWriting` still
  correctly left unported, per `pgf-roi-support.md`'s own Stage 4 finding that it's dead in this
  port's always-single-macroblock build). `WriteMacroBlock` gained the accounting block plus an
  unconditional `SetBufferStartPos()` call, mirroring Encoder.cpp:454-465 exactly.
- Wired `AdvanceLevel` into both of `PgfImageEncoder`'s per-level loops, at the same two call sites
  `CPGFImage::WriteLevel` calls the real `SetEncodedLevel` from: the non-ROI loop's own end
  (PGFimage.cpp:1116-1117), and the ROI branch's last-tile-of-last-channel point, right before that
  tile's own `EncodeTileBuffer()` (PGFimage.cpp:1093-1097).
- Wired `PgfEncoderCore.BeginLevelLengthTracking(header.NLevels)` into `PgfImageEncoder`, called
  immediately after `PgfHeaderIO.Write` returns — establishes the byte-accounting baseline at
  exactly the stream position the placeholder's own zero-fill loop leaves it at.
- **Resolved Open Question 1** (whether the placeholder write this PRD needs is separate from
  `PgfHeaderIO.Write`'s existing zero-fill loop): confirmed by reading `PgfHeader.cs`'s current write
  method (the zero-fill loop is now at lines 271-275, not 264-269 as originally cited — line numbers
  had drifted since the PRD was written, content unchanged) that it's the same placeholder region
  described in Encoder.cpp's `WriteLevelLength`. Reused it rather than duplicating: no second
  placeholder write was added anywhere. Stage 2 will seek back into this exact byte range to patch it.
- No output-visible behavior change yet by design (Stage 1's own exit test) — the accounting runs on
  every encode, but nothing reads `PgfEncoderCore.LevelLength` yet and the on-disk placeholder is
  still all zeros. Full regression suite: 1259/1259 passed, unchanged from before this stage.

### Stage 2: patch real values into the output

- `PgfHeaderIO.Write` now returns the `long` stream position where the level-length placeholder
  begins (its zero-fill loop's own start position) instead of `void` — the seek target Stage 2's
  patch needs, and confirmation in code of Stage 1's resolved Open Question 1 (one placeholder
  region, reused, not duplicated).
- `PgfImageEncoder.TryEncodeMode`'s main (non-`nLevels==0`) path captures that position, and after
  `encoder.Flush()`, seeks back (`PgfByteWriter.SetPos`) and writes `encoder.LevelLength`'s real
  accumulated values — direct port of `CEncoder::UpdateLevelLength`'s seek-write sequence
  (Encoder.cpp:202-234). No position restore afterward (unlike the native): nothing writes to the
  writer again after this point, and `WrittenSpan` reads the stream's backing buffer directly,
  independent of the current seek position — a real, harmless simplification, not a divergence in
  observable behavior.
- The `nLevels==0` raw-path call site (`PgfHeaderIO.Write(rawWriter, header, colorTable, userData)`)
  discards the new return value unchanged — that path returns before the main level loop, so no
  placeholder exists to patch (`header.NLevels == 0` means the zero-fill loop above it already wrote
  zero bytes).
- Rewrote both of this PRD's cited stale doc comments (`PgfImageEncoder.cs`'s class summary,
  `PgfHeader.cs`'s `Write` summary) to describe the new real-values behavior instead of the old
  "deliberate scope cut" framing.
- Checked existing tests referencing level lengths for an assumption this stage would break:
  `PgfUserDataTests.InsertUserData` re-serializes a fresh zero-filled placeholder when splicing user
  data into an already-encoded file (it never copies the original real values forward) — confirmed
  harmless, since every test using it only asserts on decoded pixels/user data, never level lengths.
  No test changes needed.
- Full regression suite: 1259/1259 passed (build succeeded; self-consistency verification deferred to
  Stage 3's own test, per this PRD's own Stage 2 exit-test wording — see that stage's entry below).

### Stage 3: decode-side accessor

- Added `PgfDecodeSession.LevelLengths` (`uint[]`, on-wire/native order), threaded through both
  `TryOpen` constructor call sites from `PgfHeaderIO.Read`'s previously-discarded (`_`) return value —
  no new parsing, just plumbing, exactly as this PRD's Goal 2 anticipated.
- Added `PgfProgressiveDecoder.TryGetLevelLength(int level, out uint length)` — direct port of
  `CPGFImage::GetEncodedLevelLength` (PGFimage.h:367), including its coarsest-first-array/
  finest-first-public-API index flip (`levelLengths.Length - level - 1`), matching this type's own
  `TryGetLevelSize`-style level-0-is-full-resolution convention.
- **Wrote the combined Stage 2+3 self-consistency exit test** (`PgfLevelLengthTests.cs`, new file):
  for every `TestBitmaps.EdgeCaseDimensions()` × {quality 0, 8, `MaxQuality`} × {plain, ROI} — 36
  cases — confirms (a) the level lengths `PgfHeaderIO.Read` parses back sum to exactly the real
  bitstream byte count after the placeholder, (b) every level's length is nonzero, and (c)
  `PgfProgressiveDecoder.TryGetLevelLength` reports the same values in its own level-numbering
  convention, plus out-of-range (`-1`, `NLevels`) both correctly returning `false`. This is the
  self-consistency proof Stage 2's own exit-test wording named as depending on Stage 3's
  not-yet-built accessor — writing it here, once the accessor existed, is what actually verified
  Stage 2's patch logic is correct (all 36 cases passed on the first run, both plain and ROI).
- Full regression suite: 1295/1295 passed (1259 existing + 36 new).

### Stage 4: native shim extension

- Added `pgf_debug_get_level_lengths` to `shim.cpp`, right after `pgf_debug_get_header_info` (same
  Open()-plus-plain-accessors shape, so it doesn't share `pgf_debug_decode_channel`'s unresolved
  repeated-call crash risk). Loops `img.GetEncodedLevelLength(i)` for `i in [0, img.Levels())` into a
  caller-supplied buffer, following `pgf_debug_decode_raw`'s own caller-supplied-buffer convention;
  fails closed (returns `false`, writes nothing) if the buffer is smaller than `Levels()`.
  `GetEncodedLevelLength` was already confirmed correct and unconditionally populated on every real
  `Open()` (this PRD's own Context section) - a pure "report back" extension, no new native parsing.
- **Index-order design note** (not in the original PRD text, worth recording): the export reports
  `outLevelLengths[i] = GetEncodedLevelLength(i)` directly, i.e. in the *public* level-0-is-
  full-resolution order both `CPGFImage`'s own public accessor and
  `PgfProgressiveDecoder.TryGetLevelLength` use - not `PgfHeaderIO.Read`'s raw on-wire array order
  (which is coarsest-first). This makes Stage 5's cross-implementation comparison a direct
  index-for-index match against `TryGetLevelLength(level, ...)`, with no index-flip needed on either
  side of the test.
- Added `NativePgfOracle.TryGetLevelLengths` (learns `Levels()` via the existing
  `TryGetHeaderInfo` first, then calls the new export) and the matching `LibraryImport` declaration.
- Rebuilt `PictTagPgfDecoder.dll` via the VS-bundled CMake/Ninja toolchain (`cmake --build
  native/PictTag.PgfDecoder/build --config Release`, run from a `vcvars64.bat`-initialized
  environment - `cmake`/`ninja` are not on the default `PATH` in this environment, only reachable
  under Visual Studio's own install tree). The project's existing `<None Include="...
  PictTagPgfDecoder.dll" CopyToOutputDirectory="PreserveNewest">` item picked up the rebuilt DLL on
  the next `dotnet build` automatically - no `.csproj` change needed.
- Full regression suite (existing tests, unaffected by the shim addition): 1295/1295 passed.

### Stage 5: full round-trip matrix

- New `PgfLevelLengthCrossImplementationTests.cs` covers both Test rig legs across every
  `TestBitmaps.EdgeCaseDimensions()` × {quality 0, 8, `MaxQuality`} = 18 cases each:
  - **Decode-side accessor leg**: encode with `NativePgfOracle.TryEncode` (real native encoder),
    read the same file's level lengths via both `NativePgfOracle.TryGetLevelLengths` (the new shim
    export) and the managed `PgfProgressiveDecoder.TryGetLevelLength` — a genuine independent
    oracle, since both are just parsing the same on-wire bytes two different ways.
  - **Cross-implementation encode leg** (plain and ROI variants, 2 × 18 = 36 more cases): encode the
    *same* source image with both `PgfImageEncoder.TryEncode`/`TryEncode(roi: true)` and
    `NativePgfOracle.TryEncode`/`TryEncodeRoi`, then compare the managed encoder's own level lengths
    (via its own decode accessor, already proven self-consistent in Stage 3) against the native
    encoder's reported values for its independently-produced file.
- **Resolved Open Question 2** empirically: all 54 cross-implementation cases matched **byte-for-byte
  identical**, level-by-level, on the first run — not just "each implementation's own number is
  self-consistent." This confirms the base codec's own established byte-exact bitstream-identity
  proof (managed-pgf-codec.md) extends to level lengths too, since they're a deterministic function
  of where macroblock boundaries land in that identical bitstream. No fallback to the weaker
  self-consistency-only bar was needed.
- Full regression suite: 1349/1349 passed (1295 existing + 54 new).
