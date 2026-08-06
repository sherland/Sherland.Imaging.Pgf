# PGF codec: legacy pre-Version5 interleaved decode — PRD

**Status: done — implementation landed incidentally by `pgf-bitmap-legacy-packed.md`; this PRD
completed the required all-mode safe-oracle verification matrix.** Originally closed a gap documented in [`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s
"Not yet ported — real gaps against full C++ parity" list ("Legacy pre-Version5 entropy coding"). See
[`pgf-legacy-native-oracle-sourcing.md`](pgf-legacy-native-oracle-sourcing.md) for a real investigation
into whether a historical native oracle is obtainable for this gap specifically (short answer: no,
confirmed by exhausting every plausible source). The resulting verification uses a synthetic native
writer plus both the real native decoder and the managed decoder; it cannot claim byte provenance
from a historical encoder that no longer exists.

## Context

`Sherland.Imaging.Pgf` is planned to be published as a standalone NuGet package (see
[`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s own opening note) — a real, stated goal that changes
the bar for what counts as in-scope. The decoder originally implemented only the *modern* Version5+
scheme, where the HL and LH subbands are each independently tiled and decoded via
`CDecoder::Partition` (ported as `PgfDecoderCore.Partition`). Real PGF files written before Version5
existed (missing the `Version5` bit in the preheader's version-flags byte) instead interleave HL and
LH together in the entropy-coded bitstream, decoded natively by `CDecoder::DecodeInterleaved`
(`Decoder.cpp:337-454`, declared `Decoder.h:127-133`). Commit `a18e1a9` ported that algorithm and its
version dispatch while implementing the Bitmap PRD; this PRD owns the remaining all-mode proof.

**Being honest about what this closes and what it doesn't**: this is a decode-only gap. Confirmed by
direct read of the vendored source: `EncodeInterleaved` does not exist anywhere in
`native/Sherland.Imaging.Pgf.Native/libpgf` — grepping `Encoder.cpp`/`Encoder.h` finds no trace of it, not
even as dead code. `CPGFImage::SetHeader`'s `flags` parameter (PGFimage.cpp:893, body at :905,
`m_preHeader.version = PGFVersion | flags;`) can only **add** bits onto `PGFVersion`
(`PGFtypes.h:76/78`, which unconditionally already includes `Version5`) — there is no way, through
any code path in this vendored source, to make the real encoder emit genuine pre-Version5 bytes. This
mirrors `PgfConstants.EncoderVersionFlags` (`PgfConstants.cs:99-101`), which likewise always includes
`Version5`. So: real, working native *decode* capability this port is missing for full parity; a
native *encode* capability that has never existed in the reference implementation at all, for any
build, ever — see Non-goals.

## Why this needs to be grounded

- **The algorithm** (`Decoder.cpp:337-454`, doc comment at :337-342): "Decodes and dequantizes HL,
  and LH band of one level. LH and HH are interleaved in the codestream and must be split." Unlike
  `Partition`, which tiles **one** subband independently in `LinBlockSize`(8) squares,
  `DecodeInterleaved` walks **two subbands simultaneously**, reading one `InterBlockSize`(4) HL block
  then one `InterBlockSize`(4) LH block per macroblock position (the paired `DequantizeValue` calls
  at lines 375-376, 391-392, 415-416, 432-433 — literally the same block-position walk `Partition`
  does, but decoding an HL block then an LH block from the same shared coefficient stream at each
  step, not one subband alone). It also handles HL and LH being different sizes — asserted at lines
  355-356 (`lhBand->GetWidth() >= hlBand->GetWidth()`, `hlBand->GetHeight() >= lhBand->GetHeight()`)
  — with explicit trailing fixups: an extra LH column when `lhBand->GetWidth() > hlBand->GetWidth()`
  (lines 397-399, 438-440) and an extra HL row when `hlBand->GetHeight() > lhBand->GetHeight()`
  (lines 446-453). `quantParam` is normalized by `level` inline (lines 362-363:
  `quantParam -= level; if (quantParam<0) quantParam=0;`) rather than delegated the way `PlaceTile`
  does. `InterBlockSize` (`PGFtypes.h:87`, `#define InterBlockSize 4`) is mirrored by
  `PgfConstants.InterBlockSize`; its old forward-reference saying the method was not yet ported was
  removed during this follow-up because it became stale when `a18e1a9` landed.
