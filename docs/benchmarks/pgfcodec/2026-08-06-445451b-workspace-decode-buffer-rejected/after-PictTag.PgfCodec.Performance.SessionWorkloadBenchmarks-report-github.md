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
| **DecodeFiftyImagesInNewSession**            | **False**              | **122.8 ms** | **1.25 ms** | **1.71 ms** | **330.68 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | False              | 135.1 ms | 1.89 ms | 2.58 ms | 333.89 KB |
| **DecodeFiftyImagesInNewSession**            | **True**               | **140.6 ms** | **1.63 ms** | **2.39 ms** | **330.68 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | True               | 152.0 ms | 2.99 ms | 4.20 ms | 333.89 KB |
