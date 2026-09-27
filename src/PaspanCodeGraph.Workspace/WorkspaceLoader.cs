using System.Collections.Concurrent;
using System.Diagnostics;
using Paspan;
using PaspanCodeGraph.CSharp;
using PaspanCodeGraph.Metadata;
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

    /// <summary>The types of the assemblies the projects reference (framework, packages); empty when loaded without them.</summary>
    public MetadataCatalog Metadata { get; init; } = MetadataCatalog.Empty;

    /// <summary>The assemblies each project references, by project path.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> References { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

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
    /// <param name="readReferences">Read the assemblies the projects reference, so that their types and members bind.</param>
    public static WorkspaceSnapshot Load(string path, string configuration = "Debug", string platform = "AnyCPU", CancellationToken cancellationToken = default, bool readReferences = true)
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

        // The referenced assemblies of all projects, read into one catalog while the sources are parsed
        var metadata = MetadataCatalog.Empty;
        var referencesByProject = new Dictionary<string, IReadOnlyList<string>>(SymbolIndexBuilder.PathComparer);
        if (readReferences)
        {
            var paths = new List<string>();
            foreach (var project in projects)
            {
                var resolved = ReferenceAssemblies.Resolve(project, problems);
                referencesByProject[project.Path] = resolved;
                paths.AddRange(resolved);
            }

            var unreadable = new List<string>();
            metadata = MetadataCatalog.Load(paths, unreadable);
            problems.AddRange(unreadable.Select(u => LoadProblem.Warning(u)));
        }

        var builder = new SymbolIndexBuilder();
        var binder = new CSharpBinder(builder) { Metadata = metadata };
        foreach (var project in projects)
        {
            var scope = binder.ProjectScope(project.Name);
            foreach (var ns in project.Usings)
            {
                scope.AddNamespace(ns);
            }
        }

        var sources = documents.Values
            .Where(d => d.Unit != null)
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .Select(d => new CSharpSource(d.Path, d.Project, d.Utf8, d.Lines, d.Unit!))
            .ToList();

        // Every type first, so that names in member signatures and base lists bind to types of any file
        foreach (var source in sources)
        {
            CSharpSymbolCollector.Collect(source, builder, binder, CollectPass.Types);
        }

        foreach (var source in sources)
        {
            CSharpSymbolCollector.Collect(source, builder, binder, CollectPass.Members);
        }

        CSharpHierarchy.Link(builder, binder);

        var references = new List<(string TargetId, SymbolReference Reference)>[sources.Count];
        Parallel.For(
            0,
            sources.Count,
            new ParallelOptions { CancellationToken = cancellationToken },
            i => references[i] = CSharpSymbolCollector.Collect(sources[i], builder, binder, CollectPass.References));
        foreach (var list in references)
        {
            foreach (var (target, reference) in list)
            {
                builder.AddReference(target, reference);
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
            Metadata = metadata,
            References = referencesByProject,
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
