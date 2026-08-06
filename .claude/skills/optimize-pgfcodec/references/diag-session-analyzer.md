# DiagSessionAnalyzer support

Use [bivex/DiagSessionAnalyzer](https://github.com/bivex/DiagSessionAnalyzer) to turn the ETL
inside a DiagnosticsHub `.diagsession` into PID-scoped function and call-tree evidence. The skill
does not vendor the third-party source. `scripts/Install-DiagSessionAnalyzer.ps1` checks out a
pinned revision into a temporary/cache directory, and `scripts/Invoke-DiagSessionAnalyzer.ps1`
provides the repeatable invocation.

## Setup

From the repository root:

```powershell
.\.claude\skills\optimize-pgfcodec\scripts\Install-DiagSessionAnalyzer.ps1
```

The default destination is outside the repository under `$env:TEMP`. Pass `-InstallPath` when a
shared cache is preferred. The script requires `git`, the .NET SDK, and network access on the first
run; it restores/builds the analyzer and prints the project path.

## Analyze a captured session

DiagnosticsHub stores ETL below the extracted `.diagsession` directory. Point the wrapper at that
ETL and the benchmark worker PID shown in the BenchmarkDotNet profiler log:

```powershell
.\.claude\skills\optimize-pgfcodec\scripts\Invoke-DiagSessionAnalyzer.ps1 `
  -EtwPath C:\path\to\sc.user_aux.etl `
  -Pid 7392 `
  -Top 50 `
  -TimeoutSeconds 30 `
  -OutputPath C:\tmp\pgf-decode-analysis.txt
```

PID filtering is essential. An unfiltered trace is dominated by OS, process startup, JIT, and other
work unrelated to the codec. Treat sampled percentages from a short profiling run as directional
evidence; confirm any proposed change with the managed timing benchmark and the C++ comparison.

The analyzer can resolve more names when `_NT_SYMBOL_PATH` points to a usable symbol cache. Missing
symbols do not invalidate managed method names emitted by the runtime, but they can limit source-line
and native-frame detail.
