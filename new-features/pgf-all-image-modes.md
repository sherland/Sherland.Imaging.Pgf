# PGF codec: support for all image modes — PRD

**Status: not started.** Closes a gap documented in [`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s
"Explicitly out of scope" list ("Every color mode except RGBA/32bpp").

## Context

`PictTag.PgfCodec` (this codebase's managed `libpgf` port — see
[`new-features/managed-pgf-codec.md`](managed-pgf-codec.md)) only implements `ImageModeRGBA`/32bpp —
the one mode real digiKam thumbnails and this app's own encoder actually use. Every other mode the
native codec supports is out of scope today: `PgfConstants.cs:50-52` only defines
`ImageModeIndexedColor` (2, to reject it during header parse) and `ImageModeRGBA` (17);
`PgfDecodeSession.cs:63` fails closed on anything else. This PRD ports the rest, making
`PictTag.PgfCodec` a complete, general-purpose PGF codec rather than one scoped tightly to this app's
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
- The existing RGBA-only round-trip matrix and full `PictTag.PgfCodec.Tests` suite show zero
  regression throughout.
- `docs/PGF-CODEC.md` updated to reflect the new, broader mode support.

## Open questions

- **Whether a smaller quality sweep per mode (vs. the base PRD's full `0..MaxQuality`) is actually
  sufficient**, given bit-depth/mode correctness is expected to be largely orthogonal to quantization
  behavior — confirm this empirically on the first mode or two before committing to a reduced sweep
  for the rest, rather than assuming it holds universally.
- **Whether the native shim's mode-parameterized encode export is worth building once, generally, or
  per-mode as each stage needs it** — likely cheaper to build the general version once (Stage 1-ish)
  given every subsequent stage needs *a* fixture-generation path; revisit if it turns out more
  awkward than expected.
- **How much of `RGB12`/`RGB16`/`Bitmap`'s legacy-version branching is worth porting** if the native
  oracle itself can't easily be coaxed into producing every historical sub-variant to test against —
  don't guess at untested bit-packing logic; if a sub-variant can't be verified, document it as an
  explicitly-unverified/best-effort port rather than silently claiming the same correctness bar as
  everything else in this codebase.

## Progress log

_(Empty — fill in as each stage above is actually implemented and tested, following
`managed-pgf-codec.md`'s own progress-log convention: what was built, what was found, what broke and
how it was fixed, real test counts.)_
