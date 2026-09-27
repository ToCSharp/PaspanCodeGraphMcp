namespace PaspanCodeGraph.Tests;

internal static class TestPaths
{
    /// <summary>The directory of PaspanCodeGraphMcp.slnx, found above the test binaries.</summary>
    public static string RepositoryRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PaspanCodeGraphMcp.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("PaspanCodeGraphMcp.slnx not found above " + AppContext.BaseDirectory);
    }
}
