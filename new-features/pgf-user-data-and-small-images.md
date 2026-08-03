# PGF codec: user data + small-image (nLevels=0) support — PRD

**Status: in progress (Stage 1 of 6 done).** Closes two gaps documented in
[`docs/PGF-CODEC.md`](../docs/PGF-CODEC.md)'s "Explicitly out of scope" list ("Header metadata" —
the user-data half specifically, not the color table, which `pgf-all-image-modes.md` already owns —
and "The `nLevels=0` 'raw/uncoded' path"). Two genuinely distinct features bundled into one PRD
because both were re-scoped by the same realization, not because they're technically related.

## Context

Every other PRD in this series (`managed-pgf-codec.md`, `pgf-cancellation-and-progress.md`,
`pgf-roi-support.md`, `pgf-all-image-modes.md`) scoped its "why" against *this app's* real usage —
digiKam's own small, RGBA, metadata-free thumbnail cache. Both gaps here were explicitly deprioritized
under that framing, verified directly against the real fixture rather than assumed:
`sample-thumbnail.pgf`'s own header has `hSize == HeaderSize` (16 bytes) — confirming this app's real
files carry zero post-header content — and real digiKam thumbnails never approach the ~10px
threshold that triggers the raw/uncoded path.

That framing changes if `PictTag.PgfCodec` is published as a standalone NuGet package for general
PGF use, not just consumed internally by this app. A general consumer's PGF files are not
constrained to "whatever digiKam's thumbnail writer produces" — a real file from some other PGF
encoder could carry embedded user data (arbitrary metadata a caller wrote at encode time) or a color
table, and a general-purpose caller could reasonably hand this library a very small image (an icon,
say) and expect it to work rather than hard-fail. This PRD exists specifically because of that
reframing — it has the same "no current internal need" honesty as this series' other PRDs, but a
clearer external-consumer motivation than most of them.

**A related, non-feature consideration surfaced by the same reframing, worth resolving before any
NuGet publication regardless of this PRD's own scope**: this port is a close derivative of digiKam's
vendored `libpgf` (LGPL-2.1+) — many of its own doc comments say "direct port of X," not "inspired
by." Publishing it externally likely means bundling the LGPL license text and attribution, and
probably warrants a real compliance check by someone qualified to make that call. Out of scope for
this PRD to resolve (it's a legal question, not a technical one) but flagged here since it blocks the
same goal this PRD is in service of.

## Why this needs to be grounded in the real algorithm

### User data

- **`PGFPostHeader`** (`PGFtypes.h:173-178`): `RGBQUAD clut[256]` (color table — owned by
  `pgf-all-image-modes.md`, not this PRD) plus `UINT8 *userData`, `UINT32 userDataLen` (real total
  size), `UINT32 cachedUserDataLen` (how much is actually cached in memory — see policy below).
- **`UserdataPolicy`** (`PGFtypes.h:101`): `UP_Skip = 0` (don't cache anything), `UP_CachePrefix = 1`
  (cache only the first N bytes), `UP_CacheAll = 2` (cache everything) — chosen at
  `ConfigureDecoder(useOMP, policy, prefixSize)` time (`PGFimage.h:260`), before `Open()`. The
  existing native shim's own production decode path always uses the default `UP_CacheAll` (`shim.cpp`:
  `img.ConfigureDecoder(false)`, no explicit policy argument) — this port doesn't need to replicate
  the policy distinction to match *that* real caller, but a general-purpose library plausibly should
  expose it (skipping/limiting caching is a real, sensible option for a caller that doesn't care about
  metadata and doesn't want to pay to buffer it).
- **`GetUserData`** (`PGFimage.cpp:337-341`): returns the cached pointer + `cachedUserDataLen`, plus
  optionally the real total `userDataLen` even when only a prefix was cached.
- **`SetHeader`'s userData parameters** (`PGFimage.cpp:893`, already read during this port's own
  Stage 4/8 grounding): `userData`/`userDataLength`, copied into a freshly allocated buffer and
  folded into `m_preHeader.hSize` — the encode-side mirror.
- **`MaxUserDataSize`** (`PGFtypes.h:288`): `0x7FFFFFFF` — a type-level ceiling with no real-world
  meaning, not a sane default allocation bound. **A general-purpose library reading untrusted external
  files must not blindly allocate based on a header-declared length** — this is a new consideration
  this series' other PRDs haven't needed, since this app's own input (digiKam's own SQLite-cached
  blobs) is implicitly trusted-shape; a NuGet consumer's input is not. Bound any allocation against
  the actual remaining stream length before trusting a declared size, matching this codec's existing
  "fail closed on malformed input" philosophy (`managed-pgf-codec.md` Tier 5) rather than assuming
  well-formed input.

