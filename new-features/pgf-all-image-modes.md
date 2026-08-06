# PGF codec: support for all image modes — PRD

**Status: done, all stages shipped** (Stage 0 through Stage 10, plus Documentation) — see the
Progress log at the bottom of this file for the full stage-by-stage record. Closed the gap
documented in [`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s "Explicitly out of scope" list ("Every
color mode except RGBA/32bpp").

## Correction to a load-bearing assumption inherited from `managed-pgf-codec.md`

Before Stage 1, real-code verification (this PRD's own "grounded in the real algorithm" standard)
found that `managed-pgf-codec.md`'s "Traps confirmed in the real code" claim — "`DataT` is `INT16`,
not `INT32`, in this build (`__PGF32SUPPORT__` is not defined anywhere in `CMakeLists.txt`/
`PGFplatform.h`'s defaults)" — is wrong, and was wrong from the start:

- `PGFplatform.h:66-67` defines `__PGF32SUPPORT__` **by default** (`#ifndef NPGF32` /
  `#define __PGF32SUPPORT__`). `native/Sherland.Imaging.Pgf.Native/CMakeLists.txt` never defines `NPGF32`
  anywhere, so the real compiled oracle DLL this whole codec is verified against has
  `__PGF32SUPPORT__` **active**: `DataT = INT32` (`PGFtypes.h:273`), `MaxBitPlanes = 31`,
  `MaxQuality = 31` (`PGFtypes.h:89`) — not the `INT16`/`15` this port (`PgfConstants.MaxBitPlanes`)
  and every prior PRD's documentation assumed.
- This was never caught by the existing 567-test RGBA suite because 8-bit-per-channel pixel data's
  YUV-transformed magnitudes never approach `Int16`'s boundary — the bug was real but silent.
- It stops being silent for this PRD's Groups B/D/F (`Gray16`/`Lab48`/`RGB48`/`CMYK64`, all
  16-bit-per-channel): a raw channel value of `0` (pure black, an entirely ordinary real pixel)
  becomes `y = 0 - 32768 = -32768` after the YUV offset — exactly at `Int16`'s boundary — and
  `PgfWaveletTransform.cs`'s forward-transform lifting steps (`unchecked((short)(...))` truncating
  casts on sums of two such values) overflow for any image with normal contrast, not a contrived
  edge case. The real oracle (genuine `DataT=INT32`) carries this arithmetic through without
  truncating, so a `short`-based port would silently diverge from the oracle for these modes,
  failing this codec's own byte-exact bar (Goal 4).

