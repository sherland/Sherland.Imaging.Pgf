```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8875/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                                   | ForceScalarVectors | Mean       | Error       | StdDev     | Gen0      | Allocated   |
|----------------------------------------- |------------------- |-----------:|------------:|-----------:|----------:|------------:|
| **DecodeFiftyImagesInNewSession**            | **False**              | **126.194 ms** |  **36.4033 ms** |  **1.9954 ms** |         **-** |   **330.18 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | False              | 140.172 ms |  13.5532 ms |  0.7429 ms |         - |    333.3 KB |
| EncodeOneImageInNewSession               | False              |   3.126 ms |   6.0029 ms |  0.3290 ms |         - |  2282.77 KB |
| EncodeTwentyImagesInNewSession           | False              |  69.597 ms |  15.0405 ms |  0.8244 ms | 1000.0000 | 34010.35 KB |
| EncodeTwentyImagesWithOwnedResults       | False              |  68.803 ms |  21.2953 ms |  1.1673 ms | 1000.0000 |  34618.1 KB |
| **DecodeFiftyImagesInNewSession**            | **True**               | **143.100 ms** |  **20.2505 ms** |  **1.1100 ms** |         **-** |   **330.18 KB** |
| ProgressiveDecodeFiftyImagesInNewSession | True               | 152.836 ms |  31.0801 ms |  1.7036 ms |         - |    333.3 KB |
| EncodeOneImageInNewSession               | True               |   3.757 ms |   0.9345 ms |  0.0512 ms |         - |  2282.77 KB |
| EncodeTwentyImagesInNewSession           | True               | 102.626 ms | 455.3158 ms | 24.9574 ms | 1000.0000 | 34010.35 KB |
| EncodeTwentyImagesWithOwnedResults       | True               |  95.580 ms | 491.2447 ms | 26.9268 ms | 1000.0000 |  34618.1 KB |