- **Version dispatch** — `CPGFImage::Read`, `PGFimage.cpp:438-445`:
  ```cpp
  if (m_preHeader.version & Version5) {
      // since version 5
      wtChannel->GetSubband(m_currentLevel, HL)->PlaceTile(*m_decoder, m_quant);
      wtChannel->GetSubband(m_currentLevel, LH)->PlaceTile(*m_decoder, m_quant);
  } else {
      // until version 4
      m_decoder->DecodeInterleaved(wtChannel, m_currentLevel, m_quant);
  }
  ```
  HH is always decoded separately either way (line 446). This is the *only* call site of
  `DecodeInterleaved` anywhere in the native tree. The ROI-tiled `Read(rect,...)` path
  (`PGFimage.cpp:519-547`) never reaches this branch — gated by `ROIisSupported()` (`PGFROI`), which
  in every real, producible file implies `Version5` too — so this is exclusively a non-ROI,
  pre-Version5 concern, with no interaction with `pgf-roi-support.md`'s own work.
- **The dispatch gap found during grounding is fixed**: `PgfDecodeSession.DecodeOneLevel` originally
  called `PlaceTile` for HL/LH unconditionally and silently misdecoded a genuine legacy payload.
  Commit `a18e1a9` now records the `Version5` flag on the session and dispatches legacy levels to
  `DecodeInterleaved`, matching `CPGFImage::Read`; modern files retain the existing `PlaceTile` path.
- **Architecture fit, confirmed against the real current port**: everything a ported
  `DecodeInterleaved` needs already exists. `PgfSubband.Width`/`Height` (`PgfSubband.cs:28-30`),
  `AllocMemory()` (:260), `GetBuffer()` (:278, public) cover subband access;
  `PgfDecoderCore.DequantizeValue(Span<int> band, int bandPos, int quantParam)`
  (`PgfDecoderCore.cs:46`) already takes a flat buffer, so it works unchanged for both HL's and LH's
  buffers with no new primitives needed on `PgfSubband`/`PgfWaveletTransform`.
  `PgfWaveletTransform.GetSubband` (`PgfWaveletTransform.cs:74`) already returns both bands. This is
  small enough to live as a new method on `PgfDecoderCore` next to `Partition`, not a new file.
- **Testability — weaker than a preserved historical-encoder fixture, stated plainly**:
  unlike ROI (which got a real independent cross-check just by adding one new `SetHeader(header,
  PGFROI)` flag combination — the encoder already knew how to *write* ROI-tiled output), there is no
  way to obtain genuine legacy-format bytes from the real native encoder at all (per Context above:
  `SetHeader`'s flags can only add bits, and `EncodeInterleaved` doesn't exist to invoke even if the
  bit could be forced). Manually patching just the version byte after a normal native encode does
  **not** work either — the *bitstream itself* would still be laid out in the modern per-subband
  tiled scheme, so a newly-ported `DecodeInterleaved` would misparse valid modern-format bytes while
  *believing* it's reading genuinely interleaved data, silently producing wrong output that looks
  like a decode bug in the new code when it would actually be a fixture-generation bug. The only
  viable option is a **test-only synthetic interleaved writer** invented for this PRD specifically
  (not a port of a shipping encoder function, since none exists). The as-built writer lives in the
  native shim and deliberately retains real `CPGFImage::ImportBitmap`, color conversion, wavelet
  preparation, headers, and macroblock encoding; it replaces only the absent historical HL/LH write
  order. Its output is then decoded independently by real `CPGFImage::Read`/`GetBitmap` and by the
  managed decoder. This is materially stronger than a C# encoder/C# decoder self-round-trip, but
  still weaker than bytes produced by a historical encoder because the synthetic write order is
  derived from the surviving decoder.

