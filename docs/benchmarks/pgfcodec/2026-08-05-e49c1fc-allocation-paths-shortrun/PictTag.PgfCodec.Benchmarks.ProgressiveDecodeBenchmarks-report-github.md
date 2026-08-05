```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8875)
Unknown processor
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                | Size | Quality | Fixture      | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated | Alloc Ratio |
|-------------------------------------- |----- |-------- |------------- |-----------:|------------:|----------:|------:|--------:|---------:|---------:|---------:|----------:|------------:|
| **NativeProgressiveDecodeAllLevels**      | **256**  | **0**       | **Gradient**     | **1,292.6 μs** |   **508.69 μs** |  **27.88 μs** |  **1.00** |    **0.03** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 0       | Gradient     | 2,119.3 μs |   658.79 μs |  36.11 μs |  1.64 |    0.04 | 332.0313 | 332.0313 | 332.0313 | 2580754 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 0       | Gradient     | 2,008.9 μs | 1,082.75 μs |  59.35 μs |  1.55 |    0.05 |        - |        - |        - |    6913 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **0**       | **Checkerboard** | **1,662.7 μs** |   **460.58 μs** |  **25.25 μs** |  **1.00** |    **0.02** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 0       | Checkerboard | 2,695.9 μs | 1,062.01 μs |  58.21 μs |  1.62 |    0.04 | 332.0313 | 332.0313 | 332.0313 | 2580754 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 0       | Checkerboard | 2,484.1 μs |   336.03 μs |  18.42 μs |  1.49 |    0.02 |        - |        - |        - |    6915 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **8**       | **Gradient**     |   **653.2 μs** |   **125.03 μs** |   **6.85 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 8       | Gradient     | 1,015.8 μs |   442.83 μs |  24.27 μs |  1.56 |    0.04 |  83.0078 |  83.0078 |  83.0078 | 1216616 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 8       | Gradient     |   941.6 μs |   162.04 μs |   8.88 μs |  1.44 |    0.02 |        - |        - |        - |    6913 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **8**       | **Checkerboard** |   **775.3 μs** |   **115.10 μs** |   **6.31 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 8       | Checkerboard | 1,178.8 μs |    52.15 μs |   2.86 μs |  1.52 |    0.01 |  82.0313 |  82.0313 |  82.0313 | 1216617 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 8       | Checkerboard | 1,136.3 μs |   775.60 μs |  42.51 μs |  1.47 |    0.05 |        - |        - |        - |    6913 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **0**       | **Gradient**     | **5,823.6 μs** | **1,104.97 μs** |  **60.57 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 0       | Gradient     | 8,466.7 μs | 2,332.01 μs | 127.83 μs |  1.45 |    0.02 | 812.5000 | 796.8750 | 734.3750 | 9922967 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 0       | Gradient     | 7,883.0 μs | 1,177.83 μs |  64.56 μs |  1.35 |    0.02 |        - |        - |        - |    8454 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **0**       | **Checkerboard** | **7,605.2 μs** | **3,936.79 μs** | **215.79 μs** |  **1.00** |    **0.03** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 0       | Checkerboard | 9,903.4 μs | 1,271.37 μs |  69.69 μs |  1.30 |    0.03 | 796.8750 | 781.2500 | 718.7500 | 9922957 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 0       | Checkerboard | 9,598.9 μs | 1,954.56 μs | 107.14 μs |  1.26 |    0.03 |        - |        - |        - |    8449 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **8**       | **Gradient**     | **3,183.7 μs** | **3,400.64 μs** | **186.40 μs** |  **1.00** |    **0.07** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 8       | Gradient     | 3,899.1 μs | 1,288.44 μs |  70.62 μs |  1.23 |    0.07 | 886.7188 | 867.1875 | 796.8750 | 4430263 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 8       | Gradient     | 3,702.3 μs |   365.97 μs |  20.06 μs |  1.17 |    0.06 |        - |        - |        - |    8451 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **8**       | **Checkerboard** | **3,235.7 μs** |   **381.57 μs** |  **20.92 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 8       | Checkerboard | 4,520.1 μs |    41.27 μs |   2.26 μs |  1.40 |    0.01 | 882.8125 | 859.3750 | 796.8750 | 4430266 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 8       | Checkerboard | 4,329.1 μs |    97.26 μs |   5.33 μs |  1.34 |    0.01 |        - |        - |        - |    8454 B |          NA |
