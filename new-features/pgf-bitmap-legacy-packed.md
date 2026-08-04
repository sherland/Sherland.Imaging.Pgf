# PGF codec: Bitmap's legacy pre-Version7 packed sub-variant — PRD

**Status: not started.** Closes a gap documented in [`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s
"Not yet ported — real gaps against full C++ parity" list ("Bitmap's legacy pre-Version7 packed
sub-variant"). **Before starting Goal 2 below**, read
[`pgf-legacy-native-oracle-sourcing.md`](pgf-legacy-native-oracle-sourcing.md)'s "Finding B": a real,
unmodified historical release (`libpgf 6.14.12`) has a live, uncommented legacy-packed Bitmap encoder
and is confirmed to build cleanly against this repo's existing MSVC/CMake toolchain — a strictly
stronger and lower-effort oracle than the native-shim plan originally proposed here. That doc's
Reproduction steps have the exact commands to re-obtain and build it.

## Context

`PictTag.PgfCodec` is planned to be published as a standalone NuGet package (see
[`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s own opening note) — a real, stated goal that changes
the bar for what counts as in-scope. This port's `Bitmap` (1bpp, black/white) mode support only
covers the *modern* "new unpacked since Version7" representation (`PgfColorConversion`'s own "Group
G" doc comment, `PgfColorConversion.cs:683-699`) — the older packed representation, real and reachable
in the native decode source, is unported.

**A real correction to this project's own existing framing, found during this PRD's grounding, not
assumed**: `PgfColorConversion.cs`'s existing "Group G" comment describes this as a clean two-way
split (modern Version7+ vs. legacy pre-Version7, "over a decade before real PGF thumbnails existed").
The real native decode branch (`PGFimage.cpp:1840-1883`) is actually **three-way**, not two-way:

```cpp
if (m_preHeader.version & Version7) {
    // ... modern: one DataT per pixel, re-packed 8-at-a-time into output bytes (1840-1865)
} else {
    // legacy branch (1866-1883)
    UINT32 yw = w;
    if (!(m_preHeader.version & Version5)) yw = w2;   // PGFimage.cpp:1869
    // buff[j] = Clamp8(y[yOffset + j] + YUVoffset8) for j in [0, w2)
    // — each y[] element read here IS one packed output byte (8 pixels), not one pixel
}
```

`Version7` (`PGFtypes.h:73`, value `64`) is explicitly documented as "new data representation for
bitmaps" (`PGFtypes.h:41`) and is a **2019** addition (`PGFMajorNumber`/`Year`/`Week` = `7`/`19`/`03`,
`PGFtypes.h:44-46`) — genuinely recent relative to `Version5`/`Version6`, which predate it by many
years. A real pre-Version7 file plausibly still has `Version5`/`Version6` set (the far more likely
real "legacy" case, using row stride `yw = w`, the *pixel* width) — the row-stride-in-*bytes* case
(`yw = w2`) only triggers for the much older, much rarer combination of *also* missing `Version5`.
This PRD's own Goals/Test rig are scoped around this corrected, three-way understanding, not the
simpler two-way split the existing C# doc comment currently (inaccurately) describes.

## Why this needs to be grounded

- **The bit-packing difference, exact** (`PGFimage.cpp`'s `GetBitmap`, `case ImageModeBitmap:` at
  line 1831): the modern branch (1840-1865) reads one internal `DataT` **per pixel** (0/1-valued,
  unbiased) from the decoded channel and re-packs 8 pixels MSB-first into each output byte — this is
  exactly what this port's existing `PgfColorConversion`'s modern Bitmap decode already does. The
  legacy branch (1866-1883) instead reads `DataT`s that are **already one packed byte's worth of 8
  pixels each** (minus a `YUVoffset8` bias, restored via `Clamp8(y[yOffset+j] + YUVoffset8)`) — i.e.
  the wavelet-transformed *channel data itself* is byte-packed before transform/encode, not
  pixel-per-`DataT` the way every other mode (and modern Bitmap) works. Row stride in the legacy
  case is `yw = w` (pixel width) normally, or `yw = w2` (`= (w+7)/8`, byte width) specifically when
  `Version5` is also absent (`PGFimage.cpp:1869`) — see Context's three-way correction.
