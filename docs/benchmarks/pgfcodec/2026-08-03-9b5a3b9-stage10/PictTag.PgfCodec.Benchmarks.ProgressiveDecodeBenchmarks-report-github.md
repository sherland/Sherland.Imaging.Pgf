```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8875)
Unknown processor
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                            | Size | Quality | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated | Alloc Ratio |
|---------------------------------- |----- |-------- |------------:|-----------:|----------:|------:|--------:|---------:|---------:|---------:|----------:|------------:|
| **NativeProgressiveDecodeAllLevels**  | **256**  | **0**       |  **1,969.2 μs** | **1,152.5 μs** |  **63.17 μs** |  **1.00** |    **0.04** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 256  | 0       |  3,344.9 μs | 1,622.0 μs |  88.91 μs |  1.70 |    0.06 | 164.0625 | 164.0625 | 164.0625 | 1333534 B |          NA |
|                                   |      |         |             |            |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**  | **256**  | **8**       |    **933.4 μs** |   **728.4 μs** |  **39.92 μs** |  **1.00** |    **0.05** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 256  | 8       |  1,460.0 μs | 1,664.4 μs |  91.23 μs |  1.57 |    0.10 |  41.0156 |  41.0156 |  41.0156 |  651468 B |          NA |
|                                   |      |         |             |            |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**  | **512**  | **0**       |  **8,459.4 μs** | **2,511.6 μs** | **137.67 μs** |  **1.00** |    **0.02** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 512  | 0       | 13,086.7 μs | 3,861.5 μs | 211.66 μs |  1.55 |    0.03 | 781.2500 | 765.6250 | 734.3750 | 5005413 B |          NA |
|                                   |      |         |             |            |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**  | **512**  | **8**       |  **3,970.8 μs** | **2,570.4 μs** | **140.89 μs** |  **1.00** |    **0.04** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 512  | 8       |  5,779.7 μs | 3,939.8 μs | 215.95 μs |  1.46 |    0.07 | 468.7500 | 445.3125 | 421.8750 | 2258844 B |          NA |
