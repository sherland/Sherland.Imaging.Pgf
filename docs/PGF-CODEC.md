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

**The plan is to publish `PictTag.PgfCodec` as a standalone NuGet package.** That's a real, stated
goal, not a hypothetical — and it changes the bar for "out of scope" below. Everything up to this
point was scoped against *this app's own* real usage (digiKam's own thumbnail shape: small, RGBA,
metadata-free) — "nothing in this codebase's own usage exercises X" was a legitimate reason to skip
X. A general NuGet consumer isn't constrained to that shape, so that reasoning alone no longer
justifies leaving something unported. What used to be one "Explicitly out of scope" section below is
now split accordingly: items that are **dead in the native C++ reference implementation too** (confirmed by
reading the real source, not assumed) are permanently safe to skip regardless of who's consuming this
library; items that are **real, working capabilities in the C++ reference this port just hasn't
ported yet** are genuine gaps against full parity and become real backlog items under the NuGet goal,
not permanent decisions.

## Where it's used

- `PictTag.Data.PgfDecoding.PgfDecoder` — a thin facade over `PictTag.PgfCodec` for the desktop/
  server side (`PictTag.Api.Thumbnails.ThumbnailService`, `PictTag.UI.Desktop.
  DesktopProgressiveBitmapLoader`). No native DLL involved.
- `PictTag.UI.Browser.BrowserProgressiveBitmapLoader` — references `PictTag.PgfCodec` directly. No
  native WASM linking involved (that whole subsystem was deleted, not kept as a fallback).
- `native/PictTag.PgfDecoder/` (the vendored C++ build) still exists in the repo, but only as
  test/benchmark infrastructure now — `PictTag.PgfCodec.Tests`/`.Benchmarks`' correctness oracle, not
  a production dependency of either host. See [`CLAUDE.md`](../CLAUDE.md)'s Prerequisites section.

## Performance benchmarks

`PictTag.PgfCodec.Benchmarks` is a BenchmarkDotNet console application that compares the managed
codec with the native oracle. It measures single-shot decode and encode at 128/256/512px and quality
0/8/15, plus complete coarsest-to-finest progressive decode at 256/512px and quality 0/8. It also has
an output-size sweep for every quality value.

Build `native/PictTag.PgfDecoder/build/PictTagPgfDecoder.dll` first, as described in
[`TESTING.md`](TESTING.md). Always pass an explicit `--artifacts` directory outside the repository:
BenchmarkDotNet runs the benchmarks from an isolated generated build directory and cleans that
directory at completion. Without `--artifacts`, the console summary is still valid, but the Markdown,
CSV, HTML, and detailed log files are removed with that temporary build.

```powershell
$artifacts = "C:/tmp/pgfcodec-benchmark-$(Get-Date -Format yyyyMMdd-HHmmss)"
dotnet run -c Release --project source/PictTag.PgfCodec.Benchmarks -- `
  --job short --filter "*" --artifacts $artifacts
