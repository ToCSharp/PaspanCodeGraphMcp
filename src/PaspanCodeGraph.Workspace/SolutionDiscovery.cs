using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PaspanCodeGraph.Workspace;

/// <param name="RootPath">The solution or project file that was loaded.</param>
/// <param name="Projects">Full paths of the C# projects it lists.</param>
public sealed record DiscoveryResult(string RootPath, IReadOnlyList<string> Projects, IReadOnlyList<LoadProblem> Problems);

/// <summary>Turns a .sln, .slnx or .csproj file, or a directory holding one, into the C# projects to load.</summary>
public static partial class SolutionDiscovery
{
    public static DiscoveryResult Discover(string path)
    {
        path = Path.GetFullPath(path);
        var problems = new List<LoadProblem>();

        if (Directory.Exists(path))
        {
            path = Directory.EnumerateFiles(path, "*.slnx")
                .Concat(Directory.EnumerateFiles(path, "*.sln"))
                .Concat(Directory.EnumerateFiles(path, "*.csproj"))
                .FirstOrDefault()
                ?? throw new FileNotFoundException($"No .slnx, .sln or .csproj found in directory: {path}");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Workspace file not found: {path}");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        IEnumerable<string> listed = extension switch
        {
            ".csproj" => [path],
            ".slnx" => ReadSlnx(path),
            ".sln" => ReadSln(path),
            _ => throw new ArgumentException($"Unsupported workspace file type '{extension}'. Expected .sln, .slnx or .csproj."),
        };

        var directory = Path.GetDirectoryName(path)!;
        var projects = new List<string>();
        foreach (var relative in listed)
        {
            var full = Path.GetFullPath(Path.Combine(directory, NormalizeSeparators(relative)));
            if (!full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                // Shared projects come in through the <Import> of their .projitems; other project types are not C#
                continue;
            }

            if (!File.Exists(full))
            {
                problems.Add(LoadProblem.Warning($"Project listed in the solution does not exist: {full}", path));
                continue;
            }

            if (!projects.Contains(full, SymbolIndexBuilder.PathComparer))
            {
                projects.Add(full);
            }
        }

        if (projects.Count == 0)
        {
            problems.Add(LoadProblem.Error($"The solution lists no existing .csproj projects: {path}", path));
        }

        return new DiscoveryResult(path, projects, problems);
    }

    /// <summary>MSBuild paths use '\' on every platform.</summary>
    internal static string NormalizeSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static IEnumerable<string> ReadSlnx(string path) =>
        XDocument.Load(path).Descendants()
            .Where(e => e.Name.LocalName == "Project")
            .Select(e => (string?)e.Attribute("Path"))
            .OfType<string>();

    private static IEnumerable<string> ReadSln(string path) =>
        SlnProjectLine().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value);

    // Project("{type guid}") = "Name", "relative\path.csproj", "{project guid}"
    [GeneratedRegex(@"^Project\(""\{[^}]*\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectLine();
}
