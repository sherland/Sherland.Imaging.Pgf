# Managed (pure C#) PGF codec — PRD

## Context

`new-features/client-side-pgf-and-remove-thumbnail-cache.md` and `docs/GUI.md`'s "Browser/WASM native
PGF decode" section both document the same unresolved, confirmed-at-runtime problem: the vendored
C++ `libpgf` codec (`native/PictTag.PgfDecoder/`), statically linked into `PictTag.UI.Browser`'s WASM
output, throws `DllNotFoundException("__Internal")` on every call in the real browser, in every
configuration tried — including a full AOT-compiled `dotnet publish`. Three real root-cause fixes
landed and none closed the gap (see that doc's "Update" paragraph and
`PictTag.Integration.Tests/ProgressivePgfBrowserTests.cs`'s own doc comment for the exhaustive
investigation). The remaining fix identified there — hand-written JS interop reading the wasm
module's raw exports directly, bypassing .NET's P/Invoke resolution entirely for this one binding
layer — was explicitly flagged as "a real, substantial redesign, not attempted as part of finding
this." **Real users get zero progressive PGF decoding in the browser host today.**

This PRD proposes the other substantial redesign that was on the table: stop shipping this codec as
native C++ at all. Port `libpgf` to pure, dependency-free C#. A managed implementation runs
identically under Mono's WASM interpreter and under Desktop's CoreCLR with no P/Invoke, no native
build step, and no platform-specific linking model — it structurally cannot hit
`DllNotFoundException("__Internal")` because there is no native boundary left to resolve.

This also directly serves the user's other two asks:

1. **A from-scratch C# port is a chance to write the hot decode path idiomatically** for .NET's
   modern low-allocation primitives (`Span<T>`/`Memory<T>`/`ArrayPool<T>`, potentially
   `System.Numerics` SIMD) rather than only replicating the C++ code's existing scalar-loop shape.
2. **Port both encode and decode, not decode-only, to get a genuinely strong correctness test
   methodology.** `libpgf`'s `Encoder.cpp` is already vendored in this repo (compiled into the
   existing native DLL, just never called by `shim.cpp`) — the product itself only ever *decodes*
   PGF (digiKam's own cache is read-only from PictTag's side by design, see `CLAUDE.md`), but having
   a real encoder in both languages turns "does the new decoder work?" into a **4-way cross-
   implementation round-trip matrix** (C# encode → C++ decode, C++ encode → C# decode, plus both
   same-language round trips) instead of relying only on comparing decode output against one fixed
   oracle. This is a substantially stronger test than the decode-only version of this PRD originally
   proposed.

## Why this needs to be grounded in the real algorithm, not a paraphrase

`client-side-pgf-and-remove-thumbnail-cache.md`'s own "What the original AI-drafted spec got wrong"
section is the cautionary tale for this exact kind of task: an earlier AI-authored proposal for this
same codec invented a `CPGFStream` override with the wrong virtual signature and assumed
exception-based resume semantics that the real `Decoder.cpp` doesn't support, because it was written
without reading the vendored headers. This PRD is grounded in a real read of the current source (see
citations below, decode and encode both); the port itself must hold to the same bar throughout —
verify each stage against the real algorithm and a real oracle, never against recalled/assumed
PGF-format documentation from outside this repo.

### The real decode pipeline (byte stream → BGRA)

