# PGF codec benchmark evidence

Each child directory is one immutable BenchmarkDotNet run. It contains the raw CSV, Markdown, and
HTML reports that BenchmarkDotNet produced, plus `metadata.json` containing the source revision,
purpose, and command. Raw reports make later comparisons machine-readable; this index holds only the
cross-run interpretation.

| Run | Revision | Role | Reports |
|---|---|---|---|
| [`2026-08-03-9b5a3b9-stage10`](2026-08-03-9b5a3b9-stage10/) | `9b5a3b978af270423de3bce6921b101fb81a8920` | Recovered Stage 10 baseline from before the full-parity feature series. | [Markdown](2026-08-03-9b5a3b9-stage10/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report-github.md) · [CSV](2026-08-03-9b5a3b9-stage10/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report.csv) |
| [`2026-08-04-b64488d-full-parity`](2026-08-04-b64488d-full-parity/) | `b64488d38cfa0f763d6a5ed3ff55f66459085bcc` | Full-parity codec run on 2026-08-04. | [Markdown](2026-08-04-b64488d-full-parity/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report-github.md) · [CSV](2026-08-04-b64488d-full-parity/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report.csv) |
| [`2026-08-05-5927927-post-pooling-dry`](2026-08-05-5927927-post-pooling-dry/) | `5927927` | Superseded bounded Dry smoke run. It is preserved as raw historical evidence but is not valid for throughput, allocation-path, or SIMD decisions. | [Markdown](2026-08-05-5927927-post-pooling-dry/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report-github.md) · [CSV](2026-08-05-5927927-post-pooling-dry/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report.csv) |
| [`2026-08-05-bfee39d-post-pooling-shortrun`](2026-08-05-bfee39d-post-pooling-shortrun/) | `bfee39d` | Completed ShortRun of the original convenience-API matrix after pooling. | [Markdown](2026-08-05-bfee39d-post-pooling-shortrun/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report-github.md) · [CSV](2026-08-05-bfee39d-post-pooling-shortrun/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report.csv) |
| [`2026-08-05-e49c1fc-allocation-paths-shortrun`](2026-08-05-e49c1fc-allocation-paths-shortrun/) | `e49c1fc` | Completed ShortRun of convenience and opt-in allocation-reuse paths; the current evidence for allocation performance. | [Markdown](2026-08-05-e49c1fc-allocation-paths-shortrun/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report-github.md) · [CSV](2026-08-05-e49c1fc-allocation-paths-shortrun/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report.csv) |
| [`2026-08-05-f7e21ef-simd-enabled-shortrun`](2026-08-05-f7e21ef-simd-enabled-shortrun/) | `f7e21ef` | Completed vector-enabled run; preserved evidence, but not the scalar/vector decision by itself. | [Markdown](2026-08-05-f7e21ef-simd-enabled-shortrun/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report-github.md) · [CSV](2026-08-05-f7e21ef-simd-enabled-shortrun/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report.csv) |
| [`2026-08-05-6f120cf-simd-scalar-controlled-shortrun`](2026-08-05-6f120cf-simd-scalar-controlled-shortrun/) | `6f120cf` | Completed controlled scalar-fallback versus vector matrix; authoritative SIMD decision evidence. | [Markdown](2026-08-05-6f120cf-simd-scalar-controlled-shortrun/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report-github.md) · [CSV](2026-08-05-6f120cf-simd-scalar-controlled-shortrun/PictTag.PgfCodec.Benchmarks.DecodeBenchmarks-report.csv) |
| [`2026-08-06-445451b-session-workload-baseline`](2026-08-06-445451b-session-workload-baseline/) | `445451b` | First Release baseline for the new managed-only `PictTag.PgfCodec.Performance` session workbench (`SessionWorkloadBenchmarks`); starting point for the optimize-pgfcodec loop. | [Markdown](2026-08-06-445451b-session-workload-baseline/PictTag.PgfCodec.Performance.SessionWorkloadBenchmarks-report-github.md) · [CSV](2026-08-06-445451b-session-workload-baseline/PictTag.PgfCodec.Performance.SessionWorkloadBenchmarks-report.csv) |

At 256px/quality 8, the full-parity codec is 29.9% faster for single-shot decode, 28.7% faster for
encode, and 32.2% faster for full progressive decode than Stage 10. Allocations increased 86.7%,
90.8%, and 86.7% respectively. See the raw reports for the entire parameter matrix and confidence
data.

At 256px/quality 8/gradient in the allocation-path run, reusable decode is 833.5 us with no
managed allocation reported (vs. 912.5 us / 1,216,616 B for convenience decode); workspace encode
with caller-owned output is 1,195.4 us / 614,041 B (vs. 1,421.7 us / 1,817,880 B); and workspace
progressive decode is 941.6 us / 6,913 B (vs. 1,015.8 us / 1,216,616 B). These are ShortRun results
on one AVX2 development machine, not cross-runtime guarantees. The progressive path still constructs
a per-operation session graph, so it is deliberately not described as allocation-free.

