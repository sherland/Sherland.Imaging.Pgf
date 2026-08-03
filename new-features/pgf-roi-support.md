# PGF codec: ROI (region of interest) support — PRD

**Status: not started.** Closes a gap documented in [`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s
"Explicitly out of scope" list ("Region of interest (ROI) cropped decode/encode").

## Context

The native `libpgf` codec compiles ROI support in unconditionally (`PGFplatform.h:60`'s `#define
__PGFROISUPPORT__`, never suppressed anywhere in this build — confirmed by grep, zero hits for the
suppressing macro `NPGFROI`), but it's dead code in practice for this app: nothing that touches this
codebase — not this app's own encoder, not any real digiKam thumbnail encountered — ever sets the
`PGFROI` version flag, so the real `ROIBlockHeader` bitfield is never actually read from or written
to any file this app produces or consumes. `PictTag.PgfCodec` (this codebase's managed port — see
[`new-features/managed-pgf-codec.md`](managed-pgf-codec.md)) deliberately did not port ROI at all,
for exactly that reason.

**Being honest about what problem this solves**: ROI decode means reconstructing only a cropped
rectangular sub-region of an image at a given wavelet level, without paying the cost of decoding the
whole thing — valuable for viewing a zoomed-in crop of a *large* image cheaply. This app's real PGF
usage today is digiKam's own small thumbnail cache (~128-512px) — the full-resolution original is
never PGF-encoded at all (originals are served as-is, whatever format digiKam has). So there is no
current production call site with an actual need for cropped partial decode, the same honest framing
`managed-pgf-codec.md` itself used for encode ("no production call site... exists to make the decoder
provably correct, and as a genuinely useful general capability"). This PRD is motivated by
completeness — making `PictTag.PgfCodec` a real, general-purpose PGF library rather than one scoped
tightly to today's thumbnail-only usage — and by resolving `managed-pgf-codec.md`'s own tracked open
question ("Is the ROI-enabled decode path actually reachable... resolve during stage 4/5, not
upfront" — it was resolved as "no," and this PRD is the deliberate follow-up if that capability is
ever wanted anyway).

## Why this needs to be grounded in the real algorithm

- **`ROIBlockHeader`** (`PGFtypes.h:184-206`): a 16-bit union/bitfield — 15 bits `bufferSize`
  (`RLblockSizeLen`), 1 bit `tileEnd`. Written/read per macroblock only when ROI is active; this
  port's `PgfMacroBlock`/`PgfEncodeMacroBlock` currently hardcode a fixed default instead (documented
  explicitly in their own doc comments as the reason ROI isn't modeled), since it's never read from
  or written to any real stream today.
- **`PGFRect`** (`PGFtypes.h:229-270`): a plain `(left, top, right, bottom)` rectangle in pixels, with
  `Width()`/`Height()`/`IsInside()` helpers — no wavelet-alignment awareness of its own; that's
  computed separately (below).
- **`CPGFImage::SetROI`/`GetAlignedROI`/`ComputeLevelROI`** (`PGFimage.cpp:583-640`): `SetROI` stores
  the requested rect, enables ROI decoding on the shared `CDecoder`, and calls
  `CWaveletTransform::SetROI` per channel (chroma channels get the ROI rect halved to match their own
  downsampled dimensions — the same halving `CPGFImage::Open`'s per-channel width/height setup
  already does, already ported). `GetAlignedROI`/`ComputeLevelROI` convert between the caller's exact
  requested rectangle and the wavelet-pyramid-*aligned* rectangle actually reconstructable at a given
  level (the native docs are explicit that the caller's rect "might be cropped" — i.e. snapped
  outward/inward to tile boundaries, not honored pixel-exact).
- **Tile mechanics** (`WaveletTransform.h:119-148`, `WaveletTransform.cpp:519-545`):
  `GetNofTiles(level) = 1 << (nLevels - level - 1)` — the number of tiles along one axis at a given
  level, doubling every level going finer. `CWaveletTransform::SetROI` computes `m_indices[level]`
  (a `PGFRect` of *tile indices*, not pixels) per level from the requested pixel rect, with an
  explicit invariant check between consecutive levels (`WaveletTransform.cpp:544-545`:
  `m_indices[l-1].left >= 2*m_indices[l].left && ...` — coarser levels' tile-index bounds must nest
  consistently inside finer levels' doubled bounds). `TileIsRelevant(level, tileX, tileY)` is then
  just `m_indices[level].IsInside(tileX, tileY)`.
- **Decode-side tile-aware `Read`** (`PGFimage.cpp:489-577`, the `Read(PGFRect&, level, cb, data)`
  overload the plain `Read(level, cb, data)` internally redirects to whenever `ROIisSupported()` is
  true, `PGFimage.cpp:406-413`): per level, per channel, loops `tileY`/`tileX` over
  `GetNofTiles(level)`, calling `wtChannel->TileIsRelevant(...)` to decide whether to actually decode
  that tile's macroblocks (`PlaceTile(..., true, tileX, tileY)`) or skip them
  (`m_decoder->SkipTileBuffer()` — still has to consume the encoded bytes to stay in sync with the
  stream, just doesn't decode them).
- **Encode-side tiling is unconditional, not ROI-gated** (`PGFimage.cpp:1067-1119`, `WriteLevel`'s
  `#ifdef __PGFROISUPPORT__` branch): every tile is *always* extracted and encoded
  (`ExtractTile(..., true, tileX, tileY)` for every `tileX`/`tileY`, no relevance check on the encode
  side — `TileIsRelevant` is a decode-only concept). "ROI support" from the encoder's own perspective
  is really just "lay the bitstream out in the same tile grid a decoder can later selectively skip
  through" (gated by setting the `PGFROI` version flag) — it does not mean encoding only part of the
  image; the whole image is always encoded, just tile-structured.
- **`CDecoder::SkipTileBuffer`** (`Decoder.cpp:604`) — consumes and discards one tile's worth of
  encoded macroblock bytes without decoding them, keeping the shared bitstream position correct for
  the next tile/channel/level.

## Goals

1. **Decode-side ROI**: given an ROI-flagged PGF file, decode only the tiles relevant to a requested
   rectangle at a requested level — the primary, real capability this PRD adds.
2. **Encode-side ROI**: the ability to produce an ROI-flagged, tile-structured file — needed as
   test infrastructure to generate ROI fixtures at all (no real digiKam thumbnail has this flag set,
   so self-generated fixtures are the only source), and for completeness/round-trip symmetry, matching
   exactly the reasoning `managed-pgf-codec.md`'s own Stage 1 used for adding native test-only encode
   support before the real decode-correctness work could be verified.
3. **`ROIBlockHeader` becomes real** in `PgfMacroBlock`/`PgfEncodeMacroBlock` — replacing the
   hardcoded-default simplification with actual per-macroblock `bufferSize`/`tileEnd` bits, re-verified
   against the full existing entropy-coder test suite (a real regression risk — this is the single
   most delicate, hardest-to-debug component in the whole port per `managed-pgf-codec.md`'s own
   Stage 5 history, "everything failed on the first run").
4. Extend the round-trip matrix (the core correctness methodology from `managed-pgf-codec.md`) to
   cover ROI: various rectangles (full-image equivalent, single-tile, multi-tile, odd/unaligned
   boundaries, edge/corner regions) at every quality level, both self-consistency (C# encode
   ROI-flagged → C# decode partial) and cross-implementation (native ↔ C#) — which requires either
   extending the native shim's test-only encode export to produce ROI-flagged files, or leaning on
   the new C# encoder for that leg once it exists (mirroring the self-consistency-first fallback
   `managed-pgf-codec.md`'s own Stage 5 used when a native intermediate-data hook proved unsafe to
   call repeatedly).

## Non-goals

- **Making ROI the default or only encode path.** Every real digiKam file and this app's existing
  encoder output stays non-ROI (`PGFROI` flag unset) unless a caller explicitly opts in — this PRD
  adds a capability, it doesn't change today's default encoding behavior. Confirm during
  implementation whether enabling ROI tiling has any measurable compression-ratio cost (tile
  boundaries could cut across otherwise-long coefficient runs the entropy coder currently exploits
  uninterrupted) before even considering changing any default.
- **A production UI call site that actually requests a cropped region.** Same honest framing as
  `managed-pgf-codec.md`'s own encoder Non-goal — this closes a documented completeness gap, not a
  user-facing feature request. If a real "zoom into a large image" feature is ever built on top of
  this, it can consume the capability then.
- **Multiple simultaneous/overlapping ROI requests on one open decoder instance beyond what the
  native API itself supports** (`ResetStreamPos`'s own doc comment: re-reading the same image several
  times with different ROIs requires resetting stream position first, since decoding is a one-way,
  level-decreasing, subband-consuming process — port that exact constraint, don't invent a more
  flexible model the native codec doesn't actually have).

## Proposed architecture

- **`PgfRoi`** (or similarly named) — a small internal `readonly record struct (int Left, int Top,
  int Right, int Bottom)` mirroring `PGFRect`, with `Width`/`Height`/`IsInside` — kept minimal, no
  `System.Drawing` dependency (this project is deliberately dependency-free).
- **`PgfMacroBlock`/`PgfEncodeMacroBlock`**: replace the hardcoded `ROIBlockHeader` default with a
  real 15-bit-`bufferSize`/1-bit-`tileEnd` value, read/written per macroblock exactly like the
  existing non-ROI word-length prefix is today — a delicate, surgical change to already-proven-correct
  code (see Goal 3's own caution), landed and re-verified in its own dedicated stage before anything
  ROI-specific is layered on top.
- **`PgfSubband`**: gains `GetNofTiles(level)`, per-level tile-index bounds (`m_indices` equivalent),
  and `TileIsRelevant` — plus per-tile `PlaceTile`/`ExtractTile` overloads taking `(tileX, tileY)`
  that fill/read only that tile's slice of the subband buffer (mirroring the native `Subband.cpp`
  ROI branches this port's Stage 6 already documented as deliberately not carrying over).
- **`PgfDecoderCore`/`PgfEncoderCore`**: `SkipTileBuffer` (decode) and unconditional per-tile
  `ExtractTile`/`EncodeTileBuffer` sequencing (encode) — the entropy-coder-level tile bookkeeping.
- **`PgfWaveletTransform`**: `SetROI`, computing per-level tile-index bounds from a requested pixel
  rect, with the same cross-level nesting invariant the native code asserts
  (`WaveletTransform.cpp:544-545`) — verify it, don't just port it silently, since an off-by-one here
  would silently decode/encode the wrong tiles rather than visibly fail.
- **Public API**: extend `PgfProgressiveDecoder` (not the single-shot `PgfImageDecoder` — ROI is
  fundamentally a partial/progressive-decode concept in the native design, and this port's
  progressive API already has the right per-level, stateful shape) with an ROI-aware
  `TryDecodeLevel` overload taking a `PgfRoi`. Encode-side: extend `PgfImageEncoder.TryEncode` with a
  parameter to opt into producing an ROI-flagged (tile-structured) file — every tile still gets
  encoded either way (see the native encode-side finding above), so this is purely a bitstream-layout
  and version-flag choice, not a "encode only part of the image" parameter.
- **Native shim**: `pgf_encode_bgra_alloc` (the test-only encode export from `managed-pgf-codec.md`'s
  Stage 1) likely needs a variant/flag to produce ROI-flagged files too, so the round-trip matrix has
  a real cross-implementation-encoder leg for ROI, not just C# self-consistency.

## Test rig

Extends `managed-pgf-codec.md`'s own Tier 1-4 methodology (known-pixel synthetic fixtures, oracle
comparison, intermediate-stage comparison, 4-way round-trip matrix) with an ROI dimension:

- **Tile/alignment correctness in isolation**: for a range of fixture sizes and level counts, request
  ROI rectangles at every meaningful boundary condition — the full image (should match non-ROI
  decode exactly), a single tile, several adjacent tiles, an odd/unaligned rectangle requiring
  snapping, and the four corners/edges — asserting the returned (possibly-cropped) rect and pixel
  content match what a full, non-ROI decode of the same region would produce.
- **Cross-implementation, at every quality level**: same 4-way matrix as the base PRD
  (encode-C#/decode-C#, encode-C#/decode-native, encode-native/decode-C#, encode-native/decode-native)
  but with ROI enabled and a swept set of requested rectangles, at every quality value
  `0..MaxQuality`.
- **`SkipTileBuffer` correctness**: a decode requesting a small ROI within a larger multi-tile image
  must still leave the shared bitstream position correct for a *subsequent* `TryDecodeLevel` call
  requesting a different, non-overlapping ROI or a finer level — assert this directly (a real, subtle
  place for an off-by-one to silently desync the stream rather than visibly fail).
- **Regression**: the existing non-ROI round-trip matrix and the full `PictTag.PgfCodec.Tests` suite
  (567 tests as of this PRD's writing) must stay green throughout, especially after the
  `ROIBlockHeader`-becomes-real stage — this is exactly the kind of change Stage 5's own history
  warns could silently break non-ROI decode/encode if the "always full `bufferSize`, `tileEnd`
  irrelevant" simplification isn't cleanly separable from the real per-macroblock value.

## Stage sequence

1. **`ROIBlockHeader` becomes real** in `PgfMacroBlock`/`PgfEncodeMacroBlock`, with the existing
   full `PictTag.PgfCodec.Tests` suite re-run in full afterward as the regression gate before any new
   ROI-specific code is written — this is the highest-risk stage, isolate it.
2. **Tile mechanics**: `PgfSubband`'s `GetNofTiles`/tile-index bounds/`TileIsRelevant`,
   `PgfWaveletTransform.SetROI`. Exit test: tile-index bounds computed for a range of fixture
   sizes/levels/requested rectangles match hand-verified expectations (no decode/encode involved yet
   — pure geometry).
3. **Decode-side**: per-tile `PlaceTile` overloads, `PgfDecoderCore.SkipTileBuffer`, wiring into a new
   ROI-aware `PgfProgressiveDecoder.TryDecodeLevel` overload. Exit test: self-consistency only at
   first (C# encode ROI-flagged in a later stage isn't ready yet) — cross-check against
   `pgf_debug_decode_channel`-style intermediate data if the native shim's own known crash-risk
   caveat (`managed-pgf-codec.md`'s Stage 1 finding) still applies, or extend the shim's ROI-capable
   encode export first if that's the more tractable order.
4. **Encode-side**: per-tile `ExtractTile` overloads, unconditional tile-sequenced `WriteLevel`
   equivalent, `PgfImageEncoder`'s ROI-enabling option, setting the `PGFROI` version flag. Exit test:
   the encode-then-decode self-consistency round trip for ROI-flagged files.
5. **Native shim extension** (if not already done in Stage 3): an ROI-capable variant of
   `pgf_encode_bgra_alloc`, enabling the real cross-implementation legs of the round-trip matrix.
6. **Full ROI round-trip matrix**: every fixture x every swept ROI rectangle x every quality level,
   all four legs, per the Test rig above.
7. **Documentation**: `docs/PGF-CODEC.md`'s "out of scope" list updated; this PRD's own Progress log
   filled in; note the resolution in `managed-pgf-codec.md`'s own "Open questions" section (which
   already tracks "Is the ROI-enabled decode path actually reachable" as resolved-to-"no" — this PRD
   is the deliberate follow-up, worth a cross-reference there).

## Acceptance criteria / Definition of Done

- ROI-flagged files can be produced (encode) and correctly partially decoded (decode) for a
  meaningful range of requested rectangles, at every quality level, both self-consistently and
  cross-implementation against the native oracle.
- Non-ROI decode/encode (the existing, real-world-relevant path) shows zero regression — the full
  existing round-trip matrix and test suite stay green throughout, verified explicitly after the
  `ROIBlockHeader`-becomes-real stage, not just at the end.
- `docs/PGF-CODEC.md` updated to move this item from "out of scope" to "supported."

## Open questions

- **Whether enabling ROI tiling measurably hurts compression ratio** for this app's real thumbnail
  sizes/content — confirm empirically (Stage 6's own matrix already sweeps quality; add an
  output-size comparison against the equivalent non-ROI encode) rather than assume tile boundaries
  are cheap.
- **Whether the native shim needs its own ROI-capable encode export**, or whether the C# encoder
  (once Stage 4 lands) is sufficient for the round-trip matrix's cross-implementation legs — decide
  based on how Stage 3/4 actually goes, not upfront.
- **Whether `ResetStreamPos`'s "read the same image several times with different ROIs" pattern is
  worth porting to `PgfProgressiveDecoder`**, or whether requiring a fresh `TryOpen` call per distinct
  ROI request (simpler, matches this port's existing session-per-open model) is an acceptable
  divergence — resolve once a real calling pattern (if any) exists to design against.

## Progress log

### Stage 1: `ROIBlockHeader` becomes real

Verified every native citation in this PRD's "Context"/"Why this needs to be grounded" sections
against the real `native/PictTag.PgfDecoder/libpgf` sources before writing any code (`PGFplatform.h`
still `#define __PGFROISUPPORT__` unconditionally, zero `NPGFROI` hits; `ROIBlockHeader`/`PGFRect` at
the cited `PGFtypes.h` lines; `SetROI`/`GetAlignedROI`/`ComputeLevelROI`/`WriteLevel`'s ROI branch in
`PGFimage.cpp`; `GetNofTiles`/`m_indices`/`TileIsRelevant`/`SetROI` in `WaveletTransform.h/.cpp`;
`CDecoder::SkipTileBuffer` in `Decoder.cpp`) - all matched. Also traced the full `ROIBlockHeader`
lifecycle end to end (`CEncoder::WriteValue`/`Flush`/`EncodeTileBuffer` → `EncodeBuffer(ROIBlockHeader)`
→ `WriteMacroBlock`'s `if (m_roi)`-guarded 2-byte write; the decode-side mirror in
`CDecoder::ReadMacroBlock`) to confirm a key fact this stage depends on: outside ROI mode the header's
`bufferSize` field is *always* the full `BufferSize` (every real `EncodeBuffer` call in the non-ROI
path passes either `(BufferSize, false)` on a full-buffer flush or `(BufferSize, true)` on the final,
zero-padded `Flush()` - never a partial count), and the extra 2 header bytes are only ever written to
/ read from the wire when `m_roi` is true. That confirms making the header "real" in this stage is a
behavior-preserving refactor for every real file this app produces/consumes today, not a functional
change - exactly the isolation the PRD calls for.

Added `PgfRoiBlockHeader` (new file) - a `readonly record struct` mirroring the `ROIBlockHeader`
union/bitfield exactly (15-bit `BufferSize` low, 1-bit `TileEnd` high, matching
`PgfConstants.RLblockSizeLen`), with both the raw-value constructor and the `(bufferSize, tileEnd)`
constructor the native type has. `PgfMacroBlock` (decode) replaced its old hardcoded
`bufferSizeInUse` field with a real `Header` property, `IsCompletelyRead`/`BitplaneDecode` now read
`Header.BufferSize` instead of the old always-`BufferSize` constant. `PgfEncodeMacroBlock` (encode)
gained the same `Header` property (previously had no header concept at all - the encode side's
`BitplaneEncode` already took `bufferSize` as an explicit parameter, so this is purely additive
bookkeeping, not a behavior change). `PgfDecoderCore.ReadMacroBlock` now constructs a real
`PgfRoiBlockHeader((uint)BufferSize, tileEnd: false)` (mirroring the native's own
`ROIBlockHeader h(BufferSize)` default) and passes it to `MarkReadyToDecode`, instead of a no-arg
method that hardcoded the same value internally. `PgfEncoderCore.WriteValue`/`Flush` now construct
the same two real headers the native `WriteValue`/`Flush` construct
(`(BufferSize, false)`/`(BufferSize, true)`) and pass them through a renamed `EncodeBuffer(PgfRoiBlockHeader)`
that stamps the header onto the block before encoding, mirroring `m_currentBlock->m_header = h;`.

Deliberately scoped narrower than "wire read/write the extra 2 bytes conditioned on an `m_roi` flag"
- that flag doesn't exist anywhere in this port yet, and wiring it in now would mean dead,
unverifiable code. Stage 3 (decode) and Stage 4 (encode) are where a real `m_roi`-equivalent gets
introduced and the conditional extra-byte wire (de)serialization actually gets added to
`PgfDecoderCore`/`PgfEncoderCore`, at the same time as the tile machinery that's the only thing that
ever sets it true. This stage's job was just making the per-macroblock header *value* real and
correctly modeled, which is now done and independently verified.

Regression gate: full `PictTag.PgfCodec.Tests` suite, before and after -
**1130/1130 passed both times** (the PRD's "567 tests as of this PRD's writing" figure was stale -
other PRDs' work landed more tests between then and now, confirmed by running the suite rather than
trusting the cited count). No regressions; behavior is bit-identical for every non-ROI file, as
expected from the above analysis.

### Stage 2: Tile mechanics (pure geometry)

Added `PgfRoi` (new file) - a plain `readonly record struct` mirroring `PGFRect` exactly
(`Left`/`Top`/`Right`/`Bottom`, `Width`/`Height`/`IsInside`).

`PgfSubband` gained `NTiles`/`SetNTiles`, `AlignedRoi`/`SetAlignedRoi` (clamped to the subband's real
`Width`/`Height`, mirroring `CSubband::SetAlignedROI`), `BufferWidth` (defined now, not yet consumed
by `AllocMemory` - see below), and direct ports of `CSubband::TilePosition`/`TileIndex`
(Subband.cpp:257-386) - the two binary-search tile-geometry routines the encode/decode tile machinery
both depend on. `PgfWaveletTransform` gained `GetNofTiles`/`TileIsRelevant`/`GetAlignedROI` and a
direct port of `CWaveletTransform::SetROI` (WaveletTransform.cpp:519), including the margin-enlargement
step (`delta = (FilterSize>>1) << levelCount`) ported byte-for-byte rather than re-derived.

**The native's own cross-level nesting invariant** (`WaveletTransform.cpp:544-545`) is a no-op
`ASSERT` in the original (compiled out in Release builds) - per this PRD's "verify it, don't just
port it silently" instruction, this port makes it a real, always-checked
`InvalidOperationException` instead, so a geometry bug fails loudly at the exact level it occurs
rather than silently decoding/encoding the wrong tiles three stages later, undetectably, in Release.

**Real finding, not just re-confirming the PRD**: reading `WaveletTransform.cpp`'s ROI branch in
full (needed to scope this stage correctly) surfaced that the existing (pre-this-PRD) port's
`InverseTransform`/`SubbandsToInterleaved` doc comments already flagged "ROI is not ported... ports
only the non-ROI `#else` branches" - but the *real* native ROI branch of `InverseTransform`
(WaveletTransform.cpp:257-332) is substantially more involved than the PRD's own "Proposed
architecture" section implies: it reconciles *independently-computed* per-subband aligned-ROI
offsets (LL vs. HL/LH/HH can each have a different `GetAlignedROI().left/top` at a tile boundary,
requiring an explicit `srcOffsetX`/`srcOffsetY`/`destROI` reconciliation dance, not just "read from
wherever the ROI starts"), and `SubbandsToInterleaved`'s ROI branch has its own `storePos`/
`IncBuffRow` buffer-position save/restore bookkeeping for when a subband's tile buffer is narrower
than the row being reconstructed. The PRD's architecture section undersells this as part of a
generic "PgfDecoderCore/PgfEncoderCore: SkipTileBuffer... entropy-coder-level tile bookkeeping" -
it's really its own delicate unit of work squarely inside Stage 3's scope (decode-side), not
something Stage 2 needs to touch (this stage is provably pixel-data-flow-free: `SetROI` only reads
each subband's fixed `Width`/`Height` and writes tile-index/aligned-ROI bookkeeping, confirmed by
the fact that every test above constructs a `PgfWaveletTransform` and calls `SetROI` without ever
calling `AllocMemory`/`ForwardTransform`/`InverseTransform` at all). Flagging this now so Stage 3's
own scope estimate accounts for it up front rather than being "discovered" mid-stage - this is
exactly the kind of PRD-architecture-section correction the `implement-prd` skill's step 1
anticipates, not a reason to stop.

Two hand-traced exit tests (`PgfRoiTileGeometryTests.PartialRoi_MatchesHandTracedTileIndicesAndAlignedRoi`,
64x64/3-levels/ROI=(0,0,8,8), traced through the real binary-search arithmetic by hand to
`indices[0]=(0,0,5,5)`, `indices[1]=(0,0,3,3)`, aligned ROI `(0,0,40,40)`/`(0,0,24,24)`) plus a
full-image-coverage sweep, a `GetNofTiles` doubling check, and a 9-case sweep of corners/edges/
odd-unaligned rectangles across four different image sizes/level counts asserting the nesting
invariant holds without throwing - **17 new tests**, all pure geometry (no decode/encode, no pixel
data, matching this stage's own exit-test scope from the Stage sequence).

Regression gate: full `PictTag.PgfCodec.Tests` suite - **1147/1147 passed** (1130 existing + 17 new,
zero regressions).

### Stage 3: Decode-side ROI

**Real finding that reorders this stage's own testing (not just implementation)**: real ROI decoding
only ever activates against a file whose preheader version flags declare `PGFROI`
(`CPGFImage::ROIisSupported`, `PGFimage.h:466`) - confirmed by reading `CPGFImage::Read`'s two
overloads (PGFimage.cpp:402-477 and 489-577) in full: the plain `Read(level,...)` takes the ROI-aware
tile loop only `if (ROIisSupported() && m_header.nLevels > 0)`, and the `Read(rect,...)` overload
itself falls straight back to the plain path `if (m_header.nLevels == 0 || !ROIisSupported())`. No
real digiKam thumbnail and nothing this port's own encoder has ever produced sets that flag, and
Stage 4 (encode) is the *only* stage that can ever produce one. This means Stage 3's own decode code
(`SkipTileBuffer`, tile-relevant `PlaceTile`, the ROI-generalized `InverseTransform`) is provably
**not independently testable against real bytes** - there is no ROI-flagged input anywhere to decode
until Stage 4 exists. The PRD's own Stage 3 text already anticipated needing a fallback here
("self-consistency only at first... or extend the shim's ROI-capable encode export first if that's
the more tractable order") - this is that fallback, made concrete: **substantive decode-correctness
testing (self-consistency and cross-implementation) is deferred to Stage 4's own round-trip test**,
the earliest point real ROI-flagged bytes exist to decode at all. This stage's own tests are
therefore scoped to what's genuinely independent: pure code-level regression safety (the full
existing suite staying green through the delicate `InverseTransform` generalization) and the new
public API's own contract (bounds/state validation), not decode correctness itself.

Implementation, in the order built:
- **`PgfDecoderCore`**: `SetRoi()` (mirrors `CDecoder::SetROI`) gates `ReadMacroBlock` actually
  reading the extra 2 `PgfRoiBlockHeader` bytes off the wire (Stage 1's deferred wire-format work,
  landed here since this is the first stage anything can actually set `m_roi`-equivalent true).
  `GetNextMacroBlock` promoted from `private` to `public` and ported faithfully to real ROI call
  sites even though, reasoned through carefully, it's provably redundant with `DequantizeValue`'s own
  lazy fetch in this port's always-single-macroblock configuration (documented in its own doc comment
  as "match the real sequence rather than trust that reasoning has no edge case"). New
  `SkipTileBuffer` - collapsed to the single-macroblock branch (the native's own
  `m_macroBlocks[]`-lookahead branch is dead code here, same OpenMP-disabled reason as everywhere
  else) - reads and discards macroblocks until `TileEnd`, using `PgfMemoryReader.SetPos(Current, ...)`
  to skip data bytes without buffering them (mirrors `m_stream->SetPos(FSFromCurrent, ...)` exactly).
- **`PgfSubband`**: `AllocMemory` generalized from `Width*Height` to `BufferWidth*AlignedRoi.Height`
  (identical whenever `AlignedRoi` is still its full-subband default - a value-preserving
  generalization, not a branch, matching this port's established Stage 1/2 pattern). `InitBuffPos`
  generalized to take an optional ROI-relative `(left, top)` offset (default 0,0, identical to the
  old no-arg version). New `GetBuffPos`/`IncBuffRow` (direct ports). New tile-aware `PlaceTile`/
  `ExtractTile` overloads (the latter used starting Stage 4) computing tile position via
  `TilePosition` and addressing the (possibly ROI-sized) buffer via `BufferWidth`, not `Width`.
- **`PgfWaveletTransform.InverseTransform`/`SubbandsToInterleaved`**: generalized to the full ROI
  branch rather than kept as a separate path - see this method's own doc comment for why (the four
  `srcLevel` subbands' independently-computed `AlignedRoi` can disagree by up to one tile at a
  boundary, requiring the native's `srcOffsetX`/`srcOffsetY`/`destROI` reconciliation, ported
  byte-for-byte). Verified by construction that every adjustment is a no-op when ROI was never set
  (walked through the arithmetic by hand during development: `leftD == left0 == left1 == 0` when no
  subband's `AlignedRoi` has been narrowed, so every `srcOffsetX`/`srcOffsetY` branch takes its
  `>= max(...)` zero-offset path), and empirically by the full regression suite staying green - the
  single highest-risk change in this stage, exactly as flagged when Stage 2 first surfaced this
  branch as more involved than the PRD's own architecture section implied.
- **`PgfDecodeSession`**: new `SetRoi`/`DecodeOneLevelRoi`, direct ports of `CPGFImage::SetROI`
  (including the chroma-channel halving-only-when-downsampled behavior) and one iteration of
  `CPGFImage::Read(rect,...)`'s loop body. New `RoiSupported` property (reads the preheader's
  `PGFROI` version flag, previously discarded as `_` in `TryOpen`'s destructured tuple).
- **`PgfProgressiveDecoder`**: new public `TrySetRoi`/`TryGetAlignedRoi`, and `TryDecodeLevel`
  dispatches to `DecodeOneLevelRoi` instead of `DecodeOneLevel` once ROI is enabled. **Resolves this
  PRD's third "Open question"**: this port does **not** carry forward the native's
  `ResetStreamPos`/re-readable-with-a-new-ROI model - `TrySetRoi` must be called before the first
  `TryDecodeLevel` on an instance, and calling it again (or after decoding started) throws
  `InvalidOperationException`. Chosen because no real calling pattern needing multiple ROIs on one
  open image exists anywhere in this codebase (this PRD's own Non-goals already rule out a production
  call site), and it matches this port's existing session-per-open model everywhere else - a fresh
  `TryOpen` per distinct ROI request is the divergence, not a missing feature. Also stricter than
  native in one more way: `TrySetRoi` returns `false` outright when the file's own version flags
  don't declare `PGFROI` (`PgfDecodeSession.RoiSupported`), rather than the native's silent fallback
  to a plain, non-cropped decode - a caller that explicitly asked for ROI decoding on a file that
  can't do it should get a clear "no," not a surprising full-image result.

Tests added: 7 new API-contract tests (`PgfProgressiveDecoderRoiApiTests`) covering bounds validation,
the `RoiSupported` gate, and the one-call/one-session state machine - all using the existing
non-ROI-flagged sample fixture, since (per the finding above) that's all that's independently
testable at this stage.

Regression gate: full `PictTag.PgfCodec.Tests` suite - **1154/1154 passed** (1147 existing + 7 new,
zero regressions) - the load-bearing result for this stage, given the `InverseTransform` generalization's
risk.