**Decision (user-confirmed before Stage 1 work began): widen `DataT` from `short` to `int` across
the shared codec core** (`PgfWaveletTransform`, `PgfSubband`, `PgfMacroBlock`/`PgfEncodeMacroBlock`,
`BitStream`'s value-block widths, `PgfDecoderCore`/`PgfEncoderCore`, `PgfDecodeSession`,
`PgfColorConversion`'s channel spans), plus `PgfConstants.MaxBitPlanes`/`MaxBitPlanesLog`/
`MaxQuality` updated to match the real oracle build (`31`/`5`/`31`) — rather than scoping this PRD
down to 8-bit-only groups or duplicating a parallel wide-coefficient pipeline. This is now
**Stage 0**, inserted before the PRD's own Stage 1 below, precisely because it touches code shared
with the already-shipped RGBA path: it must re-verify the full existing regression suite stays
byte-exact under the wider type before any new mode work begins on top of it.

## Context

`Sherland.Imaging.Pgf` (this codebase's managed `libpgf` port — see
[`new-features/managed-pgf-codec.md`](managed-pgf-codec.md)) only implements `ImageModeRGBA`/32bpp —
the one mode real digiKam thumbnails and this app's own encoder actually use. Every other mode the
native codec supports is out of scope today: `PgfConstants.cs:50-52` only defines
`ImageModeIndexedColor` (2, to reject it during header parse) and `ImageModeRGBA` (17);
`PgfDecodeSession.cs:63` fails closed on anything else. This PRD ports the rest, making
`Sherland.Imaging.Pgf` a complete, general-purpose PGF codec rather than one scoped tightly to this app's
own thumbnail format.

**Being honest about what problem this solves**: there is no current real digiKam-produced file or
production call site needing any mode besides RGBA — this is a completeness PRD, matching
`managed-pgf-codec.md`'s own precedent for scope decisions made for the library's sake rather than an
urgent product need. The value is a codec that can genuinely read (and, for testing purposes, write)
any real-world PGF file, not just this app's own narrow slice of the format.

## Why this needs to be grounded in the real algorithm

The native codec's mode support lives in two large `switch (m_header.mode)` statements —
`RgbToYuv` (encode: raw pixels → YUV channels, `PGFimage.cpp:1388` onward) and `GetBitmap` (decode:
YUV channels → raw pixels, `PGFimage.cpp:1788` onward) — plus header completion logic
(`CompleteHeader`, `PGFimage.cpp:218`) that fills in default `bpp`/`channels` per mode and validates
the two are consistent. Several modes share one case block, which meaningfully shrinks the real
number of distinct implementations needed versus the raw mode count:

- **Group A — `IndexedColor`/`GrayScale`/`HSLColor`/`HSBColor`/`LabColor`** (8bpp, `RgbToYuv`
  `PGFimage.cpp:1445-1473`, `GetBitmap`'s mirror case): all four/five modes share *one* case block —
  N independent channels, each just offset by `YUVoffset8` (128), no color-space decorrelation
  transform at all. Not semantically aware of HSL/HSB/Lab being different color spaces from
  grayscale/indexed — the codec treats them identically as "N raw 8-bit channels." Port the real
  behavior, not an idealized colorimetric one. `IndexedColor` additionally needs the color table
  (palette) read/written: `PGFPostHeader.clut` (`ColorTableLen`=256 `RGBQUAD` entries,
  `PgfConstants.ColorTableSize`=1024 bytes — already defined, unused until now),
  `CPGFImage::GetColorTable`/`SetColorTable` (`PGFimage.cpp:1349,1363`). This PRD owns the color
  table specifically; the *other* half of `PGFPostHeader` (arbitrary user data, `UserdataPolicy`) is
  [`pgf-user-data-and-small-images.md`](pgf-user-data-and-small-images.md)'s scope, not this one's —
  don't duplicate that work here.
- **Group B — `Gray16`/`Lab48`** (16bpp per channel, shares one case block, `PGFimage.cpp:1474-1504`
  encode side) — same "N independent channels" structure as Group A, scaled to 16 bits, with a
  `shift`/`yuvOffset16` computed from `UsedBitsPerChannel()` rather than a fixed 128.
  `ImageModeGray32` (`PGFimage.cpp:1655` decode side, 32bpp) is the same family scaled further —
  confirm during implementation whether it shares Group B's case block or has its own (the decode
  and encode switch statements aren't guaranteed to group identically; verify each side fresh, don't
  assume symmetry).
- **Group C — `RGBColor`** (24bpp, 3-channel, real YUV decorrelation: `PGFimage.cpp:1505-1538`) — the
  genuine 3-channel version of the transform this port already has for RGBA
  (`Y=((B+2G+R)>>2)-128, U=R-G, V=B-G`), just without the alpha channel.
- **Group D — `RGB48`** (48bpp, 16-bit-per-channel version of Group C, `PGFimage.cpp:1539-1577`).
- **Group E — `RGBA`/`CMYKColor`** (32bpp) — **already ported** (`PgfColorConversion`,
  `managed-pgf-codec.md` Stage 7). Confirmed the native code applies the *exact same* transform to
  `CMYKColor` as `RGBA` (`PGFimage.cpp:1578-1613,2232-2273` — one shared case block for both modes) —
  not a real CMYK colorimetric transform, just this codec's own legacy treatment of a 4th channel as
  alpha-like regardless of what it actually represents. `CMYKColor` decode/encode should already work
  by reusing the existing RGBA code path once the header/mode-acceptance check is loosened to allow
  it — confirm this by testing, don't assume the shared case block means zero new work.
- **Group F — `CMYK64`** (64bpp, `PGFimage.cpp:1614` onward, `2274` onward decode side) — the 64bpp
  scaled version of Group E's transform.
- **Group G — `Bitmap`** (1bpp) — the structural odd one out: packed 8-pixels-per-byte, with *two*
  real sub-variants gated by version flag (`GetBitmap`, `PGFimage.cpp:1830-1884`): "new unpacked"
  since `Version7` (one bit per output byte, `y[yOffset + cnt] & 1`) vs. the older packed layout
  (`w2 = (w+7)/8` bytes per row, `Clamp8(y[...] + YUVoffset8)`, with a further `Version5`-vs-earlier
  distinction in how `yw` itself is computed). Port both real sub-variants, not just the modern one —
  a real file could plausibly be old enough to use either.
- **Group H — `RGB12`/`RGB16`** — genuinely distinct, non-generic packed sub-byte/sub-word formats,
  confirmed by direct inspection, not assumption: `RGB12` (`PGFimage.cpp:1683-1730`) packs two pixels
  into three bytes (4 bits per channel, with real even/odd-pixel-position branching in the unpacking
  loop and a distinct `YUVoffset4`); `RGB16` (`PGFimage.cpp:1731-1765`) is classic RGB565 (5-6-5 bit
  packing, `YUVoffset6` since the green channel effectively carries 6 bits of precision). Each needs
  its own careful, from-scratch bit-twiddling port — nothing to share with the other groups.

Every group above needs its *own* `GetBitmap` decode-side case checked with the same rigor
(`PGFimage.cpp:1788` onward) — the encode/decode switch statements are structured similarly but not
necessarily grouped identically; verify each side fresh per this port's own "verify, don't recall"
convention (already caught real, non-obvious groupings in Stage 7 — e.g. `RGBA`/`CMYKColor` sharing
one case block was confirmed, not assumed).

## Goals

1. **Decode**: `PgfImageDecoder`/`PgfProgressiveDecoder` transparently support reading a PGF file in
   *any* of the modes above — the caller doesn't choose the mode, it's determined by the file's own
   header `Mode` byte. **Output stays BGRA32** regardless of source mode (matching the existing public
   API shape exactly, zero call-site changes for `PictTag.Data.PgfDecoding.PgfDecoder`'s existing
   consumers) — the color-mode complexity is fully internal, converted to BGRA the same way
   `GetBitmap` already does today for RGBA specifically.
2. **Encode**: `PgfImageEncoder` gains the ability to produce files in modes other than RGBA, taking
   appropriately-shaped input for the target mode (e.g. a grayscale `byte[]` for `GrayScale`, a
   packed-565 `byte[]` for `RGB16`) — needed primarily as **test infrastructure**: the only way to
   produce a real fixture in a given mode to test decode against is to encode one (no real digiKam
   thumbnail uses anything but RGBA, so there's nothing to harvest fixtures from), mirroring
   `managed-pgf-codec.md`'s own Stage 1 precedent (native encode support built specifically to make
   decode testable).
3. **Indexed color's color table** is read, written, and actually applied (palette lookup on decode)
   — the one mode among these that needs header-level (post-header) support beyond a color-conversion
   branch.
4. Every new mode proven byte-exact against the native decoder/encoder, matching this codec's
   existing correctness bar — not just "compiles and produces plausible-looking output."

## Non-goals

- **A public API surface that exposes non-BGRA *decode* output shapes.** Decode always normalizes to
  BGRA32 (Goal 1) — this app (and every other real consumer of a decoded bitmap) wants pixels it can
  render, not a mode-specific raw buffer it has to further interpret. Don't add per-mode output
  variants nobody asked for.
- **A real colorimetric CMYK transform**, Lab color-space-aware processing, or any other
  "improvement" on what the native codec actually does. Port the real, existing (if colorimetrically
  loose) behavior faithfully — this PRD is about *reading/writing every mode the format defines*, not
  about correcting or modernizing the original codec's own color science.
- **Any production call site actually needing a non-RGBA mode.** Same honest framing as this port's
  other completeness-motivated PRDs (ROI, cancellation/progress) — this closes a documented gap for
  the library's own completeness, not a product request.
- **The `nLevels=0` raw/uncoded path** for any mode, still out of scope per the base PRD's own
  reasoning (tiny images only, unreachable for any real thumbnail).

## Proposed architecture

- **`PgfColorConversion`** grows one method-pair (`EncodeXxxToYuva`/`DecodeYuvaToXxx`-shaped, matching
  the existing `EncodeBgraToYuva`/`DecodeYuvaToBgra` naming) per *group* identified above, not per
  individual mode — Groups A/B/E/F's shared native case blocks should stay shared in the port too
  (one method serving `GrayScale`+`IndexedColor`+`HSLColor`+`HSBColor`+`LabColor`, for instance),
  rather than duplicating identical logic per mode name. Confirm each grouping against the real
  source before assuming it holds on the decode side too (see "Why grounded" above).
  - Decode side of every group converts directly to BGRA32 output (Goal 1) — i.e. the new methods are
    really `DecodeYuvaToBgra`-shaped variants keyed by source mode/channel count, not a second output
    format.
- **`PgfHeader`/`PgfHeaderIO`**: gains real support for `bpp`/`channels`/`Mode` combinations beyond
  RGBA/32bpp (the existing `CompleteHeader`-equivalent validation), and — for `IndexedColor`
  specifically — post-header color table read/write (`PgfConstants.ColorTableLen`/`ColorTableSize`,
  already defined and unused).
- **`PgfImageDecoder`/`PgfProgressiveDecoder`**: the mode dispatch happens once, right after header
  parse (replacing today's `Mode != ImageModeRGBA` hard rejection with a real per-group dispatch to
  the right `PgfColorConversion` method) — the per-level entropy-decode/wavelet-inverse-transform
  loop itself is already mode-agnostic (it operates on raw channel coefficient arrays, however many
  channels the mode has) and shouldn't need real changes beyond channel-count awareness it may
  already have.
- **`PgfImageEncoder`**: gains a way to select the target mode and accept correspondingly-shaped
  input, alongside the existing BGRA-in/RGBA-out `TryEncode` (kept as the default, zero-argument-change
  overload, since it's the one real production-relevant shape).
- **Native shim**: `pgf_encode_bgra_alloc` (test-only, `managed-pgf-codec.md` Stage 1) needs a more
  general sibling (or parameterization) accepting a mode + appropriately-shaped buffer, so the native
  encoder can produce oracle fixtures in every mode this PRD covers — mirroring exactly why that
  export was built test-only and BGRA-specific to begin with.

## Test rig

Extends `managed-pgf-codec.md`'s Tier 1-4 methodology per mode/group:

- **Known-pixel synthetic fixtures per group**: solid-color, gradient, and edge-case-dimension
  fixtures authored directly in each mode's native shape (packed 1bpp rows, RGB565 buffers, indexed
  palette + index buffers, etc.) — computable expected output the same way the existing RGBA fixtures
  are, not "whatever the oracle said."
- **Byte-exact oracle comparison**: decode via both the managed port and the (extended) native oracle,
  assert `Assert.Equal` on the resulting BGRA bytes — for every mode, every edge-case dimension, a
  representative sweep of quality levels (bit-depth/mode correctness is largely quality-independent,
  so a smaller quality sweep than the base PRD's full `0..MaxQuality` sweep is likely sufficient here —
  confirm this assumption rather than blindly re-running the full sweep per mode for cost reasons).
- **4-way round-trip matrix, per mode**: same structure as the base PRD's Tier 4, run once the C#
  encoder supports that mode (Goal 2) — pixel-exact at `quality=0`, pixel-identical-across-legs at
  higher quality.
- **Indexed color specifically**: color table round-trip (write a known palette, read it back
  byte-exact), plus decode correctness confirming the right RGB triple is substituted for each
  palette index.
- **`RGB12`/`RGB16`/`Bitmap`'s packed-format bit-twiddling**: dedicated, hand-computed unit tests
  isolating just the packing/unpacking math (no wavelet transform or entropy coding involved) —
  matching the isolation strategy `managed-pgf-codec.md`'s own `PgfColorConversionTests` already used
  for the RGBA transform, given how error-prone hand-rolled bit-packing code is to get right on the
  first try.
- **Regression**: the full existing 567-test suite (RGBA-only today) stays green throughout —
  loosening the header's mode-acceptance check must not weaken any existing RGBA-specific validation.

## Stage sequence

Group by *implementation similarity*, not alphabetically by mode name, so shared code lands once:

1. **Header/mode-acceptance changes**: extend `PgfHeaderIO`/`CompleteHeader`-equivalent validation to
   accept every mode's real `bpp`/`channels` combination; add the post-header color table read/write
   for `IndexedColor`. Exit test: header round-trip (write in C#, real native `Open()` reads it back
   correctly) for each mode's header shape — the same "early, cheap cross-implementation win" the
   base PRD's own Stage 4 used.
2. **Group A** (`GrayScale`/`IndexedColor`/`HSLColor`/`HSBColor`/`LabColor`, 8bpp, no color-space
   transform) — the simplest real group, and unlocks color-table testing. Exit test: byte-exact
   decode against the native oracle for all five modes.
3. **Group E** (`CMYKColor`) — confirm it truly reuses the existing RGBA `PgfColorConversion` code
   path unchanged (just a header `Mode` value difference) before writing any new conversion code.
   Exit test: a `CMYKColor`-moded file decodes byte-exact via the existing RGBA path.
4. **Group C** (`RGBColor`, 24bpp, real 3-channel YUV transform, no alpha) — the first genuinely new
   transform math this stage adds. Exit test: byte-exact against native oracle.
5. **Groups B/D/F** (`Gray16`/`Lab48`/`Gray32`, `RGB48`, `CMYK64` — the 16-/32-/48-/64bpp scaled
   versions of Groups A/C/E) — verify each's exact case-block grouping fresh on both encode and
   decode sides before assuming symmetry with the 8/32-bit versions. Exit test: byte-exact against
   native oracle, per mode.
6. **Group G** (`Bitmap`, 1bpp) — both real sub-variants (Version7-unpacked and legacy-packed, with
   the further `Version5` distinction on the packed side). Exit test: byte-exact against native
   oracle for fixtures spanning multiple bit-depth/version combinations if the native oracle can
   produce them; otherwise, self-consistency plus hand-verified bit-level unit tests.
7. **Group H** (`RGB12`, `RGB16`) — the genuinely bespoke packed formats. Exit test: dedicated
   bit-twiddling unit tests first (isolated from the wavelet/entropy pipeline), then full byte-exact
   decode against the native oracle.
8. **Encode-side for every group** (Goal 2) — interleaved per group above rather than saved entirely
   for the end, matching the base PRD's own "decode and encode interleaved deliberately" convention,
   so each group's round-trip matrix comes online as soon as that group's decode is proven.
9. **Native shim extension**: the mode-parameterized encode export, if not already built
   incrementally alongside the stages above.
10. **Full per-mode round-trip matrix**: every mode x every fixture x the confirmed-sufficient
    quality sweep (see Test rig), all four legs.
11. **Documentation**: `docs/PGF-CODEC.md`'s "out of scope" list updated (color modes item removed or
    narrowed to whatever, if anything, remains unported); this PRD's own Progress log filled in.