dotnet run -c Release --project source/PictTag.PgfCodec.Benchmarks -- --sizes
```

The report files are under `$artifacts/results/`. `short` is BenchmarkDotNet's command-line name for
the three-iteration `ShortRun` job shown in its reports. Do not use `--job ShortRun`: it is not a
valid CLI job name. The controlled scalar-versus-vector matrix has 264 cases and takes about thirty minutes on the development
workstation. **Every comparison run must be preserved in Git**: commit its full normalized matrix,
revision, environment, and the comparison against the prior baseline to the versioned benchmark
record. Do not rely on a console transcript, ignored `BenchmarkDotNet.Artifacts`, or a temporary
artifact directory as the performance record. The existing historical and current results are both
checked in under [`benchmarks/pgfcodec/`](benchmarks/pgfcodec/); archive a new immutable run folder
with [`Archive-PgfCodecBenchmark.ps1`](../Archive-PgfCodecBenchmark.ps1) for the next comparison.
Name each folder `yyyy-MM-dd-shortsha-description`; never call an immutable historical run `current`.

## Allocation and output ownership

The default decode and encode APIs preserve their existing convenience ownership: decode exposes a
transient pooled BGRA span only for its callback, while the internal fixture encoder returns an
owned `byte[]`. Callers that perform repeated work can opt into `PgfWorkspace`, a disposable,
single-threaded owner for codec coefficient and entropy buffers. A workspace must outlive every
operation using it; `Reset()` invalidates completed workspace-backed operations so their buffers can
be reused, and `Dispose()` returns all rents. It is not safe to use concurrently.

For repeated decodes of the same immutable payload, `PgfReusableDecoder` retains the parsed session
and its persistent macroblock state. After an initial decode-and-recycle warm-up it performs zero
codec-owned managed allocations; any result allocation requested by its callback remains the
caller’s choice. Cancellation or failure dirties the reusable session, and its next decode rewinds
the stream before doing work.

The internal fixture encoder accepts an optional workspace and has a caller-owned `Span<byte>`
`TryEncodeMode` overload. It reports the exact required byte count; an undersized destination
returns `false` without writing a partial stream. This removes the final convenience path’s owned
`ToArray()` copy, but does not make the growing intermediate writer itself caller-owned.

The completed allocation-path ShortRun confirms the contract on this AVX2 development machine: at
256px/quality 8/gradient, reusable decode reduced 912.5 us / 1,216,616 B to 833.5 us with no managed
allocation reported; workspace encode with caller-owned output reduced 1,421.7 us / 1,817,880 B to
1,195.4 us / 614,041 B; and workspace progressive decode reduced 1,015.8 us / 1,216,616 B to 941.6
us / 6,913 B. The remaining progressive allocation is its per-operation session graph.

Vectorized vertical wavelet lifting is shipped when `Vector<int>.IsHardwareAccelerated`; it processes
only independent columns, keeps scalar tails, and has the identical forced-scalar fallback used by
the tests and controlled benchmark. At 256px/Q8/gradient, the same-run controlled matrix measured
13.8% faster convenience decode and 13.4% faster full progressive decode with unchanged allocations.
The full native-oracle suite is byte-exact for both paths and the Browser/WASM build validates the
fallback target. The prior Dry run is retained only as superseded smoke evidence. See
[`pgf-codec-allocation-and-simd.md`](../new-features/pgf-codec-allocation-and-simd.md) and the
[`controlled SIMD ShortRun`](benchmarks/pgfcodec/2026-08-05-6f120cf-simd-scalar-controlled-shortrun/)
for the current record.

## Supported

- **Every image mode the format defines** — RGBA/32bpp (4 channels — B, G, R, A, the one format
  real digiKam thumbnails and this app's own encoder actually use) plus GrayScale, IndexedColor
  (paletted, with a real color table), HSLColor, HSBColor, LabColor/Lab48, RGBColor, Gray16, RGB48,
  CMYKColor, CMYK64, Gray32, Bitmap (1bpp, all three real packing/entropy eras: Version7+ unpacked,
  Version5/6 packed, and pre-Version5 packed + interleaved), RGB12, and RGB16.
  Decode always normalizes to BGRA32 regardless of source mode (`PgfImageDecoder.ConvertToBgra`'s
  mode dispatch) — every real consumer of a decoded bitmap wants pixels it can render, not a
  mode-specific raw buffer. `PgfModeInfo` is the canonical per-mode (bpp, channels,
  downsample-eligibility) table.
- **Legacy pre-Version5 interleaved entropy decode across every supported mode** — the managed
  `PgfDecoderCore.DecodeInterleaved` port and `PgfDecodeSession` version dispatch are exercised by a
  test-only native writer across all 15 non-Bitmap modes at ordinary even and odd dimensions, plus a
  2049x1027 multi-level RGBA case; legacy Bitmap has its own packed-era matrix. Each synthetic
  pre-Version5 fixture is compared with its modern native sibling twice: mode-native bytes through
  the real C++ `CPGFImage::Read`/`GetBitmap`, and normalized BGRA through the managed decoder. The
  automated rig deliberately does not call `pgf_debug_decode_channel`, whose repeated
  `GetChannel()`/`memcpy` use has a documented native access-violation risk. No obtainable historical
  encoder can produce an independently-authored pre-Version5 fixture, so this is a real dual-decoder
  cross-check of a synthetic writer, not historical-byte provenance. See
  [`new-features/pgf-legacy-interleaved-decode.md`](../new-features/pgf-legacy-interleaved-decode.md).
- **Decode**: single-shot (`PgfImageDecoder.TryDecode`) and progressive, level-by-level
  (`PgfProgressiveDecoder.TryOpen`/`TryDecodeLevel`/`TryGetLevelSize`).
- **Encode**: single-shot, every quality value `0..MaxQuality` (`31` in this build —
  `__PGF32SUPPORT__` is genuinely active in the real oracle build, a correction to this doc's own
  earlier `15` claim, see `PgfConstants`' doc comment). `PgfImageEncoder.TryEncode` (RGBA) is the one
  production-relevant shape; `TryEncodeMode` (every other mode) exists as test infrastructure to
  produce real fixtures to decode-test against, not a second production path.
- Proven byte-exact against the real native decoder/encoder across a wide fixture/dimension/quality
  matrix, every mode, both directions (`PictTag.PgfCodec.Tests`, 1376 tests) — see
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
- **Region of interest (ROI) cropped decode/encode** — an opt-in capability, not the default: every
  real digiKam file and this app's own default encode output stays non-ROI (`PGFROI` version flag
  unset) unless a caller explicitly asks for it. Decode: `PgfProgressiveDecoder.TrySetRoi`/
  `TryGetAlignedRoi`/`TryGetAccurateRoi` (the tile/wavelet-margin-aligned buffer extent vs. the
  caller's own originally-requested, pixel-exact-guaranteed sub-rectangle — `CPGFImage::
  GetAlignedROI`/`ComputeLevelROI`'s own distinction, not interchangeable) decode only the tiles
  relevant to a requested rectangle, skipping the rest (`PgfDecoderCore.SkipTileBuffer`). Encode:
  `PgfImageEncoder.TryEncode`/`TryEncodeMode`'s `roi` parameter produces a tile-structured,
  `PGFROI`-flagged file — every tile is still encoded (ROI encoding is a bitstream-layout choice, not
  "encode only part of the image"), so this exists so a decoder can later selectively skip tiles, not
  to shrink encode output. Enabling it has a real, sometimes large *relative* compression-ratio cost
  at aggressive quality settings on simple content (every tile boundary forces its own macroblock
  flush) — confirmed empirically, not assumed; see `pgf-roi-support.md`'s own Progress log for the
  measured numbers and exactly why this must stay opt-in. Proven correct three ways: self-consistency
  (managed encode → managed decode) across a size x quality x rectangle-shape matrix, and
  cross-implementation against the real native C++ encoder specifically (the native shim has no
  ROI-aware *decode* export — a deliberate scope boundary, not a gap, since the higher-risk leg was
  the managed decoder, already proven against that independent reference). See
  [`new-features/pgf-roi-support.md`](../new-features/pgf-roi-support.md) for the full record.
- **Real per-level byte lengths on encode** — the encoder tracks real per-macroblock byte accounting
  (`PgfEncoderCore`'s `levelLength`/`currLevelIndex`/`bufferStartPos`, direct ports of `CEncoder`'s
  same-named fields) and patches the real, accumulated values into the level-length placeholder after
  encoding finishes (`PgfImageEncoder`, mirroring `CEncoder::UpdateLevelLength`'s seek-write
  sequence) — for both the plain and ROI-flagged paths. Decode exposes the values `PgfHeaderIO.Read`
  already parsed (previously discarded) via `PgfProgressiveDecoder.TryGetLevelLength(level, out
  length)`, matching `TryGetLevelSize`'s own level-0-is-full-resolution convention. Proven two ways:
  self-consistency (encode → the new decode accessor → matches what was actually accumulated) and
  cross-implementation against the real native encoder's own already-working
  `CPGFImage::GetEncodedLevelLength` (via a new `pgf_debug_get_level_lengths` shim export) — the
  cross-implementation values matched **byte-for-byte identical**, level by level, across every
  fixture/quality/plain-or-ROI case tried, not just self-consistently. See
  [`new-features/pgf-real-level-lengths.md`](../new-features/pgf-real-level-lengths.md) for the full
  record.

## Permanently out of scope

These are dead in the *native C++ reference implementation itself* — confirmed by reading the real
source, not inferred from this app's own usage — so no amount of "publish as a general NuGet package"
framing makes them real gaps. Porting them would mean reimplementing something `libpgf` itself never
implements.

- **Four reserved Adobe image modes with no real-world PGF usage** — `Multichannel`(7)/`Duotone`(8)/
  `DeepMultichannel`(14)/`Duotone16`(15). Not just "never defined by any real encoder" — the native
  reference's own mode-support switches have `ImageModeDuotone`/`ImageModeDuotone16` **commented out**
  in the source (`PGFimage.cpp`, the `size`/mode-support table around line 1316), i.e. the original
  authors never implemented color-conversion support for them either. `PgfModeInfo.TryGetBppAndChannels`
  returns `false` for them, matching this reality.
- **OpenMP multi-macroblock parallelism** — dead in the *native* build too (`CMakeLists.txt:22`,
  `LIBPGF_DISABLE_OPENMP`), so this port only implements the single-macroblock sequential path
  (`PgfMacroBlock.cs:17-18`'s own doc comment).
- **`CSubband::Dequantize`** — reachable only via the public `CPGFImage::Reconstruct` method (a
  decode-what-you-just-encoded self-verification helper, `PGFimage.h:118`) — technically callable by
  a NuGet consumer, but it duplicates a capability this port already fully provides (encode, then
  decode normally through the already-supported `Read`/`GetBitmap` path) rather than adding a new
  one. Not ported (`PgfSubband.cs:11`); revisit only if a real caller specifically wants the
  single-call "verify what I just wrote" convenience method itself, not the underlying capability.

## Not yet ported — real gaps against full C++ parity

Unlike the section above, each of these is a real, working capability in the native reference that
this port simply hasn't gotten to — safe to defer only while consumption was scoped to this app's own
narrow usage (digiKam's thumbnail shape). Publishing as a general-purpose NuGet package removes that
justification; treat these as the real backlog, roughly in order of how likely a general consumer is
to actually hit them:

- **Big-endian hosts** (`PGF_USE_BIG_ENDIAN`) — not handled; this port assumes a little-endian host
  throughout (`PgfDecoderCore.cs:128-131`). Every real deployment target *this app* runs on is
  little-endian, but a general NuGet consumer's target isn't this app's to assume. Lowest-priority
  item on this list in practice (real big-endian .NET targets are rare — no officially supported
  big-endian target exists in mainline .NET today), but a genuine correctness gap if one is ever hit,
  not just an untested path. Confirmed narrow: only two call sites (`PgfDecoderCore.cs`/
  `PgfEncoderCore.cs`'s raw macroblock `MemoryMarshal.AsBytes` reinterprets) are actually at risk —
  every other multi-byte field this port reads/writes already goes through host-endianness-safe
  `BinaryPrimitives.*LittleEndian` calls. See [`new-features/pgf-big-endian-hosts.md`](../new-features/pgf-big-endian-hosts.md)
  for the full grounding.

## If a real need for any of these shows up

Large-image ROI, real per-level byte lengths, and legacy Bitmap/interleaved decoding are now done
(see the Supported section above and `pgf-roi-support.md`/`pgf-real-level-lengths.md`/
`pgf-bitmap-legacy-packed.md`'s Progress logs). Only big-endian hosts remain in "Not yet ported."
Publishing as a NuGet also raises a
real, separate question this list doesn't cover: this port is a close derivative of digiKam's
vendored `libpgf`, LGPL-2.1+ — external distribution likely needs the license text/attribution
bundled and a real compliance check, which is a legal question for someone else to own, not a
technical gap to close here.

Every gap this codebase's own *completed* staged PRDs (`pgf-cancellation-and-progress.md`,
`pgf-all-image-modes.md`, `pgf-user-data-and-small-images.md`, `pgf-roi-support.md`,
`pgf-real-level-lengths.md`, `pgf-bitmap-legacy-packed.md`,
`pgf-legacy-interleaved-decode.md`) originally tracked is closed — see each one's own Progress log
for the full record. The legacy native-writer/dual-decoder proof and historical-oracle limitation
are documented above.
`pgf-legacy-native-oracle-sourcing.md` is a completed
*investigation* (not an implementation PRD itself) that grounded the legacy work — exactly which
historical `libpgf` source versions are actually obtainable today, and what each can and can't prove
— so their own Testability sections stop relying on assumption.
