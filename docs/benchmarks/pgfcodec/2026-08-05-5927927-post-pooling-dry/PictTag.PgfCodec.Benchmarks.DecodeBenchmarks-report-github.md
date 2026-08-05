```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8875)
Unknown processor
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2
  Dry    : .NET 10.0.10 (10.0.1026.32716), X64 RyuJIT AVX2

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method        | Size | Quality | Fixture      | Mean        | Error | Ratio | Gen0      | Gen1      | Gen2      | Allocated | Alloc Ratio |
|-------------- |----- |-------- |------------- |------------:|------:|------:|----------:|----------:|----------:|----------:|------------:|
| **NativeDecode**  | **128**  | **0**       | **Gradient**     |  **1,309.7 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 128  | 0       | Gradient     | 20,721.3 μs |    NA | 15.82 |         - |         - |         - |  744328 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **128**  | **0**       | **Checkerboard** |  **1,354.0 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 128  | 0       | Checkerboard | 21,091.3 μs |    NA | 15.58 |         - |         - |         - |  744328 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **128**  | **8**       | **Gradient**     |    **988.8 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 128  | 8       | Gradient     | 20,789.9 μs |    NA | 21.03 |         - |         - |         - |  412264 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **128**  | **8**       | **Checkerboard** |    **975.9 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 128  | 8       | Checkerboard | 20,278.0 μs |    NA | 20.78 |         - |         - |         - |  412552 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **128**  | **15**      | **Gradient**     |    **994.3 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 128  | 15      | Gradient     | 20,234.1 μs |    NA | 20.35 |         - |         - |         - |  412264 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **128**  | **15**      | **Checkerboard** |  **1,013.6 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 128  | 15      | Checkerboard | 20,285.4 μs |    NA | 20.01 |         - |         - |         - |  412552 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **256**  | **0**       | **Gradient**     |  **2,472.2 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 256  | 0       | Gradient     | 29,060.4 μs |    NA | 11.75 |         - |         - |         - | 2581264 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **256**  | **0**       | **Checkerboard** |  **2,834.5 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 256  | 0       | Checkerboard | 27,720.0 μs |    NA |  9.78 |         - |         - |         - | 2581264 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **256**  | **8**       | **Gradient**     |  **1,609.2 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 256  | 8       | Gradient     | 21,503.6 μs |    NA | 13.36 |         - |         - |         - | 1217296 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **256**  | **8**       | **Checkerboard** |  **1,692.9 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 256  | 8       | Checkerboard | 22,659.3 μs |    NA | 13.38 |         - |         - |         - | 1217008 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **256**  | **15**      | **Gradient**     |  **1,584.9 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 256  | 15      | Gradient     | 21,994.2 μs |    NA | 13.88 |         - |         - |         - | 1216960 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **256**  | **15**      | **Checkerboard** |  **1,517.4 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 256  | 15      | Checkerboard | 21,122.0 μs |    NA | 13.92 |         - |         - |         - | 1217296 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **512**  | **0**       | **Gradient**     |  **7,203.7 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 512  | 0       | Gradient     | 35,286.9 μs |    NA |  4.90 | 1000.0000 | 1000.0000 | 1000.0000 | 9923576 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **512**  | **0**       | **Checkerboard** |  **8,789.4 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 512  | 0       | Checkerboard | 41,203.3 μs |    NA |  4.69 | 1000.0000 | 1000.0000 | 1000.0000 | 9923864 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **512**  | **8**       | **Gradient**     |  **4,198.5 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 512  | 8       | Gradient     | 26,622.8 μs |    NA |  6.34 |         - |         - |         - | 4430480 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **512**  | **8**       | **Checkerboard** |  **4,507.9 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 512  | 8       | Checkerboard | 30,453.7 μs |    NA |  6.76 |         - |         - |         - | 4430480 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **512**  | **15**      | **Gradient**     |  **4,217.1 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 512  | 15      | Gradient     | 26,675.7 μs |    NA |  6.33 |         - |         - |         - | 4430480 B |          NA |
|               |      |         |              |             |       |       |           |           |           |           |             |
| **NativeDecode**  | **512**  | **15**      | **Checkerboard** |  **4,449.7 μs** |    **NA** |  **1.00** |         **-** |         **-** |         **-** |         **-** |          **NA** |
| ManagedDecode | 512  | 15      | Checkerboard | 27,207.2 μs |    NA |  6.11 |         - |         - |         - | 4430144 B |          NA |