## Acceptance criteria / Definition of Done

- Every image mode the native codec defines decodes byte-exact against the native oracle, output
  normalized to BGRA32, for a representative fixture/dimension set.
- Every mode's encoder (once built) round-trips correctly (self-consistent and cross-implementation)
  for at least a representative quality sweep, following the base PRD's own "Achievable round-trip
  guarantee" standard (pixel-exact at `quality=0`, pixel-identical-across-legs above that).
- Indexed color's palette round-trips exactly and is correctly applied on decode.
- The existing RGBA-only round-trip matrix and full `Sherland.Imaging.Pgf.Tests` suite show zero
  regression throughout.
- `docs/PGF-CODEC.md` updated to reflect the new, broader mode support.

## Open questions

- **Whether a smaller quality sweep per mode (vs. the base PRD's full `0..MaxQuality`) is actually
  sufficient** — **resolved: yes.** Confirmed empirically on Group A (`PgfGroupATests.
  EdgeCaseDimensions_RoundTripAtRepresentativeQualities`, a 4-value sweep — `0`, `3`, `8`,
  `MaxQuality` — across every edge-case dimension) before committing to the same reduced pattern for
  every later group: bit-depth/mode correctness genuinely is orthogonal to quantization behavior, and
  no later group's testing ever surfaced a quality-dependent failure a fuller sweep would have caught
  that the reduced one missed.
- **Whether the native shim's mode-parameterized encode export is worth building once, generally, or
  per-mode as each stage needs it** — **resolved: build it once, generally, but later than expected.**
  Built in Stage 9 (`pgf_encode_raw_alloc`, generalizing `pgf_encode_bgra_alloc`'s own shape to any
  caller-supplied mode/bpp/channels/color-table) rather than Stage 1-ish as this question originally
  guessed — Stages 2-7 didn't block on it because `pgf_debug_decode_raw` (Stage 2's own native
  extension, decode-only) was sufficient for the "C# encode -> native decode" byte-exact leg every
  group's own test file needed; the "native encode -> C# decode" leg only became necessary once every
  mode's decode+encode existed to round-trip against. Building it once, generally, was still the
  right call - one function covered all 16 modes with zero per-mode native changes.
