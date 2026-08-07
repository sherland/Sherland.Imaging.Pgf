# Repository Guidelines

Instructions for any coding agent working in this repository — Claude Code, Codex, Cursor,
Copilot, or otherwise. Agent-specific directives (if any) live in that agent's own config file
(e.g. `CLAUDE.md`), which imports this file rather than duplicating it.

## Project

`Sherland.Imaging.Pgf` is a dependency-free C# port of digiKam's vendored `libpgf` codec (PGF =
Progressive Graphics File, a wavelet-based image format): decode (single-shot and
progressive/level-by-level) and encode, with no native/P/Invoke dependency in the shipping
library. The core codec is a from-scratch **managed** reimplementation rather than a wrapper around
a compiled native binary — though, per the licensing rationale in [README.md](README.md), much of
it is a close, line-by-line translation of the original C++ source rather than an independent
reimplementation. It was originally built as part of a larger photo-tagging application and is now
being extracted to stand alone and be published as its own NuGet package — see
[`docs/PGF-CODEC.md`](docs/PGF-CODEC.md) for exactly what's supported, what's permanently out of
scope (dead in the native reference too), and what's a real gap against full C++ parity.

This repository's git history was filtered from that larger application's own history down to just
the commits that touch this codec, its native C++ oracle, and the three skills listed below — see
each `new-features/*.md` PRD's own Progress log for the stage-by-stage build history behind the
current code. Some of those PRDs mention other projects (e.g. a `PictTag.Data`/`PictTag.UI.*`
facade) by name in places; those describe the application this codec was originally integrated
into and are not part of this repository — they're accurate history, not dangling references to
fix.

## Project Structure & Architecture

- [`source/Sherland.Imaging.Pgf/`](source/Sherland.Imaging.Pgf/) — the managed codec library
  itself.
- [`source/Sherland.Imaging.Pgf.Tests/`](source/Sherland.Imaging.Pgf.Tests/) — xUnit v3 tests,
  proven byte-exact against the native oracle across every mode/dimension/quality combination.
- [`source/Sherland.Imaging.Pgf.Benchmarks/`](source/Sherland.Imaging.Pgf.Benchmarks/) —
  BenchmarkDotNet console app comparing the managed codec against the native oracle.
- [`source/Sherland.Imaging.Pgf.Performance/`](source/Sherland.Imaging.Pgf.Performance/) — a
  managed-only, end-to-end optimization workbench (no native comparison), driven by the
  `optimize-pgfcodec` skill's profiling loop.
- [`native/Sherland.Imaging.Pgf.Native/`](native/Sherland.Imaging.Pgf.Native/) — digiKam's own
  vendored `libpgf` C++ source (LGPL-2.1+, unmodified, license + provenance kept alongside) behind
  a thin Qt-free C shim, built via CMake. Test/benchmark infrastructure only — the
  correctness/performance oracle `Sherland.Imaging.Pgf.Tests`/`.Benchmarks` compare against — not a
  production dependency of the managed library.
- [`docs/PGF-CODEC.md`](docs/PGF-CODEC.md) — the current-state reference: what's supported and
  what's deliberately not, with reasoning and exact source pointers.
- [`docs/benchmarks/pgfcodec/`](docs/benchmarks/pgfcodec/) — one immutable, versioned folder per
  BenchmarkDotNet run (raw CSV/Markdown/HTML reports + `metadata.json`), plus a `README.md` index
  holding the cross-run interpretation and the running "Native vs. managed" comparison table.
- [`new-features/*.md`](new-features/) — staged PRDs; each is the authoritative, stage-by-stage
  build history for one piece of the codec (why a decision was made, what was tried, what broke).
- [`data/test-digikam/sample-thumbnail.pgf`](data/test-digikam/sample-thumbnail.pgf) — a real PGF
  blob extracted from an actual digiKam thumbnail cache, used as a fixture by
  `Sherland.Imaging.Pgf.Tests`.

## Build, Test, and Development

```bash
dotnet build Sherland.Imaging.Pgf.slnx      # whole solution
dotnet test source/Sherland.Imaging.Pgf.Tests
```

Test projects use **xUnit v3 on the Microsoft.Testing.Platform runner** (`global.json` sets
`"test": {"runner": "Microsoft.Testing.Platform"}`), not VSTest. Running a single test class needs
`--filter-class` after a `--` separator, **not** VSTest's `--filter`:

```bash
dotnet test source/Sherland.Imaging.Pgf.Tests -- --filter-class "*.PgfRoiRoundTripTests"
```

Benchmarks and the managed-only performance workbench:

```powershell
$artifacts = "C:/tmp/pgfcodec-benchmark-$(Get-Date -Format yyyyMMdd-HHmmss)"
dotnet run -c Release --project source/Sherland.Imaging.Pgf.Benchmarks -- `
  --job short --filter "*" --artifacts $artifacts
