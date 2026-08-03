# PGF codec

`PictTag.PgfCodec` is a from-scratch, dependency-free C# port of digiKam's vendored `libpgf` codec
(PGF = Progressive Graphics File, a wavelet-based image format). It decodes (single-shot and
progressive/level-by-level) and encodes PGF thumbnails — the format digiKam itself caches thumbnail
images in — with no native/P/Invoke dependency at all.

This doc is the current-state reference: what's supported, and what's deliberately not, with the
reasoning and exact source pointers. For the stage-by-stage build history (why each decision was
made, what was tried, what broke and how it was fixed), see
[`new-features/managed-pgf-codec.md`](../new-features/managed-pgf-codec.md) — that PRD's own
"Progress log" is the authoritative narrative; this page is the lookup table distilled from it.

## Where it's used

- `PictTag.Data.PgfDecoding.PgfDecoder` — a thin facade over `PictTag.PgfCodec` for the desktop/
  server side (`PictTag.Api.Thumbnails.ThumbnailService`, `PictTag.UI.Desktop.
  DesktopProgressiveBitmapLoader`). No native DLL involved.
- `PictTag.UI.Browser.BrowserProgressiveBitmapLoader` — references `PictTag.PgfCodec` directly. No
  native WASM linking involved (that whole subsystem was deleted, not kept as a fallback).
- `native/PictTag.PgfDecoder/` (the vendored C++ build) still exists in the repo, but only as
  test/benchmark infrastructure now — `PictTag.PgfCodec.Tests`/`.Benchmarks`' correctness oracle, not
  a production dependency of either host. See [`CLAUDE.md`](../CLAUDE.md)'s Prerequisites section.

## Supported

