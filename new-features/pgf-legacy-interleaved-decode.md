# PGF codec: legacy pre-Version5 interleaved decode — PRD

**Status: partially implemented incidentally by `pgf-bitmap-legacy-packed.md`; broad non-Bitmap
coverage remains to be staged.** Originally closed a gap documented in [`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s
"Not yet ported — real gaps against full C++ parity" list ("Legacy pre-Version5 entropy coding"). See
[`pgf-legacy-native-oracle-sourcing.md`](pgf-legacy-native-oracle-sourcing.md) for a real investigation
into whether a historical native oracle is obtainable for this gap specifically (short answer: no,
confirmed by exhausting every plausible source) — this PRD's self-consistency-only verification
approach below is written with that already confirmed, not as an open question anymore.

## Context

`PictTag.PgfCodec` is planned to be published as a standalone NuGet package (see
[`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s own opening note) — a real, stated goal that changes
the bar for what counts as in-scope. This port's decoder currently only implements the *modern*
Version5+ scheme, where the HL and LH subbands are each independently tiled and decoded via
`CDecoder::Partition` (already ported as `PgfDecoderCore.Partition`). Real PGF files written before
Version5 existed (missing the `Version5` bit in the preheader's version-flags byte) instead
interleave HL and LH together in the entropy-coded bitstream, decoded natively by
`CDecoder::DecodeInterleaved` (`Decoder.cpp:337-454`, declared `Decoder.h:127-133`) — currently not
ported at all, deliberately scoped out because no real digiKam thumbnail or this port's own encoder
has ever produced pre-Version5 output.

**Being honest about what this closes and what it doesn't**: this is a decode-only gap. Confirmed by
direct read of the vendored source: `EncodeInterleaved` does not exist anywhere in
`native/PictTag.PgfDecoder/libpgf` — grepping `Encoder.cpp`/`Encoder.h` finds no trace of it, not
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
  does. `InterBlockSize` (`PGFtypes.h:87`, `#define InterBlockSize 4`) — `PgfConstants.cs:46-48`'s
  existing forward-reference doc comment ("`DecodeInterleaved`'s tiling unit... itself is not
  ported") is confirmed accurate against the real source, not stale.
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
- **A real gap found during this PRD's own grounding, not previously documented**:
  `PgfDecodeSession.DecodeOneLevel` (`PgfDecodeSession.cs:254-268`) calls `PlaceTile` for HL/LH
  **unconditionally** — it never reads `PgfHeader.PgfPreHeader.VersionFlags` to check for `Version5`
  at all (confirmed: `Version5` appears nowhere in `PgfDecodeSession.cs`/`PgfImageDecoder.cs` outside
  comments; the only flag actually consulted at decode time today is `PGFROI`,
  `PgfDecodeSession.cs:111`). This means a genuine legacy file handed to this port today isn't
  rejected — it's **silently misdecoded** via the wrong subband layout, with no exception and no
  obviously-wrong output a caller could easily catch. This is worse than the "fails closed" posture
  this codec otherwise holds itself to everywhere else (managed-pgf-codec.md's Tier 5 framing) and is
  worth fixing as its own, first, small stage regardless of how the rest of this PRD goes.
- **Architecture fit, confirmed against the real current port**: everything a ported
  `DecodeInterleaved` needs already exists. `PgfSubband.Width`/`Height` (`PgfSubband.cs:28-30`),
  `AllocMemory()` (:260), `GetBuffer()` (:278, public) cover subband access;
  `PgfDecoderCore.DequantizeValue(Span<int> band, int bandPos, int quantParam)`
  (`PgfDecoderCore.cs:46`) already takes a flat buffer, so it works unchanged for both HL's and LH's
  buffers with no new primitives needed on `PgfSubband`/`PgfWaveletTransform`.
  `PgfWaveletTransform.GetSubband` (`PgfWaveletTransform.cs:74`) already returns both bands. This is
  small enough to live as a new method on `PgfDecoderCore` next to `Partition`, not a new file.
- **Testability — genuinely weaker than every other PRD in this project's history, stated plainly**:
  unlike ROI (which got a real independent cross-check just by adding one new `SetHeader(header,
  PGFROI)` flag combination — the encoder already knew how to *write* ROI-tiled output), there is no
  way to obtain genuine legacy-format bytes from the real native encoder at all (per Context above:
  `SetHeader`'s flags can only add bits, and `EncodeInterleaved` doesn't exist to invoke even if the
  bit could be forced). Manually patching just the version byte after a normal native encode does
  **not** work either — the *bitstream itself* would still be laid out in the modern per-subband
  tiled scheme, so a newly-ported `DecodeInterleaved` would misparse valid modern-format bytes while
  *believing* it's reading genuinely interleaved data, silently producing wrong output that looks
  like a decode bug in the new code when it would actually be a fixture-generation bug. The only
  viable option is a **test-only, C#-only "encode interleaved" fixture generator** invented for this
  PRD specifically (not a port of anything that exists in the reference, since nothing does) — see
  Goals/Test rig. This makes the whole feature self-consistency-verified only, with no independent
  oracle — flag this honestly in review, don't let it read as equivalent-confidence to the other PGF
  PRDs.

## Goals

1. **Fail closed on legacy files today, immediately, regardless of the rest of this PRD's fate**: make
   `PgfDecodeSession`/`PgfDecodeSession.TryOpen` (or `DecodeOneLevel`) check the `Version5` flag and
   return `null`/fail rather than silently misdecoding. This alone closes the "silent wrong output"
   risk found during grounding and should ship even if Stage 2+ below turns out not worth finishing.
2. **Port `CDecoder::DecodeInterleaved`** faithfully (including the HL/LH size-mismatch trailing
   fixups) as a new `PgfDecoderCore` method, wired into `PgfDecodeSession.DecodeOneLevel`'s dispatch
   once the `Version5` check (Goal 1) identifies a legacy file.
3. **A test-only fixture generator** ("encode interleaved," invented for this PRD, not a port of a
   real native function — see Context) sufficient to produce self-consistent legacy-format bytes to
   decode-test the new path against, since no independent oracle is obtainable (see "Why this needs
   to be grounded"'s Testability point).

## Non-goals

- **A production encode path for legacy/interleaved output.** The real reference implementation has
  never had one, for any build, ever (confirmed: no `EncodeInterleaved` anywhere in the vendored
  source) — nothing would ever have a reason to *write* pre-Version5 files, since Version5 has been
  the encoder's own baseline for the entire lifetime of this vendored codebase. The Goal 3 fixture
  generator is test infrastructure only, exactly like `pgf_encode_bgra_alloc`/`TryEncodeMode` are for
  other test rigs in this codebase — never exposed as a production API.
- **An independent cross-implementation oracle leg.** Explicitly acknowledged as unobtainable (see
  "Why this needs to be grounded"). This PRD's correctness claim rests on self-consistency
  (round-trip through the new test-only encoder) plus close, careful fidelity to the cited native
  decode algorithm — a genuinely weaker guarantee than `pgf-roi-support.md`'s native-cross-checked
  decode, and should be described that way wherever this work is referenced later, not glossed over.
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
- **Test-only fixture generator**: a new, clearly-test-scoped encoder (e.g. in
  `PictTag.PgfCodec.Tests` itself, or a small `internal` helper in `PictTag.PgfCodec` documented as
  test-infrastructure-only the way `PgfImageEncoder.TryEncodeMode` already is) that writes HL/LH
  coefficients in the same interleaved block order `DecodeInterleaved` expects to read, then omits
  the `Version5` flag. Derive its exact block-interleaving order directly from the decode algorithm's
  own read order (Goal 2's implementation), not independently — the two must agree by construction,
  which is exactly why this is self-consistency-only verification, not independent proof.

## Test rig

- **Fail-closed regression** (Goal 1): a `[Theory]`/`[Fact]` asserting a hand-constructed or
  synthetically-flagged pre-Version5 header is rejected (`TryOpen` returns `null`) rather than
  proceeding — this part *is* independently verifiable without a full legacy bitstream, since it only
  needs a valid header with the `Version5` bit cleared, not real interleaved payload bytes.
  Regression: the full existing suite must confirm zero behavior change for every real (Version5+)
  fixture already in this test rig.
- **Self-consistency round trip** (Goals 2/3): fixture generator writes a known test pattern
  (`TestBitmaps.Gradient`, matching this codec's own established convention) as interleaved,
  pre-Version5-flagged bytes; the new `DecodeInterleaved` path decodes it; compare pixel-for-pixel
  against decoding the *same* source pixels through the normal modern path (proves the interleaved
  decode reconstructs the same wavelet coefficients the modern scheme would, not just "round-trips
  through itself without crashing").
- **HL/LH size-mismatch edge cases specifically**: fixture dimensions chosen so HL and LH genuinely
  differ in width/height at some level (odd/prime dimensions, matching this test rig's own established
  `TestBitmaps.EdgeCaseDimensions` convention) — exercises the two documented trailing-fixup branches
  directly, not just the common equal-size case.

## Stage sequence

1. **Fail-closed `Version5` check** (Goal 1) — small, independently valuable, ships regardless of the
   rest. Exit test: rejecting a cleared-`Version5`-flag header; full regression suite stays green.
2. **Port `DecodeInterleaved`** (Goal 2) onto `PgfDecoderCore`, wired into `PgfDecodeSession`'s
   dispatch. Not yet independently testable beyond compiling and not regressing the modern path (no
   fixture exists yet).
3. **Test-only fixture generator** (Goal 3) — the interleaved-format encoder invented for this PRD.
4. **Self-consistency round-trip matrix**: the Test rig's own cases, including the size-mismatch
   edge cases, run and green.
5. **Documentation**: `docs/PGF-CODEC.md`'s "Not yet ported" list updated (move this item, honestly
   describing the self-consistency-only verification — don't imply native cross-checking that doesn't
   exist here); this PRD's own Progress log filled in.

## Acceptance criteria / Definition of Done

- A genuine pre-Version5 file no longer silently misdecodes — either fails closed (if only Stage 1
  ships) or decodes correctly (once Stage 2+ ships).
- The ported `DecodeInterleaved` reconstructs pixel-identical output to the modern decode path for
  the same underlying image content, across the size-mismatch edge cases specifically, verified via
  the test-only fixture generator (self-consistency).
- Zero regression on the existing (Version5+) test suite throughout.
- `docs/PGF-CODEC.md` updated, honestly describing this feature's weaker (self-consistency-only)
  verification relative to this project's other PRDs.

## Open questions

- **Where the test-only fixture generator should live** — inside `PictTag.PgfCodec` as an `internal`
  test-infrastructure type (mirroring `PgfImageEncoder.TryEncodeMode`'s own "exists for tests, not
  production" framing) or entirely inside `PictTag.PgfCodec.Tests` — resolve once Stage 3 shows how
  much of `PgfEncoderCore`'s existing machinery (bitplane encoding, macroblock writing) it can
  actually reuse vs. needing its own parallel copy.
- ~~Whether it's worth trying to obtain or build a real historical `libpgf` reference build (an old
  pre-2006 pre-Version5 release) purely to generate one real, independently-authored legacy fixture
  for extra confidence~~ — **answered**, see
  [`pgf-legacy-native-oracle-sourcing.md`](pgf-legacy-native-oracle-sourcing.md): no pre-Version5
  source is obtainable anywhere (SourceForge files/git/SVN, Debian's archive, and digiKam's complete
  2004-onward git history — whose own *first-ever* libpgf import in 2009 already postdates Version5's
  introduction — were all checked). Self-consistency-only verification isn't just the current best
  option, it's confirmed to be the only one; that doc's own "Finding A" also notes a free, low-cost
  extra: the 2009 vendored snapshot's commented-out `EncodeInterleaved` body is a second,
  independently-dated description of the interleaving order worth cross-reading against while
  implementing Goal 2/3, even though it can't be compiled as a real oracle.

## Progress log

**Incidental implementation while shipping `pgf-bitmap-legacy-packed.md`.** That PRD needed a valid
pre-Version5 Bitmap fixture to exercise its `yw=w2` stride. The investigation proved that clearing
Version5 alone invalidates a tiled payload, so it added a test-only native reverse-`DecodeInterleaved`
writer, then ported `CDecoder::DecodeInterleaved` into `PgfDecoderCore` and dispatched it for every
pre-Version5 session. Native-versus-managed decode is currently proven through legacy Bitmap fixtures,
including odd dimensions and a 2049x1027 case; this PRD remains partially open only because its own
broader multi-mode fixture matrix has not been separately run. See that PRD's Stage 1-3 entries and
commit `a18e1a9` for the complete implementation record.