## Goals

1. **Preserve the already-landed `Version5` dispatch** so genuine legacy files take the interleaved
   decoder and modern files remain on `PlaceTile`.
2. **Verify the faithful `CDecoder::DecodeInterleaved` port** (including both HL/LH size-mismatch
   trailing fixups) across every supported non-Bitmap image mode; Bitmap has its own completed matrix.
3. **Use a test-only native fixture writer and a safe native decode oracle** to cross-check the
   managed result without relying on the known-unsafe repeated `GetChannel()` debugging export.

## Non-goals

- **A production encode path for legacy/interleaved output.** The real reference implementation has
  never had one, for any build, ever (confirmed: no `EncodeInterleaved` anywhere in the vendored
  source) — nothing would ever have a reason to *write* pre-Version5 files, since Version5 has been
  the encoder's own baseline for the entire lifetime of this vendored codebase. The Goal 3 fixture
  generator is test infrastructure only, exactly like `pgf_encode_bgra_alloc`/`TryEncodeMode` are for
  other test rigs in this codebase — never exposed as a production API.
- **A historical encoder oracle.** Explicitly acknowledged as unobtainable (see "Why this needs to
  be grounded"). The real current native decoder is still used as a cross-implementation oracle for
  the synthetic fixture; what cannot be claimed is that an independently preserved old encoder
  authored those bytes.
- **`DecodeInterleaved`'s OpenMP/multi-macroblock interactions.** Same standing simplification as
  everywhere else in this port (`LIBPGF_DISABLE_OPENMP` is real in the native build too) — the
  single-macroblock sequential path is the only one that matters.

## Proposed architecture

- **`PgfDecoderCore.DecodeInterleaved(PgfSubband hlBand, PgfSubband lhBand, int level, int
  quantParam)`** — direct port of the cited algorithm: walks `InterBlockSize`(4) blocks positionally
  across both bands' shared coordinate space, decoding one HL block then one LH block per position via
  the existing `DequantizeValue`, with the two documented trailing fixups (extra LH column, extra HL
  row) ported byte-for-byte, not re-derived, given the native code's own explicit size-mismatch
  handling is easy to get subtly wrong by "simplifying."
- **`PgfDecodeSession`**: read `PgfHeader.PgfPreHeader.VersionFlags` (already parsed, currently
  unused for this purpose) once per session; store whether this is a legacy (pre-`Version5`) file.
  `DecodeOneLevel` branches: modern files keep calling `PlaceTile` for HL/LH exactly as today (zero
  behavior change, verified by the existing regression suite staying green); legacy files call the
  new `DecodeInterleaved` instead. HH decode is unaffected either way (matches the native's own
  "HH is always decoded separately" structure).
- **Test-only fixture generator**: `LegacyBitmapTestImage` in the native shim subclasses
  `CPGFImage`, retains native import/color/wavelet setup, writes HL/LH coefficients in the inverse of
  `DecodeInterleaved`'s order, and omits `Version5`. `pgf_encode_legacy_interleaved_raw_alloc`
  generalizes that path across the 15 supported non-Bitmap modes; Bitmap remains covered by its
  specialized packed writer.

## Test rig

- **Version-dispatch regression** (Goal 1): every generated legacy fixture must open with
  `PgfDecodeSession.Version5 == false`; its modern sibling must retain the normal flag and path. The
  full existing suite confirms zero behavior change for real Version5+ fixtures.
- **Modern-versus-legacy equivalence** (Goals 2/3): native writers encode the same deterministic raw
  source once through the normal modern layout and once through the synthetic pre-Version5 layout.
  Decode both through the managed decoder and compare their normalized BGRA output byte-for-byte.
- **HL/LH size-mismatch edge cases specifically**: fixture dimensions chosen so HL and LH genuinely
  differ in width/height at some level (odd/prime dimensions, matching this test rig's own established
  `TestBitmaps.EdgeCaseDimensions` convention) — exercises the two documented trailing-fixup branches
  directly, not just the common equal-size case.
- **All supported image modes through a safe native oracle**: the generic pre-Version5 writer uses
  real `CPGFImage::ImportBitmap`/color/wavelet preparation and only replaces the absent interleaved
  output step. Decode both modern and legacy siblings through `pgf_debug_decode_raw`, which calls the
  real native `CPGFImage::GetBitmap`, and compare their mode-native raw results byte-for-byte before
  comparing managed BGRA. `NativePgfOracle.TryDecodeRaw` already falls back from the RGBA-only
  `pgf_get_dimensions` export to `pgf_debug_get_header_info`, so 1- and 3-channel modes are sized
  correctly. The automated matrix must never call `pgf_debug_decode_channel`: its `GetChannel()`/
  `memcpy` path has a separately documented repeatable `STATUS_ACCESS_VIOLATION` under repeated calls.
  Exercise all 15 non-Bitmap modes at ordinary even and odd dimensions, plus a deterministic
  2049x1027 multi-level RGBA case whose source/rendered data exceeds 1 MiB without committing a
  megabyte-scale fixture.

## Stage sequence

1. **Reconcile the incidental implementation** — record that `PgfDecoderCore.DecodeInterleaved`, its
   Version5 dispatch, and a Bitmap-only synthetic fixture generator already shipped while closing the
   Bitmap PRD. Replace the outdated C#-only self-consistency plan with native fixture writing plus
   native safe-raw and managed decode comparison.
2. **Generic native pre-Version5 fixture writer** — generalize the test-only writer to every supported
   image mode, retaining real native import/color/wavelet setup and replacing only the absent
   interleaved entropy output step. Exit test: native `GetBitmap` opens and decodes each fixture.
3. **All-mode safe-oracle matrix** — compare native `GetBitmap` and managed BGRA results for modern
   and legacy sibling fixtures across ordinary even and odd dimensions plus a large multi-level RGBA
   image. Exit test: all 15 modes green without automated `GetChannel()` calls, and modern suites
   unchanged.
4. **Documentation** — mark this PRD done, reconcile `docs/PGF-CODEC.md` and this PRD's Progress log,
   and remove stale claims that pre-Version5 decoding is only Bitmap-proven.

## Acceptance criteria / Definition of Done

- A genuine pre-Version5 file no longer silently misdecodes; it dispatches to the verified
  interleaved decoder.
- The ported `DecodeInterleaved` reconstructs pixel-identical output to the modern decode path for
  the same underlying image content, across the size-mismatch edge cases specifically. The synthetic
  fixture is accepted by both native `CPGFImage::Read`/`GetBitmap` and the managed decoder.
- Zero regression on the existing (Version5+) test suite throughout.
- `docs/PGF-CODEC.md` updated, honestly distinguishing the real dual-decoder cross-check from the
  historical encoder oracle that could not be sourced.

## Open questions

- ~~Where the test-only fixture generator should live~~ — **answered**: it belongs in the native
  test shim because that preserves the real `CPGFImage::ImportBitmap`, mode conversion, wavelet, and
  macroblock code on the fixture-writing side. `LegacyBitmapTestImage` supplies only the absent
  historical interleaved ordering; no synthetic writer was added to the production managed package.
- ~~Whether it's worth trying to obtain or build a real historical `libpgf` reference build (an old
  pre-2006 pre-Version5 release) purely to generate one real, independently-authored legacy fixture
  for extra confidence~~ — **answered**, see
  [`pgf-legacy-native-oracle-sourcing.md`](pgf-legacy-native-oracle-sourcing.md): no pre-Version5
  source is obtainable anywhere (SourceForge files/git/SVN, Debian's archive, and digiKam's complete
  2004-onward git history — whose own *first-ever* libpgf import in 2009 already postdates Version5's
  introduction — were all checked). A synthetic writer checked by both current native and managed
  decoders is therefore the strongest executable setup available, but still cannot establish
  historical-byte provenance. That doc's own "Finding A" also supplied a useful structural check:
  the 2009 vendored snapshot's commented-out `EncodeInterleaved` body is a second,
  independently-dated description of the interleaving order, even though it cannot be compiled as a
  real historical encoder oracle.

