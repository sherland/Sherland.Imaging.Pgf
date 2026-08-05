```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8875)
Unknown processor
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2
  Dry    : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method        | Size | Quality | Fixture      | Mean      | Error | Ratio | Gen0      | Gen1      | Gen2      | Allocated  | Alloc Ratio |
|-------------- |----- |-------- |------------- |----------:|------:|------:|----------:|----------:|----------:|-----------:|------------:|
| **NativeEncode**  | **128**  | **0**       | **Gradient**     |  **2.504 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 128  | 0       | Gradient     | 23.827 ms |    NA |  9.52 |         - |         - |         - |   772144 B |          NA |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **128**  | **0**       | **Checkerboard** |  **2.718 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |    **12920 B** |        **1.00** |
| ManagedEncode | 128  | 0       | Checkerboard | 23.329 ms |    NA |  8.58 |         - |         - |         - |   801112 B |       62.01 |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **128**  | **8**       | **Gradient**     |  **2.067 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 128  | 8       | Gradient     | 24.499 ms |    NA | 11.85 |         - |         - |         - |   570056 B |          NA |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **128**  | **8**       | **Checkerboard** |  **2.262 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 128  | 8       | Checkerboard | 24.145 ms |    NA | 10.67 |         - |         - |         - |   585352 B |          NA |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **128**  | **15**      | **Gradient**     |  **2.134 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 128  | 15      | Gradient     | 23.916 ms |    NA | 11.21 |         - |         - |         - |   565832 B |          NA |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **128**  | **15**      | **Checkerboard** |  **2.064 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 128  | 15      | Checkerboard | 24.087 ms |    NA | 11.67 |         - |         - |         - |   565832 B |          NA |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **256**  | **0**       | **Gradient**     |  **3.897 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |    **16080 B** |        **1.00** |
| ManagedEncode | 256  | 0       | Gradient     | 26.476 ms |    NA |  6.79 |         - |         - |         - |  2635496 B |      163.90 |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **256**  | **0**       | **Checkerboard** |  **4.584 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |    **44408 B** |        **1.00** |
| ManagedEncode | 256  | 0       | Checkerboard | 30.950 ms |    NA |  6.75 |         - |         - |         - |  2749464 B |       61.91 |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **256**  | **8**       | **Gradient**     |  **2.993 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 256  | 8       | Gradient     | 25.529 ms |    NA |  8.53 |         - |         - |         - |  1818168 B |          NA |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **256**  | **8**       | **Checkerboard** |  **3.318 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |    **17256 B** |        **1.00** |
| ManagedEncode | 256  | 8       | Checkerboard | 26.163 ms |    NA |  7.89 |         - |         - |         - |  1868960 B |      108.31 |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **256**  | **15**      | **Gradient**     |  **2.953 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 256  | 15      | Gradient     | 24.500 ms |    NA |  8.30 |         - |         - |         - |  1813288 B |          NA |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **256**  | **15**      | **Checkerboard** |  **3.212 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 256  | 15      | Checkerboard | 25.312 ms |    NA |  7.88 |         - |         - |         - |  1813288 B |          NA |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **512**  | **0**       | **Gradient**     |  **9.413 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |    **61376 B** |        **1.00** |
| ManagedEncode | 512  | 0       | Gradient     | 39.739 ms |    NA |  4.22 | 1000.0000 | 1000.0000 | 1000.0000 | 10142304 B |      165.25 |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **512**  | **0**       | **Checkerboard** | **12.454 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |   **172824 B** |        **1.00** |
| ManagedEncode | 512  | 0       | Checkerboard | 51.870 ms |    NA |  4.16 | 1000.0000 | 1000.0000 | 1000.0000 | 10541592 B |       61.00 |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **512**  | **8**       | **Gradient**     |  **6.346 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 512  | 8       | Gradient     | 31.038 ms |    NA |  4.89 | 1000.0000 | 1000.0000 | 1000.0000 |  6802184 B |          NA |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **512**  | **8**       | **Checkerboard** |  **7.197 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |    **66024 B** |        **1.00** |
| ManagedEncode | 512  | 8       | Checkerboard | 36.129 ms |    NA |  5.02 | 1000.0000 | 1000.0000 | 1000.0000 |  7027440 B |      106.44 |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **512**  | **15**      | **Gradient**     |  **6.522 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 512  | 15      | Gradient     | 31.334 ms |    NA |  4.80 | 1000.0000 | 1000.0000 | 1000.0000 |  6796928 B |          NA |
|               |      |         |              |           |       |       |           |           |           |            |             |
| **NativeEncode**  | **512**  | **15**      | **Checkerboard** |  **6.089 ms** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |          **-** |          **NA** |
| ManagedEncode | 512  | 15      | Checkerboard | 32.273 ms |    NA |  5.30 | 1000.0000 | 1000.0000 | 1000.0000 |  6796928 B |          NA |