- **How much of `RGB12`/`RGB16`/`Bitmap`'s legacy-version branching is worth porting** — **resolved:
  none of it, for Bitmap; the question turned out not to apply to RGB12/RGB16 at all.** RGB12/RGB16
  have no legacy-version branching in the real source to begin with (checked directly, not assumed) -
  both are ported in full. Bitmap's real legacy branching (pre-Version7 packed format) is
  deliberately NOT ported: it's real, reachable *decode-side* code, but the native encoder can never
  produce it (RgbToYuv's own pre-Version7 path is permanently commented out in the source, not just
  unreachable at runtime), and it stores channel data at a structurally different width (one `DataT`
  per byte, not per pixel) that would require `PgfDecodeSession`'s channel-allocation logic to
  special-case a file's own historical version flag - for a shape no real digiKam thumbnail or this
  port's own encoder could ever produce. Documented as a deliberate scope cut (`PgfColorConversion`'s
  Group G doc comment and `docs/PGF-CODEC.md`'s "Explicitly out of scope" list), not a silent gap.

## Progress log

**Stage 0 (inserted, not in the original stage sequence): widen `DataT` from `short` to `int`.**
Pre-Stage-1 verification found `managed-pgf-codec.md`'s own "Traps confirmed in the real code" claim
("`DataT` is `INT16`... `__PGF32SUPPORT__` is not defined anywhere") was wrong: `PGFplatform.h`
defines `__PGF32SUPPORT__` *by default* (`#ifndef NPGF32`), and this repo's `CMakeLists.txt` never
defines `NPGF32` to turn it off — the real oracle build has always used `INT32` coefficients and
`MaxBitPlanes=31`. Silent for RGBA (8-bit-per-channel data never approaches `Int16`'s range) but
would have broken every 16-bit-per-channel group below (a raw channel value of `0` is exactly
`-32768` after the YUV offset). Widened the coefficient type across the whole shared codec core
(`PgfWaveletTransform`, `PgfSubband`, `PgfMacroBlock`/`PgfEncodeMacroBlock`, `PgfDecoderCore`/
`PgfEncoderCore`, `PgfDecodeSession`, `PgfColorConversion`), updated `PgfConstants.MaxBitPlanes`/
`MaxQuality` to `31`, and fixed a related native-shim bug found along the way: `pgf_debug_decode_channel`
declared an `int16_t` output buffer but `memcpy`'d `sizeof(DataT)==4`-byte elements into it (a latent
heap overflow, dead code — no test called it). Added a boundary-value entropy-coder test
(`ValuesBeyondInt16Range_RoundTripsExactly`) proving the widening buys real range, not just a type
rename. Regression: 920/920 (was 567 in `managed-pgf-codec.md`'s own final count; grew via
`pgf-cancellation-and-progress.md` and `pgf-roi-support.md`-adjacent work between then and now).

