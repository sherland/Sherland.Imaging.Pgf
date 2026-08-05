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

To archive a new run, use [`Archive-PgfCodecBenchmark.ps1`](../../../Archive-PgfCodecBenchmark.ps1)
after running BenchmarkDotNet with `--artifacts`; the script refuses to overwrite an existing run.
Name a run `yyyy-MM-dd-shortsha-description` (for example,
`2026-08-04-b64488d-full-parity`), never `current`.