## Progress log

**Incidental implementation while shipping `pgf-bitmap-legacy-packed.md`.** That PRD needed a valid
pre-Version5 Bitmap fixture to exercise its `yw=w2` stride. The investigation proved that clearing
Version5 alone invalidates a tiled payload, so it added a test-only native reverse-`DecodeInterleaved`
writer, then ported `CDecoder::DecodeInterleaved` into `PgfDecoderCore` and dispatched it for every
pre-Version5 session. At that point native-versus-managed decode was proven only through legacy
Bitmap fixtures, including odd dimensions and a 2049x1027 case, so this PRD remained partially open
until its broader multi-mode fixture matrix ran. See that PRD's Stage 1-3 entries and commit
`a18e1a9` for the complete implementation record.

**Stage 1: Reconciled the incidental implementation.** User-directed follow-up promotes the missing
all-mode verification from a note into this PRD's remaining scope. The original fail-closed and
C#-only-generator stages are superseded: the decoder already handles Version5 dispatch and native
fixture writing can validate a real C++ decoder before comparing native and managed decoded output.
The revised stages test the layout switch against both native and managed decoders so every mode is
covered without relying only on the managed implementation.

**Stage 2: Generic native pre-Version5 fixture writer and safe oracle.** Added
`pgf_encode_legacy_interleaved_raw_alloc` and `NativePgfOracle.TryEncodeLegacyInterleavedMode`,
generalizing the Bitmap-only synthetic writer across all 15 supported non-Bitmap modes while
retaining real native import/color/wavelet/macroblock machinery. The first matrix attempt appeared to
fail 12/15 modes, but investigation corrected that diagnosis: `TryDebugDecodeChannel` sized its
buffer through `TryGetDimensions`, whose `pgf_get_dimensions` export deliberately rejects
`Channels() != 4`; those 12 cases never reached decode. Adding the same header-info fallback would
have exposed the test process to `pgf_debug_decode_channel`'s separately documented repeated-call
`STATUS_ACCESS_VIOLATION`, so the automated matrix instead uses the already-safe
`TryDecodeRaw`/`pgf_debug_decode_raw`/`CPGFImage::GetBitmap` path, which already has the required
non-RGBA dimension fallback. No automated loop calls `GetChannel()`.

