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
| **DecodeFiftyImagesInNewSession**            | **False**              | **143.7 ms** | **0.51 ms** | **0.67 ms** | **330.18 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | False              | 156.0 ms | 1.14 ms | 1.67 ms |  333.3 KB |
| **DecodeFiftyImagesInNewSession**            | **True**               | **138.7 ms** | **1.14 ms** | **1.67 ms** | **330.18 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | True               | 150.2 ms | 1.01 ms | 1.45 ms |  333.3 KB |
