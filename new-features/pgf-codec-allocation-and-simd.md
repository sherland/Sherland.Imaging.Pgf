# PGF codec: reusable workspaces, allocation reduction, and measured SIMD — PRD

**Status: done.** Allocation ownership and the measured, byte-exact SIMD path are complete; see the
Progress log for the corrected evidence.

## Context

`PictTag.PgfCodec` is a dependency-free managed PGF codec used by both the Desktop/server stack and
the Browser/WASM UI, and is intended to become a standalone NuGet package. Its public decode APIs
already rent the transient BGRA callback buffer, but its larger coefficient, subband, macroblock,
and encoder buffers are ordinary GC allocations.

The checked-in .NET 10 AVX2 BenchmarkDotNet baseline at
`docs/benchmarks/pgfcodec/2026-08-04-b64488d-full-parity/` makes the cost concrete: at 256px/quality
8, managed single-shot decode is 940.6 us and allocates 1,216,264 B; all-level progressive decode
is 989.5 us and allocates 1,216,265 B; encode is 1,465.9 us and allocates 2,014,048 B. At 512px/Q8,
managed decode allocates 4,429,871 B and encode 7,587,693 B. The managed codec remains about
1.5-1.8x native latency on that machine.

This reopens an intentionally deferred decision in `managed-pgf-codec.md`: pooling was not adopted
because `ArrayPool<T>` can over-rent and some low-level code treated array capacity as logical data
length. That constraint is real. Pooling before fixing it could corrupt a valid bitstream. The same
PRD deferred SIMD because the thumbnail latency then measured as acceptable; the new allocation
baseline justifies a fresh, evidence-led evaluation, but does not make SIMD a presumed win.

## Goals

1. Provide an opt-in, reusable `PgfWorkspace : IDisposable` (or an equivalently explicit resource
   owner) that can make codec working memory allocation-free after warm-up for supported steady-state
   decode and encode shapes.
2. Preserve all existing APIs and their ownership semantics. A caller choosing an owned `byte[]`
   result may still allocate that result; “allocation-free” never means that convenience result is
   magically allocation-free.
3. Add opt-in caller-owned output APIs so callers can avoid a final encode-array allocation/copy and
   use a supplied destination safely.
4. Reduce obvious independent encoder allocations, including the downsampled chroma range slices.
5. Evaluate SIMD only where profiling demonstrates a material benefit, with byte-exact scalar
   fallbacks on Desktop, ARM, and Browser/WASM.

## Non-goals

- Changing the existing callback's transient BGRA lifetime. It is returned to the pool immediately
  after the callback today; exposing it as a persistent result would create use-after-return bugs.
- Claiming zero allocations for first invocation (JIT, static initialization, pool population),
  arbitrary image dimensions, or callers that request owned arrays.
- Vectorizing entropy coding speculatively. Its control-flow- and bitstream-heavy loops are not an
  initial SIMD candidate.
- Adding unsafe target-specific code with no managed scalar fallback, or breaking Browser/WASM.
- Parallel/OpenMP-style macroblock processing; it remains deliberately disabled in the native oracle
  and is a distinct concurrency design problem.

## Verified current state

- `PgfImageDecoder.TryDecode` and `PgfProgressiveDecoder.TryDecodeLevel` rent only their output
  BGRA buffers (`PgfImageDecoder.cs`, `PgfProgressiveDecoder.cs`).
- `PgfDecodeSession` creates wavelet transforms; `PgfSubband.AllocMemory` creates `int[]` buffers,
  and `FreeMemory` merely drops references (`PgfDecodeSession.cs`, `PgfWaveletTransform.cs`,
  `PgfSubband.cs`). Decoder and encoder macroblocks also own fixed allocated scratch arrays.
- `PgfImageEncoder.TryEncodeMode` allocates one full-resolution `int[]` per channel and creates
  additional chroma arrays via `channelBuffers[c][..chromaSize]`. `PgfByteWriter` uses a growing
  `MemoryStream`, followed by `WrittenSpan.ToArray()` for the current owned-array contract.
