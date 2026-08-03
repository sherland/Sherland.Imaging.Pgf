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

- **Image mode: RGBA / 32bpp only** (4 channels — B, G, R, A) — the one format real digiKam
  thumbnails and this app's own encoder actually use.
- **Decode**: single-shot (`PgfImageDecoder.TryDecode`) and progressive, level-by-level
  (`PgfProgressiveDecoder.TryOpen`/`TryDecodeLevel`/`TryGetLevelSize`).
- **Encode**: single-shot (`PgfImageEncoder.TryEncode`), every quality value `0..MaxQuality` (`15`
  in this build).
- Proven byte-exact against the real native decoder/encoder across every quality level and a wide
  fixture/dimension matrix (`PictTag.PgfCodec.Tests`, 567 tests) — see that PRD's Stage 7-9 progress
  log entries for exactly what was checked.

## Explicitly out of scope

Each of these was a deliberate decision made by grepping this codebase's own real usage (this app's
encoder, every real digiKam thumbnail encountered) and confirming the feature is never actually
exercised — not an oversight, and not silently dropped.

- **Every color mode except RGBA/32bpp** — no `GrayScale`/`Gray16`/`Gray32`, `RGBColor`/`RGB12`/
  `RGB16`/`RGB48`, `IndexedColor` (paletted, with a color table), `Bitmap` (1-bit), `Lab`/`Lab48`,
  `HSL`/`HSB`, or `CMYKColor`/`CMYK64`. Confirmed by `PgfConstants.cs:50-52` only defining
  `ImageModeIndexedColor` (to reject it) and `ImageModeRGBA`; `PgfDecodeSession.cs:63` fails closed
  on any other mode/channel count/bpp, and `PgfHeaderIO.Read` (`PgfHeader.cs:112`) throws outright
  on indexed color specifically rather than silently mis-parsing a color table this port never
  models.
- **The `nLevels=0` "raw/uncoded" path** — for tiny images (`min(width,height) < 10`), the original
  codec stores channel data directly with no wavelet transform at all. Not ported: both
  `PgfDecodeSession.cs:70` (decode) and `PgfImageEncoder.cs:39` (encode) treat `NLevels == 0` as a
  hard failure, matching `TestBitmaps.MinimumSupportedDimension` (10) — real digiKam thumbnails
  never approach this size.
- **Region of interest (ROI) cropped decode/encode** — the native codec compiles this in
  unconditionally (`PGFplatform.h:60`'s `#define __PGFROISUPPORT__`, never suppressed in this
  build), but it's dead code in practice: nothing that touches this codebase ever sets the `PGFROI`
  version flag (`PgfConstants.EncoderVersionFlags`, `PgfConstants.cs:67`, never includes it), so the
  real `ROIBlockHeader` is never actually read from or written to any file this app produces or
  consumes. Not ported at all — see `PgfMacroBlock.cs:10-19`'s doc comment for the full reasoning.
- **OpenMP multi-macroblock parallelism** — dead in the *native* build too (`CMakeLists.txt:22`,
  `LIBPGF_DISABLE_OPENMP`), so this port only implements the single-macroblock sequential path
  (same doc comment, `PgfMacroBlock.cs:19`).
- **Legacy pre-Version5 entropy coding** (`DecodeInterleaved`, the older HL/LH interleaved scheme) —
  not ported; this port's encoder always sets the Version5 flag, and so does every modern real PGF
  file (`PgfDecoderCore.cs:104-105`).
- **Header metadata** — no color table, no user data (`PGFPostHeader`), no `UserDataPolicy`
  handling. `PgfHeaderIO.Write` always writes a bare header with `hSize = HeaderSize`
  (`PgfHeader.cs:152,184`); reading skips over any post-header bytes rather than parsing them.
- **Real per-level byte lengths on encode** — the encoder always writes zero placeholders instead of
  patching in the real values after encoding (`PgfImageEncoder.cs:10`) — grepping every consumer in
  this codebase found level-length data is genuinely optional/unused by the real decode path
  (`CDecoder::ReadEncodedData`, the only real reader, is itself never called from anywhere this app
  touches).
- **`CSubband::Dequantize`** — confirmed dead code even in the original (only called from an
  encode-time "verify what I just wrote" helper nothing here calls) — not ported
  (`PgfSubband.cs:11`).
- **Progress callbacks and cooperative mid-decode cancellation** — the native `Read`/`Write` APIs
  accept an optional callback invoked periodically with a percentage, which can request early abort.
  `PictTag.PgfCodec`'s public API (`PgfImageDecoder.TryDecode`, `PgfImageEncoder.TryEncode`,
  `PgfProgressiveDecoder.TryDecodeLevel`) has no equivalent parameter — no progress reporting, no
  mid-decode abort hook. (The UI loaders layer their own `CancellationToken` checks *between* level
  calls, but that's outside the codec itself, not a substitute for it.)
- **Big-endian hosts** (`PGF_USE_BIG_ENDIAN`) — not handled; this port assumes a little-endian host
  throughout (`PgfDecoderCore.cs:88-91`), matching every real deployment target here.

## If a real need for any of these shows up

None of the above are architectural dead ends — they're scope cuts based on *today's* real usage,
not permanent limitations of the approach. If a real digiKam library or a future feature ever needs
one of them (a non-RGBA thumbnail mode, say), start from the equivalent native code path cited above
and the doc comment at the matching C# file — each one already explains exactly what would need to
change and why it was safe to skip until now.
