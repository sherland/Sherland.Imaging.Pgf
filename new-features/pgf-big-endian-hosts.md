# PGF codec: big-endian host support — PRD

**Status: not started.** Closes a gap documented in [`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s
"Not yet ported — real gaps against full C++ parity" list ("Big-endian hosts").

## Context

`Sherland.Imaging.Pgf` is planned to be published as a standalone NuGet package (see
[`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s own opening note) — a real, stated goal that changes
the bar for what counts as in-scope. This port's C# code currently assumes a little-endian host
throughout, documented explicitly in `PgfDecoderCore.cs`'s own doc comment. Every real deployment
target *this app itself* runs on (x64/ARM64 desktop, browser WASM) is little-endian, so that
assumption has never been wrong in practice — but a general NuGet consumer's own deployment target
isn't this app's to assume.

**The framing that matters here, resolved by real investigation, not assumed**: is this about (A)
correctly reading/writing PGF files that were themselves produced by a big-endian *encoder* (a
per-file format concern), or (B) making this C# code run correctly if the .NET process itself
executes on a big-endian CPU (a host-architecture concern)? It's **(B)**. `PGF_USE_BIG_ENDIAN`
(`PGFplatform.h:40-54`) is a pure compile-time CPU/compiler detection macro (`__BIG_ENDIAN__`,
`WORDS_BIGENDIAN`, `__powerpc__`, `__sparc__`, `__ppc__`, `__s390__`, `TARGET_CPU_PPC`, etc.) — never
anything read from a PGF file. The on-disk PGF format itself is **always fixed little-endian**: the
non-big-endian branch of the `__VAL` macro (`PGFplatform.h:626`, `#define __VAL(x) (x)`) is a
no-op identity, confirming little-endian is the implicit wire format every build — little-endian or
big-endian host — normalizes to/from. `__VAL` exists solely so a build compiled for a genuinely
big-endian host CPU can swap its native word order to/from that fixed on-disk convention; a
little-endian build (i.e. `PGF_USE_BIG_ENDIAN` undefined, which is every real target this app has
ever run on) does nothing at all. There is no host-endianness *detection at runtime* in the native
code at all — it's baked in at compile time via `#ifdef`.

C# has no equivalent compile-time mechanism (no multi-targeting story sane for a single NuGet
package), but it does have a real *runtime* equivalent: `System.BitConverter.IsLittleEndian` reflects
the actual executing host's endianness and is available on every target framework this port
supports. This PRD's whole architecture rests on that one fact — see "Proposed architecture" below.

## Why this needs to be grounded

- **The `__VAL` macro** (`PGFplatform.h:595-628`): `#define __VAL(x) ByteSwap(x)` when
  `PGF_USE_BIG_ENDIAN` is defined, `#define __VAL(x) (x)` otherwise. `ByteSwap` overloads for
  `UINT16`/`UINT32`/`UINT64` are defined just above (`PGFplatform.h:601-620`), compiled in only for
  big-endian builds.
- **Every real call site** in `native/Sherland.Imaging.Pgf.Native/libpgf`, confirmed by direct read (not
  grepped-and-assumed):
  - `Decoder.cpp`: `preHeader.hSize` (:119), `header.width`/`header.height` (:133-134), the
    `levelLength[]` array inside its own `#ifdef PGF_USE_BIG_ENDIAN` block (:197-202), `wordLen` in
    `ReadMacroBlock` (:561), `ROIBlockHeader h.val` in `ReadMacroBlock` (:570, inside
    `__PGFROISUPPORT__`), the raw `m_codeBuffer[i]` words inside their own `#ifdef` block
    (:581-587), and `wordLen`/`ROIBlockHeader` again in `SkipTileBuffer` (:635, :642).
  - `Encoder.cpp`: `preHeader.hSize` (:113, and again in `UpdatePostHeaderSize` at :166),
    `header.width`/`header.height` (:118-119), `levelLength[]` in `UpdateLevelLength`'s `#ifdef`
    branch (:209-221), and in `WriteMacroBlock`'s big-endian branch: `wordLen` (:421),
    `ROIBlockHeader h.val` (:428), `m_codeBuffer[i]` (:434-436) — one contiguous
    `#ifdef PGF_USE_BIG_ENDIAN ... #else ... #endif` block (:419-448).
  - `PGFtypes.h`: a *separate* mechanism from `__VAL` serving the same underlying goal —
    `PGFVersionNumber`'s bitfield member order (:134-148) and `ROIBlockHeader::RBH`'s bitfield
    member order (:188-194) are each declared in reversed order under
    `#ifdef PGF_USE_BIG_ENDIAN`. This is C bitfield-packing-order compensation (which end of a word
    the compiler considers "first" is ABI/endianness-dependent for C bitfields specifically), not
    byte-swapping — genuinely irrelevant to this port, see Non-goals.
  - Color table and user data are always copied as raw byte blocks (`memcpy`-style `Read`/`Write`)
    and never pass through `__VAL` anywhere — nothing to swap for byte arrays.
