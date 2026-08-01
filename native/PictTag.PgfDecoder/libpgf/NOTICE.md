# Vendored libpgf

The files in this directory (`BitStream.h`, `Decoder.{cpp,h}`, `Encoder.{cpp,h}`, `PGFimage.{cpp,h}`,
`PGFplatform.h`, `PGFstream.{cpp,h}`, `PGFtypes.h`, `Subband.{cpp,h}`, `WaveletTransform.{cpp,h}`,
`README`) are an unmodified copy of digiKam's vendored libpgf codec, taken from
[`core/libs/pgfutils/libpgf/`](https://github.com/KDE/digikam/tree/master/core/libs/pgfutils/libpgf)
in the [KDE/digikam](https://github.com/KDE/digikam) repository. Original project:
[libpgf.org](http://www.libpgf.org).

Licensed under the **GNU Lesser General Public License, version 2.1 or later** (see `LICENSE` in
this directory, and the per-file license headers). digiKam's own Qt-dependent wrapper around this
codec (`pgfutils.cpp`/`pgfutils.h`, one level up in digiKam's source tree) is **not** vendored here
- `PictTag.PgfDecoder`'s `shim.cpp` (in the parent directory) is a from-scratch, Qt-free wrapper
that calls this codec directly, replicating the same real decode call sequence read from digiKam's
wrapper (see `new-features/albums-tags-browser.md`'s "Native PGF decoder" section).

This codec is linked dynamically (a separate DLL, P/Invoked from .NET - never statically linked
into `PictTag.Api`), keeping this a clean LGPL dynamic-linking case.