- The codec and benchmark projects target `net10.0`, allow unsafe code, and contain no present
  `Vector<T>` or hardware-intrinsic implementation. The native oracle has no explicit SIMD either.
- `PgfColorConversion` has independent per-pixel loops; wavelet vertical lifting has independent
  horizontal lanes. Wavelet row lifting has loop-carried neighbor dependencies, so a naive vector
  loop is not correct. Entropy loops are branch-heavy.
- Existing `PictTag.PgfCodec.Tests` include native-oracle full matrices plus focused color and
  wavelet tests. `PictTag.PgfCodec.Benchmarks` already has `MemoryDiagnoser` for single-shot decode,
  encode, and full progressive decode. No allocation-budget test exists.

## Proposed architecture

`PgfWorkspace` owns rented codec work buffers and is explicitly non-thread-safe. It grows to meet
the largest requested shape and returns all arrays in `Dispose`, including exception paths. It does
not silently attach pooled state to the existing non-disposable `PgfProgressiveDecoder`; that type's
caller-controlled lifetime requires either a workspace supplied per operation or a separate,
explicitly disposable session API.

Every pooled buffer must carry a logical length independent of its physical array capacity. The
logical-length audit is a gate before any `ArrayPool<T>` migration. It includes `BitStream`, entropy
buffer consumers, subband/macroblock operations, and any `Span` created from a rented array.

Output ownership is separate from workspace ownership. Preserve current callback APIs. Add an opt-in
encode destination contract only after its behavior is specified: exact bytes written, buffer-too-
small result, failure/partial-write behavior, and whether a streaming writer can replace a fixed
destination when encoded size is not knowable beforehand.

## Test rig and benchmark rules

- Extend existing native-oracle decode/encode matrices and focused `PgfWaveletTransformTests` /
  `PgfColorConversionTests`; output must remain byte-exact for every scalar, pooled, and SIMD path.
- Add focused allocation tests using `GC.GetAllocatedBytesForCurrentThread`, warmed first and scoped
  to an explicit operation/runtime. They prove the workspace contract, not a universal GC promise.
- Run `dotnet build source/PictTag.UI.Browser` and relevant browser coverage when public workspace
  or SIMD APIs change.
- For every performance stage, run the existing BenchmarkDotNet matrix and archive it with
  `Archive-PgfCodecBenchmark.ps1` into a new immutable versioned directory as required by
  `docs/benchmarks/pgfcodec/README.md`. Report environment-specific numbers; do not generalize one
  machine's throughput to all runtimes.

## Stage sequence

### Stage 1 — Performance contract, attribution, and baseline tests

Document operation-specific allocation targets: existing convenience decode/encode, workspace decode
with transient caller output, and caller-owned encode output. Extend benchmarks with a committed
fixture/mode complement to the synthetic gradient matrix where useful, and add warmed allocation
contract tests. Record the allocation contributors separately (output, coefficient planes,
subbands, macroblocks, tuple bookkeeping, writer growth/final copy).

Exit criteria: the current baseline is reproducible and every later stage has a precise comparison
target; no production codec behavior changes.

### Stage 2 — Logical-length safety and workspace ownership spike

Audit each buffer-consuming path that can receive an oversized rent. Replace capacity-derived
semantics with explicit logical counts before pooling. Design and test `PgfWorkspace` ownership:
non-concurrent use, growth/reuse across image sizes, post-dispose behavior, exception cleanup, and
the deliberate pool-clearing policy for image coefficients. Confirm the design builds and runs under
the Browser/WASM target without relying on unsupported intrinsics, pinning, or stream behavior.

Exit criteria: targeted over-rented-buffer tests pass, the workspace has no hidden retention path,
and Browser/WASM validation passes. No large work buffer is pooled yet.

### Stage 3a — Workspace-backed decoder storage

