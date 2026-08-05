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
| **NativeProgressiveDecodeAllLevels**      | **256**  | **0**       | **Gradient**     | **1,339.7 μs** | **4,224.92 μs** | **231.58 μs** |  **1.02** |    **0.21** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 0       | Gradient     | 1,822.7 μs |   162.20 μs |   8.89 μs |  1.39 |    0.19 | 332.0313 | 332.0313 | 332.0313 | 2580753 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 0       | Gradient     | 1,610.8 μs |   245.46 μs |  13.45 μs |  1.22 |    0.17 |        - |        - |        - |    6913 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **0**       | **Checkerboard** | **1,633.1 μs** |   **544.29 μs** |  **29.83 μs** |  **1.00** |    **0.02** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 0       | Checkerboard | 2,344.7 μs |    69.86 μs |   3.83 μs |  1.44 |    0.02 | 332.0313 | 332.0313 | 332.0313 | 2580753 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 0       | Checkerboard | 2,068.6 μs | 1,294.28 μs |  70.94 μs |  1.27 |    0.04 |        - |        - |        - |    6914 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **8**       | **Gradient**     |   **627.8 μs** |   **142.67 μs** |   **7.82 μs** |  **1.00** |    **0.02** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 8       | Gradient     |   882.9 μs |   154.43 μs |   8.46 μs |  1.41 |    0.02 |  83.0078 |  83.0078 |  83.0078 | 1216616 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 8       | Gradient     |   758.9 μs |   197.46 μs |  10.82 μs |  1.21 |    0.02 |        - |        - |        - |    6912 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **8**       | **Checkerboard** |   **754.5 μs** |   **100.62 μs** |   **5.52 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 8       | Checkerboard | 1,075.9 μs |   306.89 μs |  16.82 μs |  1.43 |    0.02 |  82.0313 |  82.0313 |  82.0313 | 1216617 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 8       | Checkerboard |   958.0 μs |   298.96 μs |  16.39 μs |  1.27 |    0.02 |        - |        - |        - |    6913 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **0**       | **Gradient**     | **5,461.6 μs** | **3,467.63 μs** | **190.07 μs** |  **1.00** |    **0.04** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 0       | Gradient     | 7,226.1 μs | 1,073.83 μs |  58.86 μs |  1.32 |    0.04 | 828.1250 | 812.5000 | 742.1875 | 9922967 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 0       | Gradient     | 6,420.9 μs |   958.20 μs |  52.52 μs |  1.18 |    0.04 |        - |        - |        - |    8454 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **0**       | **Checkerboard** | **7,379.1 μs** | **4,506.65 μs** | **247.02 μs** |  **1.00** |    **0.04** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 0       | Checkerboard | 9,121.6 μs | 1,908.35 μs | 104.60 μs |  1.24 |    0.04 | 796.8750 | 781.2500 | 718.7500 | 9922957 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 0       | Checkerboard | 8,161.1 μs | 3,953.31 μs | 216.69 μs |  1.11 |    0.04 |        - |        - |        - |    8460 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **8**       | **Gradient**     | **2,608.4 μs** |   **439.09 μs** |  **24.07 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 8       | Gradient     | 3,444.3 μs |   272.10 μs |  14.91 μs |  1.32 |    0.01 | 886.7188 | 867.1875 | 796.8750 | 4430262 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 8       | Gradient     | 2,932.8 μs |   101.26 μs |   5.55 μs |  1.12 |    0.01 |        - |        - |        - |    8450 B |          NA |
|                                       |      |         |              |            |             |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **8**       | **Checkerboard** | **3,123.7 μs** |   **717.47 μs** |  **39.33 μs** |  **1.00** |    **0.02** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 8       | Checkerboard | 4,086.9 μs |   149.34 μs |   8.19 μs |  1.31 |    0.01 | 882.8125 | 859.3750 | 796.8750 | 4430266 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 8       | Checkerboard | 3,602.8 μs | 1,163.74 μs |  63.79 μs |  1.15 |    0.02 |        - |        - |        - |    8451 B |          NA |
