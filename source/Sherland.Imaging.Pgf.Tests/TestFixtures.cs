// SPDX-License-Identifier: MIT
// Copyright (C) 2026 Steinar Herland.

namespace Sherland.Imaging.Pgf.Tests;

/// <summary>Shared paths to committed real-world fixtures, resolved relative to the repo root (not
/// the test output directory) so they work regardless of build configuration.</summary>
internal static class TestFixtures
{
    public static string SampleThumbnailPath =>
        Path.Combine(RepoRoot, "data", "test-digikam", "sample-thumbnail.pgf");

    private static string RepoRoot
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sherland.Imaging.Pgf.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? AppContext.BaseDirectory;
        }
    }
}