dotnet run -c Release --project source/Sherland.Imaging.Pgf.Performance
```

See [`docs/PGF-CODEC.md`](docs/PGF-CODEC.md)'s "Performance benchmarks" section for the full
matrix, why an `--artifacts` directory outside the repo is mandatory, and the archiving convention
([`Archive-PgfCodecBenchmark.ps1`](Archive-PgfCodecBenchmark.ps1)) every reportable run must
follow — never rely on a console transcript or the gitignored `BenchmarkDotNet.Artifacts/` as the
performance record.

**Both commands above are long-running** (BenchmarkDotNet's own warmup/pilot/actual phases easily
run several minutes per benchmark, longer under `--job full` or a wide `--filter`) and **must never
be killed for running past an agent tool's default foreground timeout** — that isn't a hang, it's
the benchmark doing its job. Run them backgrounded (or with an explicitly extended timeout) and
wait for real completion; only treat a run as stuck and intervene if it's still running with no
output/progress after literally hours, or has visibly crashed/thrown. Killing a run early doesn't
just waste the time already spent — a truncated BenchmarkDotNet process can also leave a corrupt
partial report in `BenchmarkDotNet.Artifacts/`, which is exactly the kind of unreliable console/
partial-artifact result the paragraph above says never to treat as the performance record.

There is **no CI configuration in this repo** — running the test suite locally before pushing is
the current verification step.

### Prerequisites

- **The native oracle needs a C++ toolchain + CMake**, but only for
  `Sherland.Imaging.Pgf.Tests`/`.Benchmarks`, which use it as their correctness/round-trip and
  performance *oracle* — not a production dependency of the managed library itself:

  ```bash
  cmake -S native/Sherland.Imaging.Pgf.Native -B native/Sherland.Imaging.Pgf.Native/build
  cmake --build native/Sherland.Imaging.Pgf.Native/build --config Release
  ```

  This produces `SherlandImagingPgfNative.dll` (or the platform equivalent), which
  `Sherland.Imaging.Pgf.Tests`/`.Benchmarks` pick up automatically via a `Condition="Exists(...)"`
  `<None Include>` in their `.csproj` — no manual copy step needed. On Windows, run this from a
  Visual Studio Developer Command Prompt (or after `vcvarsall.bat x64`) if `cmake`/`cl` aren't
  already on `PATH`; a full Visual Studio install's own bundled CMake+Ninja
  (`Common7\IDE\CommonExtensions\Microsoft\CMake\`) works without a separate CMake install. Without
  the built DLL, the native-comparison tests fail outright — there is no skip/fallback path.
- `global.json` pins the `Microsoft.Testing.Platform` test runner; no specific SDK version is
  pinned.

## Coding & Testing

Follow the surrounding C# style and XML-comment density. Prefer explicit, tested behavior over
assumptions: inspect installed APIs, source, and real fixtures before making claims. Add or update
focused tests with every behavior change.

- **Verify, don't recall.** This codebase's docs and comments repeatedly emphasize confirming
  claims against the real native source, a real build, or an actual decoded fixture rather than
  trusting documentation or memory — several real findings here (an `OverflowException` bug, a
  wrong `MaxQuality` claim, byte-for-byte level-length cross-checks) were only caught this way.
- **Byte-exact parity against the native oracle is the standing correctness bar**, not just "close
  enough" — every decode/encode path change needs a fixture proving it against the real C++
  reference, not just a self-consistency round-trip.
- **The benchmark archive is a permanent record, not scratch output.** Every comparison run that's
  reported anywhere must be committed under `docs/benchmarks/pgfcodec/` via
  `Archive-PgfCodecBenchmark.ps1`, named `yyyy-MM-dd-shortsha-description`; the "Native vs.
  managed" table in that folder's `README.md` must stay current whenever a reported run changes
  the picture.

## Commits & Pull Requests

Use concise, information-dense commit subjects, such as `Stage 4: native shim export for real
per-level byte lengths`. Keep each commit scoped and tested. PRs should explain the behavioral
change and name tests run. **Never add AI/agent co-author lines** (e.g. Claude/Anthropic, Copilot)
to commits or PRs in this repo.

## Agent Skills

Repository skills are maintained in both `.claude/skills/` and `.agents/skills/`. Keep matching
skills aligned. `name` and `description` are the cross-agent required fields; retain compatible
host-specific extensions such as `allowed-tools` and provenance metadata, but do not rely on them
for cross-host permission enforcement.

- `implement-prd` — work through one of this repo's own staged `new-features/*.md` PRDs end to
  end.
- `spec-to-staged-plan` — turn an externally-drafted spec into a verified, staged PRD in this
  repo's own style, before `implement-prd` can be used on it.
- `optimize-pgfcodec` — iteratively profile and optimize the managed codec for end-to-end
  performance, validating every accepted change against the native oracle.
