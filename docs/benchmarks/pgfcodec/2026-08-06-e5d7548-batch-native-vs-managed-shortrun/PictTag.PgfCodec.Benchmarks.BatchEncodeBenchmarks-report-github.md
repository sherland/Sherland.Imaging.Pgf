```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8875/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                      | BatchSize | Mean       | Error       | StdDev     | Ratio | RatioSD | Gen0      | Gen1      | Gen2      | Allocated    | Alloc Ratio |
|---------------------------- |---------- |-----------:|------------:|-----------:|------:|--------:|----------:|----------:|----------:|-------------:|------------:|
| **NativeEncodeBatch**           | **1**         |   **1.285 ms** |   **0.6915 ms** |  **0.0379 ms** |  **1.00** |    **0.04** |         **-** |         **-** |         **-** |     **22.43 KB** |        **1.00** |
| ManagedEncodeBatch          | 1         |   1.945 ms |   0.8092 ms |  0.0444 ms |  1.51 |    0.05 |  181.6406 |  181.6406 |  181.6406 |   1487.79 KB |       66.33 |
| ManagedWorkspaceEncodeBatch | 1         |   2.004 ms |   1.3302 ms |  0.0729 ms |  1.56 |    0.06 |   44.9219 |   33.2031 |         - |    752.56 KB |       33.55 |
|                             |           |            |             |            |       |         |           |           |           |              |             |
| **NativeEncodeBatch**           | **10**        |  **20.658 ms** |  **14.3930 ms** |  **0.7889 ms** |  **1.00** |    **0.05** |         **-** |         **-** |         **-** |    **262.01 KB** |        **1.00** |
| ManagedEncodeBatch          | 10        |  25.296 ms |   5.7973 ms |  0.3178 ms |  1.23 |    0.04 | 2125.0000 | 1937.5000 | 1500.0000 |  29915.77 KB |      114.18 |
| ManagedWorkspaceEncodeBatch | 10        |  26.552 ms |  20.8334 ms |  1.1419 ms |  1.29 |    0.06 |  625.0000 |  468.7500 |  125.0000 |  12575.96 KB |       48.00 |
|                             |           |            |             |            |       |         |           |           |           |              |             |
| **NativeEncodeBatch**           | **50**        | **116.205 ms** |  **32.6245 ms** |  **1.7883 ms** |  **1.00** |    **0.02** |         **-** |         **-** |         **-** |   **1246.33 KB** |        **1.00** |
| ManagedEncodeBatch          | 50        | 142.762 ms | 203.0188 ms | 11.1281 ms |  1.23 |    0.08 | 4000.0000 | 3250.0000 | 1000.0000 | 181120.36 KB |      145.32 |
| ManagedWorkspaceEncodeBatch | 50        | 125.768 ms |   9.9497 ms |  0.5454 ms |  1.08 |    0.01 | 2500.0000 | 1000.0000 |         - |  78022.88 KB |       62.60 |
