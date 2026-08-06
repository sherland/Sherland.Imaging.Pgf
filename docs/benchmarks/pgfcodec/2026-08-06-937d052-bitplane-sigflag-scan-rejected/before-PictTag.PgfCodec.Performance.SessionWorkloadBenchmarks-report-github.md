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
| **DecodeFiftyImagesInNewSession**            | **False**              | **121.8 ms** | **1.06 ms** | **1.55 ms** | **330.18 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | False              | 132.9 ms | 0.54 ms | 0.75 ms |  333.3 KB |
| **DecodeFiftyImagesInNewSession**            | **True**               | **136.7 ms** | **0.94 ms** | **1.31 ms** | **330.18 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | True               | 150.3 ms | 0.82 ms | 1.18 ms |  333.3 KB |