### The `nLevels=0` raw/uncoded path

- **`CPGFImage::ComputeLevels`** (already ported, `PgfHeaderIO.ComputeLevels`) can itself produce
  `nLevels = 0` for small enough images — the encode-side trigger. This port's own encoder currently
  treats that as a hard failure (`PgfImageEncoder.cs:39`) rather than taking the raw path.
  `TestBitmaps.MinimumSupportedDimension` (10) is this port's own floor, not a limitation of the
  format itself.
- **Decode** (`CPGFImage::Open`, `PGFimage.cpp:198-214`): when `nLevels == 0`, no wavelet transform,
  no entropy coding at all — each channel's raw `DataT` (`short`) coefficients are read directly from
  the stream, one value at a time, straight into `m_channel[c]`. Critically, **color conversion is
  completely unaffected** — `Read(0)`/`GetBitmap` proceed exactly as normal afterward, since
  `m_channel[]` ends up populated the same way a full inverse-transform would have left it; this
  path only changes how the channel arrays get *populated*, not anything downstream. That should
  make this the single cheapest feature in this whole PRD series to actually implement, once decided
  on.
- **Encode** (`CPGFImage::WriteImage`, `PGFimage.cpp:1159-1175`): the mirror — each channel's already
  color-converted (YUV-space) coefficients are written directly, uncoded, one `DataT` at a time.
- **A real, documented risk to be aware of, not avoid by staying ignorant of it**: this exact code
  path (reached via `min(width,height) < 10`) was implicated in real crash investigation during
  `managed-pgf-codec.md`'s own Stage 1 — the native shim's test-only exports deliberately reject this
  size range rather than exercise it, and the root cause was never fully resolved (see that PRD's
  Progress log). Porting this path faithfully in *this* port's own from-scratch C# doesn't inherit the
  native shim's specific bug (this port doesn't share that code), but the investigation is worth
  re-reading before starting, since it's the only prior evidence anyone has actually looked closely at
  this exact size boundary.

## Goals

1. **User data**: `PgfImageDecoder`/`PgfProgressiveDecoder` can read and expose a file's post-header
   user data (bytes + total length), and `PgfImageEncoder` can write arbitrary caller-supplied user
   data into an encoded file. Round-trips byte-exact.
