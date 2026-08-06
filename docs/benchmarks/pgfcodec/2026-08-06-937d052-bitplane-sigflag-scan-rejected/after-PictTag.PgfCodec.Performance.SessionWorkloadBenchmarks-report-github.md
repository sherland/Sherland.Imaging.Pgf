```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8875/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]    : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  MediumRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=MediumRun  InvocationCount=1  IterationCount=15  
LaunchCount=2  UnrollFactor=1  WarmupCount=10  

```
| Method                                   | ForceScalarVectors | Mean     | Error   | StdDev  | Allocated |
|----------------------------------------- |------------------- |---------:|--------:|--------:|----------:|
| **DecodeFiftyImagesInNewSession**            | **False**              | **121.7 ms** | **0.84 ms** | **1.20 ms** | **330.18 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | False              | 133.8 ms | 1.85 ms | 2.77 ms |  333.3 KB |
| **DecodeFiftyImagesInNewSession**            | **True**               | **137.2 ms** | **0.82 ms** | **1.18 ms** | **330.18 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | True               | 148.6 ms | 1.12 ms | 1.64 ms |  333.3 KB |