1. **Header parse** (`CDecoder`'s constructor, `Decoder.cpp:83`): `PGFPreHeader` → `PGFHeader` →
   optional `PGFPostHeader` (color table / user data) → `levelLength[nLevels]` array. All on-disk
   multi-byte fields are little-endian; `PGFtypes.h`'s `#pragma pack(1)` structs and the
   endian-conditional bitfields in `PGFVersionNumber`/`ROIBlockHeader` (`PGFtypes.h:132-149,
   184-206`) have no direct C# equivalent and must be hand-decoded via explicit shift/mask reads, not
   a blitted-struct `BinaryReader` cast.
2. **`CPGFImage::Open`** (`PGFimage.cpp:141`): builds per-channel width/height (chroma halved if
   downsampled) and a `CWaveletTransform` pyramid per channel.
3. **`CPGFImage::Read(level)`** (`PGFimage.cpp:402`, redirects internally to the ROI-aware overload
   at `:489` — see "ROI is always compiled in" below) drives, per level per channel: `CSubband::
   PlaceTile` → `CDecoder::Partition` (`Decoder.cpp:276`) → `CDecoder::DequantizeValue`
   (`Decoder.cpp:472`), refilling from the entropy coder via `GetNextMacroBlock`/`DecodeBuffer`
   (`Decoder.cpp:487,504`) as needed.
4. **Entropy decode** — `CDecoder::CMacroBlock::BitplaneDecode` (`Decoder.cpp:660`). **Not**
   arithmetic coding: a bitplane/significance-map coder (Malvar's "Fast Progressive Wavelet Coder"
   scheme, per the comment at `Decoder.h:90`) with an adaptive zero-run-length option per bitplane
   (`ComposeBitplane`/`ComposeBitplaneRLD`, `Decoder.cpp:773,834,937`). Fully deterministic, integer-
   only, no floating point, no probability model to replicate approximately.
5. **Inverse wavelet transform** — `CWaveletTransform::InverseTransform`/`InverseRow`
   (`WaveletTransform.cpp:246`+): integer lifting scheme (`c1=1,c2=2`, `WaveletTransform.cpp:31-32`),
   separable (row pass then column pass), run per level right after that level's entropy decode. **A
   decoded level's coefficient memory is freed immediately after use**
   (`WaveletTransform.cpp:404-406`) — this is the actual mechanism behind "continuing from wherever it
   left off" between separate `Read(level)` calls, not a special resume protocol; it also means
   re-decoding an already-consumed coarser level from the same handle is structurally impossible,
   matching `shim.cpp`'s existing decreasing-level-only contract exactly.
6. **`CPGFImage::GetBitmap`** (`PGFimage.cpp:1788`, the `Channels()==4` / `ImageModeRGBA` branch at
   `:2232` — the only mode this repo's shim ever requests): YUV(A) → interleaved BGRA, chroma
   upsampling, channel-map applied, via a plain per-pixel scalar loop today. This is real headroom
   for the "very performant" ask — a data-parallel `System.Numerics.Vector`/`Vector128<short>` version
   of this loop and the lifting row/column passes is a legitimate, natural improvement over the C++
   baseline, not just parity.

### The real encode pipeline (BGRA → byte stream) — the algebraic mirror of decode

1. **`SetHeader()`** (`PGFimage.cpp:893`, asserts `!m_decoder` — must be the first call) allocates
   `m_channel[i]`, calls `ComputeLevels()` (`PGFimage.cpp:853`, auto-picks `nLevels` from image size),
   and decides chroma/alpha downsampling (`quality > DownsampleThreshold(3)` →
   `Downsample()`, `PGFimage.cpp:809`).
2. **`ImportBitmap()`** (`PGFimage.cpp:791`, asserts `m_channel[0]` already allocated by `SetHeader`)
   does `RgbToYuv` (integer-only; the RGBA/BGRA branch this repo needs is at `PGFimage.cpp:1578-1613`
   of a 382-line switch covering pixel formats PictTag never uses) then downsamples if enabled.
3. **`Write()`** (`PGFimage.cpp:1220`) calls **`WriteHeader()`** (builds a `CWaveletTransform` per
   channel and runs `ForwardTransform` per level, `PGFimage.cpp:1009-1019`, then constructs
   `CEncoder`), then **`WriteImage()`**/`WriteLevel` (`PGFimage.cpp:1067-1119`), which drives
   `CSubband::ExtractTile` (`Subband.cpp:177-193` — the direct mirror of decode's `PlaceTile`) per
   subband per level, feeding raw coefficients into `CEncoder::Partition`, then `Flush()` +
   `UpdateLevelLength()`.
4. **Forward wavelet transform** — `ForwardTransform`/`ForwardRow` (`WaveletTransform.cpp:89-203`),
   the literal algebraic inverse of the decode-side lifting steps, same integer constants, opposite
   order (forward: high-pass-then-low-pass, `:111-112`; inverse: low-pass-then-high-pass,
   `:351,360`). All-integer, no floats — this is what makes lossless round-tripping exact by
   construction, not approximate.
5. **Quantization** — `CSubband::Quantize(int quantParam)` (`Subband.cpp:112-147`) computes a
   level-adjusted quant parameter and **only mutates data when that adjusted value is `> 0`**
   (`Subband.cpp:116,133`). `SetHeader` sets `m_quant = m_header.quality`, and
   `ForwardTransform` only calls `Quantize` when `quant > 0` (`WaveletTransform.cpp:161`). At
   `quality = 0` (`PGFtypes.h:160`: "0 = lossless"), `Quantize` is **never invoked at all** — lossless
   round-tripping is structural, not incidental, confirmed directly in the code path, not inferred
   from the doc comment alone.
6. **Entropy encode** — `CEncoder::CMacroBlock::BitplaneEncode` (`Encoder.cpp:482-622`), the mirror
   of `BitplaneDecode`: same bitplane loop and significance/refinement split
   (`DecomposeBitplane`, `Encoder.cpp:634-745`) and the same adaptive sign-RLE scheme
   (`RLESigns`, `Encoder.cpp:774-817`). Two things decode never had to do: computing how many
   bitplanes are needed from the actual data (`NumberOfBitplanes`, `Encoder.cpp:750-766`) rather than
   just reading a stored count, and a genuine **per-plane cost decision** — `BitplaneEncode` computes
   both the RLE-coded and raw-bit costs and picks whichever is cheaper (`Encoder.cpp:529,566`). This
   is real, deterministic, data-dependent logic — see "Achievable round-trip guarantee" below for why
   it still doesn't threaten lossless correctness even if two independent implementations make
   different cost/tie-break calls.

### Pure/stateless vs. stateful (drives the C# type design)

- **Pure, mechanically portable**: `BitStream.h`'s bit-array primitives (operate on caller-supplied
  words, no persistent state of their own) → a `static` helper type or `ref struct` over `Span<uint>`.
  `CWaveletTransform::InverseRow`/`ForwardRow`/`SubbandsToInterleaved`/`InterleavedToSubbands`,
  `CDecoder::Partition`/`DecodeInterleaved`, `CSubband::Quantize`/`Dequantize`,
  `CMacroBlock::ComposeBitplane*`/`DecomposeBitplane` — all pure functions over supplied buffers.
- **Stateful, needs real classes**: `CDecoder`/`CEncoder` (macroblock ring buffer + stream cursor,
  persists across many `DequantizeValue`/`Partition` calls — `CEncoder` additionally buffers whole
  macroblocks for its own cost-comparison step), `CSubband` (read/write coefficient cursor),
  `CWaveletTransform` (the per-level pyramid, decode side frees each level after use), `CPGFImage`
  itself (`m_currentLevel`, `m_channel[]`, `m_wtChannel[]`, plus the encode-side ordering requirement
  that `SetHeader → ImportBitmap → Write` is enforced by real asserts, not just documented
  convention). The C# port's class boundaries should mirror this split directly rather than inventing
  a different shape.

### Traps confirmed in the real code (not assumed)

- **`__PGFROISUPPORT__` is compiled in** (`PGFplatform.h:60`, this repo's `CMakeLists.txt` never
  disables it), so `Read(int level)` always goes through the ROI-checking overload even for the
  common non-ROI case. The port must implement that branch (even if the fast path — ROI flag unset —
  is what every real fixture exercises); if a niche full ROI-crop implementation turns out not worth
  porting for v1, fail closed (return `false`/throw a clear "unsupported" error) for the ROI-enabled
  case rather than silently mis-decoding it — confirm against real fixtures whether this is even
  reachable before deciding. The new encoder never needs to *emit* ROI-flagged output (PictTag never
  needs ROI encoding), so this trap is decode-only.
- **`DataT` is `INT16`, not `INT32`**, in this build (`__PGF32SUPPORT__` is not defined anywhere in
  `CMakeLists.txt`/`PGFplatform.h`'s defaults) — coefficient buffers should be `short`/`Int16` in the
  port, matching `MaxBitPlanes = 15`. Getting this wrong changes bit-plane counts and silently breaks
  bit-exactness in both directions.
- **OpenMP is moot**: `CMakeLists.txt` already sets `LIBPGF_DISABLE_OPENMP`, so the shipped native
  build (the oracle) is already single-threaded; the parallel sites in both `Decoder.cpp`/
  `PGFimage.cpp` (decode) and `Encoder.cpp`'s macroblock array/`PGFimage.cpp` (encode) parallelize
  independent work with no output difference, so there's nothing to preserve semantically. Managed
  parallelism (`Parallel.For`/`Task`) can be considered later purely as a perf option, not for
  compatibility.
- **`CPGFMemoryStream`'s real contract** (`PGFstream.h:106-152`, `PGFstream.cpp`): `Read` silently
  truncates at end-of-stream rather than throwing, `SetPos` throws if it would move past `m_eos`. A
  `ReadOnlyMemory<byte>`-backed C# equivalent must replicate these exact semantics for the read side;
  the write/encode side needs a growable-buffer equivalent (see "Architecture" below).
- **Byte-identical bitstreams across implementations is not a realistic or valuable target — see
  "Achievable round-trip guarantee" immediately below.** This shapes several test-rig and acceptance
  decisions throughout this document.

### Achievable round-trip guarantee: decode-correctness, not bitstream-identity

The user's ask was: *"C# encoder → C++ decoder should restore the image, and C++ encoder → C# decoder
should give the same result."* Extended per follow-up feedback to cover **every compression level**,
not only lossless. That is a **decode-correctness** property (feed encoded bytes through the other
implementation's decoder and get pixels back that match what the *same* implementation that encoded
them would have produced), and it is fully achievable and should be a hard requirement:

- **At `quality = 0` (lossless)**: every step in both directions is integer-only with no quantization
  applied at all (see "Quantization" above) — a correct, bug-free port of the forward and inverse
  lifting transforms is bit-exact by construction, regardless of which implementation produced the
  bitstream. The decoded output must equal the *original source bitmap*, pixel-exact, on all four
  matrix legs.
- **At `quality > 0` (lossy)**: `CSubband::Quantize(int quantParam)` (`Subband.cpp:112-147`) is a
  fixed, deterministic formula over the quant parameter and the subband's level — not a heuristic or
  auto-tuned choice — so two correct implementations quantizing the same coefficients at the same
  quality value should produce identical quantized coefficients, and therefore identical decoded
  output, even though that output necessarily differs from the original (that's what "lossy" means).
  The bar at every lossy level is therefore: **all four matrix legs decode to pixel-identical output as
  each other**, not "decodes without crashing." This is a real, testable hypothesis grounded in reading
  `Quantize`'s implementation, not an assumption — validate it empirically starting at Stage 8; if a
  specific quality value turns out not to hold up (see the entropy-coder caveat below), narrow the
  claim for that value rather than silently weakening it for all of them.

What is **not** a realistic target, at any quality level, is *byte-identical PGF files* between the
C# and C++ encoders for the same input. `BitplaneEncode`'s RLE-vs-raw cost comparison
(`Encoder.cpp:529,566`) is real, data-dependent, deterministic logic operating on the (possibly
quantized) coefficients — a faithful port should usually make the identical choice, and since it's a
deterministic function of identical input data the *decoded result* should still match even if it
doesn't — but this PRD does not require literal bitstream identity as an acceptance bar, since a
subtly different but still-correct tie-break or ordering would produce a different-yet-equally-valid,
still fully (and identically) decodable PGF file. Treat exact bitstream identity as a bonus signal
worth *logging* when it happens (strong extra confidence the port is faithful), never as a blocking
test assertion. If it turns out a careful port does produce identical bytes in practice, that's a
nice discovery to note in this doc's "what was built" record later — not something to engineer for up
front.

Rough scope, decode + encode, of what's directly relevant to port (excludes pixel formats/modes
PictTag's BGRA-only shim never uses):

| Area | Decode-relevant | Encode-relevant (new) |
|---|---|---|
| `Decoder.cpp/h` | 1,273 LOC, entirely decode | — |
| `Encoder.cpp/h` | — | ~1,065 LOC, entirely encode |
| `PGFimage.cpp` (2,732 total) | ~1,240 (`Open`/`Read`/`GetBitmap`/ROI) | ~350-400 (`SetHeader`/`ImportBitmap`/`Downsample`/`ComputeLevels`/`WriteHeader`/`WriteImage`/`Write`/the RGBA slice of `RgbToYuv`) |
| `Subband.cpp/h` (568 total) | ~350-400 (`PlaceTile`/`Dequantize`) | ~55 (`Quantize`/`ExtractTile`) |
| `WaveletTransform.cpp/h` (733 total) | ~350 (`InverseTransform`/`InverseRow`) | ~150 (`ForwardTransform`/`ForwardRow`) |
| `BitStream.h` | 341, header-only, shared both directions | (same) |
| `PGFstream.*` | Read/SetPos/GetPos/IsValid contract | growable-write equivalent, new design (native encode writes into a stream too, but the C# side doesn't need to mirror `CPGFStream`'s abstract class shape — see Architecture) |

**Total realistic scope: roughly 3,300-3,450 LOC of directly relevant C++ logic to port** (decode
~3,213 from the original assessment, plus ~1,620-1,670 newly in scope for encode, minus double-counted
shared infrastructure like `BitStream.h`).

## Goals

1. A pure, dependency-free managed C# implementation of **both** the PGF decode path (single-shot and
   the existing progressive/level-by-level API) and the PGF encode path (single-shot, `quality`
   parameter supported, BGRA input only — matching the native shim's own real-world scope).
2. **Decode-correctness round-trip guarantee across every valid quality/compression level**, not just
   lossless, proven by a 4-way cross-implementation matrix for every fixture at every quality value
   from `0` (lossless) through `MaxQuality` (`= MaxBitPlanes = 15` in this build, per
   `PGFtypes.h:88-94` — confirm during Stage 8 whether `SetHeader`/`Quantize` actually accept the full
   `0..15` range or only the three documented presets `0`/`4`/`6`, see "Open questions"): C# encode →
   C# decode, C# encode → C++ decode, C++ encode → C# decode, C++ encode → C++ decode. At `quality=0`
   all four must reproduce the original bitmap pixel-exact. At `quality>0` (lossy), the four legs must
   still agree with **each other** pixel-exact for a given quality level — see "Achievable round-trip
   guarantee" above: quantization is a deterministic formula, not a heuristic choice, so a correct port
   should make the decoded (lossy) output implementation-invariant even though it necessarily differs
   from the original source bitmap. (Byte-identical *bitstreams* between the two encoders is still not
   a goal at any quality level — only decoded-pixel agreement is required.)
3. Decode output remains byte-for-byte identical to the vendored native decoder's output for every
   real and synthetic fixture, matching this PRD's original decode-only bar — the round-trip goal
   above is additive, not a replacement for direct oracle comparison.
4. Both directions run unmodified under Desktop (CoreCLR) and Browser (Mono/WASM, both the default
   interpreter and an AOT-published build) with **zero P/Invoke and zero native build step** for the
   *decode* path specifically — structurally closing the confirmed `DllNotFoundException("__Internal")`
   gap, not working around it. (The encoder has no in-browser production caller — see Non-goals — so
   this requirement is decode-specific, though there's no reason the encoder wouldn't also just work
   there given the same dependency-free design.)
5. A hot path built on `Span<T>`/`ReadOnlySpan<T>`/`Memory<T>` and `ArrayPool<T>` throughout —
   decode-into-caller-supplied-buffer (matching the existing `PgfDecoder.TryDecode`/`TryDecodeLevel`
   callback shape in `source/PictTag.Data/PgfDecoding/PgfDecoder.cs`) for decode, and a
   pooled/growable-buffer output for encode (output size isn't known upfront the way decode's is —
   see Architecture) — no per-call heap allocation beyond pooled scratch buffers, verified by a real
   `MemoryDiagnoser` benchmark, not assumed from code review alone.
6. A genuine correctness test rig built on live in-process comparison (native oracle for decode, the
   new 4-way round-trip matrix for encode) plus known-pixel synthetic fixtures whose expected output
   is independently computable — not "matches whatever the oracle said" as the only signal.
7. A genuine performance test rig (BenchmarkDotNet) for both encode and decode: wall-clock latency and
   allocation profile across Desktop-managed, Desktop-native-P/Invoke (decode only, while it still
   exists), WASM-interpreted, and WASM-AOT-published configurations.
8. Once decode correctness and performance are proven, both hosts' real decode call sites
   (`PictTag.Data.PgfDecoding.PgfDecoder`, `PictTag.UI.Browser.Interop.NativePgf`, and their
   respective `IProgressiveBitmapLoader` implementations) switch to the managed decoder, and
   `ProgressivePgfBrowserTests.cs` gets a real Playwright assertion proving correct, visibly
   progressive pixels in an actual running browser — closing the gap with a live proof, matching this
   repo's stated "verify, don't recall" bar (`CLAUDE.md`), not a build/link success.

## Non-goals

- **No production write/encode call site within PictTag itself.** `PictTag.Data`/`PictTag.Api` are
  read-only by design (`CLAUDE.md`: "There are no write operations anywhere in the GUI... a
  deliberate, standing scope boundary") — this PRD does not add a feature that writes PGF thumbnails
  back to digiKam or anywhere else. The new encoder is real, fully-featured for PictTag's actual pixel
  format (BGRA, matching the existing shim's `Channels()==4` scope), and lives in the shared library —
  but it exists to make the *decoder* provably correct via round-trip testing, and as a genuinely
  useful general capability, not because the product needs to write PGF files.
- **Byte-identical bitstream matching between the C# and native C++ encoders for the same input** —
  not a realistic or required target; see "Achievable round-trip guarantee" above. The required
  property is decode-correctness across implementations.
- **Full ROI (region-of-interest) cropped *decoding***, unless real fixtures are found that need it —
  see "ROI is always compiled in" above. Track as an explicit open question, not a silent gap. ROI
  *encoding* is not needed at all — PictTag's encoder never needs to emit ROI-flagged files.
- **Rewriting `ProgressiveImage`/dwell-gating/the grid.** Out of scope — this PRD only replaces what's
  underneath `IProgressiveBitmapLoader`, not the control or loading strategy built on top of it
  (`client-side-pgf-and-remove-thumbnail-cache.md`).
- **Deciding the final fate of `native/PictTag.PgfDecoder/` up front.** Whether the native project is
  fully retired from this repo or kept solely as a permanent test-oracle is an explicit open question
  for late in this PRD (see "Open questions"), not a decision this document makes now.
- **General PGF encode/decode features this codebase never uses** (indexed-color/CMYK modes, bit
  depths other than 8-bit-per-channel RGBA, 16-/32-bit-per-channel modes) — the shim's existing
  `Channels() == 4` constraint is the actual product requirement; port only what real thumbnails need
  in both directions, matching the native shim's own scope rather than the full PGF spec.

## Proposed architecture

- **New project: `source/PictTag.PgfCodec`** — a plain, dependency-free C# library (no EF Core, no
  Avalonia, no ASP.NET) targeting whatever TFM both `PictTag.Data` (Desktop, `net10.0`) and
  `PictTag.UI.Browser` (`net10.0-browser`) can reference directly. This is the structural fix: today
  `PictTag.UI.Browser.Interop`'s own doc comment explains it exists *only* because `PictTag.Data`
  carries SQLite/EF Core that Browser can't reference — a managed codec with zero dependencies removes
  that constraint entirely, so both hosts can reference the *same* implementation instead of two
  P/Invoke wrappers around the same native shim. Both encode and decode live in this one project
  (`PictTag.PgfCodec.Decoding`/`PictTag.PgfCodec.Encoding` namespaces, or similar) since they share
  `BitStream`, header structs, and the `Subband`/`WaveletTransform` infrastructure.
- **Decode public API mirrors the existing shape** in `PictTag.Data.PgfDecoding.PgfDecoder`
  (`TryDecode`, `OpenProgressive`/`ProgressivePgfDecoder.TryDecodeLevel`, the
  `DecodedCallback<TResult>` pattern writing into a pooled buffer) so call-site changes in
  `PictTag.Data` and `PictTag.UI.Browser.Interop`/`NativePgf` are mechanical facade swaps, not
  redesigns.
- **Encode public API is necessarily shaped differently from decode**, because output size isn't
  known upfront the way decode's `width*height*4` is: something like
  `PgfEncoder.TryEncode(ReadOnlySpan<byte> bgra, int width, int height, byte quality, out byte[] pgfBytes)`
  returning a rented-then-trimmed or pooled owned buffer, rather than decode's write-into-a-
  caller-supplied-span pattern. This asymmetry is expected and fine — it mirrors the native API shape
  too (`pgf_open`/`pgf_close`'s owned-handle pattern vs. `pgf_decode_bgra`'s caller-buffer pattern).
- **Internal layering follows the pure/stateful split above**: a stateless `BitStream` helper (`ref
  struct` over `Span<uint>`, shared read/write), a `PgfMemoryReader` replicating `CPGFMemoryStream`'s
  exact truncate/seek-past-eos read contract over `ReadOnlyMemory<byte>`, a growable `PgfByteWriter`
  (e.g. `IBufferWriter<byte>`-backed, or a pooled-array-with-resize like `ArrayBufferWriter<byte>`) for
  encode output, and stateful `Decoder`/`Encoder`/`Subband`/`WaveletTransform`/`PgfImage` classes
  mirroring the native class boundaries (not a single monolithic function per direction) — this keeps
  each stage below independently testable against the native oracle at the same granularity the C++
  code already has.
- **Memory strategy**: decode input stays a `ReadOnlySpan<byte>`/`ReadOnlyMemory<byte>` over the
  caller's buffer wherever a call is single-shot; the progressive/stateful decode path still needs its
  own copy of the input bytes for cross-call lifetime independence (mirroring `PgfDecoderHandle`'s
  existing copy-on-open behavior in `shim.cpp`). Decode output always writes into a caller-supplied
  `Span<byte>`/pooled `ArrayPool<byte>` buffer, never an internally-allocated array. Encode input is a
  `ReadOnlySpan<byte>` (the caller's BGRA buffer); encode output is necessarily an owned/pooled buffer
  handed back to the caller (see above). Scratch buffers on both sides (macroblock words, subband
  coefficient blocks, lifting row buffers) come from `ArrayPool<T>`/`stackalloc` (for small,
  compile-time-bounded sizes) rather than `new[]`.
- **SIMD as an explicit, separately-verified optimization pass**, not folded into the initial
  bit-exact port: get a correct scalar port passing the oracle/round-trip comparisons first
  (stage-by-stage, per below), *then* vectorize `GetBitmap`'s YUV→BGRA/upsample loop, `RgbToYuv`, and
  the lifting row/column passes (both directions) with `System.Numerics.Vector128`/`Vector256`,
  re-verifying bit-exactness after each vectorization since a SIMD rewrite is exactly the kind of
  change that can silently alter rounding/clamping behavior.
- **Native shim gains test-only encode exports.** `Encoder.cpp` is already compiled into
  `PictTagPgfDecoder.dll` but nothing exports it (`shim.cpp` calls only decode functions). Add a small
  new export, e.g. `pgf_encode_bgra_alloc(bgra, width, height, quality, out dataPtr, out dataLen)` +
  `pgf_free_encoded(dataPtr)` (an owned-pointer pattern, mirroring the existing `pgf_open`/`pgf_close`
  handle lifecycle, appropriate here since this is test-tooling, not the performance-critical
  per-request path `pgf_decode_bgra`'s caller-buffer design exists for). This is what makes "C++
  encode" a real, callable leg of the 4-way matrix rather than only being reachable via real
  digiKam-produced fixtures.

## Test rig: functional correctness

**Principle: compare live against real implementations in-process; don't commit golden byte arrays
that can silently drift from what either implementation actually produces.** The existing native
`PictTagPgfDecoder.dll` (extended with the new encode exports above) stays available throughout this
PRD specifically to serve as the decode oracle and as one leg of the encode round-trip matrix — its
own existing test suite (`PictTag.Data.Tests/PgfDecoderTests.cs`) is not touched or reduced.

- **Tier 1 — known-pixel synthetic fixtures (strongest signal, and now cheap to produce).** Once the
  C# encoder exists (stage 8 below), most synthetic fixtures — solid colors, gradients,
  checkerboards, a 1×1 image, odd/prime dimensions, sizes spanning several wavelet levels — can be
  authored directly as raw BGRA pixel arrays in test code and encoded on the fly with either
  implementation; no external tool or committed binary fixture needed for these. Expected BGRA output
  is the original array itself (at `quality = 0`), computed directly, not "whatever the oracle said."
  For the earlier decode-only stages (before the C# encoder exists), the new native
  `pgf_encode_bgra_alloc` shim export produces the same synthetic fixtures as real `.pgf` bytes to
  validate the C# *decoder* against before the C# *encoder* is ready — see Stage 1 below.
- **Tier 2 — byte-exact oracle comparison on real-world decode data.** For `sample-thumbnail.pgf`
  (the existing real digiKam-derived fixture) and every Tier 1 fixture, decode with both the native
  P/Invoke path and the new managed path and assert `Assert.Equal` on the resulting BGRA bytes
  (width/height too). Cover both the single-shot `TryDecode` path and every progressive level via
  `OpenProgressive`/`TryDecodeLevel`, matching `PgfDecoderTests.cs`'s existing `ProgressiveDecoder_*`
  test shapes.
- **Tier 3 — intermediate-stage comparison, not just end-to-end.** Byte-exact BGRA equality alone
  makes a decode mismatch hard to localize (entropy decode vs. inverse transform vs. color conversion
  could all be the culprit). Add a small **test-only** debug export to the native shim (e.g.
  `pgf_debug_decode_channel`, dumping `CPGFImage::GetChannel(c)`'s raw post-decode/pre-colorconversion
  `DataT` buffer) so the port's own intermediate YUV channel data can be compared stage-by-stage
  against the real oracle while building the decode entropy-decode and inverse-transform stages, before
  the full `GetBitmap` equivalent even exists.
- **Tier 4 — the 4-way round-trip cross-matrix, swept across every quality level (the new core of this
  PRD).** For every fixture, run all four legs — encode with C#/decode with C# (self round-trip);
  encode with C#/decode with native via the existing `pgf_decode_bgra` (the single most important new
  proof — validates the new encoder against the trusted, unmodified real decoder); encode with native
  via the new `pgf_encode_bgra_alloc`/decode with C# (validates the new decoder against real encoder
  output, broader than only real digiKam fixtures); encode with native/decode with native (sanity check
  that the new shim export itself is wired correctly) — **at every quality value from `0` through
  `MaxQuality` (confirm the real accepted range against `SetHeader`/`Quantize` during this stage, not
  just the three documented presets), not a single representative sample.** The assertion differs by
  quality:
  - `quality = 0`: all four legs reproduce the **original source bitmap** pixel-exact.
  - `quality > 0`: all four legs must still produce **pixel-identical decoded output to each other**
    (the "Achievable round-trip guarantee" hypothesis above — validate this empirically per quality
    value here, since it's the first point in this plan where it's actually checkable), plus baseline
    structural sanity (decodes without error, correct dimensions/level count, and — since this is a
    compression codec — output size at higher quality values should be no larger than at `quality=0`
    for the same source, a cheap invariant worth asserting alongside pixel agreement).
  - If cross-implementation pixel agreement turns out not to hold at some specific quality value
    (see the entropy-coder cost-decision caveat above), narrow the claim for that value specifically —
    document it as a known deviation with its root cause, not a silently lowered bar for every level.
- **Tier 5 — negative/malformed input (decode).** Port the existing
  `TruncatedFixtureBlob_FailsCleanlyWithoutThrowing`/`GarbageBytes_*` tests unchanged in spirit: the
  managed decoder must fail closed (return `false`/`null`, never throw or corrupt memory) on truncated
  or garbage input, same as the native shim's `catch (...) { return false; }` boundary. Add the encode
  equivalent: invalid dimensions (zero/negative width or height), a BGRA buffer shorter than
  `width*height*4`, etc., must also fail closed with a clear result rather than throwing or producing
  a malformed file.
- **Where these tests live**: a new `PictTag.PgfCodec.Tests` project (xUnit v3/MTP, this repo's
  standard runner) referencing both `PictTag.PgfCodec` (new) and `PictTag.Data.PgfDecoding` (existing,
  for the native oracle/round-trip legs) — `dotnet test source/PictTag.PgfCodec.Tests` joins the
  standard "always run" tier in `docs/TESTING.md`, not a new opt-in tier, since it needs neither
  Ollama nor a live digiKam library.

## Test rig: performance

- **BenchmarkDotNet**, a new dependency for this repo (not currently used anywhere — confirmed by
  grep) — the standard, idiomatic .NET tool for exactly this need, run as its own console-app project
  (`source/PictTag.PgfCodec.Benchmarks`), not folded into the xUnit/MTP test projects (BenchmarkDotNet
  needs its own process/toolchain and doesn't compose with the Microsoft.Testing.Platform runner this
  repo's other test projects use).
- **What to measure, both directions, swept across every quality level (`0..MaxQuality`), not one
  representative value**: wall-clock latency (decode: single-shot and full progressive
  level-by-level sequence; encode: single-shot per quality value) and allocation profile
  (`[MemoryDiagnoser]`), across the real and synthetic fixture corpus, at realistic digiKam thumbnail
  dimensions. Also record **output size per quality value** (encode) — not a latency metric, but the
  other half of "performance" for a compression codec, and a useful cross-check that higher quality
  values do in fact compress more (monotonically non-increasing size as quality increases, for the
  same source image) — a regression here would indicate a quantization-logic bug even if every
  correctness test still happens to pass.
- **Configurations that must each be benchmarked separately, not assumed to generalize from one
  another**:
  - Desktop CoreCLR, managed vs. the existing native P/Invoke path (decode: parity/regression check
    while both still exist; encode: managed vs. the new native shim export).
  - Browser/WASM under Mono's **interpreter** (the default inner-loop `dotnet run`/`aspire start`
    workflow this repo's developers actually use day to day) — decode is the practically relevant
    direction here since that's the real in-browser use case; benchmark encode there too since it's
    cheap to include, even without a production caller.
  - Browser/WASM under a **real AOT-compiled `dotnet publish`** (`RunAOTCompilation=true`) — the prior
    native investigation confirmed AOT compiles successfully even though P/Invoke resolution still
    failed there, so this configuration is real and reachable, not hypothetical.
- **Concrete pass/fail latency numbers, set from Stage 10's first real `BenchmarkDotNet` measurement**
  (Desktop CoreCLR, `ShortRun` job, `PictTag.PgfCodec.Benchmarks`) against the actual constraint that
  matters for decode: thumbnails must stay interactively responsive during dwell-gated grid scrolling
  (`client-side-pgf-and-remove-thumbnail-cache.md`). At realistic thumbnail dimensions (128-256px) and
  quality values a real digiKam library would actually use (4 and above, not the lossless `quality=0`
  extreme), managed single-shot decode measured **0.3-1.4 ms** and managed progressive decode's full
  coarsest-to-finest sequence measured **1.0-1.5 ms** — both comfortably inside "interactively
  responsive" by any reasonable definition (single-digit milliseconds, not the 16 ms/frame or ~100 ms/
  interaction budgets that would actually matter for a scrolling grid). **Bar: managed decode of a
  thumbnail up to 256x256 at quality &gt;= 4 must complete in under 5 ms** on comparable hardware -
  chosen with real headroom over the ~1.4 ms worst case actually measured, not a value backed into
  from "whatever the code currently does." Encode has no latency-sensitive production caller (PictTag
  never writes PGF files - CLAUDE.md's "read-only by design" GUI scope boundary), so its numbers
  (1.2-2.1 ms at the same sizes) remain informative rather than gating, as originally planned.
- **Allocation target — informative, not met, and now backed by real numbers instead of aspiration**:
  the "zero or near-zero per call" target stated in the original plan does **not** hold for this port
  as of Stage 10 - measured managed allocation is real and size-proportional (roughly 650 KB per
  256x256 decode, 2.3 MB per 512x512 decode; similar for encode), 800-2000x the size-independent
  native P/Invoke leg's own marshaling overhead. Stage 10's hardening pass pooled the single largest
  per-call allocation (the output BGRA buffer, both single-shot and progressive decode, via
  `ArrayPool<byte>`, matching `PictTag.Data.PgfDecoding.PgfDecoder`'s existing production shape) but
  deliberately did not extend pooling to the per-channel wavelet-coefficient buffers
  (`PgfSubband`/`PgfWaveletTransform`) or the macroblock entropy-coder scratch buffers
  (`PgfMacroBlock`/`PgfEncodeMacroBlock`) - see Stage 10's progress log entry for why, and for the
  measured breakdown that makes clear those two are where the remaining allocation actually comes
  from (not "the code uses `Span&lt;T&gt;` so it must be fine" - the measurement is what settled this,
  exactly per this bullet's own original instruction).

## Progress log

**Stage 1 — done.** `native/PictTag.PgfDecoder/shim.cpp` gained `pgf_encode_bgra_alloc`/
`pgf_free_encoded` and `pgf_debug_decode_channel`; `source/PictTag.PgfCodec` (empty skeleton) and
`source/PictTag.PgfCodec.Tests` (20 passing tests, `NativeEncodeExportsTests.cs`) exist and are wired
into `PictTag.slnx`. Three real, non-obvious findings came out of getting this far, all now fixed or
documented — exactly the "verify, don't recall" bar this repo holds itself to elsewhere:

- **A genuine P/Invoke marshaling bug, not specific to this PRD's new code.** Every `bool`-returning
  export in this shim (new and pre-existing) was marshaled with `[return: MarshalAs(UnmanagedType.
  Bool)]` (4-byte Win32 `BOOL`), but MSVC's C++ `bool` return is only ABI-guaranteed correct in the
  return register's low byte (AL) — the upper 3 bytes are compiler/codegen-dependent, not guaranteed
  zero. A genuine `false` could be misread as `true` whenever those upper bytes happened to be
  nonzero. Confirmed with a deliberately adversarial repro (a function that unconditionally returns
  `false` but writes nonzero out-parameters right before returning — the out-parameters marshaled
  correctly, the return value didn't). Fixed by switching every occurrence to
  `MarshalAs(UnmanagedType.U1)`, in **all three** P/Invoke wrapper classes:
  `PictTag.Data.PgfDecoding.PgfDecoder` and `PictTag.UI.Browser.Interop.NativePgf` (both pre-existing,
  production) as well as this PRD's new `PictTag.PgfCodec.Tests.Oracle.NativePgfOracle`. The
  pre-existing decode functions had apparently been "getting lucky" (their specific compiled code
  happens to leave the upper return-register bytes clean) rather than being provably correct — worth
  fixing regardless of whether it had ever caused an observed failure in production.
- **A real `realloc()`/`delete[]` mismatch, fixed.** `CPGFMemoryStream`'s allocating constructor grows
  its buffer via `realloc()` when written past capacity, but its destructor always frees via
  `delete[]` — undefined behavior when mixing the two allocators. `pgf_encode_bgra_alloc` now uses a
  pre-sized, non-owning buffer instead, sidestepping the growth path entirely (fails closed via a
  caught `IOException` if the fixed size is ever exceeded, rather than growing/corrupting).
- **`pgf_debug_decode_channel` has a real, unresolved crash risk under repeated calls — scoped out of
  automated testing, not fixed.** Calling this specific function repeatedly (a modest number of times
  is enough — not just hundreds) produces a real `STATUS_ACCESS_VIOLATION`, even using only the
  already-known-good real fixture with no encoding involved at all. Narrowed to the final
  `GetChannel()`/`memcpy` read specifically (removing it eliminates the crash) but not further
  root-caused: a real, correctly-configured AddressSanitizer rebuild (confirmed genuinely active via
  its own startup diagnostics) found no violation report before the crash, and the ABI marshaling fix
  above — initially suspected as the same root cause — did *not* resolve it when tested directly.
  `PictTag.PgfCodec.Tests` deliberately does not call this function at all; it remains available for
  occasional manual/interactive use during later stages (its actual intended purpose) with a
  prominent warning in its own doc comment. Revisit if a real need for automated intermediate-channel
  comparison arises in Stage 5/6 — don't reuse this function in a loop without solving this first.
- **`min(width, height) < 10` triggers a separate, real vendored-library code path** (`CPGFImage::
  ComputeLevels()`'s `nLevels=0` fallback — a wavelet-transform-free "store raw/uncoded channel data"
  path, never exercised by this shim's original decode-only exports since real digiKam thumbnails
  never approach this size) that was also implicated in early crash investigation. Both
  `pgf_encode_bgra_alloc` and `pgf_debug_decode_channel` now explicitly reject it
  (`TestBitmaps.MinimumSupportedDimension = 10`) — a legitimate scope narrowing (real thumbnails never
  need it) independent of the debug-channel finding above.

**Stage 2 — done.** `source/PictTag.PgfCodec/BitStream.cs`: a direct, method-for-method port of
`BitStream.h`'s stateless bit-array primitives over `Span<uint>`/`ReadOnlySpan<uint>`, verified by 38
hand-constructed-bit-pattern tests (`BitStreamTests.cs`) with no PGF file involved. Writing real tests
against the real port (rather than assuming the C++ and the port agree) surfaced two more genuine,
worth-recording findings:

- **`SetBitBlock`/`ClearBitBlock` have "at least `len`" semantics for real, not just per their doc
  comment's wording** — there is no end-mask on the last word touched (confirmed by testing, not by
  re-reading the comment more carefully), so both round the affected range up to the end of whatever
  word contains the last requested bit, *even in the single-word case*
  (`ClearBitBlock(stream, 4, 8)` clears bits 4-31, not 4-11). Both methods' C# doc comments now state
  this explicitly with a worked example — a future caller (Stages 5-6) assuming an exact range would
  have introduced a real, hard-to-spot bug.
- **`SeekBitRange`/`SeekBit1Range`'s original C++ deliberately over-reads one word past the caller's
  logical range** when the scanned range is entirely zero (all-one) and ends exactly on a word
  boundary — harmless there only because real callers' buffers always have slack past the "in use"
  length; a bounds-checked `Span<T>` has no such implicit slack. Fixed by reordering the loop's
  `&&` operands (`count < len` first) so the port never reads past `len` bits — provably safe instead
  of safe-by-caller-convention, with identical return values for every valid input (verified by the
  existing tests, not just argued).

**Stage 3 — done.** `PgfMemoryReader` (decode-side, wraps `ReadOnlyMemory<byte>`, replicates
`CPGFMemoryStream`'s real truncate-at-EOS `Read` and upper-bound-only `SetPos` contract exactly, plus
a deliberate added lower-bound check the original never had - seeking negative was already invalid
in the original, just uncaught there) and `PgfByteWriter` (encode-side, backed by a real
`MemoryStream` rather than replicating the C++ workaround from Stage 1's `realloc()`/`delete[]`
finding, since C# has no such allocator mismatch to avoid) - 20 new tests, all passing first try
(the `BitStream` stage's lesson - verify against real behavior, not the doc comment - had already
been absorbed by the time these were written). `PgfStreamException` is the shared "invalid stream
position" error type both use, matching the spirit of the original `IOException`.

**Stage 4 — done.** `PgfHeader`/`PgfHeaderIO`: pre-header/header/level-length-array read and write,
byte-exact with `CDecoder`'s constructor (Decoder.cpp:83) and the header-writing portion of
`CEncoder`'s constructor (Encoder.cpp:70) plus `WriteLevelLength`. `PgfHeaderIO.ComputeLevels` is a
direct port of `CPGFImage::ComputeLevels`'s auto-selection branch. Also added `pgf_open`/`pgf_close`
to the test oracle wrapper (the existing, already-proven-safe production progressive-decode entry
point - not `pgf_debug_decode_channel`) so `NLevels` could be cross-validated against the real codec
too, not just width/height. All 18 new tests passed on the first run, including the strongest proof
available for this stage: a C#-written header opened successfully by the *real native decoder*
(`pgf_get_dimensions`/`pgf_open` reporting the exact width/height/level-count for four different
dimension pairs) - a single wrong byte anywhere in the pre-header/header would have made that fail,
so this is a genuine confirmation the byte layout (including the hand-packed
`PGFVersionNumber`/version-flags bitfields) is exactly right, not just self-consistent.

**Stage 5 — done.** `PgfMacroBlock`/`PgfDecoderCore` (decode) and `PgfEncodeMacroBlock`/
`PgfEncoderCore` (encode): the bitplane/significance-map entropy coder, both directions, plus
`Partition`'s `LinBlockSize`-tiled subband traversal. Two real, deliberate scope reductions turned
out to be justified by the vendored code's own dead branches, not assumptions: ROI header modeling
(this port's encoder never sets the `PGFROI` flag, so the real `ROIBlockHeader` is never actually
read from the stream for any file this codebase touches) and the OpenMP multi-macroblock array path
(this build always compiles with `LIBPGF_DISABLE_OPENMP`, so only the single-macroblock branch is
ever real).

Given Stage 1's finding that the only native hook exposing intermediate coefficient data
(`pgf_debug_decode_channel`) has an unresolved crash risk under repeated calls, this stage's
correctness proof is **self-consistency** (C# encode → C# decode, verified exact) rather than
cross-implementation - a deliberate, documented adjustment from the original plan, with full
cross-implementation validation deferred to Stage 7's end-to-end BGRA comparison against the
always-safe `pgf_decode_bgra`. 22 new tests (constant/alternating/random/sparse-spike/full-short-range
coefficient patterns, exact-macroblock-boundary and multi-macroblock-spanning sequences, and
`Partition`'s full 2D tiling including non-multiple-of-8 dimensions) - **all failed on the first run**
(everything decoded to zero) from a real, self-introduced bug this port's own tests caught
immediately: `PgfMacroBlock` hard-coded its "current block size" as the constant `BufferSize` from
construction, when the original deliberately initializes it to `0` specifically so a fresh,
never-decoded block reports "completely read" and forces a real decode before the first value is
consumed (`CMacroBlock`'s own constructor comment: "makes sure that `IsCompletelyRead()` returns true
for an empty macro block") - missing that meant every read silently pulled from an undecoded, all-zero
array instead of ever invoking the entropy decoder at all. Fixed by giving the block a real "not yet
decoded" initial state (`bufferSizeInUse = 0`, set to the real value only once a block has actually
been read) matching the original's sentinel exactly. Every test has passed on every run since.

**Stage 6 — done.** `PgfSubband` (the coefficient buffer + sequential read/write cursor
`ForwardRow`/`InverseRow` use, distinct from `PgfSubband.SetData`/`GetData`'s indexed access
`Partition` uses) and `PgfWaveletTransform` (the per-channel LL/HL/LH/HH pyramid, forward and inverse
integer lifting transform). `CSubband::Dequantize` was confirmed (by grepping every call site) to be
called only from `CPGFImage::Reconstruct`, an encode-time "verify what I just wrote" helper this
codebase never calls - genuinely dead code for this port, not an oversight.

15 self-consistency tests (forward transform → inverse transform on the same in-memory pyramid, no
entropy coding or serialization involved, `quant=0` so no quantization loss) - 13 passed immediately;
2 deliberately-adversarial edge cases (a literal 1x1 image, a 3x3 image) reproduced a real, latent
one-past-buffer read in the *original* C++: `ForwardTransform`/`InverseTransform`'s
"dimension `&lt;` FilterSize" branch forms a `row1 = row0 + width` pointer that, for a single-row
buffer, points one element past its end and gets unconditionally dereferenced. Re-derived the
original's own pointer arithmetic by hand to confirm this is a real bug there too, not just a
translation error here - then confirmed it's unreachable in practice: `PgfHeaderIO.ComputeLevels`
(Stage 4) guarantees every pyramid level's dimensions stay `&gt;=` FilterSize for any image with
`min(width, height) &gt;=` `TestBitmaps.MinimumSupportedDimension` (10, the same real-world floor
Stage 1's encode guard already enforces) - the failing cases could only be reached by manually
forcing an unrealistic level count `ComputeLevels` itself would never choose for such tiny starting
dimensions. Removed those two test cases with a comment recording the finding, consistent with this
PRD's other confirmed-unreachable-branch decisions (ROI, OpenMP, `nLevels=0`) - not a gap to fix,
since porting the real, reachable behavior faithfully is the actual goal, not handling every input
the vendored pointer arithmetic could theoretically be pointed at.

**Stage 7 — done.** `PgfColorConversion` (both directions: `EncodeBgraToYuva`/`Downsample`/
`DecodeYuvaToBgra`, direct ports of `RgbToYuv`/`Downsample`/`GetBitmap`'s `ImageModeRGBA` branches,
`channelMap`/`pitch` hardcoded to the identity/contiguous values every real caller in this codebase
actually uses rather than threaded through as unused generality) and `PgfImageDecoder` (the
single-shot decode orchestrator tying header parse + entropy decode + inverse wavelet transform +
color conversion together across all 4 channels, direct port of `CPGFImage::Open`+`Read(0)`+
`GetBitmap` chained - `PgfDecoderCore`'s single shared bitstream-reading state, confirmed from
`CPGFImage::Read`'s loop structure, decodes all 4 channels' subbands *per level* before
inverse-transforming any of them, not channel-by-channel to completion - the bitstream interleaves
that way, so getting the loop nesting right here was load-bearing, not cosmetic).

This is the stage the PRD calls out as "the milestone proving the whole scalar decode pipeline is
correct end to end" - and it passed in full on the first run: 100 new tests (`PgfColorConversionTests`
in isolation - color transform alone is exactly invertible with no wavelet/entropy coding involved,
including at the exact byte-value corners worked out by hand in the doc comments, plus known-value
box-average arithmetic for `Downsample`'s odd-dimension branches; `PgfImageDecoderTests` end-to-end -
every `TestBitmaps` fixture x every `EdgeCaseDimensions` pair x quality `{0, 1, 4, 6, 15}`, encoded by
the native shim's `pgf_encode_bgra_alloc` and decoded by both the managed port and the native oracle,
asserted byte-exact against each other at *every* quality level, not just 0 - decode is deterministic
given the same bitstream, so unlike Stage 8's round-trip matrix there's no cross-implementation-encoder
ambiguity to narrow here). Also covers the real `sample-thumbnail.pgf` fixture (Tier 2's actual
real-world leg) and Tier 5's truncated/garbage/empty-input fail-closed cases. Full suite (250 tests
total across all stages so far) run 5x for stability - no flakiness, matching Stage 1's finding that
avoiding `pgf_debug_decode_channel` in automated tests keeps this rig solid.

One subtlety confirmed rather than assumed: `RgbToYuv`'s `(uAvg + vAvg) >> 2` (decode-side dequant
combine) carries an original-code comment claiming "must be logical shift operator" that, taken
literally, would be wrong for the signed, possibly-negative `int` sum involved here (worked through by
hand for pure green: `U=V=-255`, and a *logical* shift of that sum would produce a wildly wrong,
un-clamped result) - C#'s ordinary arithmetic (sign-extending) `>>` on `int`, the same operator C++
would actually use for a signed operand in practice, is what the byte-exact test results confirm is
correct; the comment most likely means "must stay a shift, not integer division" (which round
differently for negative operands) rather than the CS-terminology "logical vs. arithmetic" distinction.

**Stage 8 — done.** `PgfImageEncoder`: single-shot managed encode, the algebraic mirror of
`PgfImageDecoder` - direct port of `CPGFImage::SetHeader`+`WriteHeader`+`WriteImage` chained together
(BGRA in, PGF bytes out), reusing every earlier stage's pieces (`PgfColorConversion.EncodeBgraToYuva`/
`Downsample`, `PgfWaveletTransform.ForwardTransform`, `PgfEncoderCore`/`PgfSubband.ExtractTile` from
Stage 5, `PgfHeaderIO.Write` from Stage 4) with no new port surface beyond the orchestration itself.
One deliberate, permanent scope cut, made explicit in both `PgfImageEncoder`'s and
`PgfHeaderIO.Write`'s doc comments: level-length bytes are always written as zero placeholders, never
patched with real values afterward (no `CEncoder::UpdateLevelLength` equivalent) - grepping every real
consumer confirmed `Decoder.cpp` calls level-length information "optional" and the only reader in this
codebase, `CPGFImage::ReadEncodedData`, is never called by anything in scope; porting the real
value's macroblock/level-boundary deferred-accounting exactly would add real complexity for a field
nothing decodes, asserts on, or even could be tested against (this PRD's own "decode-correctness, not
bitstream-identity" guarantee already rules out byte-identical files as a goal).

This stage is "the central proof this PRD exists to deliver" per its own stage-sequence description,
and the Tier 4 four-way round-trip matrix (`PgfRoundTripMatrixTests`) confirms the "quantization is
deterministic, so lossy output should be implementation-invariant" hypothesis holds cleanly with **no
exceptions found at any tested quality level** - 288 combinations (3 fixtures x 6
`EdgeCaseDimensions` pairs x all 16 quality values `0..MaxQuality`), each running all four legs
(encode C#/decode C#, encode C#/decode native, encode native/decode C#, encode native/decode native)
and asserting pixel-exact agreement with the original source bitmap at `quality=0` or pixel-identical
cross-leg agreement at every other quality value - passed in full on the first real test run, after a
quick standalone smoke check (a scratch console app compiling `PictTag.PgfCodec`'s sources directly,
P/Invoking the native shim, swept across the same quality range) had already confirmed no obvious
bugs before committing to writing the full matrix. `PgfImageEncoderTests` adds Tier 5's encode-side
negative cases (invalid/mismatched dimensions, quality above `MaxQuality`, below-minimum-dimension
rejection) plus an output-size sanity check.

One real, non-obvious finding from that sanity check: output size is **not** strictly
monotonically non-increasing step-by-step across adjacent quality values - a first version of the
"higher quality never regresses size" test asserted exactly that and failed at 10x10/quality 8→9 (82
then 86 bytes). Traced to the *native* encoder itself via the same scratch console app: it reproduces
the identical 82-then-86 byte sequence at that exact size/quality, byte-for-byte - a real property of
the original codec (entropy-coding overhead occasionally exceeds the coefficient-magnitude savings
from one quantization step to the next, especially at tiny single-macroblock sizes), not a port bug.
Fixed by testing what the PRD's own wording actually specifies - every quality value compared against
the `quality=0` baseline, not adjacent-step monotonicity - which does hold at every tested size.

Full suite (556 tests total across all stages so far) run 5x for stability - no flakiness.

**Stage 9 — done.** `PgfProgressiveDecoder`: level-by-level decode mirroring `PictTag.Data.PgfDecoding.
PgfDecoder.ProgressivePgfDecoder`'s public shape (`Width`/`Height`/`Levels`, `TryGetLevelSize`,
`TryDecodeLevel<TResult>` with the same `DecodedCallback<TResult>` pattern) - direct port of
`CPGFImage::Open` + repeated `Read(level)` + `GetBitmap`. Building this surfaced that Stage 7/8's
`PgfImageDecoder` and the new type needed near-identical header-parse-plus-channel-setup logic, so
that preamble was factored out into a new shared `PgfDecodeSession` (used by both) rather than
duplicated - a real, judged-in-the-moment refactor, not scope creep: `PgfImageDecoder.TryDecode` is
now a thin loop over `PgfDecodeSession.DecodeOneLevel` down to 0, and `PgfProgressiveDecoder` calls
the same method down to whatever level the caller most recently requested, picking up where a
previous call left off exactly like the original's own `m_currentLevel` bookkeeping (each level's
subbands freed once consumed - see `PgfWaveletTransform.InverseTransform`'s existing doc comment).

Unlike the native wrapper, this type isn't `IDisposable` - no unmanaged handle exists to free in a
pure-managed port, a genuine simplification, not a gap.

One real regression caught by the existing suite, not new tests: refactoring
`PgfImageDecoder.TryDecode` to call `PgfDecodeSession.DecodeOneLevel` moved the per-level entropy-decode
calls **outside** the try/catch that used to wrap the whole method, so `TruncatedStream_FailsClosed_
WithoutThrowing` (a Stage 7 test) started throwing `PgfFormatException` instead of returning `false`.
Fixed by moving the try/catch into `DecodeOneLevel` itself, alongside `PgfDecodeSession.TryOpen`'s own
header-parse try/catch - every throwing path across a session's lifetime now fails closed the same way,
in both callers, instead of relying on the caller to wrap it correctly.

Also required a real, deliberate divergence from the native shim's own permissiveness, documented in
`TryDecodeLevel`'s doc comment: requesting a level *already passed* (coarser than the finest level
reached so far on this instance) is a genuine caller-contract violation - PictTag.Data.PgfDecoding's
native wrapper doesn't guard against it (`CPGFImage::Read`'s `while (m_currentLevel > level)` loop
simply becomes a no-op, silently returning whatever the *previous* level's data was, at the *previous*
level's dimensions, not the requested one). This port fails closed (`false`) instead, matching this
PRD's general Tier 5 philosophy elsewhere. Re-requesting the *exact same* level again (idempotent, not
a contract violation) remains valid and is explicitly tested.

11 new tests: the three named `ProgressiveDecoder_*` shapes ported from `PgfDecoderTests.cs`
(level-0-matches-single-shot, monotonic-resolution-growth using the real `sample-thumbnail.pgf`
fixture, malformed-input-fails-closed), plus out-of-range/already-passed-level/idempotent-re-request
cases this port's stricter contract needed, plus the Tier 2 cross-check the test rig section calls out
by name ("every progressive level via OpenProgressive/TryDecodeLevel") - every level of both the real
fixture and several Stage-8-encoded synthetic fixtures (multiple quality values), decoded via this
port and the native oracle's own newly-added `pgf_level_size`/`pgf_decode_level_bgra` P/Invoke wrappers
(added to `NativePgfOracle` specifically for this stage), asserted byte-exact. All passed on the first
run except the pre-existing regression above (caught immediately by the existing suite, fixed before
any new test was even run). Full suite (567 tests total) run 5x for stability - no flakiness.

**Stage 10 — done, with a deliberately narrowed scope documented below.** Two real pieces: an
allocation-hardening pass on the decode API surface, and a new `PictTag.PgfCodec.Benchmarks`
BenchmarkDotNet console project (Desktop CoreCLR) that produced this PRD's first real latency/
allocation numbers (now recorded above in "Test rig: performance").

*Hardening.* `PgfImageDecoder.TryDecode` changed from `out byte[]? bgra` to a
`TryDecode<TResult>(pgfData, PgfDecodedCallback<TResult> onDecoded, out TResult? result)` shape - a
real breaking API change to this port's own code (not shipped anywhere yet), made now because the
PRD's own "Proposed architecture" section specifies it explicitly ("Decode public API mirrors the
existing shape... `DecodedCallback&lt;TResult&gt;` pattern writing into a pooled buffer... never an
internally-allocated array") and because `PgfProgressiveDecoder.TryDecodeLevel` (Stage 9) already used
exactly this shape - the two were inconsistent before this stage, not just unhardened. The output BGRA
buffer for both is now `ArrayPool&lt;byte&gt;`-rented, matching what
`PictTag.Data.PgfDecoding.PgfDecoder.TryDecode` already does in production, so Stage 12's call-site
swap stays mechanical. The shared `DecodedCallback&lt;TResult&gt;` delegate (previously duplicated as
a nested type on `PgfProgressiveDecoder` alone) moved to a single top-level `PgfDecodedCallback&lt;
TResult&gt;` both types now share.

This refactor moved per-level entropy-decode calls (`PgfDecodeSession.DecodeOneLevel`) so they were no
longer wrapped by `PgfImageDecoder.TryDecode`'s own try/catch - a real regression the existing suite
caught immediately (`TruncatedStream_FailsClosed_WithoutThrowing` started throwing instead of
returning `false`), fixed by moving the try/catch into `DecodeOneLevel` itself so both callers get the
same fail-closed guarantee regardless of which one happens to wrap it.

*Deliberately not hardened this stage, with real reasoning, not an oversight:* the per-channel
wavelet-coefficient buffers (`PgfSubband.AllocMemory`'s `short[]` allocations, aliased directly by
`PgfWaveletTransform`'s level-0 LL band via `SetBuffer` - real aliasing complexity, not just an unpooled
array) and the fixed-size macroblock entropy-coder scratch buffers (`PgfMacroBlock`/
`PgfEncodeMacroBlock`'s `Value`/`CodeBuffer`/`sigFlagVector`, each allocated once per decode/encode
session, not per-call-in-a-loop). Two real blockers, not laziness: (1) `ArrayPool&lt;T&gt;.Rent` can
return an array *larger* than requested, and `BitStream`'s helpers (`SeekBitRange`, `NumberOfWords`,
etc.) and `ComposeBitplane`'s bounds logic trust the array's own `.Length` in several places - pooling
these safely would mean auditing and fixing every such call site to use an explicit `BufferSize`
constant instead, a real correctness risk for the single most delicate, hardest-to-debug component in
this whole port (Stage 5's "all tests failed on the first run" experience is the concrete reason this
risk is taken seriously, not a hypothetical one). (2) `PgfDecoderCore`/`PgfMacroBlock` are shared,
through `PgfDecodeSession`, by both the single-shot path (session-scoped, safe to release in a
`finally`) *and* `PgfProgressiveDecoder` (caller-controlled, unbounded lifetime, deliberately not
`IDisposable` per Stage 9's own documented reasoning) - pooling at that shared layer would mean either
reversing Stage 9's simplification or threading two different lifetime policies through one shared
type. Given the measured numbers below don't show these as clearly worth that risk/complexity trade
yet, they're left as plain arrays, with the real allocation cost now quantified instead of guessed.

*Measurements* (`PictTag.PgfCodec.Benchmarks`, `ShortRun` job - 3 iterations, wider confidence
intervals than a full default run, but real numbers from real runs, appropriate for "first
measurement" per this stage's own charter, not a final tuned baseline): at 256x256/quality=8 (a
realistic thumbnail), managed single-shot decode measured ~1.34 ms vs. native's ~0.86 ms (~1.6x),
allocating ~651 KB per call; managed encode measured ~2.06 ms vs. native's ~1.37 ms (~1.5x), allocating
~1.06 MB; managed progressive decode's full level sequence measured ~1.46 ms vs. native's ~0.93 ms
(~1.6x), allocating the same ~651 KB (expected - it does the same total work as single-shot). At
512x512/quality=0 (worst case: largest tested size, lossless), managed decode allocated ~5 MB and took
~11 ms vs. native's ~8.1 ms. Output size matches the native encoder exactly at every quality value 0-15
for the 256x256 fixture (a direct, non-benchmark sweep, `--sizes`) - expected from Stage 8's round-trip
matrix, reconfirmed here as a real cross-check rather than assumed to still hold.

*Explicitly out of scope this stage, deferred to Stage 13, not skipped silently:* Browser/WASM
benchmarking (Mono interpreter and real AOT). `BenchmarkDotNet` itself only runs Desktop CoreCLR-style
process-spawning jobs, and more fundamentally, Stage 12 (wiring the managed codec into
`PictTag.UI.Browser`) hasn't happened yet - there is currently no way to execute `PictTag.PgfCodec`
code inside an actual browser session at all, so a standalone throwaway WASM timing harness built now
would duplicate infrastructure Stage 13's real-browser Playwright work needs to build anyway once
there's a real call site to measure. Revisit there, not here.

**Stage 11 — deliberately skipped, per its own stated gate.** The stage sequence itself frames this as
"a stretch goal gated on stage 10's numbers showing real headroom to chase, not a mandatory stage."
Stage 10's real numbers don't show that headroom being needed: at realistic thumbnail dimensions and
quality values, both native (~0.9 ms) and managed (~1.3-1.5 ms) decode complete in low single-digit
milliseconds - comfortably inside the "under 5 ms" acceptance bar Stage 10 set, itself derived with
real headroom over the worst case actually measured. The ~1.5-1.7x gap to native is real but not a
product problem worth chasing with a vectorization rewrite (and the correctness-re-verification risk
that comes with one, per the architecture section's own warning that SIMD "can silently alter
rounding/clamping behavior") when the unvectorized scalar port already meets the actual constraint
that matters (interactive responsiveness during thumbnail grid scrolling). Revisit if a future
real-world measurement (e.g. Stage 13's eventual in-browser numbers, where the interpreter's overhead
is much larger) shows an actual case where this gap matters.

## Stage sequence

Each stage independently committable with its own tests, per this repo's convention. Decode and
encode are interleaved deliberately (not decode-then-encode as two back-to-back halves) so the
round-trip matrix comes online as early as the shared infrastructure allows, rather than only at the
very end.

1. **Fixture + oracle infrastructure**: add the native shim's test-only `pgf_encode_bgra_alloc`/
   `pgf_free_encoded` and `pgf_debug_decode_channel` exports. Exit test: the extended native oracle
   encodes a handful of known-pixel bitmaps (solid color, checkerboard) and decodes them back
   correctly through its own existing `pgf_decode_bgra` — proving the new native exports work before
   any C# port work depends on them.
2. **`PictTag.PgfCodec` project skeleton + `BitStream` port**: the stateless bit-array primitives,
   shared by both directions. Exit test: unit tests against hand-constructed bit patterns, no PGF file
   involved yet.
3. **`PgfMemoryReader`** (the `CPGFMemoryStream`-equivalent read contract) over `ReadOnlyMemory<byte>`,
   and **`PgfByteWriter`** (growable output for encode). Exit test: edge-case unit tests for
   seek/read past the end (reader) and growth/resize correctness (writer).
4. **Header read + write**: pre-header/header/post-header/level-length array in both directions,
   explicit little-endian reads/writes, hand-decoded `PGFVersionNumber`/`ROIBlockHeader` bitfields.
   Exit test: parse every fixture's header (decode side) against known values; separately, write a
   header with the C# encoder path and confirm the *native* decoder's `Open()` reads it back correctly
   — an early, cheap cross-implementation win before the entropy coder exists.
5. **Macroblock/bitplane entropy decode + encode** (`Decoder`/`Encoder`/`CMacroBlock` equivalents,
   ported together since they're mirror images sharing buffer shapes) — the highest-risk stage in
   both directions. Exit test: Tier 3 intermediate-channel comparison for decode against the oracle's
   `pgf_debug_decode_channel`; for encode, feed a known coefficient array through the C# encoder then the
   *native* decoder's debug export and confirm the coefficients survive the round trip.
6. **Inverse + forward wavelet transform**, including decode's level-freed-after-use lifecycle. Exit
   test: Tier 3 comparison again, now downstream of the transform in both directions, still isolated
   from color conversion.
7. **YUV↔BGRA conversion both directions** (`GetBitmap` equivalent for decode, `RgbToYuv`+
   `ImportBitmap` equivalent for encode, `Channels()==4`/BGRA only) plus `Downsample`/`ComputeLevels`
   for encode. Exit test: full Tier 1 + Tier 2 byte-exact decode pass — the milestone proving the
   whole scalar decode pipeline is correct end to end.
8. **Full single-shot encode API + the 4-way round-trip matrix (Tier 4) goes live, swept across every
   quality level.** This is the central proof this PRD exists to deliver. Exit test: every fixture, at
   `quality=0`, passes all four legs of the matrix pixel-exact against the original; every fixture, at
   every other valid quality value through `MaxQuality`, passes all four legs pixel-identical to each
   other plus the size-monotonicity check. This stage is where the "quantization is deterministic, so
   lossy output should be implementation-invariant" hypothesis (see "Achievable round-trip guarantee")
   gets its first real empirical test — expect to spend real time here if it doesn't hold cleanly at
   every level on the first attempt.
9. **Progressive/level-by-level decode API**: `Open`/`Read(level)` state management mirroring
   `PgfDecoder.ProgressivePgfDecoder`'s existing public shape (encode has no progressive analog —
   PictTag never needs partial/streamed encode). Exit test: port `PgfDecoderTests.cs`'s
   `ProgressiveDecoder_*` assertions (level-0-matches-single-shot, monotonic-resolution-growth,
   malformed-input-fails-closed) against the new implementation.
10. **Span/Memory/ArrayPool hardening + benchmarking**: replace any remaining array allocations in
    either hot path with pooled/stack buffers; stand up the BenchmarkDotNet project; get the first real
    latency/allocation numbers across all configurations listed above, and use them to set this PRD's
    concrete performance acceptance bar (deferred from "Test rig: performance" above).
11. **SIMD exploration** (`System.Numerics.Vector128`/`Vector256` for the per-pixel color-conversion
    loops and the lifting passes, both directions), re-verified against the full Tier 1-4 correctness
    suite after each change — treat this as a stretch goal gated on stage 10's numbers showing real
    headroom to chase, not a mandatory stage. Decode is the actual product hot path, so prioritize
    there if time-boxing is needed.
12. **Wire real decode call sites over**: `PictTag.Data.PgfDecoding.PgfDecoder` and
    `PictTag.UI.Browser.Interop.NativePgf` (or their replacements) switch to `PictTag.PgfCodec`;
    evaluate collapsing `DesktopProgressiveBitmapLoader`/`BrowserProgressiveBitmapLoader`'s duplicated
    decode-loop logic into one shared `PictTag.UI` implementation now that both platforms share one
    real decoder with no platform-specific P/Invoke linking-model difference to justify the
    duplication (`client-side-pgf-and-remove-thumbnail-cache.md`, item 7). The encoder gets no
    production call site (see Non-goals) — it stays library/test infrastructure.
13. **Real-browser Playwright verification**: extend `ProgressivePgfBrowserTests.cs` (today documents
    the confirmed-broken native state) to prove genuinely progressive, correct pixels in a real
    headless Chromium session — the live proof this problem is actually solved, not a build success.
14. **Documentation**: update `docs/GUI.md`'s "Browser/WASM native PGF decode" section,
    `client-side-pgf-and-remove-thumbnail-cache.md`'s "what's still not built" bullet, and
    `CLAUDE.md`'s Prerequisites section (the C++ toolchain/CMake requirement may become fully
    optional/test-tooling-only — see "Open questions"). Resolve and document the native project's
    final fate.

## Acceptance criteria / Definition of Done

- All four legs of the round-trip matrix (Tier 4) pass, **for every quality level from `0` through
  `MaxQuality`**, for every fixture: pixel-exact against the original at `quality=0`, pixel-identical
  to each other (plus the size-monotonicity check) at every lossy level — not just a single sampled
  quality value.
- Decode output is byte-for-byte identical to the vendored native decoder's output for every real and
  synthetic fixture, for both single-shot and every progressive level (Tiers 1-2).
- The managed decoder runs with zero P/Invoke/native dependency under Desktop and under Browser/WASM
  in both the interpreted and AOT-published configurations.
- A real Playwright test in `PictTag.Integration.Tests` proves correct, visibly progressive pixel
  decode in an actual running headless-Chromium browser — closing the specific gap
  `ProgressivePgfBrowserTests.cs` currently documents as broken.
- BenchmarkDotNet results exist and are reported for every configuration in "Test rig: performance",
  for both encode and decode, **swept across every quality level**, with steady-state hot-path
  allocation at or near zero and output size confirmed non-increasing as quality increases.
- `dotnet test` (all existing tiers, plus the new `PictTag.PgfCodec.Tests`) stays green.
- `docs/GUI.md`, `client-side-pgf-and-remove-thumbnail-cache.md`, and `CLAUDE.md` are updated to
  reflect the new state and the resolved native-project-fate question.

## Open questions

- **Native project's final fate.** Keep `native/PictTag.PgfDecoder/` indefinitely as the test-oracle
  (this PRD's design leans on it throughout, now for both decode oracle *and* one leg of the encode
  round-trip matrix), or retire it from the repo entirely once confidence is high enough? This also
  determines whether `CLAUDE.md`'s C++ toolchain/CMake prerequisite can be dropped for everyone, or
  only becomes optional for contributors who don't touch PGF test tooling. Don't decide now — revisit
  once stage 8 (the round-trip matrix going live) has run for a while.
- **Is the ROI-enabled decode path actually reachable** by any real digiKam thumbnail this app
  encounters? If confirmed never set, document the fail-closed behavior as a known, intentional
  limitation rather than fully porting ROI cropping. Resolve during stage 4/5, not upfront.
- **SIMD scope**: how far to take vectorization (stage 11) is explicitly open — gate on stage 10's real
  numbers, not decided speculatively here.
- **Whether `PictTag.Api`'s server-side Tier 1 thumbnail decode path** (`ThumbnailService`, which also
  decodes PGF today via the same native shim through `PictTag.Data`) should switch to
  `PictTag.PgfCodec` too as part of stage 12, or stay on native/P/Invoke server-side (where the
  P/Invoke problem never existed) — leaning toward switching everything for one implementation to
  maintain, but confirm there's no server-side performance regression risk (CoreCLR JIT vs. native)
  during stage 10's benchmarking before committing to that.
- **Whether exact bitstream identity between the two encoders turns out to hold in practice** (see
  "Achievable round-trip guarantee") is worth tracking empirically once stage 8 lands, purely as a
  confidence signal — not a requirement to design toward.
- **What quality values are actually valid/meaningful.** `PGFtypes.h:88-94` defines
  `MaxBitPlanes = 15` and `MaxQuality = MaxBitPlanes` for this build (no `__PGF32SUPPORT__`), but
  `PGFHeader`'s own doc comment (`PGFtypes.h:160`) only documents three presets — "0=lossless,
  4=standard, 6=poor quality." Confirm during stage 8 whether `SetHeader`/`Quantize` actually behave
  sensibly across the full `1..15` range or whether values past ~6 are untested/degenerate in the
  original codec too (in which case the quality sweep should cover whatever the real accepted range
  turns out to be, not blindly assume `0..15` is all meaningful) — resolve empirically, don't assume
  either the type's nominal bound or the doc comment's narrower preset list without checking.