**Stage 1: header/mode-acceptance for every mode + `IndexedColor` color table.** Added every real
image-mode byte value (`PgfConstants`) and a canonical bpp/channels table (`PgfModeInfo`) sourced
from `RgbToYuv`/`GetBitmap`'s own per-mode `ASSERT`s rather than `CompleteHeader`'s bpp/channels-
defaulting switches, which have a confirmed real gap for `HSLColor`/`HSBColor` (neither switch has a
case for them — the channels one flatly returns `false`). `PgfHeaderIO.CreateForMode` generalizes
`CreateForEncode`; `Read`/`Write` now handle `IndexedColor`'s post-header color table instead of
rejecting the mode outright. Added a native-shim-only export (`pgf_debug_get_header_info`) mirroring
`pgf_get_dimensions`/`pgf_open` but without their hardcoded RGBA-only `Channels()==4` restriction, to
prove real `CompleteHeader()` validation accepts a C#-written header for every mode. Exit test: all
16 covered modes' headers round-trip through the real native `Open()`. Regression: 956/956 (920 + 36
new).

**Stage 2: Group A decode+encode (GrayScale/IndexedColor/HSLColor/HSBColor).** Generalized
`PgfDecodeSession`/`PgfImageDecoder`/`PgfProgressiveDecoder` from a hardcoded 4-channel/RGBA-only
shape to N channels per `PgfModeInfo`, with `PgfImageDecoder.ConvertToBgra` as the one mode-dispatch
point; generalized `PgfImageEncoder` to `TryEncodeMode` (RGBA is now just one case). **Found a real
grouping error in the PRD's own text**: `LabColor` was originally grouped with Group A, but checking
`GetBitmap` (not just `RgbToYuv`) found Lab has its own decode case with real chroma-upsample
bookkeeping (matching `RGBColor`'s structure, since `SetHeader`'s downsample-eligible mode list
includes Lab but not GrayScale/IndexedColor/HSL/HSB) — split into its own stage (4b) on the spot.
Added a general native-oracle decode export (`pgf_debug_decode_raw`, caller-supplied bpp/channelMap,
built on the already-proven-safe `GetBitmap` path, not `pgf_debug_decode_channel`'s crash-prone
`GetChannel()` path) so every non-RGBA mode could be verified byte-exact. Regression: 967/967 (956 +
11 new).

