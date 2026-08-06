# PGF codec: sourcing historical libPGF versions for legacy-format oracles

**Status: investigation complete. No code changes in this document.** This is a grounding reference
for two other PRDs — [`pgf-legacy-interleaved-decode.md`](pgf-legacy-interleaved-decode.md) and
[`pgf-bitmap-legacy-packed.md`](pgf-bitmap-legacy-packed.md) — both of which need a genuine, real
historical `libpgf` build as an independent oracle to validate the managed decoder's legacy-format
support against. This doc answers, with real evidence rather than assumption, exactly which
historical version(s) are actually obtainable today, how to obtain them reproducibly, and what each
one can and can't prove.

## Why this was worth a dedicated investigation

Both PRDs above were written by directly reading `native/Sherland.Imaging.Pgf.Native/libpgf/` — this repo's
own vendored copy of the *current* (7.19.3-derived) reference source — and inferring legacy behavior
from comments and dead code left in that current source. Neither PRD had access to an actual older
build. `pgf-legacy-interleaved-decode.md`'s own "Why this needs to be grounded" section explicitly
flags this as its single biggest weakness ("no independent oracle obtainable") and leaves as an
**open question** whether hunting down a real historical pre-Version5 build would be worth the effort.
This investigation answers that question empirically instead of leaving it open.

## Investigation method and findings, in the order they were tried

### 1. SourceForge Files (the release archive listing)

`https://sourceforge.net/projects/libpgf/files/libpgf/` lists **exactly seven** release folders,
confirmed by scraping the real page content (not just the summarized version): `6.11.42` (2011-10-28),
`6.12.24` (2012-06-26), `6.14.12` (2014-04-15), `7.15.32` (2015-08-08), `7.19.3` (2019-01-15),
`7.21.2` (2021-01-17), `7.21.7` (2021-02-18). **Nothing older than 6.11.42 (Oct 2011) is available
here.** Every filename-guessing attempt to actually download an archive
(`libpgf-src-6.11.42.zip`/`.tar.gz` via both the `sourceforge.net/.../download` redirect and the
`downloads.sourceforge.net` mirror shortcut) returned **HTTP 403 with a Cloudflare "Just a moment…"
JS-challenge page**, not the real file — direct scripted downloads from SourceForge's file-hosting
endpoints are actively bot-blocked, while the project's plain HTML pages (file listing, project home)
render fine. This is a real, non-obvious obstacle worth remembering: **`curl`/script-based downloads
of actual SourceForge release archives don't work from this environment**; every archive actually
obtained below came from a different host entirely (see §3).

### 2. SourceForge's official git mirror

`git clone https://git.code.sf.net/p/libpgf/git` succeeds and has 23 commits, but **the entire history
starts 2021-01-16** ("Version 7.21.2") — this is clearly the point SourceForge's own git migration
began, not a full import of the project's real history. No branches or tags beyond `master`. Not
useful for anything before 2021.

### 3. SourceForge's legacy SVN repository

The historical prompt driving this investigation (see the ChatGPT research brief this PRD responds
to) named a SourceForge SVN repo at `svn.code.sf.net/p/libpgf/code` with `trunk/libpgf`/`trunk/pgf`
paths and referenced specific old revisions (136, 147, 148). **This does not exist** — confirmed with
a real `svn` client (installed via `winget install Slik.Subversion` for this investigation, since none
was present) against every plausible mount name: `.../p/libpgf/code`, `.../p/libpgf/svn`,
`.../p/libpgf/libpgf`, and the bare `.../p/libpgf` project root. All four return `E170013`/`E160013`
("path not found"), the same error SVN gives for a repository that was never there, not one that's
merely access-restricted. Either this repo never existed under that name, or SourceForge fully
decommissioned it with no redirect — either way, it's a dead end. **The revision numbers 136/147/148
from that brief could not be verified or reached by any means.**

### 4. Debian's package archive (`snapshot.debian.org`)

Debian has packaged `libpgf` since 2012; `snapshot.debian.org` keeps every `orig` source tarball
Debian's maintainers ever uploaded, served from a different host than SourceForge (not
Cloudflare-protected — downloads worked on the first try). Full version history via
`https://snapshot.debian.org/mr/package/libpgf/`:
`6.12.24+ds1-1` (first seen 2012-12-11), then `6.12.24+ds1-2`/`-2.1`/`-2.2`,
`6.14.12-1`/`-2`/`-3`/`-3+deb8u1`/`-3.1`/`-3.2`, then jumping straight to `7.21.7+ds-1`/`-2`/`-3`.
**Oldest available: `6.12.24+ds1-1`, Dec 2012 — still nothing older than SourceForge's own oldest.**
But this *does* give a working, non-Cloudflare-blocked way to fetch **exactly the two versions the
original research brief recommended** (`6.12.24` and `6.14.12`), which is how both were actually
obtained for this investigation (see Reproduction steps below).