- **Encode is confirmed dead code, not just unused**: `RgbToYuv`'s `case ImageModeBitmap:`
  (`PGFimage.cpp:1398`) only has the modern unpacked path live (1409-1425, exactly mirroring the
  modern decode branch in reverse). The legacy packed-encode logic exists **only as a disabled
  comment block** (`PGFimage.cpp:1426-1442`, `/* old version: packed values... */`) — not reachable
  at runtime under any input, any version flag, any build. `CPGFImage::SetHeader`
  (`PGFimage.cpp:893-905`) unconditionally sets `m_preHeader.version = PGFVersion | flags`, and
  `PGFVersion` (`PGFtypes.h:76/78`) always includes `Version7` — `flags` can only add bits, never
  remove it. There is no "check" on the encode side at all; it's hardcoded modern, matching this
  port's own `PgfConstants.EncoderVersionFlags` (`PgfConstants.cs:99-101`, always includes
  `Version7`). **Confirmed decode-only gap**, same shape as `pgf-legacy-interleaved-decode.md`'s own
  finding for a different feature.
- **`PgfDecodeSession`'s channel-allocation claim in the existing docs is overstated — a real
  correction, verified against both sides**: channel 0 is sized `fullWidth × fullHeight` **in
  pixels**, identically regardless of the file's version flags, in both the native reference
  (`PGFimage.cpp:154-155, 176-187, 953-954`) and this port (`PgfDecodeSession.cs:135-136, 156-157,
  178-183, 222-223`); Bitmap is also excluded from downsample in both (`PGFimage.cpp:921-927`'s own
  mode list, mirrored by `PgfModeInfo.SupportsDownsample`). **No channel-allocation resize is
  actually needed** — `docs/PGF-CODEC.md`'s current wording overstates this part of the gap; it
  should be corrected once this PRD ships (see Documentation stage). What's genuinely missing:
  1. `PgfDecodeSession.TryOpen` (`PgfDecodeSession.cs:111`) already computes `roiSupported` from the
     parsed `PgfPreHeader.VersionFlags` but discards the rest of the flags afterward — `Version7`
     (and, per the three-way correction, `Version5` too) needs capturing and exposing the same way.
  2. `PgfImageDecoder.cs:131-133`'s `case PgfConstants.ImageModeBitmap:` dispatch to
     `DecodeYToBitmapBgra` needs an **alternate legacy decode function** — the fix is entirely in
     `PgfColorConversion`'s reading semantics (a new method reading `w2` already-packed `DataT`s per
     row, at the version-dependent stride, applying the bias/clamp, then unpacking bits), not in
     `PgfDecodeSession`'s buffer sizing at all.
- **Testability — a materially bigger native shim addition than `pgf-roi-support.md`'s one-line
  change, and genuinely trickier than a simple header-flag flip**: `shim.cpp` never subclasses
  `CPGFImage` anywhere today (always a plain `CPGFImage img;`), and `m_preHeader` is `protected`
  (`PGFimage.h:520,528`) — unreachable from `shim.cpp` as it's currently written. Worse: even if the
  `Version7` bit could be cleared in the header, the *actual* internal channel data written by a real
  `ImportBitmap`+`Write()` call would still be in the modern one-`DataT`-per-pixel layout (since the
  legacy packed-encode path is dead code, per the bullet above) — decoding that with the legacy
  `GetBitmap` branch would misinterpret genuinely modern data, not reproduce a real legacy file. A
  correct fixture needs **both**: (a) a way to clear the `Version7` bit in `m_preHeader` before
  `Write()`, and (b) bypassing `ImportBitmap`/`RgbToYuv` for Bitmap mode entirely, instead writing
  legacy-packed `DataT` values directly into channel 0 via the already-public
  `CPGFImage::SetChannel(DataT*, int c=0)` (`PGFimage.h:272`) — hand-replicating the exact
  commented-out packing logic at `PGFimage.cpp:1427-1441` from *outside* the class, in the shim,
  rather than uncommenting/modifying the vendored library file itself (this project's own established
  convention is to keep `native/PictTag.PgfDecoder/libpgf/` as close to an unmodified vendored drop as
  possible, with all extensions living in `shim.cpp` — see `pgf-roi-support.md`'s Stage 5 for the same
  pattern applied to a smaller change).

## Goals