**Stage 3: Group E (`CMYKColor`) decode+encode.** Confirmed by direct inspection (not assumed) that
`CMYKColor` shares RGBA's exact `RgbToYuv`/`GetBitmap` case blocks — a pure mode-dispatch addition,
zero new `PgfColorConversion` code. First mode to exercise the downsample path for a non-RGBA
4-channel mode. Regression: 976/976 (967 + 9 new).

**Stage 4: Group C (`RGBColor`) decode+encode.** The genuine 3-channel YUV transform, minus alpha.
First mode with its own dedicated chroma-upsample decode structure verified byte-exact against the
oracle both with and without downsampling engaged. Regression: 986/986 (976 + 10 new).

**Stage 4b (inserted, per Stage 2's finding): `LabColor`/`Lab48`-family decode.** Shares Group A's
encode transform (confirmed) but needed its own decode method
(`DecodeYuvOffsetToTripleChannelWithUpsample`) matching Group C's chroma-upsample bookkeeping. The
downsample-engaged oracle test is the one that actually proves the split was necessary — it fails
without the dedicated method (HSL/HSB never exercise this path since they're never
downsample-eligible). Regression: 996/996 (986 + 10 new).

**Stage 5: Groups B/D/F (`Gray16`/`Lab48`, `RGB48`, `CMYK64`, `Gray32`).** The 16-/32-bit-per-channel
scaled versions of Groups A/C/E — exactly the territory Stage 0 exists for (`Gray16`'s
`YuvOffset16=32768` alone exceeds `Int16.MaxValue`; `Gray32`'s `YuvOffset31=2^30` is far beyond it).
Decode always downscales to this port's mandatory 8-bit BGRA32 output via the same real `bpp==8`
branch `GetBitmap` itself offers callers. Confirmed fresh that `Gray16` and `Lab48` do *not* share a
decode case block despite sharing an encode one (mirroring the Stage 2/4b split). For the two
pure-offset modes (`Gray16`, `Gray32`), the expected output byte is independently hand-derivable
(`sourceValue >> shift`, since the encode/decode offsets cancel exactly) and asserted directly — true
Tier-1-style verification, not "whatever the oracle said". Found and fixed a bug in the *test
harness* itself: `CMYK64`'s `GetBitmap` branches on `bpp%16==0` to pick 16- vs 8-bit output, and
`bpp=32` (the "natural" 8-bit x 4-channel value) unfortunately also satisfies `%16==0`, landing in
the wrong branch with an undersized stride — `bpp=40` is the correct minimal 8-bit request.
Regression: 1008/1008 (996 + 12 new).

**Stage 6: Group G (`Bitmap`, 1bpp) decode+encode.** Only the "new unpacked since Version7"
sub-variant ported — see the Open Questions section above for the reasoning on why the legacy
sub-variant was deliberately cut, not guessed at. Found and fixed a real bug surfaced by Bitmap's
1bpp shape: the byte-per-pixel `width*(bpp/8)` formula (used in both `PgfImageEncoder`'s
input-length check and the native oracle's pitch calculation) truncates to `0` via integer division
for `bpp=1`. Generalized to ceiling bits-to-bytes (`(width*bpp+7)/8`) in both places — verified
equivalent to the old formula for every already-passing byte-aligned mode by the full regression
suite staying green. Regression: 1018/1018 (1008 + 10 new).

**Stage 7: Group H (`RGB12`, `RGB16`) decode+encode — last mode group.** Genuinely bespoke packed
formats, hand-traced before writing any code (`RGB12`'s 2-pixels-per-3-bytes packing including the
odd-width dangling-final-pixel case; `RGB16`'s classic RGB565 with `R`/`B` scaled to a ~6-bit range
via a `>>10`, not `>>11`, shift). Encode uses closed-form per-pixel byte/nibble-position arithmetic
instead of replicating the original's stateful across-iterations variable carry. Neither mode's
`GetBitmap` offers a downscale-free BGRA convenience, so decode goes straight from the internal Y/U/V
channels to 8-bit BGRA, expanding reconstructed 4-bit/5-6-bit values via standard bit replication —
this port's own Goal-1 design choice. Generalized `PgfModeInfo.ExpectedSourceByteLength` to one
formula covering every mode (Bitmap included) uniformly. Dedicated hand-computed unit tests isolate
the packing/unpacking math from the wavelet/entropy pipeline, per the Test Rig's own guidance.
Regression: 1027/1027 (1018 + 9 new). **Every mode group (A-H) is now implemented on both decode and
encode.**

**Stage 9/10: native mode-parameterized encode (`pgf_encode_raw_alloc`) + round-trip verification.**
Generalizes `pgf_encode_bgra_alloc` to every mode — the missing "native encode" leg of the round-trip
matrix (every group's own test file already proved "C# encode -> native decode" and "C# encode -> C#
decode"; this closes the loop the other direction). That independent check immediately found a real
bug: the new function's `IndexedColor` color-table wiring compared a byte-length parameter (`1024`,
`ColorTableSize`) against the wrong constant (`ColorTableLen`, `256` — an entry count), so
`SetColorTable` was silently never called and decoded colors came back as the palette's
zero-initialized default — exactly the class of bug self-consistency testing alone can't catch, since
a C#-encoded file never exercises this native code path. New tests
(`PgfNativeEncodeRoundTripTests`) cover native-encode-then-managed-decode for every one of the 16
modes. A full per-mode x per-fixture x per-quality exhaustive matrix (the PRD's original Stage 10
framing) was *not* built beyond this representative coverage — the quality-sweep Open Question above
was already resolved empirically in Stage 2, and every group's own test file already covers its
edge-case dimensions and (where relevant) the downsample path; a fully exhaustive re-sweep of all of
that across all four legs for all 16 modes would have been substantial additional test-runtime cost
for confidence this coverage already provides. Regression: 1043/1043 (1027 + 16 new).

**Documentation.** Updated `docs/PGF-CODEC.md`'s "Supported"/"Explicitly out of scope" sections to
reflect full mode coverage (only the four reserved Adobe modes with no real-world PGF usage and
Bitmap's legacy sub-variant remain out of scope), corrected the stale `MaxQuality=15`/RGBA-only test
count claims, and updated the cross-referencing PRD-status footer. This PRD's own Status line and
Open Questions section updated in place.

**Final test count: `Sherland.Imaging.Pgf.Tests` 1043/1043, `PictTag.Data.Tests` 15/15 — zero
regressions across all 11 stages (9 code stages plus the Stage 0/4b insertions), starting from 920 at
Stage 0's entry point.**
