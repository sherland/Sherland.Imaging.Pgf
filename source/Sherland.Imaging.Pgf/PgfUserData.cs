// SPDX-License-Identifier: LGPL-2.1-or-later
// Copyright (C) 2006 xeraina GmbH. Portions Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf;

/// <summary>Direct port of <c>UserdataPolicy</c> (PGFtypes.h:101) - chosen at decode time (mirroring
/// <c>CPGFImage::ConfigureDecoder</c>, PGFimage.h:260) to control how much of a file's post-header
/// user data actually gets buffered. Every existing call site keeps its current "just works" default
/// (<see cref="CacheAll"/>, matching the native default) - this is opt-out API surface for a caller
/// that wants to avoid paying to buffer metadata it doesn't care about (pgf-user-data-and-small-
/// images.md Goal 2), not a required decision.</summary>
public enum PgfUserDataPolicy
{
    /// <summary>Skip and discard all user data - matches <c>UP_Skip</c> (0). Cheapest option for a
    /// caller that never reads <see cref="PgfUserData"/>.</summary>
    Skip = 0,

    /// <summary>Cache only the first <c>prefixSize</c> bytes - matches <c>UP_CachePrefix</c> (1).
    /// <see cref="PgfUserData.TotalLength"/> still reports the real total even when only a prefix
    /// was cached (mirrors <c>GetUserData</c>'s own <c>pTotalSize</c> out-parameter,
    /// PGFimage.cpp:337-341).</summary>
    CachePrefix = 1,

    /// <summary>Cache the entire user data block - matches <c>UP_CacheAll</c> (2), and is this port's
    /// own default (<c>ConfigureDecoder</c>'s own default parameter value).</summary>
    CacheAll = 2,
}

/// <summary>A decoded file's post-header user data (PGFPostHeader's <c>userData</c>/<c>userDataLen</c>/
/// <c>cachedUserDataLen</c>, PGFtypes.h:173-178) - the metadata half of <c>PGFPostHeader</c> not
/// already owned by pgf-all-image-modes.md's color-table support. <see cref="CachedBytes"/> may be
/// shorter than <see cref="TotalLength"/> under <see cref="PgfUserDataPolicy.CachePrefix"/> (or empty
/// under <see cref="PgfUserDataPolicy.Skip"/>, or when the file simply has no user data at all -
/// these two zero-length cases are indistinguishable from this struct alone, same as the native
/// <c>GetUserData</c>'s own return contract).</summary>
public readonly record struct PgfUserData(byte[] CachedBytes, uint TotalLength)
{
    /// <summary>No user data present, or none cached - the default returned by every decode path
    /// when the file's post-header carries nothing beyond an optional color table.</summary>
    public static PgfUserData None { get; } = new([], 0);
}