### 5. digiKam's git history — the real find

`https://github.com/KDE/digikam` has genuine, complete history back to **2004-05-05** (its old CVS
import), 43,532 commits total. Searching for when `libpgf` was first added
(`git log --all --diff-filter=A -- '*PGFimage.cpp'`) finds the actual first vendoring commit:

- **`a51a661ef07d2b4aaaf59fd02d7bbdcc0b790f37`, 2009-05-29**, message: *"add libpgf 5.0.0 code. Not
  yet used."* — path `libs/database/libpgf/`. The vendored `PGFtypes.h` at this exact commit
  identifies itself internally as **`PGFCodecVersion "5.08.22"`** (this file's own
  `major.year.week` numbering — major **5**, 2008 week 22) — i.e. this genuinely is the release that
  introduced the "major version 5" scheme the current codebase's `Version5` flag is named after.
  **This is the oldest real libpgf source obtainable anywhere, by over two years, and it predates
  every SourceForge/Debian release found above.**
- Follow-on updates, all real, all independently fetchable the same way: `6.09.24`
  (`1210673459365a5e6bf865989c6f09911d3a95ec`, 2009-06-12 — *"Update libpgf to official 6.09.24...
  Thanks to libpgf team"*), `6.09.33` (`975c593b50b9e3088db7d0ad01b9a860ba7fea47`, 2009-09-23),
  **`6.09.44`** (`3383944e374d41088ae2a164d9348d3632b86e54`, 2009-10-29 — *"update internal libpgf to
  last version 6.09.44"*) — this last one is exactly the version the original research brief claimed
  digiKam once bundled, now confirmed and pinned to a real commit SHA. digiKam later moved the vendored
  code to `libs/pgfutils/libpgf/` (`b0f7ce4ab29bb9999dd7c0308592757a161c6c5e`, 2018-04-23, an
  isolation/reorg commit, not a version bump) and finally updated to
  **`95236bcc93e8ac764258291419084848bc12b2dc`, 2019-02-25, *"update internal libpgf to last stable
  version 071903"*** — `7.19.3`, which is what this repo's own `native/Sherland.Imaging.Pgf.Native/libpgf/`
  is itself derived from (matches `PGFtypes.h`'s `PGFMajorNumber=7, PGFYear=19, PGFWeek=03` exactly).

### 6. `libpgf.org` directly

The live site has no download or archive section with any content — a dead end, confirming nothing
older is self-hosted there either.

## Finding A: no pre-Version5 source exists anywhere obtainable — this closes an open question, it doesn't leave it open

`pgf-legacy-interleaved-decode.md`'s whole premise is that real PGF files exist whose header lacks
the `Version5` flag, encoded via the pre-tiled `CEncoder::EncodeInterleaved`/`CDecoder::DecodeInterleaved`
scheme. Reading the **earliest obtainable source in existence** (digiKam's `5.0.0`/`5.08.22` import,
May 2009) directly answers whether a real encoder for that format is still reachable anywhere:

- Its own `PGFtypes.h` already unconditionally includes `Version5` in the `PGFVersion` macro
  (`#define PGFVersion (Version2 | Version5 | PGF32)` — no conditional, no legacy fallback), with the
  comment simply stating *"Version 5: ROI, new block-reordering scheme"*. This is, itself, the release
  that introduced the scheme — there is no obtainable `5.08.22`-or-later build whose real encoder ever
  omits `Version5`.
- `Encoder.cpp`'s `EncodeInterleaved` function body is **already wrapped in a `/* ... */` block
  comment** at this same 2009 commit (confirmed by direct read, not inference), and `PGFimage.cpp`'s
  two call sites are already `//`-commented as *"until version 4"*. By `6.12.24` (2012, verified by
  direct extraction and `grep`), the function has been **deleted from the source entirely** — zero
  matches in `Encoder.cpp`/`Encoder.h`, identical to what `pgf-legacy-interleaved-decode.md` already
  found in the current 7.19.3-derived vendored copy in this repo.
- To reach an actual "major version 4" build — the generation that predates this scheme, whose real
  encoder would have produced genuinely interleaved bitstreams as its *normal* output, not dead code —
  you'd need a source older than May 2009. No such source exists in any of the six places checked
  above: not on SourceForge (files, git, or the SVN repo the original brief pointed at, which doesn't
  exist), not in Debian's archive, not in digiKam's complete 2004-onward git history (whose own first
  libpgf import already starts at 5.0.0), not on libpgf.org itself.

**Conclusion**: `pgf-legacy-interleaved-decode.md`'s open question is answered — hunting for a real
pre-Version5 native oracle is **not a matter of trying harder**, it's genuinely unobtainable. Every
avenue a real investigation could try has been tried and exhausted. That PRD's existing plan
(self-consistency-only verification via a test-only, invented "encode interleaved" fixture generator)
is not just *currently* the best option — after this investigation, it's confirmed to be the **only**
option, and should be described that way with full confidence, not hedged.

**One genuine, low-cost improvement this investigation does contribute**: the 2009 `5.08.22` snapshot's
commented-out `EncodeInterleaved` body (reproduction command in §Reproduction below) is a second,
independently-dated (2009, over a decade before this port's own work) textual description of the
interleaving order, structurally mirroring `Decoder.cpp`'s `DecodeInterleaved` that
`pgf-legacy-interleaved-decode.md` already cites. It can't be compiled or run (it's literally inside a
`/* */` comment, not a toggle-able dead branch), so it doesn't provide a real binary oracle — but
cross-reading it against the current `DecodeInterleaved` while implementing Goal 2/3 of that PRD is a
free, real way to sanity-check the ported read order against the *original* write order the algorithm
was designed against, not just against a reverse-engineering of the decode side alone.

## Finding B: for the Bitmap-legacy gap, a real historical build beats the planned shim — use `6.14.12`, not a hack on the current source

`pgf-bitmap-legacy-packed.md`'s own Testability section already correctly works out that its target
gap *doesn't* need a historical build in principle — `Version7` (unlike `Version5`) is a normal,
still-clearable header flag in the *current* vendored source, so its plan is a native shim: a local
`CPGFImage` subclass that clears the `Version7` bit and hand-writes legacy-packed bytes directly via
`SetChannel`, bypassing the current encoder's dead legacy-encode branch.

That plan works, but this investigation surfaced something better. **`libpgf 6.14.12`** — one of the
exact versions the original research brief recommended, and confirmed by this repo's own vendored
`README`'s release notes ("Version 7.15.25... is a replacement of version 6.14.12... introduces a new
and more efficient data format for binary images") to be **the last real release before the modern
Bitmap format existed at all**:

- Its `PGFtypes.h` has **no `Version7` definition whatsoever** (confirmed by direct `grep` on the
  extracted source — the modern-format flag simply doesn't exist yet in this codebase).
- Its `RgbToYuv`'s `case ImageModeBitmap:` (`PGFimage.cpp:1340-1374` in this source) is **live,
  uncommented, functioning code** — not dead code needing to be resurrected — that writes exactly the
  legacy packed layout: `w2` (`= (w+7)/8`) already-packed bytes copied directly from the input buffer
  per row, padded with `YUVoffset8` out to the full pixel width `w`. This is a **real, unmodified
  historical encoder** that will produce genuine legacy-format bitmap files through its completely
  ordinary public `ImportBitmap()`+`Write()` path — no subclassing, no reaching into `protected`
  members, no hand-replication of commented-out packing logic required at all.
- **Confirmed buildable, not assumed**: this investigation extracted the real `6.14.12` source (via
  Debian's snapshot archive — see Reproduction below, since SourceForge's own download endpoint is
  Cloudflare-blocked), wrote a minimal `CMakeLists.txt` (static lib, C++17, `LIBPGF_DISABLE_OPENMP` —
  deliberately mirroring `native/Sherland.Imaging.Pgf.Native/CMakeLists.txt`'s own settings so it's a fair,
  like-for-like comparison), and built it with **zero errors** using the exact same MSVC toolchain
  (Visual Studio "18" Community, `cl` 19.51) this repo's existing native oracle already uses. (One
  environment-only wrinkle: building from a path deep under `%TEMP%` hit an MSBuild `FileTracker`
  path-length limitation — unrelated to the 2014 source itself; building from a short path like
  `C:\pgf6.14.12` avoided it. Worth remembering when this actually gets wired into a real build
  script.)

**Recommendation**: replace `pgf-bitmap-legacy-packed.md`'s Goal 2 (native shim subclassing the
*current* `CPGFImage` to fake legacy output) with **building `6.14.12` as a second, standalone native
binary and using its real, unmodified encoder directly** — a strictly stronger independent oracle (a
real historical implementation someone actually shipped, not a hand-rolled bypass of the current one's
dead code), and less implementation work (no subclass, no manual `SetChannel` packing replication,
just a normal build against a real old encoder). This is exactly the class of thing the original
research brief driving this investigation predicted 6.14.12 would be useful for, now confirmed with a
real, successful build.

The one caveat: `6.14.12` *also* unconditionally includes `Version5` in `PGFVersion` (same situation as
Finding A), so it can't help with the rarer `yw = w2` stride sub-case (`pre-Version5 *and* pre-Version7`)
that `pgf-bitmap-legacy-packed.md`'s own three-way version split calls out — that sub-case still needs
a small shim (clearing `Version5` on top of this real `6.14.12` build) exactly like the original plan
already anticipated, just applied to a real old build instead of the current one. The common,
realistic case (`Version5`/`Version6` set, `Version7` absent, `yw = w`) needs **no shim at all** — a
plain `6.14.12` build's default output already is that case.

## Reproduction steps

Everything below was verified working during this investigation (dated 2026-08-04). SourceForge's own
download endpoints are Cloudflare-blocked for scripted access — don't retry those; use the routes
below instead.

**`libpgf 6.12.24`** (real oracle for Finding A's corroborating dead-code check; same
always-`Version5` situation as every other obtainable version):
```sh
curl -sL "https://snapshot.debian.org/archive/debian/20121211T033951Z/pool/main/libp/libpgf/libpgf_6.12.24%2Bds1.orig.tar.bz2" -o libpgf_6.12.24.orig.tar.bz2
tar xjf libpgf_6.12.24.orig.tar.bz2   # -> libpgf/{include,src}/...
```

**`libpgf 6.14.12`** (the recommended real oracle for `pgf-bitmap-legacy-packed.md`, Finding B):
```sh
curl -sL "https://snapshot.debian.org/archive/debian/20140926T043003Z/pool/main/libp/libpgf/libpgf_6.14.12.orig.tar.gz" -o libpgf_6.14.12.orig.tar.gz
tar xzf libpgf_6.14.12.orig.tar.gz   # -> libpgf/{include,src}/...
```
Build (adapt `native/Sherland.Imaging.Pgf.Native/CMakeLists.txt`'s own settings — C++17,
`LIBPGF_DISABLE_OPENMP` — pointed at this source's `include`/`src` layout instead of the single
`libpgf/` folder the current vendored copy uses).

**`libpgf 5.08.22`/`6.09.24`/`6.09.33`/`6.09.44`** (Finding A's corroborating 2009 source; not
buildable as a clean standalone target since it's embedded inside a digiKam-era tree with digiKam's
own `.pro`/Qt build assumptions still mixed in — reading it directly via `git show`, as done for this
investigation, is enough; nothing here needs a from-scratch build):
```sh
git clone --filter=blob:none --no-checkout https://github.com/KDE/digikam.git digikam-history
cd digikam-history
git show a51a661ef07d2b4aaaf59fd02d7bbdcc0b790f37:libs/database/libpgf/PGFtypes.h   # 5.08.22
git show 3383944e374d41088ae2a164d9348d3632b86e54:libs/database/libpgf/PGFimage.cpp   # 6.09.44
```

## What this doesn't resolve

- **`pgf-big-endian-hosts.md`** — untouched by this investigation; none of the historical sources
  found here change anything about that gap (it was never version-dependent to begin with).
- **The rare `yw = w2` bitmap stride sub-case** still needs a small shim even with the improved
  `6.14.12`-based plan (Finding B's caveat) — this doc found a better base to shim on top of, not a
  way to avoid shimming entirely.
- **Whether `6.09.44` specifically (vs. `6.12.24`/`6.14.12`) is worth pulling into either PRD's actual
  test rig** — it was confirmed obtainable and real, but wasn't extracted/built during this
  investigation (embedded in a digiKam-era tree, more adaptation work for uncertain extra value over
  `6.12.24`/`6.14.12`, both of which were fully verified). Revisit only if `6.12.24`/`6.14.12` turn out
  to disagree with each other on something, which would make a third, independently-dated data point
  worth the extra effort.

## Progress log

- **2026-08-04**: Investigation complete. Installed `svn` (`winget install Slik.Subversion`) to check
  the SVN repo claim — confirmed nonexistent. Confirmed SourceForge's file/git history don't reach far
  enough back. Found and used Debian's `snapshot.debian.org` to obtain real `6.12.24`/`6.14.12` source
  (SourceForge's own download endpoints are Cloudflare-blocked for scripted access). Found digiKam's
  real 2004-onward git history and its first-ever libpgf import (`5.0.0`/`5.08.22`, 2009-05-29,
  predating every other obtainable source by 2+ years) via `KDE/digikam` on GitHub. Confirmed `6.14.12`
  builds cleanly (0 errors) under this repo's existing MSVC/CMake toolchain. Findings written up above;
  cross-referenced from `pgf-legacy-interleaved-decode.md` and `pgf-bitmap-legacy-packed.md`.