2. **User data policy**: expose the skip/prefix/cache-all choice as an optional parameter (defaulting
   to cache-all, matching this port's existing "just works" defaults elsewhere) — a general library
   consumer decoding many files without caring about metadata shouldn't be forced to pay to buffer it.
3. **Untrusted-length defense**: no allocation is ever sized directly from an unvalidated
   header-declared length without bounding it against the real remaining stream length first — for
   user data specifically, but treat this as a standing principle worth a second look across the rest
   of the header-parsing code too while here (confirm the existing level-length-array and post-header
   size handling already does this correctly; `managed-pgf-codec.md`'s own header reader was written
   before this consideration was explicit, so verify rather than assume it already holds).
4. **`nLevels=0` raw path**: both directions, so this port's encoder stops hard-failing on very small
   images and can genuinely round-trip them, matching the format's own real behavior rather than this
   port's current, narrower-than-necessary floor.

## Non-goals

- **The color table (indexed-color palette)** — fully owned by `pgf-all-image-modes.md`; don't
  duplicate that work here.
- **A real production call site inside this app needing either feature.** Same honest framing as
  this series' other PRDs — this is general-library completeness, motivated by a possible future
  NuGet publication, not a current product need.
- **Resolving the LGPL/licensing question this PRD's own Context section flags.** Explicitly a
  separate, non-technical concern for someone else to own — not blocked on this PRD, and this PRD
  isn't blocked on it either, but don't let this document's existence be mistaken for having answered
  it.
- **Streaming/incrementally-delivered user data** (a caller receiving bytes as they arrive over a
  slow transport). This port's whole model is "the whole input is already in memory" — matches
  `managed-pgf-codec.md`'s own existing scope decision, not something this PRD reopens.

## Proposed architecture

- **`PgfHeaderIO`**: gains post-header user-data read (respecting a policy parameter — skip/prefix/
  cache-all) and write, alongside its existing pre-header/header/level-length handling. The
  untrusted-length bounding (Goal 3) belongs here specifically, since this is where every
  header-declared length first gets trusted or not.
- **Public API**: `PgfImageDecoder.TryDecode`/`PgfProgressiveDecoder.TryOpen` gain a way to retrieve
  user data alongside the decoded image (an additional `out` value, or a small result struct — decide
  based on which reads more naturally against this port's existing `TryX(..., out result)` convention
  once actually drafting the signature, not decided here). `PgfImageEncoder.TryEncode` gains an
  optional user-data parameter (`ReadOnlySpan<byte>? userData`, defaulting to none — every existing
  call site keeps compiling unchanged).
- **`nLevels=0` path**: a new, narrow branch in `PgfDecodeSession`'s setup (decode) and
  `PgfImageEncoder.TryEncode` (encode) that reads/writes raw channel coefficients directly instead of
  building a `PgfWaveletTransform`/running the entropy coder at all — bypassing both, not routing a
  degenerate 1-level case through them. `PgfColorConversion` needs no changes at all (per the grounding
  above, this path only affects how channel arrays are populated, not what happens to them
  afterward).

## Test rig

- **User data round-trip**: encode with a range of user-data payloads (empty, small, a size that
  exercises the prefix-caching boundary if that policy option is built) — decode and assert
  byte-exact recovery, for each caching policy.
- **Untrusted-length defense**: a deliberately corrupted/truncated header claiming a user-data length
  far larger than the actual remaining stream — must fail closed (return `false`, per this codec's
  existing Tier 5 philosophy) rather than attempt a huge allocation or throw an unhandled exception.
  Same treatment for the level-length array and any other header-declared-length field touched while
  verifying Goal 3's "second look."
- **`nLevels=0` round-trip**: known-pixel synthetic fixtures at every size from 1x1 up through
  whatever this port's `MinimumSupportedDimension` boundary currently is, both directions, at
  `quality=0` (pixel-exact — this path has no quantization concept distinct from the normal path,
  confirm that directly rather than assume) — extending `managed-pgf-codec.md`'s existing
  `EdgeCaseDimensions`-style fixture approach downward into the size range it currently excludes.
- **Regression**: the existing 567-test suite, plus every other PRD's own test suite once
  implemented, stays green — this PRD touches header parsing broadly enough (Goal 3) to warrant
  re-running everything, not just its own new tests.

## Stage sequence

1. **User data — decode side**, including the untrusted-length defensive bound (Goal 3) worked out
   here first, since it's the header-parsing change with the most real robustness value. Exit test:
   read a real-or-synthetic file's user data byte-exact; fail closed on a corrupted length claim.
2. **User data — encode side.** Exit test: round-trip (encode with payload, decode, compare).
3. **Apply the same untrusted-length-bounding review to the rest of `PgfHeaderIO`** (level-length
   array, any other declared-length field) — a deliberate, separate stage so it gets real attention
   rather than being an incidental side effect of Stage 1.
4. **`nLevels=0` decode.** Exit test: byte-exact against the native oracle for the smallest sizes the
   native shim's own test-only exports can safely produce (if any — the native shim currently rejects
   this range entirely; may need the shim's guard loosened specifically for this PRD's own testing, a
   real prerequisite to resolve early, not assume away) or self-consistency if not.
5. **`nLevels=0` encode**, completing the round trip.
6. **Documentation**: `docs/PGF-CODEC.md` updated (both list items resolved or narrowed); this PRD's
   own Progress log filled in.

## Acceptance criteria / Definition of Done

- User data (any size within a sane, defensively-bounded range) round-trips byte-exact, under each
  supported caching policy.
- A corrupted/malicious header-declared length (user data or otherwise) fails closed rather than
  attempting an unbounded allocation or throwing an unhandled exception.