- **Every image mode the format defines** — RGBA/32bpp (4 channels — B, G, R, A, the one format
  real digiKam thumbnails and this app's own encoder actually use) plus GrayScale, IndexedColor
  (paletted, with a real color table), HSLColor, HSBColor, LabColor/Lab48, RGBColor, Gray16, RGB48,
  CMYKColor, CMYK64, Gray32, Bitmap (1bpp, modern/Version7 packing only — see
  [`new-features/pgf-all-image-modes.md`](../new-features/pgf-all-image-modes.md)'s own Progress log
  for why the legacy pre-Version7 sub-variant was deliberately left unported), RGB12, and RGB16.
  Decode always normalizes to BGRA32 regardless of source mode (`PgfImageDecoder.ConvertToBgra`'s
  mode dispatch) — every real consumer of a decoded bitmap wants pixels it can render, not a
  mode-specific raw buffer. `PgfModeInfo` is the canonical per-mode (bpp, channels,
  downsample-eligibility) table.
- **Decode**: single-shot (`PgfImageDecoder.TryDecode`) and progressive, level-by-level
  (`PgfProgressiveDecoder.TryOpen`/`TryDecodeLevel`/`TryGetLevelSize`).
- **Encode**: single-shot, every quality value `0..MaxQuality` (`31` in this build —
  `__PGF32SUPPORT__` is genuinely active in the real oracle build, a correction to this doc's own
  earlier `15` claim, see `PgfConstants`' doc comment). `PgfImageEncoder.TryEncode` (RGBA) is the one
  production-relevant shape; `TryEncodeMode` (every other mode) exists as test infrastructure to
  produce real fixtures to decode-test against, not a second production path.
- Proven byte-exact against the real native decoder/encoder across a wide fixture/dimension/quality
  matrix, every mode, both directions (`PictTag.PgfCodec.Tests`, 1130 tests) — see
  `managed-pgf-codec.md`'s Stage 7-9 progress log entries for the original RGBA-only verification and
  `pgf-all-image-modes.md`'s own Progress log for the per-mode extension.
- **Header metadata: user data, and the `nLevels=0` "raw/uncoded" small-image path** — arbitrary
  caller-supplied post-header user data round-trips byte-exact under any `PgfUserDataPolicy`
  (`Skip`/`CachePrefix`/`CacheAll`, defaulting to `CacheAll`), read/written by `PgfHeaderIO.Read`/
  `Write` and exposed via `PgfImageDecoder.TryDecode`'s `PgfUserData` overload and
  `PgfProgressiveDecoder.UserData`; `PgfImageEncoder.TryEncode`/`TryEncodeMode` take it as an optional
  parameter. Images below `TestBitmaps.MinimumSupportedDimension` (10, `min(width,height) < 10`) - too
  small for even one wavelet level - decode and encode via the format's own wavelet-transform-free
  "raw/uncoded" path (`PgfDecodeSession.RawChannelData`/`PgfImageEncoder`'s `WriteRawChannels`), rather
  than failing closed the way this port used to. Untrusted header-declared lengths (post-header user
  data size, and separately, `Width`/`Height` themselves) are bounds-checked against the real stream
  before any allocation, failing closed on a corrupted/malicious claim instead of attempting an
  oversized allocation or throwing an uncaught exception. See
  [`new-features/pgf-user-data-and-small-images.md`](../new-features/pgf-user-data-and-small-images.md)
  for the full record, including a real `OverflowException` bug this work found and fixed along the
  way (Stage 3).
- **Progress reporting and cooperative cancellation**: `PgfImageDecoder.TryDecode`,
  `PgfProgressiveDecoder.TryDecodeLevel`, and `PgfImageEncoder.TryEncode` all take optional
  `IProgress<double>?`/`CancellationToken` parameters (backward-compatible defaults — every existing
  call site keeps compiling and behaving unchanged). Progress reports once per level actually
  decoded/encoded, as an area-weighted fraction in `[0, 1]` (mirroring the native codec's own
  `percent *= 4`-per-level curve, since each level covers 4x the previous level's linear coverage in
  the wavelet pyramid — a plain per-level-count fraction would misreport "almost done" after only the
  cheap coarse levels finish). Cancellation is checked once per level, before that level's work
  starts, and surfaces as `OperationCanceledException` — a deliberate departure from this codec's
  usual fail-closed-return-`false` convention, since cancellation is caller-requested, not a
  malformed-input failure mode. `PictTag.Data.PgfDecoding.PgfDecoder`'s facade passes both parameters
  through; no UI call site consumes them yet (`DesktopProgressiveBitmapLoader`/
  `BrowserProgressiveBitmapLoader` already have their own natural, coarser-grained cancellation point
  between per-level calls). See
  [`new-features/pgf-cancellation-and-progress.md`](../new-features/pgf-cancellation-and-progress.md)
  for the full record.

## Explicitly out of scope

Each of these was a deliberate decision made by grepping this codebase's own real usage (this app's
encoder, every real digiKam thumbnail encountered) and confirming the feature is never actually
exercised — not an oversight, and not silently dropped.

- **Four reserved Adobe image modes with no real-world PGF usage** — `Multichannel`(7)/`Duotone`(8)/
  `DeepMultichannel`(14)/`Duotone16`(15) — never defined by any real encoder, PGF-specific or
  otherwise; `PgfModeInfo.TryGetBppAndChannels` returns `false` for them, matching this PRD's own
  Non-goals.
- **Bitmap's legacy pre-Version7 packed sub-variant** — the modern ("new unpacked since Version7")
  sub-variant is fully supported (decode and encode); the older packed format is real, reachable
  decode-side code in the native source, but stores channel data at a different width entirely (one
  `DataT` per *byte*, not per *pixel*), which would need `PgfDecodeSession`'s channel-allocation logic
  to special-case a file's own historical version flag, for a shape no real digiKam thumbnail or this
  port's own encoder (which always sets `Version7`) could ever produce — deliberately left unported,
  not silently dropped (`PgfColorConversion`'s Group G doc comment has the full reasoning).
- **Region of interest (ROI) cropped decode/encode** — the native codec compiles this in
  unconditionally (`PGFplatform.h:60`'s `#define __PGFROISUPPORT__`, never suppressed in this
  build), but it's dead code in practice: nothing that touches this codebase ever sets the `PGFROI`
  version flag (`PgfConstants.EncoderVersionFlags`, `PgfConstants.cs:67`, never includes it), so the
  real `ROIBlockHeader` is never actually read from or written to any file this app produces or
  consumes. Not ported at all — see `PgfMacroBlock.cs:10-19`'s doc comment for the full reasoning.
  Planned: [`new-features/pgf-roi-support.md`](../new-features/pgf-roi-support.md).
- **OpenMP multi-macroblock parallelism** — dead in the *native* build too (`CMakeLists.txt:22`,
  `LIBPGF_DISABLE_OPENMP`), so this port only implements the single-macroblock sequential path
  (same doc comment, `PgfMacroBlock.cs:19`).
- **Legacy pre-Version5 entropy coding** (`DecodeInterleaved`, the older HL/LH interleaved scheme) —
  not ported; this port's encoder always sets the Version5 flag, and so does every modern real PGF
  file (`PgfDecoderCore.cs:104-105`).
- **Real per-level byte lengths on encode** — the encoder always writes zero placeholders instead of
  patching in the real values after encoding (`PgfImageEncoder.cs:10`) — grepping every consumer in
  this codebase found level-length data is genuinely optional/unused by the real decode path
  (`CDecoder::ReadEncodedData`, the only real reader, is itself never called from anywhere this app
  touches).
- **`CSubband::Dequantize`** — confirmed dead code even in the original (only called from an
  encode-time "verify what I just wrote" helper nothing here calls) — not ported
  (`PgfSubband.cs:11`).
- **Big-endian hosts** (`PGF_USE_BIG_ENDIAN`) — not handled; this port assumes a little-endian host
  throughout (`PgfDecoderCore.cs:88-91`), matching every real deployment target here.

## If a real need for any of these shows up

None of the above are architectural dead ends — they're scope cuts based on *today's* real usage,
not permanent limitations of the approach. If a real digiKam library or a future feature ever needs
one of them (a non-RGBA thumbnail mode, say), start from the equivalent native code path cited above
and the doc comment at the matching C# file — each one already explains exactly what would need to
change and why it was safe to skip until now. One real candidate for "a future need": if
`PictTag.PgfCodec` is ever published as a standalone NuGet package, a general consumer's real usage
won't be constrained to digiKam's own thumbnail shape (small, RGBA, metadata-free) the way this app's
own usage is — large-image ROI is the remaining item above that gets noticeably more likely to matter
under that framing (user data and tiny-image support, the other two candidates this reasoning
originally applied to, are done — see `pgf-user-data-and-small-images.md`'s own Progress log), even
though none of them have a current internal need. (Publishing as a NuGet also raises a real, separate
question this list doesn't cover: this port is a close derivative of digiKam's vendored `libpgf`,
LGPL-2.1+ — external distribution likely needs the license text/attribution bundled and a real
compliance check, which is a legal question for someone else to own, not a technical gap to close
here.)

One of these gaps still has a draft PRD (not started, written for completeness rather than an urgent
product need — it says so honestly in its own Context section):
[`pgf-roi-support.md`](../new-features/pgf-roi-support.md). (`pgf-cancellation-and-progress.md`,
`pgf-all-image-modes.md`, and `pgf-user-data-and-small-images.md` are all done — see each one's own
Progress log.)
