namespace PictTag.PgfCodec.Tests;

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
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PictTag.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? AppContext.BaseDirectory;
        }
    }
}