- Images below this port's current `MinimumSupportedDimension` round-trip correctly via the
  `nLevels=0` path, both directions, at `quality=0`.
- The full existing test suite (this PRD's own new tests plus every prior PRD's) stays green
  throughout.
- `docs/PGF-CODEC.md` updated.

## Open questions

- **Whether the native shim's test-only encode export needs its own guard loosened** to produce real
  `nLevels=0` oracle fixtures, given its existing rejection of this size range was itself motivated by
  a real, unresolved crash risk (`managed-pgf-codec.md` Stage 1) — decide whether that risk is
  specific to the debug-channel export this PRD doesn't need, or general enough to require real
  caution here too, before assuming it's safe to just remove the guard.
- **Exact public API shape for retrieving user data alongside a decoded image** — an additional `out`
  parameter vs. a small result type vs. a separate explicit call — resolve by whichever reads most
  naturally against this port's existing conventions once actually drafting it, not decided upfront.
- **Whether exposing `UserdataPolicy`'s skip/prefix/cache-all distinction is worth the API surface**,
  or whether a general consumer is just as well served by "always cache all, it's already bounded to
  something sane" (Goal 3) — resolve based on how large real-world user data payloads actually tend to
  be, if that's discoverable, rather than guessing.

## Progress log

**Stage 0 (verification) — done.** Every citation in this PRD's "Why this needs to be grounded in the
real algorithm" section was re-checked against the real files before implementing anything:
`PGFtypes.h`'s `PGFPostHeader`/`UserdataPolicy`/`MaxUserDataSize`, `PGFimage.h`'s `ConfigureDecoder`,
`PGFimage.cpp`'s `GetUserData`/`SetHeader`/`Open`'s `nLevels==0` branch/`WriteImage`'s mirror, and
`Decoder.cpp`'s constructor (not itself cited by the PRD, but where the real read/skip/cache-prefix
logic actually lives - PGFimage.cpp only shows the call site). All matched the PRD's description.
One correction: `PgfHeaderIO` turned out to live in `PgfHeader.cs`, not a separate `PgfHeaderIO.cs`
file (the PRD's own citation pattern implied a dedicated file) - same class, just co-located with the
`PgfHeader`/`PgfPreHeader` records it operates on.

**Open Question 1 (native shim guard) — resolved empirically, guard removed.** Before Stage 4/5 could
even be attempted, this had to be settled: does `pgf_encode_bgra_alloc`'s `width < 10 || height < 10`
guard reject a real, still-live crash risk, or was it conflated with the separately-fixed
`realloc()`/`delete[]` bug from the same Stage 1 investigation (`managed-pgf-codec.md`)? Resolved by
isolated experiment, not by re-reading the old investigation harder: a scratch copy of `shim.cpp` with
only that guard removed was built (CMake/Ninja, the same toolchain `docs/TESTING.md` documents), then
stress-tested via a throwaway P/Invoke console app - 5000 encode-then-decode round trips across ten
sizes (1x1 up to 100x1), alternating gradient and solid-color content, quality=0 - with zero crashes
and zero pixel mismatches. That result is consistent with the original heap corruption having been the
realloc()/delete[] bug (already fixed) rather than anything inherent to the `nLevels==0` path itself,
so the guard was removed for real in `shim.cpp`'s `pgf_encode_bgra_alloc` and `pgf_encode_raw_alloc`
(same reasoning, same fix) - both functions' doc comments now record this finding in full.
`pgf_debug_decode_channel`'s own, separate, still-unresolved repeated-call crash is untouched and
unaffected either way (independently narrowed to its own `GetChannel()`/`memcpy` read, a code path
neither encode function calls). Two existing tests that asserted the old rejection behavior were
updated to assert successful round-trips instead: `NativeEncodeExportsTests.
BelowMinimumDimension_EncodeThenDecode_Lossless_MatchesOriginalExactly` (renamed from
`..._EncodeFailsClosed_WithoutThrowing`) and `PgfImageEncoderTests.BelowMinimumDimension_FailsClosed_
WithoutThrowing`'s doc comment (assertion itself unchanged - this port's own encoder still hard-fails
on `nLevels=0` until Stage 5). Full suite re-run after the native DLL rebuild: 1043/1043 green.

