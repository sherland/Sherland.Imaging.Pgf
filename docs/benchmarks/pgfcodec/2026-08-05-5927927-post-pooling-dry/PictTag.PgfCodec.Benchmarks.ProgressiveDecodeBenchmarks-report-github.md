```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8875)
Unknown processor
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2
  Dry    : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                            | Size | Quality | Fixture      | Mean      | Error | Ratio | Gen0      | Gen1      | Gen2      | Allocated | Alloc Ratio |
|---------------------------------- |----- |-------- |------------- |----------:|------:|------:|----------:|----------:|----------:|----------:|------------:|
| **NativeProgressiveDecodeAllLevels**  | **256**  | **0**       | **Gradient**     |  **3.010 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 256  | 0       | Gradient     | 26.895 ms |    NA |  8.94 |         - |         - |         - | 2581328 B |          NA |
|                                   |      |         |              |           |       |       |           |           |           |           |             |
| **NativeProgressiveDecodeAllLevels**  | **256**  | **0**       | **Checkerboard** |  **3.619 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 256  | 0       | Checkerboard | 27.914 ms |    NA |  7.71 |         - |         - |         - | 2581328 B |          NA |
|                                   |      |         |              |           |       |       |           |           |           |           |             |
| **NativeProgressiveDecodeAllLevels**  | **256**  | **8**       | **Gradient**     |  **2.113 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 256  | 8       | Gradient     | 21.651 ms |    NA | 10.25 |         - |         - |         - | 1217360 B |          NA |
|                                   |      |         |              |           |       |       |           |           |           |           |             |
| **NativeProgressiveDecodeAllLevels**  | **256**  | **8**       | **Checkerboard** |  **2.224 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 256  | 8       | Checkerboard | 22.787 ms |    NA | 10.25 |         - |         - |         - | 1217072 B |          NA |
|                                   |      |         |              |           |       |       |           |           |           |           |             |
| **NativeProgressiveDecodeAllLevels**  | **512**  | **0**       | **Gradient**     |  **9.776 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 512  | 0       | Gradient     | 37.556 ms |    NA |  3.84 | 1000.0000 | 1000.0000 | 1000.0000 | 9923640 B |          NA |
|                                   |      |         |              |           |       |       |           |           |           |           |             |
| **NativeProgressiveDecodeAllLevels**  | **512**  | **0**       | **Checkerboard** |  **9.613 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 512  | 0       | Checkerboard | 40.957 ms |    NA |  4.26 | 1000.0000 | 1000.0000 | 1000.0000 | 9923928 B |          NA |
|                                   |      |         |              |           |       |       |           |           |           |           |             |
| **NativeProgressiveDecodeAllLevels**  | **512**  | **8**       | **Gradient**     |  **4.695 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 512  | 8       | Gradient     | 27.640 ms |    NA |  5.89 |         - |         - |         - | 4430256 B |          NA |
|                                   |      |         |              |           |       |       |           |           |           |           |             |
| **NativeProgressiveDecodeAllLevels**  | **512**  | **8**       | **Checkerboard** |  **5.782 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels | 512  | 8       | Checkerboard | 30.556 ms |    NA |  5.28 |         - |         - |         - | 4430544 B |          NA |
