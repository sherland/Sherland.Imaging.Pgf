# PictTag.PgfCodec.Performance

This is the managed-only, end-to-end optimization harness for `PictTag.PgfCodec`. It intentionally
does not duplicate low-level codec correctness tests or the native C++ comparison matrix; those stay
in `PictTag.PgfCodec.Tests` and `PictTag.PgfCodec.Benchmarks`.

The workloads model a thumbnail service starting a fresh session:

- decode 50 mixed-size thumbnails from 300×120 through 500×500;
- progressively decode the same 50-image browsing session, coarse-to-fine;
- encode one 500×500 image through the owned-result API;
- encode 20 mixed-size images into caller-owned buffers with a recycled workspace;
- encode the same 20 images through the owned-result API as an allocation baseline.

Run the complete workload set in Release mode:

```powershell
dotnet run -c Release --project source/PictTag.PgfCodec.Performance
```

Use BenchmarkDotNet filters to run one scenario, for example:

```powershell
dotnet run -c Release --project source/PictTag.PgfCodec.Performance -- --filter '*DecodeFifty*'
```

For CPU call trees in Visual Studio (with allocation totals from `MemoryDiagnoser`), select the opt-in DiagnosticsHub
variant. It requires Visual Studio 17.9+ (or matching Remote Tools):

```powershell
dotnet run -c Release --project source/PictTag.PgfCodec.Performance -- --filter '*Profiled*' --job Short
```

The normal `SessionWorkloadBenchmarks` type deliberately has no profiler diagnoser, so ordinary
timing runs are not distorted by profiler collection.