**Stage 3: All-mode safe-oracle matrix.** `PgfLegacyInterleavedAllModeTests` generates modern and
pre-Version5 native sibling fixtures from identical deterministic source bytes. For every mode it
compares the real native `GetBitmap` raw result and the managed normalized BGRA result across an
ordinary even 64x48 case and an odd 37x23 case; the odd case exercises the unequal HL/LH trailing
fixups. A separate 2049x1027 RGBA case asserts multiple wavelet levels and synthesizes 8,417,292
source bytes, exercising large multi-macroblock data without committing a fixture binary. Focused
verification: `dotnet test source/Sherland.Imaging.Pgf.Tests -- --filter-class
"*.PgfLegacyInterleavedAllModeTests"` — **16/16 passed** (15 mode cases plus the large case). Full
regression: `dotnet test source/Sherland.Imaging.Pgf.Tests` — **1376/1376 passed**.

**Stage 4: Documentation.** Reconciled the PRD's original pre-implementation assumptions with the
as-built native synthetic writer and dual-decoder rig, including the precise 12/15 false-failure root
cause and why the tempting dimension-fallback-only fix would have been unsafe. Updated
`docs/PGF-CODEC.md` to record all-mode legacy support, the even/odd/large coverage, the deliberate
`GetBitmap` oracle choice, the unavailable historical-encoder limitation, and the current
**1376/1376 passing** suite. Removed the stale `PgfConstants.InterBlockSize` claim that
`DecodeInterleaved` was not ported. All acceptance criteria are satisfied.