1. **Decode**: a new `PgfColorConversion` method for the legacy packed Bitmap layout — reading `w2`
   pre-packed `DataT`s per row (at the version-dependent stride: `w` when `Version5` is set, `w2`
   when it's also absent, per the three-way correction), applying the bias/clamp, then unpacking bits
   to BGRA — wired into `PgfImageDecoder`'s Bitmap dispatch once `PgfDecodeSession` exposes the
   file's own `Version7`/`Version5` flags (currently discarded after `roiSupported` is computed).
2. **A test-only native fixture generator using a real historical build of `libpgf 6.14.12`**
   (revised from this PRD's original plan of shimming the *current* vendored source — see
   [`pgf-legacy-native-oracle-sourcing.md`](pgf-legacy-native-oracle-sourcing.md)'s "Finding B": `6.14.12`
   predates `Version7` entirely and its real, unmodified `RgbToYuv`/`ImageModeBitmap` encode path is
   live code, not dead code needing a shim to resurrect — confirmed buildable against this repo's
   existing MSVC/CMake toolchain with zero source changes). Producing genuine legacy-format bytes this
   way is this PRD's real independent oracle, unlike `pgf-legacy-interleaved-decode.md`'s
   self-consistency-only situation (this one gets a real native cross-check, and — with this revision —
   one from an actual historical binary rather than a hacked-open modern one). The rarer `yw = w2`
   stride sub-case (pre-Version5-*and*-pre-Version7) still needs a small shim on top of this real
   `6.14.12` build (clearing `Version5`, mirroring the original plan's `ClearVersion7` idea) since
   `6.14.12` itself still always sets `Version5`; the common `yw = w` case needs no shim at all.
3. **Correct the existing, overstated framing** in `PgfColorConversion.cs`'s "Group G" comment and
   `docs/PGF-CODEC.md`'s own gap description (the two-way-vs-three-way version split, and the
   channel-allocation claim that turned out not to apply) as part of shipping this PRD, not left
   stale.

## Non-goals

- **A production encode path for legacy packed Bitmap output.** Confirmed dead code in the reference
  implementation for any build, ever — nothing would have a reason to write it. The Goal 2 fixture
  generator is test infrastructure only, mirroring `pgf-legacy-interleaved-decode.md`'s own
  test-only-encoder framing for the same reason.
- **Modifying `native/PictTag.PgfDecoder/libpgf/` (the vendored source) itself.** The
  `Version7`-clearing access needed for Goal 2 stays entirely inside a small, local, shim-only
  derived class — not a change to the vendored library files, matching this project's own standing
  convention.
- **The `yw = w2` (pre-Version5-*and*-pre-Version7) sub-case as a separate, independently-prioritized
  path.** It's the same code path as the more common `yw = w` case (one function, one version-checked
  stride variable) — porting the function covers both automatically; no extra design work, just
  ensure the Test rig's fixtures cover both stride values (see Test rig) rather than only the more
  likely real-world one.

## Proposed architecture

