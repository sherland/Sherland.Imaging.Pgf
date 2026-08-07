# Sherland.Imaging.Pgf

A dependency-free C# port of digiKam's vendored `libpgf` codec — PGF (Progressive Graphics File) is
a wavelet-based image format offering high compression ratios and progressive, level-by-level
display. This library decodes (single-shot and progressive) and encodes PGF images, with no
native/P/Invoke dependency in the shipping package: the core codec is a from-scratch **managed**
reimplementation, not a wrapper around a compiled native binary — though, as the License section
below explains, much of it is a close, line-by-line translation of the original C++ source rather
than an independent reimplementation.

- **Original C++ library:** [libpgf.org](http://www.libpgf.org), as vendored by
  [KDE/digikam](https://github.com/KDE/digikam).
- **C# port author:** Steinar Herland.

See [`docs/PGF-CODEC.md`](docs/PGF-CODEC.md) for exactly what's supported and what's deliberately
out of scope, and [`AGENTS.md`](AGENTS.md) for repository layout, build, and test instructions.

## License

This repository uses a dual-license structure depending on the component:

- **Core library** ([`source/Sherland.Imaging.Pgf/`](source/Sherland.Imaging.Pgf/)): licensed under
  the **GNU Lesser General Public License v2.1 or later** ([LICENSE](LICENSE)), since it is a close
  derivative of the original C++ codec — many of its files are direct, line-by-line translations of
  specific `libpgf` source files (see each file's own doc comment for the exact original it ports).
  - Original C++ code: Copyright (C) 2006 xeraina GmbH, Switzerland.
  - C# port: Copyright (C) 2026 Steinar Herland.

  Individual files that have no C++ counterpart (new infrastructure such as pooled workspaces or
  the reusable decoder, added after the initial port) are Copyright (C) 2026 Steinar Herland alone,
  but are still distributed under the same LGPL-2.1-or-later terms as the rest of the library: LGPL
  §2 requires the whole of a combined work to be licensed under its terms once any part of it is
  based on the Library, even for sections that are independently original.
- **Tests, benchmarks, and the performance workbench**
  ([`source/Sherland.Imaging.Pgf.Tests/`](source/Sherland.Imaging.Pgf.Tests/),
  [`.Benchmarks/`](source/Sherland.Imaging.Pgf.Benchmarks/),
  [`.Performance/`](source/Sherland.Imaging.Pgf.Performance/)): original code by Steinar Herland
  that uses the core library's public API rather than containing ported code, licensed under the
  **[MIT License](LICENSE-MIT)**.
- **`native/Sherland.Imaging.Pgf.Native/libpgf/`**: an unmodified vendored copy of digiKam's
  `libpgf`, kept only as the test/benchmark correctness and performance oracle — not a production
  dependency. Retains its own original LGPL-2.1-or-later license and copyright notices; see
  [`native/Sherland.Imaging.Pgf.Native/libpgf/NOTICE.md`](native/Sherland.Imaging.Pgf.Native/libpgf/NOTICE.md).

Every source file carries an SPDX `SPDX-License-Identifier` header identifying which license and
copyright line applies to that specific file.
