# PGF codec: user data + small-image (nLevels=0) support — PRD

**Status: not started.** Closes two gaps documented in
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

_(Empty — fill in as each stage above is actually implemented and tested, following
`managed-pgf-codec.md`'s own progress-log convention: what was built, what was found, what broke and
how it was fixed, real test counts.)_