**Stage 1 (user data — decode side) — done.** `PgfUserDataPolicy` (Skip/CachePrefix/CacheAll, matching
`UserdataPolicy` exactly) and `PgfUserData` (`CachedBytes`/`TotalLength`) added as new public types.
`PgfHeaderIO.Read` gained `policy`/`prefixSize` parameters and now really reads (or skips, or
prefix-caches) the post-header user data instead of unconditionally skipping it - the untrusted-length
defense (Goal 3) lives here: the post-header size is derived from the file's own `hSize` field, which
a general (non-digiKam) caller cannot trust, so it's bounds-checked against the stream's actual
remaining length *before* any allocation or read is attempted, throwing `PgfFormatException` (fail
closed) otherwise. `PgfDecodeSession.TryOpen` threads the policy through and exposes `UserData` as a
property (alongside its existing `ColorTable`). Public API surface, resolving Open Question 2:
`PgfImageDecoder.TryDecode` gained a second overload (`out PgfUserData` + policy parameters) rather
than widening the existing signature in place - C# forbids a required (`out`) parameter after optional
ones, so the only backward-compatible option was a new overload, with the original delegating to it
(`out _`) to avoid duplicating the decode loop. `PgfProgressiveDecoder.TryOpen` gained the same policy
parameters directly (no `out`-vs-overload conflict there, since it returns a nullable object) and
exposes `UserData` as a plain property, matching how it already exposes `Width`/`Height`/`Levels`.
Open Question 3 (whether exposing the skip/prefix/cache-all distinction is worth the API surface) is
resolved by Goal 2 itself, which already mandates exposing it - implemented as designed, defaulting to
`CacheAll` so no existing call site needs to change.

New tests: `PgfUserDataTests.cs` (15 tests) - decode-only round trips against hand-assembled synthetic
headers (Stage 2 hasn't added encoder-side user data yet, matching this stage's own exit-test wording
of "a real-or-synthetic file"), covering all three policies, the color-table-plus-user-data
combination, the skip-path landing at the correct level-length offset afterward, two corrupted-`hSize`
fail-closed cases (one specifically sized to prove the check happens before any oversized allocation
would be attempted), and two full-pipeline tests (a real encoded image with user data spliced in,
decoded through both `PgfImageDecoder.TryDecode` and `PgfProgressiveDecoder`, proving pixel data and
user data both come back correctly together) plus one confirming the simpler `TryDecode` overload
still compiles and behaves unchanged. Full regression: `PictTag.PgfCodec.Tests` 1043 → 1058 (15 new,
0 failed), `PictTag.Data.Tests` 15/15 unchanged (facade untouched by this stage).

**Stage 2 (user data — encode side) — done.** `PgfHeaderIO.Write` gained an optional
`ReadOnlySpan<byte> userData = default` parameter, folded into `hSize` alongside the existing color
table and written right after it (`PGFPostHeader ::= [ColorTable] [UserData]`, matching `Read`'s own
order). `PgfImageEncoder.TryEncode`/`TryEncodeMode` both gained the same optional parameter as their
new trailing argument (no `out`-vs-optional conflict here, unlike Stage 1's decoder overload, since
`userData` is an input) - every existing call site keeps compiling unchanged.

