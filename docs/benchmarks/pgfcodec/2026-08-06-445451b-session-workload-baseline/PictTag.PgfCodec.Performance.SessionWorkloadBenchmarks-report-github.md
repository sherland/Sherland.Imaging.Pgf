```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8875/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                                   | ForceScalarVectors | Mean       | Error      | StdDev    | Gen0      | Allocated   |
|----------------------------------------- |------------------- |-----------:|-----------:|----------:|----------:|------------:|
| **DecodeFiftyImagesInNewSession**            | **False**              | **129.268 ms** |  **93.367 ms** | **5.1178 ms** |         **-** |   **330.18 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | False              | 137.917 ms |   9.278 ms | 0.5086 ms |         - |    333.3 KB |
| EncodeOneImageInNewSession               | False              |   3.263 ms |   1.769 ms | 0.0970 ms |         - |  2282.77 KB |
| EncodeTwentyImagesInNewSession           | False              |  72.851 ms |  23.280 ms | 1.2761 ms | 1000.0000 | 34010.35 KB |
| EncodeTwentyImagesWithOwnedResults       | False              |  75.116 ms | 152.417 ms | 8.3545 ms | 1000.0000 |  34618.1 KB |
| **DecodeFiftyImagesInNewSession**            | **True**               | **139.019 ms** |  **25.655 ms** | **1.4063 ms** |         **-** |   **330.18 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | True               | 151.638 ms |  32.412 ms | 1.7766 ms |         - |    333.3 KB |
| EncodeOneImageInNewSession               | True               |   3.391 ms |   1.677 ms | 0.0919 ms |         - |  2282.77 KB |
| EncodeTwentyImagesInNewSession           | True               |  73.774 ms |  24.887 ms | 1.3641 ms | 1000.0000 | 34010.35 KB |
| EncodeTwentyImagesWithOwnedResults       | True               |  75.931 ms |  53.751 ms | 2.9463 ms | 1000.0000 |  34618.1 KB |
