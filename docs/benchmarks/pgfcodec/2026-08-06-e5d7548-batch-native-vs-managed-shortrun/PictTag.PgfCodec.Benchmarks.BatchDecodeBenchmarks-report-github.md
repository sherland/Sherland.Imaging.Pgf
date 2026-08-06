```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8875/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5800X 3.80GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                     | BatchSize | Mean         | Error       | StdDev      | Ratio | RatioSD | Gen0      | Gen1      | Gen2      | Allocated   | Alloc Ratio |
|--------------------------- |---------- |-------------:|------------:|------------:|------:|--------:|----------:|----------:|----------:|------------:|------------:|
| **NativeDecodeBatch**          | **1**         |     **899.6 μs** |    **285.1 μs** |    **15.63 μs** |  **1.00** |    **0.02** |         **-** |         **-** |         **-** |           **-** |          **NA** |
| ManagedDecodeBatch         | 1         |   1,298.8 μs |    403.8 μs |    22.14 μs |  1.44 |    0.03 |  181.6406 |  181.6406 |  181.6406 |   1449890 B |          NA |
| ManagedReusableDecodeBatch | 1         |   1,147.6 μs |    138.2 μs |     7.57 μs |  1.28 |    0.02 |         - |         - |         - |      5368 B |          NA |
|                            |           |              |             |             |       |         |           |           |           |             |             |
| **NativeDecodeBatch**          | **10**        |  **13,636.6 μs** |  **1,620.9 μs** |    **88.85 μs** |  **1.00** |    **0.01** |         **-** |         **-** |         **-** |           **-** |          **NA** |
| ManagedDecodeBatch         | 10        |  20,034.4 μs |  2,782.9 μs |   152.54 μs |  1.47 |    0.01 | 1718.7500 | 1500.0000 | 1125.0000 |  22819320 B |          NA |
| ManagedReusableDecodeBatch | 10        |  18,123.2 μs |  4,308.1 μs |   236.14 μs |  1.33 |    0.02 |         - |         - |         - |     62944 B |          NA |
|                            |           |              |             |             |       |         |           |           |           |             |             |
| **NativeDecodeBatch**          | **50**        |  **86,196.3 μs** | **26,731.7 μs** | **1,465.25 μs** |  **1.00** |    **0.02** |         **-** |         **-** |         **-** |           **-** |          **NA** |
| ManagedDecodeBatch         | 50        | 105,299.1 μs | 10,423.4 μs |   571.34 μs |  1.22 |    0.02 | 3600.0000 | 3000.0000 |  800.0000 | 141893994 B |          NA |
| ManagedReusableDecodeBatch | 50        | 102,458.6 μs | 34,434.3 μs | 1,887.46 μs |  1.19 |    0.03 |         - |         - |         - |    333216 B |          NA |