- **Native shim** (`shim.cpp`): a small local class,
  e.g. `class TestPGFImage : public CPGFImage { public: void ClearVersion7() { m_preHeader.version &=
  ~Version7; } };` (and, if the pre-Version5 stride sub-case also needs a dedicated fixture, an
  equivalent `ClearVersion5()`) — the minimal protected-access surface needed, no other changes to
  `CPGFImage`'s own behavior. A new `pgf_encode_bitmap_legacy_alloc`-style export: builds a header via
  the normal `SetHeader`, hand-writes legacy-packed bits into a `DataT` buffer (replicating
  `PGFimage.cpp:1427-1441`'s commented-out logic), calls `SetChannel(0, buffer)`, clears the version
  bit(s) via the local subclass, then `Write()` normally — skipping `ImportBitmap`/`RgbToYuv`
  entirely for this one mode, per the Context/Testability finding above.
- **`PgfColorConversion`**: a new decode method, e.g. `DecodeLegacyPackedBitmapToBgra(...)`, taking
  the same channel-data shape `PgfDecodeSession` already provides but reading it at the legacy
  stride/packing instead of the modern one — kept as a clearly-separate method from the existing
  modern Bitmap decode, not a branch spliced into it, matching this codebase's own preference for
  explicit, separately-documented code paths over hard-to-follow conditionals in hot decode code.
- **`PgfDecodeSession`**: capture `Version7`(and `Version5`, needed for the stride sub-case) from the
  already-parsed `PgfPreHeader.VersionFlags` (mirroring exactly how `RoiSupported` already does this
  for `PGFROI`, `PgfDecodeSession.cs:111`) as new properties.
- **`PgfImageDecoder`**: `case PgfConstants.ImageModeBitmap:` dispatch checks the new
  `PgfDecodeSession.Version7` flag and calls the new legacy decode method instead of
  `DecodeYToBitmapBgra` when absent.

## Test rig

- **Cross-implementation, the real oracle this PRD gets**: encode legacy-packed Bitmap fixtures via
  the new `pgf_encode_bitmap_legacy_alloc` shim export (both stride sub-cases — `Version5` set/
  `Version7` absent, and both absent — per the three-way correction), decode via the new C#
  `PgfColorConversion` method, compare pixel-for-pixel against the known source bitmap the fixture was
  built from (the same "known-pixel synthetic fixture" methodology `managed-pgf-codec.md`'s Tier 1
  established, applied to Bitmap-mode 1bpp patterns: solid black/white, checkerboard, and a
  non-trivial mixed pattern to catch a bit-order/stride mistake a too-regular pattern could hide).
- **Regression**: the existing modern-Bitmap round-trip tests (`pgf-all-image-modes.md`'s own Group G
  coverage) must stay green — this PRD adds a new, separate decode path; it must not perturb the
  already-correct modern one.
- **Odd/edge dimensions**: widths not a multiple of 8 (so `w2 = (w+7)/8` genuinely rounds up,
  exercising the packed-row's own trailing partial byte) — matching this test rig's established
  `TestBitmaps.EdgeCaseDimensions` convention of deliberately including non-aligned sizes.

## Stage sequence

1. **Native shim fixture generator**: the local `TestPGFImage` subclass + `pgf_encode_bitmap_legacy_alloc`
   export, verified by first decoding its own output back through the *existing* modern C# Bitmap
   decode and confirming it visibly fails/mismatches (proving the fixture really is legacy-formatted,
   not accidentally still modern) before writing any new C# decode logic — a real "does this actually
   exercise the gap" check, not just "does it compile."
2. **`PgfDecodeSession` flag exposure**: `Version7`/`Version5` properties, zero behavior change to
   existing decode (nothing consumes them yet). Exit test: full existing regression suite unaffected.
3. **`PgfColorConversion`'s legacy decode method** + `PgfImageDecoder`'s dispatch branch. Exit test:
   the Test rig's cross-implementation fixtures decode correctly, both stride sub-cases, both edge
   dimensions.
4. **Documentation**: `docs/PGF-CODEC.md`'s "Not yet ported" list updated; `PgfColorConversion.cs`'s
   "Group G" doc comment corrected (two-way → three-way version split, matching Context's own
   correction); this PRD's own Progress log filled in.

## Acceptance criteria / Definition of Done

- Legacy packed Bitmap files (both stride sub-cases) decode correctly, verified against known-pixel
  fixtures the new native shim export produces — a real independent oracle, not self-consistency
  only.
- Zero regression on the existing modern-Bitmap decode path or any other mode.
- `PgfColorConversion.cs`'s "Group G" comment and `docs/PGF-CODEC.md` both corrected to the three-way
  version-split understanding this PRD's own grounding established, not left describing the simpler
  (inaccurate) two-way split.
- `docs/PGF-CODEC.md` updated to move this item from "Not yet ported" to "Supported."

## Open questions

- **Whether the pre-Version5-and-pre-Version7 stride sub-case (`yw = w2`) is worth its own dedicated
  fixture/test weight**, given how much older and rarer that combination is than the more likely real
  "legacy" case (`Version5`/`Version6` set, `Version7` absent, `yw = w`) — lean toward covering both
  in the Test rig regardless (Non-goals already scopes this as "no extra design work," just fixture
  coverage), but confirm the relative real-world likelihood isn't low enough to deprioritize entirely
  once Stage 1's fixture generator makes producing both cheap either way.
- **Whether `PgfColorConversion`'s new legacy decode method should live as a fully separate function
  or share more structure with the existing modern Bitmap decode** — resolve once Stage 3's
  implementation shows how much of the bit-unpacking tail (shared between both: both eventually unpack
  8 bits per byte to BGRA pixels) is genuinely common vs. how much the differing row-stride/packing
  semantics force apart.