New tests (11, appended to `PgfUserDataTests.cs`): byte-exact round trip across a range of payload
sizes (0/1/37/256 bytes) through the real `PgfImageEncoder` → `PgfImageDecoder` pipeline; no-user-data
still decodes as `PgfUserData.None`-equivalent; user data survives every lossy quality level
byte-exact (plain bytes, no quantization concept, unlike pixel data - confirmed directly rather than
assumed, per this stage's own test rig note); `TryEncodeMode`'s indexed-color path round-trips color
table and user data together; `PgfProgressiveDecoder.TryOpen` correctly exposes user data written by
the real encoder (not just the Stage 1 hand-spliced case). One test cross-checks against the **real
native decoder** (`NativePgfOracle.TryDecode`) that a C#-encoded file with user data still opens and
decodes correctly there too - proof the `hSize`/post-header accounting is right by the format's own
real parser, not just self-consistent within this port's own reader. Full regression:
`PictTag.PgfCodec.Tests` 1058 → 1069 (11 new, 0 failed).

**Stage 3 (untrusted-length bounding review) — done, found and fixed a real bug beyond user data.**
Re-verified `PgfHeaderIO.Read`'s own two named cases first: the level-length array is safe regardless
of `hSize` (`header.NLevels` is a `byte`, max 255, and each entry read already fails closed via
`PgfMemoryReader.Read`'s truncate-and-report-actual-count contract if the stream runs out early); the
post-header size is safe too (the color table allocation is always the fixed `ColorTableSize`
constant, never sized from the declared `hSize`, and Stage 1 already bounds user data specifically).
Both confirmed via existing tests, no code change needed there.

The broader "second look" Goal 3 explicitly invites turned up a real, previously-undetected bug one
level down from `PgfHeaderIO` itself, in `PgfDecodeSession.TryOpen` (its very next consumer):
`checked((int)header.Width)`/`Height` threw an uncaught `OverflowException` - not
`PgfFormatException`/`PgfStreamException`, so none of this codec's existing catch blocks caught it -
for any header declaring a width/height that doesn't fit in an `int` (confirmed empirically with a
throwaway repro before touching any code, not assumed: `Width = 0xFFFFFFFF` crashed straight through
`PgfImageDecoder.TryDecode`). A second, related overflow existed one step further: two
individually-int-sized dimensions (e.g. 100,000 × 100,000 - unremarkable on their own) whose product
times 4 (the BGRA buffer size `PgfImageDecoder.TryDecode`/`PgfProgressiveDecoder.TryDecodeLevel` each
separately compute via their own `checked(width * height * 4)`) overflows `int` and throws there
instead. Both fixed with explicit range checks in `TryOpen` (before the cast, and via
`(long)fullWidth * fullHeight > int.MaxValue / 4` before returning), so every downstream `checked(...)`
can now never actually overflow - failing closed once, at the single point that already parses the
header, rather than duplicating checks in every consumer. `PgfModeInfo.ExpectedSourceByteLength`'s own
`checked(...)` was checked too and left alone: it's encode-side only, driven by a caller's own
width/height (their own image), not a value read from an untrusted file - out of Goal 3's scope, which
is specifically about decoding untrusted external input. `PgfWaveletTransform`'s per-level loop (up to
256 iterations for a maximal byte `NLevels`) was also checked and confirmed already safe: dimensions
shrink via `>>1` toward 0 and stay there, never negative, terminating in exactly `NLevels+1`
iterations regardless of input - no fix needed.

New tests (8, `PgfUntrustedLengthTests.cs`): width/height that don't fit in `int` at all, width×height
that overflows the buffer-size multiplication, zero width/height, the same fixed-closed behavior via
`PgfProgressiveDecoder.TryOpen`, and a normal-sized-image regression proving the new bound doesn't
narrow real, valid input. Full regression: `PictTag.PgfCodec.Tests` 1069 → 1077 (8 new, 0 failed).

**Stage 4 (`nLevels=0` decode) — done.** `PgfDecodeSession.TryOpen` no longer rejects
`header.NLevels == 0`: a new `TryReadRawChannels` helper reads each channel's raw `DataT`
(`int`) coefficients directly and sequentially (whole channel, then the next - not interleaved the
way the leveled bitstream is), matching `CPGFImage::Open`'s own `nLevels==0` branch exactly
(PGFimage.cpp:198-214). The chroma downsample decision (quality/mode-driven) is computed once,
before branching on `NLevels`, and applies identically to both paths - confirmed directly against
the native source (PGFimage.cpp:161-187, evaluated before the `nLevels` check), not assumed. The
result is stored as `PgfDecodeSession.RawChannelData`, populated at `TryOpen` time rather than
lazily during level decode - mirroring the native codec's own timing (`CPGFImage::Open` reads this
data synchronously; `Read(level)` is a no-op for `nLevels==0`, PGFimage.cpp:415-422). `PgfImageDecoder.
TryDecode` and `PgfProgressiveDecoder` both seed their per-channel working array from
`RawChannelData` when present, so their existing level loops (which naturally run zero iterations
when `Levels == 0`) need no restructuring - only `PgfProgressiveDecoder.TryDecodeLevel`'s level-range
guard needed a real change, to accept `level == 0` when `Levels == 0` (the old `level >= Levels`
check rejected level 0 itself when `Levels` is 0) - this mirrors the native `ASSERT((level >= 0 &&
level < m_header.nLevels) || m_header.nLevels == 0)` (PGFimage.cpp:403) exactly, which explicitly
carves out this case rather than treating it as an ordinary range check.