- **This port's own current risk, catalogued precisely**: every multi-byte field the native code
  protects with `__VAL` is *already* read/written in this port via
  `System.Buffers.Binary.BinaryPrimitives.*LittleEndian` calls (`PgfHeader.cs`, `PgfDecoderCore.cs`,
  `PgfEncoderCore.cs`, `PgfDecodeSession.cs`, `PgfImageEncoder.cs`, `PgfColorConversion.cs` — every
  one of these normalizes to/from little-endian regardless of host endianness, by construction, so
  none of them are actually at risk). The **real, and only**, gap is two raw
  `MemoryMarshal.AsBytes` reinterpret-casts that bypass `BinaryPrimitives` entirely and assume the
  host's own in-memory `uint` layout already matches the little-endian wire format — correct on
  every real little-endian host, silently wrong on a genuinely big-endian one:
  - `PgfDecoderCore.cs:121` (`ReadMacroBlock`) — reads the raw macroblock code-buffer bytes directly
    into `block.CodeBuffer` (`uint[]`) via `MemoryMarshal.AsBytes(block.CodeBuffer.AsSpan())`. The
    method's own doc comment (`PgfDecoderCore.cs:128-131`) already states the assumption explicitly:
    "this host is little-endian, so the raw bytes already have the correct in-memory uint layout —
    no per-word byte-swap needed."
  - `PgfEncoderCore.cs:108` (`WriteMacroBlock`) — the mirror-image write-side reinterpret.
  - These correspond exactly to the native code's own `#ifdef PGF_USE_BIG_ENDIAN` blocks around
    `m_codeBuffer` (`Decoder.cpp:581-587`, `Encoder.cpp:419-448`) — i.e. this port's gap is real, but
    precisely as narrow as the native code's own conditional compilation implies, not broader.
- **Real-world relevance, checked rather than assumed**: mainline .NET (.NET 5+/CoreCLR, and Mono)
  has no officially supported big-endian target today — every RID in Microsoft's own support matrix
  (x64, x86, ARM64, ARM, WASM) is little-endian. This means there is no real CI machine or test
  target to run a genuine end-to-end big-endian verification against — see "Test rig" for how this
  PRD verifies correctness anyway.

## Goals

1. Make `PgfDecoderCore.ReadMacroBlock`/`PgfEncoderCore.WriteMacroBlock`'s raw code-buffer I/O
   correct on a genuinely big-endian .NET host, using a runtime `BitConverter.IsLittleEndian` check
   (the only mechanism C# has — no compile-time equivalent to `#ifdef PGF_USE_BIG_ENDIAN` exists,
   and inventing multi-targeting for this would be a much larger, unjustified change), matching the
   native code's own per-word `__VAL`/`ByteSwap` behavior exactly.
2. Confirm (not just assert) that every other multi-byte field this port reads/writes is already
   host-endianness-safe via `BinaryPrimitives`, closing the loop on the "Why this needs to be
   grounded" catalog above rather than leaving it as an unverified claim.
3. Directly unit-test the byte-swap logic itself (not just an end-to-end round trip that can't
   actually exercise the big-endian branch on any real CI host — see Test rig).

## Non-goals

- **Testing on genuine big-endian hardware or a big-endian .NET runtime build.** None exists in any
  realistically obtainable form for this project (per the "Real-world relevance" finding above) —
  this PRD verifies the swap logic directly and unit-tests it in isolation, not via an end-to-end
  round trip gated on real BE execution.
