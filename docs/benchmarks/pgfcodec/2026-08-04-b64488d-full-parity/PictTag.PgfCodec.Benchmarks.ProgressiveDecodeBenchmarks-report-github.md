```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8875)
Unknown processor
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                            | Size | Quality | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated | Alloc Ratio |
|---------------------------------- |----- |-------- |-----------:|-----------:|----------:|------:|--------:|---------:|---------:|---------:|----------:|------------:|
| **NativeProgressiveDecodeAllLevels**  | **256**  | **0**       | **1,249.5 μs** |   **213.3 μs** |  **11.69 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 256  | 0       | 2,071.5 μs |   181.5 μs |   9.95 μs |  1.66 |    0.02 | 332.0313 | 332.0313 | 332.0313 | 2580401 B |          NA |
|                                   |      |         |            |            |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**  | **256**  | **8**       |   **638.5 μs** |   **191.4 μs** |  **10.49 μs** |  **1.00** |    **0.02** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 256  | 8       |   989.5 μs |   165.0 μs |   9.04 μs |  1.55 |    0.03 |  82.0313 |  82.0313 |  82.0313 | 1216265 B |          NA |
|                                   |      |         |            |            |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**  | **512**  | **0**       | **5,570.2 μs** | **2,951.1 μs** | **161.76 μs** |  **1.00** |    **0.04** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 512  | 0       | 8,384.5 μs | 1,402.6 μs |  76.88 μs |  1.51 |    0.04 | 812.5000 | 796.8750 | 734.3750 | 9922575 B |          NA |
|                                   |      |         |            |            |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**  | **512**  | **8**       | **2,647.4 μs** |   **818.3 μs** |  **44.85 μs** |  **1.00** |    **0.02** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 512  | 8       | 3,968.2 μs | 2,482.2 μs | 136.06 μs |  1.50 |    0.05 | 886.7188 | 863.2813 | 796.8750 | 4429871 B |          NA |
