// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
