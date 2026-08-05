```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8875)
Unknown processor
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                | Size | Quality | ForceScalarVectors | Fixture      | Mean        | Error        | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated | Alloc Ratio |
|-------------------------------------- |----- |-------- |------------------- |------------- |------------:|-------------:|----------:|------:|--------:|---------:|---------:|---------:|----------:|------------:|
| **NativeProgressiveDecodeAllLevels**      | **256**  | **0**       | **False**              | **Gradient**     |  **1,318.9 μs** |  **2,938.90 μs** | **161.09 μs** |  **1.01** |    **0.15** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 0       | False              | Gradient     |  1,849.3 μs |    518.43 μs |  28.42 μs |  1.42 |    0.14 | 332.0313 | 332.0313 | 332.0313 | 2580752 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 0       | False              | Gradient     |  1,622.0 μs |    161.19 μs |   8.84 μs |  1.24 |    0.13 |        - |        - |        - |    6913 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **0**       | **False**              | **Checkerboard** |  **1,699.5 μs** |    **375.28 μs** |  **20.57 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 0       | False              | Checkerboard |  2,280.3 μs |    672.58 μs |  36.87 μs |  1.34 |    0.02 | 332.0313 | 332.0313 | 332.0313 | 2580754 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 0       | False              | Checkerboard |  2,058.4 μs |    656.11 μs |  35.96 μs |  1.21 |    0.02 |        - |        - |        - |    6914 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **0**       | **True**               | **Gradient**     |  **1,228.5 μs** |    **264.28 μs** |  **14.49 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 0       | True               | Gradient     |  2,105.7 μs |    563.32 μs |  30.88 μs |  1.71 |    0.03 | 332.0313 | 332.0313 | 332.0313 | 2580754 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 0       | True               | Gradient     |  1,871.3 μs |    433.33 μs |  23.75 μs |  1.52 |    0.02 |        - |        - |        - |    6913 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **0**       | **True**               | **Checkerboard** |  **1,688.5 μs** |    **473.64 μs** |  **25.96 μs** |  **1.00** |    **0.02** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 0       | True               | Checkerboard |  2,493.6 μs |    331.25 μs |  18.16 μs |  1.48 |    0.02 | 332.0313 | 332.0313 | 332.0313 | 2580754 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 0       | True               | Checkerboard |  2,263.1 μs |    385.12 μs |  21.11 μs |  1.34 |    0.02 |        - |        - |        - |    6915 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **8**       | **False**              | **Gradient**     |    **624.7 μs** |    **135.75 μs** |   **7.44 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |      **12 B** |        **1.00** |
| ManagedProgressiveDecodeAllLevels     | 256  | 8       | False              | Gradient     |    844.4 μs |     34.64 μs |   1.90 μs |  1.35 |    0.01 |  83.0078 |  83.0078 |  83.0078 | 1216616 B |  101,384.67 |
| ManagedProgressiveDecodeWithWorkspace | 256  | 8       | False              | Gradient     |    796.7 μs |    295.84 μs |  16.22 μs |  1.28 |    0.03 |        - |        - |        - |    6912 B |      576.00 |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **8**       | **False**              | **Checkerboard** |    **758.5 μs** |    **453.24 μs** |  **24.84 μs** |  **1.00** |    **0.04** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 8       | False              | Checkerboard |  1,061.3 μs |    706.86 μs |  38.75 μs |  1.40 |    0.06 |  82.0313 |  82.0313 |  82.0313 | 1216617 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 8       | False              | Checkerboard |    958.9 μs |    128.67 μs |   7.05 μs |  1.27 |    0.04 |        - |        - |        - |    6912 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **8**       | **True**               | **Gradient**     |    **635.3 μs** |    **243.66 μs** |  **13.36 μs** |  **1.00** |    **0.03** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 8       | True               | Gradient     |    975.6 μs |    241.99 μs |  13.26 μs |  1.54 |    0.03 |  83.0078 |  83.0078 |  83.0078 | 1216616 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 8       | True               | Gradient     |    879.5 μs |    124.82 μs |   6.84 μs |  1.38 |    0.03 |        - |        - |        - |    6913 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **256**  | **8**       | **True**               | **Checkerboard** |    **748.9 μs** |    **142.86 μs** |   **7.83 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 256  | 8       | True               | Checkerboard |  1,140.8 μs |     80.41 μs |   4.41 μs |  1.52 |    0.01 |  82.0313 |  82.0313 |  82.0313 | 1216617 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 256  | 8       | True               | Checkerboard |  1,090.5 μs |    206.93 μs |  11.34 μs |  1.46 |    0.02 |        - |        - |        - |    6913 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **0**       | **False**              | **Gradient**     |  **5,479.7 μs** |  **1,011.30 μs** |  **55.43 μs** |  **1.00** |    **0.01** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 0       | False              | Gradient     |  7,540.1 μs |    256.54 μs |  14.06 μs |  1.38 |    0.01 | 812.5000 | 796.8750 | 734.3750 | 9922967 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 0       | False              | Gradient     |  6,426.1 μs |  1,423.85 μs |  78.05 μs |  1.17 |    0.02 |        - |        - |        - |    8454 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **0**       | **False**              | **Checkerboard** |  **7,347.7 μs** |  **3,688.26 μs** | **202.17 μs** |  **1.00** |    **0.03** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 0       | False              | Checkerboard |  8,990.5 μs |  1,774.74 μs |  97.28 μs |  1.22 |    0.03 | 796.8750 | 781.2500 | 718.7500 | 9922957 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 0       | False              | Checkerboard |  8,021.5 μs |  1,362.61 μs |  74.69 μs |  1.09 |    0.03 |        - |        - |        - |    8460 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **0**       | **True**               | **Gradient**     |  **5,607.3 μs** |  **2,466.81 μs** | **135.21 μs** |  **1.00** |    **0.03** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 0       | True               | Gradient     |  8,406.7 μs |  1,322.52 μs |  72.49 μs |  1.50 |    0.03 | 812.5000 | 796.8750 | 734.3750 | 9922967 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 0       | True               | Gradient     |  7,546.1 μs |    992.58 μs |  54.41 μs |  1.35 |    0.03 |        - |        - |        - |    8454 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **0**       | **True**               | **Checkerboard** |  **7,505.7 μs** | **11,329.23 μs** | **620.99 μs** |  **1.00** |    **0.10** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 0       | True               | Checkerboard | 10,147.8 μs |  1,782.62 μs |  97.71 μs |  1.36 |    0.09 | 796.8750 | 781.2500 | 718.7500 | 9922957 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 0       | True               | Checkerboard |  9,588.0 μs |  6,106.11 μs | 334.70 μs |  1.28 |    0.10 |        - |        - |        - |    8460 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **8**       | **False**              | **Gradient**     |  **2,686.4 μs** |    **999.74 μs** |  **54.80 μs** |  **1.00** |    **0.03** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 8       | False              | Gradient     |  3,403.3 μs |     96.76 μs |   5.30 μs |  1.27 |    0.02 | 886.7188 | 867.1875 | 796.8750 | 4430263 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 8       | False              | Gradient     |  3,014.2 μs |    275.24 μs |  15.09 μs |  1.12 |    0.02 |        - |        - |        - |    8451 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **8**       | **False**              | **Checkerboard** |  **3,126.6 μs** |  **1,019.96 μs** |  **55.91 μs** |  **1.00** |    **0.02** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 8       | False              | Checkerboard |  4,081.4 μs |    460.51 μs |  25.24 μs |  1.31 |    0.02 | 882.8125 | 859.3750 | 796.8750 | 4430266 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 8       | False              | Checkerboard |  3,594.5 μs |  1,081.88 μs |  59.30 μs |  1.15 |    0.02 |        - |        - |        - |    8451 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **8**       | **True**               | **Gradient**     |  **2,735.6 μs** |  **1,837.65 μs** | **100.73 μs** |  **1.00** |    **0.04** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 8       | True               | Gradient     |  3,859.5 μs |    437.38 μs |  23.97 μs |  1.41 |    0.04 | 886.7188 | 867.1875 | 796.8750 | 4430262 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 8       | True               | Gradient     |  3,427.5 μs |    597.45 μs |  32.75 μs |  1.25 |    0.04 |        - |        - |        - |    8451 B |          NA |
|                                       |      |         |                    |              |             |              |           |       |         |          |          |          |           |             |
| **NativeProgressiveDecodeAllLevels**      | **512**  | **8**       | **True**               | **Checkerboard** |  **3,131.7 μs** |    **162.40 μs** |   **8.90 μs** |  **1.00** |    **0.00** |        **-** |        **-** |        **-** |         **-** |          **NA** |
| ManagedProgressiveDecodeAllLevels     | 512  | 8       | True               | Checkerboard |  4,660.0 μs |    136.69 μs |   7.49 μs |  1.49 |    0.00 | 882.8125 | 859.3750 | 796.8750 | 4430266 B |          NA |
| ManagedProgressiveDecodeWithWorkspace | 512  | 8       | True               | Checkerboard |  4,019.3 μs |    529.87 μs |  29.04 μs |  1.28 |    0.01 |        - |        - |        - |    8452 B |          NA |
