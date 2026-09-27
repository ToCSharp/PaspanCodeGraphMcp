using System.Collections.Concurrent;
using System.Diagnostics;
using Paspan;
using PaspanCodeGraph.CSharp;
using PaspanParsers.CSharp;

namespace PaspanCodeGraph.Workspace;

/// <summary>A source file of the workspace and its parse.</summary>
/// <param name="Project">The project the file was parsed for (the first one for files linked into several).</param>
/// <param name="Unit">The syntax tree, or null when the file could not be read or parsed.</param>
/// <param name="Failure">Why there is no tree.</param>
public sealed record SourceDocument(string Path, string Project, ReadOnlyMemory<byte> Utf8, LineMap Lines, CompilationUnit? Unit, string? Failure)
{
    public IReadOnlyList<SyntaxError> Errors => Unit?.Errors ?? [];
}

/// <summary>A loaded workspace. Immutable: a reload builds a new snapshot.</summary>
public sealed class WorkspaceSnapshot
{
    public required string RootPath { get; init; }

    public required string Configuration { get; init; }

    public required IReadOnlyList<ProjectModel> Projects { get; init; }

    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required SymbolIndex Index { get; init; }

    /// <summary>Problems of discovery and of reading projects; parse errors are in <see cref="SourceDocument.Errors"/>.</summary>
    public required IReadOnlyList<LoadProblem> Problems { get; init; }

    public required DateTimeOffset LoadedAt { get; init; }

    public required TimeSpan Elapsed { get; init; }

    /// <summary>The document at <paramref name="path"/>, which may be relative to the workspace directory.</summary>
    public SourceDocument? FindDocument(string path)
    {
        if (Documents.TryGetValue(Path.GetFullPath(path), out var document))
        {
            return document;
        }

        var relative = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RootPath)!, path));
        if (Documents.TryGetValue(relative, out document))
        {
            return document;
        }

        // A file name or a trailing part of a path, when only one document ends with it
        var suffix = SolutionDiscovery.NormalizeSeparators(path).TrimStart(System.IO.Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = Documents.Values
            .Where(d => d.Path.EndsWith(System.IO.Path.DirectorySeparatorChar + suffix, comparison))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}

/// <summary>Loads a workspace: finds its projects, reads them, parses their sources in parallel and indexes the declarations.</summary>
public static class WorkspaceLoader
{
    public static WorkspaceSnapshot Load(string path, string configuration = "Debug", string platform = "AnyCPU", CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var discovery = SolutionDiscovery.Discover(path);
        var problems = new List<LoadProblem>(discovery.Problems);

        // The listed projects and, transitively, the projects they reference
        var projects = new List<ProjectModel>();
        var seen = new HashSet<string>(discovery.Projects, SymbolIndexBuilder.PathComparer);
        var queue = new Queue<string>(discovery.Projects);
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectPath = queue.Dequeue();
            ProjectModel project;
            try
            {
                project = ProjectFileReader.Read(projectPath, configuration, platform);
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException or InvalidDataException)
            {
                problems.Add(LoadProblem.Error($"Could not read project: {e.Message}", projectPath));
                continue;
            }

            projects.Add(project);
            problems.AddRange(project.Problems);
            foreach (var reference in project.ProjectReferences)
            {
                if (!seen.Add(reference))
                {
                    continue;
                }

                if (File.Exists(reference))
                {
                    queue.Enqueue(reference);
                }
                else
                {
                    problems.Add(LoadProblem.Warning($"{project.Name}: referenced project does not exist: {reference}", project.Path));
                }
            }
        }

        // A file compiled by several projects is parsed once, with the options of the first
        var files = new Dictionary<string, ProjectModel>(SymbolIndexBuilder.PathComparer);
        foreach (var project in projects)
        {
            foreach (var source in project.Sources)
            {
                files.TryAdd(source, project);
            }
        }

        var options = projects.ToDictionary(
            p => p,
            p => new CSharpParseOptions(p.LanguageVersion, p.PreprocessorSymbols, errorRecovery: true));

        var documents = new ConcurrentDictionary<string, SourceDocument>(SymbolIndexBuilder.PathComparer);
        Parallel.ForEach(
            files,
            new ParallelOptions { CancellationToken = cancellationToken },
            file => documents[file.Key] = ParseFile(file.Key, file.Value.Name, options[file.Value]));

        var builder = new SymbolIndexBuilder();
        foreach (var document in documents.Values.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            if (document.Unit is { } unit)
            {
                CSharpSymbolCollector.Collect(new CSharpSource(document.Path, document.Project, document.Utf8, document.Lines, unit), builder);
            }
        }

        return new WorkspaceSnapshot
        {
            RootPath = discovery.RootPath,
            Configuration = configuration,
            Projects = projects,
            Documents = new Dictionary<string, SourceDocument>(documents, SymbolIndexBuilder.PathComparer),
            Index = builder.Build(),
            Problems = problems,
            LoadedAt = DateTimeOffset.UtcNow,
            Elapsed = stopwatch.Elapsed,
        };
    }

    /// <summary>Reads and parses one file, with error recovery.</summary>
    public static SourceDocument ParseFile(string path, string project, CSharpParseOptions options)
    {
        ReadOnlyMemory<byte> utf8;
        try
        {
            utf8 = CSharpParser.GetUtf8Source(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SourceDocument(path, project, ReadOnlyMemory<byte>.Empty, new LineMap([]), null, e.Message);
        }

        var lines = new LineMap(utf8.Span);
        try
        {
            if (CSharpParser.TryParse(utf8, options, out var unit, out var error))
            {
                return new SourceDocument(path, project, utf8, lines, unit, null);
            }

            return new SourceDocument(path, project, utf8, lines, null, Describe(error));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A parser bug must not take the whole workspace down
            return new SourceDocument(path, project, utf8, lines, null, $"Parser failure: {e.GetType().Name}: {e.Message}");
        }
    }

    private static string Describe(ParseError? error) => error?.ToString() ?? "The file could not be parsed.";
}