Migrate decoder transform/subband and macroblock scratch storage to workspace-owned rents, including
the single-shot and progressive call paths. Add only caller-owned workspace parameters; do not make
the existing non-disposable progressive decoder implicitly own pooled memory. Ensure all success,
false-return, cancellation, and exception paths return rents exactly once when the caller disposes
its workspace.

Exit criteria: oracle and focused transform/color suites stay byte-exact; existing public APIs
preserve behavior; workspace-backed paths hold no GC-owned coefficient/subband/macroblock arrays.

### Stage 3b — Reusable workspace backing buffers

**Inserted after Stage 3a's implementation finding.** `PgfWorkspace` correctly owns pooled backing
arrays, but a fresh decode rents another set. Add explicit `Reset` semantics that recycle completed
operation backing buffers for a later compatible operation without changing the stateless API or
letting an undisposed progressive decoder own opaque pooled memory. Define the invalidation rule
and prove exact backing reuse.

Exit criteria: repeated supported decode operations reuse completed workspace backing arrays and
disposal returns all rents exactly once.

### Stage 3c — Reusable decoder-session allocation contract

**Inserted after Stage 3b's implementation finding.** Resetting a workspace reuses large backing
arrays but still constructs the managed session/wavelet object graph. Add the smallest explicit
reusable session/lease API that caches that graph for the same immutable payload, then set a warmed
allocation budget that includes only the caller-selected output ownership. Define cancellation and
failure reset behavior before implementation.

Exit criteria: repeated supported decode operations using the explicit reusable path meet the
documented warmed allocation budget without changing the convenience API.

### Stage 4a — Eliminate downsampled chroma copies

Remove downsampled chroma range-copy allocations while preserving channel ownership and wavelet
lifetime. The transform's declared dimensions, not a backing array's capacity, are its logical
extent.

Exit criteria: encoding remains native-oracle/self-round-trip correct and no downsampled chroma
prefix is copied solely to produce a shorter array.

### Stage 4b — Encoder workspace and output pipeline

Migrate safe encoder work buffers to the workspace. Then add the opt-in
caller-owned output destination or writer API with exact byte-count and failure semantics, retaining
the current `out byte[]` method unchanged. Measure each sub-step separately rather than bundling
them into one unverifiable performance claim.

Exit criteria: encoding remains native-oracle/self-round-trip correct; each sub-step has a benchmark
comparison; the caller-owned-output path avoids final owned-output allocation by contract.

### Stage 5 — Profile-gated SIMD experiments

Profile after pooling, because removed GC pressure may change the hotspot ranking. Experiment first
with BGRA/YUVA color conversion, then independently with the vertical-lifting horizontal lanes.
Each experiment must retain a scalar tail and fallback; guard intrinsic use by supported hardware and
validate the fallback under Browser/WASM. Reject any experiment that does not meet a predeclared,
meaningful improvement threshold on its measured target or that risks rounding, clamping, overflow,
or bit-exactness changes.

Exit criteria per accepted SIMD path: byte-exact full/focused test matrix, scalar fallback coverage,
Browser/WASM verification, and an archived benchmark showing the measured benefit. A rejected
experiment is documented and removed rather than retained as complexity without payoff.

### Stage 6 — Documentation and final performance record

Update `docs/PGF-CODEC.md` with the exact opt-in allocation contract, ownership/lifetime rules,
supported SIMD acceleration and fallbacks, and any intentionally retained allocations. Archive the
final BenchmarkDotNet comparison and update this PRD's Progress log with actual test counts and
findings.

Exit criteria: public documentation matches shipped APIs and all benchmark evidence is versioned.

## Acceptance criteria / Definition of Done

- Existing APIs remain source- and behavior-compatible.
- The workspace contract is explicit, testable, and does not leak pooled buffers through a
  non-disposable API.
- Logical length is never inferred from an over-rented array's capacity in codec data paths.
- The chosen workspace operations meet their documented warmed allocation contract without changing
  decoded pixels or encoded PGF bytes.
