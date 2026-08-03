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

_(Empty — fill in as each stage above is actually implemented and tested, following
`managed-pgf-codec.md`'s own progress-log convention: what was built, what was found, what broke and
how it was fixed, real test counts.)_