**A wrong assumption caught by its own test, not assumed correct**: an initial test asserted every
quality level decodes pixel-identically on this path, reasoning "no forward transform, so no
quantization." That's true, but incomplete - RGBA's chroma downsample decision (quality >
`DownsampleThreshold`) is independent of quantization and applies on this path too (confirmed above),
so quality 4/6/15 genuinely decode different (still correct, chroma-subsampled) pixels, not a bug.
Split into `..._QualityAtOrBelowDownsampleThreshold_StillLossless` (0-3, byte-exact) and
`..._AboveDownsampleThreshold_StillDecodesSuccessfully` (4/6/15, dimensions-only) once the real
behavior was confirmed empirically rather than re-asserting the original wrong expectation.

New tests (21, `PgfNLevelsZeroDecodeTests.cs`): byte-exact decode against the real native oracle
(`NativePgfOracle.TryEncode`/`TryEncodeMode`, whose own size guard Open Question 1 already removed)
across eight sizes down to 1x1, both single-shot and progressive decode, an indexed-color (paletted)
tiny image, a truncated-stream fail-closed case, and `PgfProgressiveDecoder`'s level-0-only
contract (rejecting level 1, idempotent on repeat level-0 requests). Full regression:
`PictTag.PgfCodec.Tests` 1077 → 1098 (21 new, 0 failed); `PictTag.Data.Tests` 15/15 unchanged.

**Stage 5 (`nLevels=0` encode, completing the round trip) — done.** `PgfImageEncoder.TryEncodeMode`'s
old `if (header.NLevels == 0) return false;` hard-fail is gone. Color conversion and the chroma
downsample decision run exactly as before (both are shared with the normal path, unaffected by
`NLevels`); a new `header.NLevels == 0` branch, inserted right where the normal path would otherwise
start building `PgfWaveletTransform`s, instead writes the header and then each channel's
already-color-converted (and, if downsampled, already-subsampled) coefficients directly via a new
`WriteRawChannels` helper - no forward transform, no entropy coding at all, matching
`CPGFImage::WriteImage`'s own `nLevels==0` branch exactly (PGFimage.cpp:1159-1175) and mirroring
`PgfDecodeSession`'s Stage 4 decode-side reader (same per-channel-sequential order, same `int`/
`DataT` little-endian representation). `progress` reports once at `1.0` (no incremental per-level
work to report across, matching the native callback's own single fire here);
`cancellationToken` is checked once before writing, for parity with the normal path's "once per unit
of work" grain even though there's only one unit of work on this path.

New tests (32, `PgfNLevelsZeroEncodeTests.cs`): self-consistent round trip (this port's own encoder →
decoder) at every size from 1x1 through `MinimumSupportedDimension`, plus a handful of extreme aspect
ratios (1x100, 100x1); a cross-check that the **real native decoder** opens this port's own
`nLevels=0`-encoded output byte-exact (the strongest available proof, matching this codec's usual bar
- not just internal self-consistency); the same downsample-threshold quality split Stage 4 needed
(lossless at quality 0-3, dimensions-only above); indexed-color and user-data composed with the raw
path; progressive decode of this port's own encoded output; and a progress-reports-1.0 check. One
existing test (`PgfImageEncoderTests.BelowMinimumDimension_FailsClosed_WithoutThrowing`) was renamed
to `..._NoLongerFailsClosed_EncodesViaRawPath` and its assertion flipped, since the behavior it was
locking in is exactly what this stage changed; `TestBitmaps.MinimumSupportedDimension`'s own doc
comment was updated to stop calling this range "out of scope" now that every consumer (native shim,
this port's decoder, this port's encoder) supports it. Full regression: `PictTag.PgfCodec.Tests`
1098 → 1130 (32 new, 0 failed); `PictTag.Data.Tests` 15/15 unchanged.
