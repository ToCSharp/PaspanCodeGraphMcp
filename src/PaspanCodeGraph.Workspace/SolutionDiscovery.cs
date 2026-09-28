using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PaspanCodeGraph.Workspace;

/// <param name="RootPath">The solution, project or compilation database that was loaded, or the directory of a C++ folder.</param>
/// <param name="Projects">
/// Full paths of the projects it lists: .csproj and .vcxproj files, a compile_commands.json, or the directory of
/// a C++ folder.
/// </param>
public sealed record DiscoveryResult(string RootPath, IReadOnlyList<string> Projects, IReadOnlyList<LoadProblem> Problems);

/// <summary>
/// Turns a .sln, .slnx, .csproj or .vcxproj file, a compile_commands.json, or a directory holding one of them (or
/// holding C++ sources, loaded as a folder), into the projects to load.
/// </summary>
public static partial class SolutionDiscovery
{
    public static DiscoveryResult Discover(string path)
    {
        path = Path.GetFullPath(path);
        var problems = new List<LoadProblem>();

        if (Directory.Exists(path))
        {
            var folder = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (folder.Length == 0)
            {
                folder = path;
            }

            var file = Directory.EnumerateFiles(folder, "*.slnx")
                .Concat(Directory.EnumerateFiles(folder, "*.sln"))
                .Concat(Directory.EnumerateFiles(folder, "*.csproj"))
                .Concat(Directory.EnumerateFiles(folder, "*.vcxproj"))
                .FirstOrDefault();
            if (file == null)
            {
                // A C++ workspace without project files: a compilation database, else the folder's sources
                if (CppFiles.FindCompilationDatabase(folder) is { } database)
                {
                    return new DiscoveryResult(folder, [database], problems);
                }

                if (CppFiles.HasSources(folder))
                {
                    return new DiscoveryResult(folder, [folder], problems);
                }

                throw new FileNotFoundException($"No .slnx, .sln, .csproj, .vcxproj, compile_commands.json or C++ sources found in directory: {path}");
            }

            path = file;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Workspace file not found: {path}");
        }

        if (Path.GetFileName(path).Equals("compile_commands.json", StringComparison.OrdinalIgnoreCase))
        {
            return new DiscoveryResult(path, [path], problems);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        IEnumerable<string> listed = extension switch
        {
            ".csproj" or ".vcxproj" => [path],
            ".slnx" => ReadSlnx(path),
            ".sln" => ReadSln(path),
            _ => throw new ArgumentException($"Unsupported workspace file type '{extension}'. Expected .sln, .slnx, .csproj, .vcxproj or compile_commands.json."),
        };

        var directory = Path.GetDirectoryName(path)!;
        var projects = new List<string>();
        foreach (var relative in listed)
        {
            var full = Path.GetFullPath(Path.Combine(directory, NormalizeSeparators(relative)));
            if (!full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
            {
                // Shared projects come in through the <Import> of their .projitems; other project types are not C# or C++
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
            problems.Add(LoadProblem.Error($"The solution lists no existing .csproj or .vcxproj projects: {path}", path));
        }

        return new DiscoveryResult(path, projects, problems);
    }

    /// <summary>
    /// The directory of a workspace, which relative paths and the graph cache are relative to: the folder of a C++
    /// folder, the directory of a solution or project, the source directory above the build directory of a
    /// compilation database.
    /// </summary>
    public static string WorkspaceDirectory(string rootPath)
    {
        rootPath = Path.GetFullPath(rootPath);
        if (Directory.Exists(rootPath))
        {
            return rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is { Length: > 0 } trimmed ? trimmed : rootPath;
        }

        var directory = Path.GetDirectoryName(rootPath)!;
        if (Path.GetFileName(rootPath).Equals("compile_commands.json", StringComparison.OrdinalIgnoreCase))
        {
            // build/, out/build/<preset>/ or cmake-build-debug/ under the sources
            for (var i = 0; i < 3; i++)
            {
                var name = Path.GetFileName(directory);
                var isBuild = File.Exists(Path.Combine(directory, "CMakeCache.txt")) || name.StartsWith("build", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("cmake-build", StringComparison.OrdinalIgnoreCase) || name == "out";
                if (!isBuild || Path.GetDirectoryName(directory) is not { } parent)
                {
                    break;
                }

                directory = parent;
                if (File.Exists(Path.Combine(directory, "CMakeLists.txt")) || Directory.Exists(Path.Combine(directory, ".git")))
                {
                    break;
                }
            }
        }

        return directory;
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