The controlled SIMD matrix compares vector-enabled (`ForceScalarVectors=false`) directly with the
forced scalar fallback. At 256px/quality 8/gradient, vectorized vertical lifting reduces convenience
decode from 923.0 to 795.8 us (13.8%), reusable decode from 852.2 to 771.2 us (9.5%), and full
progressive decode from 975.6 to 844.4 us (13.4%), with identical allocations.

To archive a new run, use [`Archive-PgfCodecBenchmark.ps1`](../../../Archive-PgfCodecBenchmark.ps1)
after running BenchmarkDotNet with `--artifacts`; the script refuses to overwrite an existing run.
Name a run `yyyy-MM-dd-shortsha-description` (for example,
`2026-08-04-b64488d-full-parity`), never `current`. `Archive-PgfCodecBenchmark.ps1` validates its
input against `PictTag.PgfCodec.Benchmarks`' own three-class, nine-report shape; runs from the
managed-only `source/PictTag.PgfCodec.Performance` workbench (`SessionWorkloadBenchmarks`, a
different report-file count) are archived by copying the same `results/` reports and writing
`metadata.json` by hand in the same shape, as `2026-08-06-445451b-session-workload-baseline` does.

## Managed-only session workbench (`PictTag.PgfCodec.Performance`)

Started 2026-08-06 with no active PRD; evidence for this optimization line is recorded here rather
than in a `new-features/*.md` progress log. The workbench models a thumbnail service: decode/
progressive-decode a 50-image mixed-size session, encode one image, and bulk-encode 20 images
(caller-buffer and owned-result paths) - see `source/PictTag.PgfCodec.Performance/README.md`.

`2026-08-06-445451b-session-workload-baseline` is the starting point (ShortRun, N=3, AMD Ryzen 7
5800X, .NET 10.0.10). A DiagSessionAnalyzer pass against the incidentally-captured
`ProfiledSessionWorkloadBenchmarks.DecodeFiftyImagesInNewSession(ForceScalarVectors: False)` trace
(PID 18488, 739 samples) found `PgfImageDecoder.TryDecode`'s own body as the single largest
exclusive-sample function (~11.8%), ahead of `PgfMacroBlock.BitplaneDecode`, `PgfImageDecoder.
ConvertToBgra`, and `PgfWaveletTransform.InverseRow`/`InverseTransform`. Reading `TryDecode` showed
a plausible reason: its final BGRA result buffer always calls `ArrayPool<byte>.Shared.Rent`/`Return`
directly, even when the caller supplies a `PgfWorkspace` - the one remaining codec buffer not routed
through the workspace's own reuse pool that Stage 3b/3c/4b already built for every other decode/
encode buffer.

**Rejected**: routing that buffer through the workspace (see
[`2026-08-06-445451b-workspace-decode-buffer-rejected`](2026-08-06-445451b-workspace-decode-buffer-rejected/))
showed no measurable timing improvement under a controlled MediumRun (deltas of -0.8% to +1.6%, all
within noise) and a small but reproducible ~500-600 byte *increase* in allocated bytes per 50-image
session (`PgfWorkspace`'s byte-rent list structures growing from empty for the first time). Reverted,
not committed. Likely explanation: `ArrayPool<byte>.Shared` already serves this single-threaded
repeated-same-size-class pattern from a fast per-thread cache, so `TryDecode`'s own high exclusive
sample weight in the profile is probably attributable to something else in that method body (most
likely the `onDecoded` delegate invocation) rather than the pool rent/return calls - worth a fresh,
more targeted profiling pass rather than assuming the same cause next time.

**Also rejected**: vectorizing `PgfWaveletTransform.InverseRow` (horizontal wavelet lifting, the one
lifting pass the earlier PRD's SIMD work left scalar - see
[`2026-08-06-33bae9d-row-lifting-simd-rejected`](2026-08-06-33bae9d-row-lifting-simd-rejected/)). The
row's lifting recurrence does separate into two independent passes (every even position reads only
original odd neighbors, then every odd position reads the now-final even neighbors), so it was
implemented as gather-into-scratch/vectorize/scatter-back per pass, byte-exact against the scalar
fallback (1397/1397 including 4 new focused parity tests) and Browser/WASM-buildable. But a controlled
MediumRun showed a clear ~17-18% *regression*, not a wash: splitting one cache-friendly fused pass
into two full separate passes over the row, plus the gather/scatter every chunk needs (even/odd
operands are stride-2, not contiguous, so plain `Vector<int>` loads can't read them directly),
together cost substantially more than the ~4-op-per-element arithmetic they replaced ever saved.
Reverted, not committed. Do not retry this same two-pass-plus-gather shape without a fundamentally
cheaper way to get even/odd operands into vector lanes.

**No measurable performance effect, kept anyway for de-duplication**: vectorizing `PgfMacroBlock`'s
significance-flag scan (see
[`2026-08-06-937d052-bitplane-sigflag-scan-rejected`](2026-08-06-937d052-bitplane-sigflag-scan-rejected/)
for the raw before/after evidence; directory name predates the final call below). Re-examining the
same baseline profile confirmed `BitplaneDecode` as the largest real (non-harness) codec hotspot
after excluding `TryDecode`'s top-ranked exclusive-sample bucket, which is actually inlined
`SessionWorkloadBenchmarks.Checksum` benchmark-harness code, not codec work. While the bulk of
`BitplaneDecode`/`ComposeBitplane`/`ComposeBitplaneRld` genuinely is inherently sequential bit-level
entropy decoding (unchanged assessment, see below), one specific sub-operation duplicated three times
across those methods - `while (!sigFlagVector[sigEnd]) sigEnd++;`, a linear scan through a `bool[]`
for the next already-significant coefficient - is a plain memory search, independent of bitstream
decode order. Replaced with a shared `FindNextSignificant` helper reinterpreting the array as `byte[]`
and calling the BCL's hardware-accelerated `ReadOnlySpan<byte>.IndexOf`. Byte-exact (1393/1393, all
pre-existing tests) and Browser/WASM-buildable, but a matched-conditions MediumRun (`git stash`
isolating the one file, before/after run back-to-back) showed deltas of -1.1% to +0.7% with no
consistent direction - a clean null result, not borderline. Likely explanation: the original scalar
loop was already about as cheap as a bounds-checked byte scan gets, and most individual scan lengths
in this workload are short enough that the vectorized path's fixed per-call overhead cancels out its
per-element savings. Initially reverted as a failed performance experiment; **reinstated** on
explicit direction that a genuine structural improvement (de-duplicating three copies of the same
loop into one named, documented helper) is worth keeping even without a measured speedup - the
`FindNextSignificant` helper is committed in `PgfMacroBlock.cs`.