- Every retained SIMD path is byte-exact, has a scalar fallback, and has archived measured evidence.
- Desktop and Browser/WASM builds/tests relevant to the changed APIs pass.

## Open questions

- Whether encode's destination contract is best expressed as `TryEncode(Span<byte>, out int
  bytesWritten)` plus sizing support, an `IBufferWriter<byte>` path, or both. Stage 4 must decide
  from real header-patching/output-size constraints rather than assume a fixed destination suffices.
- Whether a reusable progressive decoder session should become disposable or accept a workspace per
  decode operation. Stage 3 must choose the smallest API that makes pooled-memory lifetime explicit.
- The SIMD improvement threshold and target matrix. Stage 5 sets these from Stage 4 profiles rather
  than inventing one now.

## Progress log

**Stage 1 — done.** Added `PgfAllocationBaselineTests` to establish the deliberately qualitative,
warmed convenience-path baseline: both decode and encode still allocate after warm-up while
preserving lossless decode pixels and producing native-decodable output. The first draft compared a
quality-8 lossy decode with its original source; that was a wrong test expectation, not a codec
defect, and the exact-pixel assertion now correctly uses quality 0. Expanded all three
BenchmarkDotNet matrices from only a gradient to deterministic gradient and checkerboard fixtures
via `FixtureKind`, so future comparisons do not silently optimize only smooth imagery. Focused
tests: 2/2 green. Full suite: 1378/1378 green (1376 existing + 2 new, zero regressions).
**Stage 2 — done.** Added `PgfWorkspace` as the explicit, single-threaded owner of
`int`/`uint`/`bool`/`byte` pool rents. Its API returns `Memory<T>` sliced to the requested logical
length, never a raw over-rented array, and disposal is idempotent while rejecting new rents. The
intentional clearing decision is now code-level documentation: image coefficients are not secret,
and clearing multi-megabyte work buffers would defeat this feature; consumers must overwrite every
logical element they read. Focused ownership/logical-length tests: 2/2 green. Browser/WASM build:
green. Full suite: 1380/1380 green (1378 existing + 2 new, zero regressions).
**Stage 3a — done.** Wired an optional caller-owned workspace through both single-shot and
progressive decode opening, then migrated normal wavelet subband buffers and decoder macroblock
coefficient/code/significance buffers to it. The existing APIs retain their allocation behavior when
no workspace is supplied; a caller opts in with `using var workspace` and therefore controls pool
lifetime explicitly. Real finding: this is not yet a literal warmed zero-allocation decode path,
because each stateless decode still constructs a session/wavelet object graph and rents fresh backing
arrays. The PRD therefore splits the original Stage 3 rather than falsely claim that the convenience
API is allocation-free. Focused tests:
3/3 green. Full suite: 1381/1381 green (1380 existing + 1 new, zero regressions).
**Stage 3b — done.** `PgfWorkspace.Reset()` now moves completed operation rents into private
type-specific reuse lists instead of returning them immediately to the shared pool. Reopening a
decoder after reset reuses those backing arrays; reset explicitly invalidates all earlier
workspace-backed decoder/session internals, so it cannot silently race an in-flight operation.
Focused tests: 5/5 green (including exact-array identity and two sequential lossless decodes). Full
suite: 1383/1383 green (1381 existing + 2 new, zero regressions). The remaining managed session
object graph is a separate Stage 3c concern.
**Stage 3c — done.** Added the opt-in, disposable `PgfReusableDecoder` for repeated full decodes
of one immutable PGF payload. It retains the parsed session graph, macroblock state, result
descriptors, and workspace while rewinding the stream/subbands between calls. Real finding: the
workspace needs a retention boundary—without it, reset made the live macroblock buffers available
as wavelet scratch and corrupted the next decode. `PgfWorkspaceMark` now preserves session-owned
arrays while recycling only per-pass storage; partial macroblock wire reads also explicitly restore
their original zero-padding semantics. Cancellation and failed work mark the reusable session dirty
before entering the decode loop, so the next call always starts from the stream beginning. The
measured contract is zero codec-owned allocations after one decode-and-recycle warm-up; the callback
still deliberately owns any result allocation it requests. Focused tests: 8/8 green, including
byte-identical repeat decode, allocation measurement, and cancellation recovery. Full suite:
1386/1386 green (1383 existing + 3 new, zero regressions).
**Stage 4a — done.** Removed `channelBuffers[c][..chromaSize]`: downsampling already compacts
chroma into the source plane prefix, and `PgfWaveletTransform` uses its supplied dimensions for all
logical reads and writes. This removes one allocation/copy for every downsampled chroma channel
without changing emitted bytes. Focused encoder tests: 18/18 green. Full suite: 1381/1381 green.
**Stage 4b — done.** Migrated encoder channel planes and every fixed entropy macroblock/scratch
array into the optional caller-owned workspace, while retaining the allocation-based convenience
path unchanged. Added the span-destination `TryEncodeMode` overload: it runs the same encoder core,
reports the exact completed byte count, never partially writes an undersized destination, and skips
only the convenience overload's final owned `ToArray()` copy. Real finding: encoding remains
internal fixture infrastructure, so the new overload correctly stays internal too rather than
expanding the planned NuGet package surface prematurely. Focused encoder tests: 21/21 green,
covering byte-for-byte equivalence, required-size failure semantics, and workspace equivalence.
Full suite: 1389/1389 green (1386 existing + 3 new, zero regressions).
**Stage 5 — done (corrected allocation record and accepted SIMD).** The earlier bounded `Dry`
matrix under `2026-08-05-5927927-post-pooling-dry/` was incorrectly treated as a SIMD decision. It
is immutable historical smoke evidence only: BenchmarkDotNet explicitly says its samples are too
short for throughput conclusions. A complete convenience-only ShortRun was then archived under
`2026-08-05-bfee39d-post-pooling-shortrun/`; it was valid, but did not exercise the opt-in paths and
therefore could not measure the allocation work itself. Stage 5 now adds the reusable decoder,
workspace encoder with caller-owned output, and workspace progressive decoder to the benchmark
matrix (`e49c1fc`) and archives the completed 132-case ShortRun under
`2026-08-05-e49c1fc-allocation-paths-shortrun/`. At 256px/Q8/gradient it measures reusable decode
at 833.5 us / no managed allocation reported vs. convenience 912.5 us / 1,216,616 B; workspace
encode at 1,195.4 us / 614,041 B vs. 1,421.7 us / 1,817,880 B; and workspace progressive decode at
941.6 us / 6,913 B vs. 1,015.8 us / 1,216,616 B. Full codec suite: 1389/1389 green. No SIMD path
was implemented or benchmarked at that point, so no accept/reject conclusion followed. A real
experiment then vectorized only independent vertical-lifting columns with `Vector<int>`, preserving
scalar tails and a forced-scalar fallback. Focused fallback parity plus the full native-oracle suite
are byte-exact (1390/1390), and Browser/WASM builds. The completed vector-enabled run is archived
under `2026-08-05-f7e21ef-simd-enabled-shortrun/`; the authoritative controlled 264-case ShortRun,
which compares `ForceScalarVectors=false` and `true` in one matrix, is archived under
`2026-08-05-6f120cf-simd-scalar-controlled-shortrun/`. At 256px/Q8/gradient it measures vector
enabled at 795.8 us vs. forced scalar 923.0 us for convenience decode (13.8% faster), 771.2 vs.
852.2 us for reusable decode (9.5%), and 844.4 vs. 975.6 us for progressive decode (13.4%), with
unchanged allocations. This meets the meaningful-benefit threshold and keeps the SIMD path.
**Stage 6 — done.** `docs/PGF-CODEC.md` and the benchmark index now distinguish the invalid Dry
smoke run, completed allocation-path run, vector-enabled evidence, and the authoritative controlled
SIMD comparison. Final regression gate: 1390/1390 green and Browser/WASM build green.