- **`PGFVersionNumber`/`ROIBlockHeader` C-bitfield reordering parity.** This is a C-language
  bitfield-packing-order concern specific to how a C/C++ compiler lays out `struct { UINT16 a:15,
  b:1; }`-style bitfields in memory on a given ABI/endianness — this port never uses C#'s own
  (different, and irrelevant here) bitfield-like constructs for these types. `PgfRoiBlockHeader`
  (the ROI PRD's own type) already builds and reads its packed value via explicit bit arithmetic
  (`bufferSize | (tileEnd ? 1u << 15 : 0)`, masks/shifts on a plain `ushort`) — inherently
  endianness-agnostic, since bit shifting within a single scalar value never depends on byte order;
  byte order only matters at the I/O boundary where that `ushort` gets serialized, which already
  goes through `BinaryPrimitives.WriteUInt16LittleEndian`/`ReadUInt16LittleEndian`
  (`PgfDecoderCore.cs`/`PgfEncoderCore.cs`'s own ROI header read/write, already confirmed safe).
  Nothing to port here.
- **Any new public API surface.** This is purely an internal correctness fix to two private methods
  — no caller-visible signature changes anywhere in this port.

## Proposed architecture

- A small internal helper (e.g. `PgfEndian.SwapWordsIfNeeded(Span<uint> words)` or similar, in a new
  `PgfEndian.cs` or added to `BitStream.cs` alongside this port's other low-level bit-manipulation
  helpers) that does nothing when `BitConverter.IsLittleEndian` is true (the real, common case on
  every actual CI/production host today) and byte-swaps every `uint` in place otherwise — a direct,
  faithful mirror of the native `ByteSwap`/`__VAL` pair, just decided at runtime instead of compile
  time.
- Wire this into `PgfDecoderCore.ReadMacroBlock` immediately after the `MemoryMarshal.AsBytes` read
  (swap the just-read words in place before anything reads `block.CodeBuffer`), and into
  `PgfEncoderCore.WriteMacroBlock` immediately before the `MemoryMarshal.AsBytes` write (swap a
  scratch copy — or swap in place and swap back — since `block.CodeBuffer` must retain its
  original, in-process values for anything that reads it again after the write, if anything does;
  verify this during implementation, don't assume).
- On every real (little-endian) host this is a single cheap boolean check per macroblock read/write
  with the swap branch never taken — no measurable overhead, matching the native code's own
  zero-cost `__VAL(x) = (x)` identity on non-big-endian builds.

## Test rig

- **Direct unit tests of the swap helper itself**, isolated from `BitConverter.IsLittleEndian`
  (which reflects the *real* host and can't be faked to pretend big-endian on a real little-endian
  CI machine): call the swap function directly with known input `uint[]` values and assert the
  byte-swapped output matches hand-computed expected values (e.g. `0x12345678` → `0x78563412`) —
  this is the only way to get real code coverage of the actual swap arithmetic on any real CI host,
  since the `BitConverter.IsLittleEndian`-gated call site itself will never take the swap branch
  during a normal test run.
- **Regression**: the existing full `Sherland.Imaging.Pgf.Tests` suite must stay green throughout —
  confirms this change is a no-op on every real (little-endian) test host, exactly as intended.
- **A whole-pipeline sanity check with the swap forced on**: temporarily/locally invoking the
  round-trip encode→decode path with the swap helper's "always swap" branch forced true (e.g. via an
  internal test-only overload/flag, not `BitConverter.IsLittleEndian` itself) to prove that swapping
  on write and swapping back on read is self-consistent and doesn't corrupt real macroblock data —
  the strongest verification available without genuine big-endian hardware.

## Stage sequence

1. **Swap helper + unit tests**: implement the `uint[]` word-swap helper and its direct, isolated
   unit tests (hand-computed expected values). Exit test: swap helper tests pass; not yet wired into
   the codec.
2. **Wire into `PgfDecoderCore`/`PgfEncoderCore`**: gate both `MemoryMarshal.AsBytes` sites on
   `BitConverter.IsLittleEndian`, matching the native `__VAL` semantics exactly. Exit test: full
   existing regression suite stays green (proves zero behavior change on real little-endian hosts).
3. **Forced-swap round-trip verification**: the "whole-pipeline sanity check" from the Test rig,
   proving self-consistency of the swap-on-write/swap-on-read pair without needing real BE hardware.
4. **Documentation**: `docs/PGF-CODEC.md`'s "Not yet ported" list updated (move this item to
   "Supported," with the real caveat that it's verified via isolated swap-logic tests plus a forced
   round trip, not genuine big-endian hardware, spelled out honestly); this PRD's own Progress log
   filled in.

## Acceptance criteria / Definition of Done

- The two identified `MemoryMarshal.AsBytes` sites are the only remaining host-endianness risk in
  this codec (re-confirmed at implementation time, not just carried forward from this PRD's own
  research) and are both fixed.
- The swap helper has direct unit test coverage exercising the actual swap arithmetic, since the
  real call sites' swap branch is untestable end-to-end on any real CI host.
- Zero regression on the existing (little-endian-host) test suite — this must be a provably
  zero-behavior-change fix for every real deployment target this app itself uses.
- `docs/PGF-CODEC.md` updated to move this item from "Not yet ported" to "Supported," honestly
  describing the verification method's real limits (no genuine big-endian hardware involved).

## Open questions

- **Whether `PgfEncoderCore.WriteMacroBlock`'s swap-before-write needs a scratch copy or can safely
  swap-in-place-then-swap-back** — resolve during Stage 2 by checking whether anything reads
  `block.CodeBuffer` again after `WriteMacroBlock` returns (if not, in-place swap-and-restore is
  simplest; if so, a scratch buffer avoids a transient corrupted-order window even though it's never
  observed under real single-threaded sequential use).
- **Where the swap helper should live** (new `PgfEndian.cs` vs. added to the existing `BitStream.cs`
  low-level helpers) — resolve once Stage 1 shows how much surrounding context it needs; either is a
  minor, easily-changed choice, not worth deciding upfront.

## Progress log

_(Empty — fill in as each stage above is actually implemented and tested, following
`pgf-roi-support.md`'s own progress-log convention: what was built, what was found, what broke and
how it was fixed, real test counts.)_