**Also tried, also flat, not kept**: collapsing `SetBitAtPos`/`SetSign`'s separate read-then-write
`Value[pos]` array accesses into a single `ref int slot = ref Value[pos];` (one bounds check instead
of two) - the next-largest concrete micro-target inside the same `BitplaneDecode` hotspot, called
once per newly-significant coefficient. Byte-exact, but two independent matched-conditions MediumRun
comparisons (run at a point where the machine had visibly more background load - baseline StdDev rose
to 2-6ms from the usual ~1ms) both showed inconsistent, sub-2% deltas in mixed directions. Unlike the
sigFlagVector scan, this rewrite doesn't remove any duplication or otherwise clarify the code on its
own merits, so - lacking both a measured win and an independent structural justification - it was
reverted (`git checkout --`) rather than kept. Not separately archived given how noisy both runs were;
the codebase-outcome record here is the archive entry.

**Assessed, not implemented**: the two remaining profiled functions. `PgfImageDecoder.ConvertToBgra`
(really `PgfColorConversion.DecodeYuvaToBgra`, RGBA being the only mode any real caller - including
this benchmark - ever produces) has heavier per-pixel arithmetic than the wavelet filters (three
branchy `Clamp8` calls SIMD could make branchless), but only its `downsample=false` case (chroma at
full resolution, lockstep with Y - genuinely contiguous) is cleanly vectorizable; `downsample=true`
(chroma reused across 2x2 blocks - 2/3 of this benchmark's images by construction, and the realistic
case for real digiKam thumbnails) needs the same kind of gather/duplicate pattern that just cost the
row-lifting experiment 18%. Since the session images are quality 0/8/15 in even thirds, the
`downsample=false`-only subset caps the *maximum possible* measured gain at roughly 2-2.5% of decode
time before even accounting for the interleaved-BGRA-write overhead - marginal enough, on top of two
already-rejected candidates, not to be worth the implementation/testing effort without first covering
the `downsample=true` majority, which carries the same risk class that just failed. The rest of
`PgfMacroBlock.BitplaneDecode`/`ComposeBitplane`/`ComposeBitplaneRld` (Malvar bitplane/significance-
map entropy decoding), beyond the sigFlagVector scan above, remains unattempted: it is inherently
sequential, bit-by-bit, data-dependent bitstream parsing (variable-length RLE runs, sign/refinement
bits whose meaning depends on everything decoded before them) - the same problem class as Huffman/
arithmetic decoding, not vectorizable via ordinary SIMD loop transforms. Meaningfully speeding it up
would need research-level bit-parallel decoding techniques, out of scope for measure-and-revert
iteration.

This closes out every candidate the 2026-08-06 baseline profile surfaced, including a second pass
specifically re-mining it for non-memory candidates: three were implemented, correctly, and rejected
on real measurement; two were assessed and found to have a poor risk/reward ratio before
implementation. A fresh profile of a further-optimized build would be needed to find the next real
candidate.
