using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Paspan;
using PaspanCodeGraph.Cpp;
using PaspanCodeGraph.CSharp;
using PaspanCodeGraph.Metadata;
using PaspanParsers;
using PaspanParsers.CSharp;
using PaspanParsers.Cpp;

namespace PaspanCodeGraph.Workspace;

/// <summary>A source file of the workspace and its parse.</summary>
/// <param name="Project">The project the file was parsed for (the first one for files linked into several).</param>
/// <param name="Unit">
/// The syntax tree, or null when the file could not be read or parsed, or when the snapshot was read from the graph
/// cache and the file has not been parsed yet.
/// </param>
/// <param name="Failure">Why there is no tree.</param>
public sealed record SourceDocument(string Path, string Project, ReadOnlyMemory<byte> Utf8, LineMap Lines, CompilationUnit? Unit, string? Failure)
{
    /// <summary>The syntax errors of a file read from the graph cache, which has no tree until it is parsed.</summary>
    public IReadOnlyList<SyntaxError>? CachedErrors { get; init; }

    public IReadOnlyList<SyntaxError> Errors => Unit?.Errors ?? CachedErrors ?? [];

    /// <summary>The syntax tree of a C++ file (the C++ parser has no error recovery: a file with an error has none).</summary>
    public TranslationUnit? CppUnit { get; init; }

    public SourceLanguage Language { get; init; }

    /// <summary>C++: the file parsed only with the function-like macros that stand for syntax expanded.</summary>
    public bool CppExpandedFunctions { get; init; }

    /// <summary>C++: takes the offsets of <see cref="CppUnit"/> back to the file when macros were expanded; null when they are the same.</summary>
    public PaspanCodeGraph.Cpp.CppOffsetMap? CppMap { get; init; }

    /// <summary>Whether the document has a tree, or a failure that parsing again would repeat.</summary>
    public bool IsParsed => Unit != null || CppUnit != null || Failure != null;
}

/// <summary>How a snapshot was made from the one before it.</summary>
public enum SnapshotKind
{
    /// <summary>Every file parsed and bound.</summary>
    Full,

    /// <summary>Changed files parsed again; the files that can bind to what changed bound again.</summary>
    Incremental,

    /// <summary>Nothing changed since the snapshot before.</summary>
    Unchanged,

    /// <summary>Read from the graph cache, without parsing.</summary>
    Cache,
}

/// <summary>A loaded workspace. Immutable: a reload or an update builds a new snapshot.</summary>
public sealed class WorkspaceSnapshot
{
    public required string RootPath { get; init; }

    public required string Configuration { get; init; }

    public string Platform { get; init; } = "AnyCPU";

    /// <summary>Whether the referenced assemblies were read.</summary>
    public bool ReadReferences { get; init; } = true;

    public required IReadOnlyList<ProjectModel> Projects { get; init; }

    public required IReadOnlyDictionary<string, SourceDocument> Documents { get; init; }

    public required SymbolIndex Index { get; init; }

    /// <summary>Problems of discovery and of reading projects; parse errors are in <see cref="SourceDocument.Errors"/>.</summary>
    public required IReadOnlyList<LoadProblem> Problems { get; init; }

    /// <summary>The types of the assemblies the projects reference (framework, packages); empty when loaded without them.</summary>
    public MetadataCatalog Metadata { get; init; } = MetadataCatalog.Empty;

    /// <summary>The assemblies each project references, by project path.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> References { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>What an update keeps of each file, by path.</summary>
    public IReadOnlyDictionary<string, FileState> Files { get; init; } = new Dictionary<string, FileState>();

    /// <summary>What binding depends on in each project file (everything but its sources), by project path.</summary>
    public IReadOnlyDictionary<string, string> ProjectPrints { get; init; } = new Dictionary<string, string>();

    /// <summary>The referenced assembly files with their sizes and times, so that an update knows when to read them again.</summary>
    public string MetadataKey { get; init; } = "";

    /// <summary>The assemblies that could not be read, kept for the updates that reuse <see cref="Metadata"/>.</summary>
    public IReadOnlyList<LoadProblem> MetadataProblems { get; init; } = [];

    /// <summary>False for a snapshot read from the graph cache before its referenced assemblies are read.</summary>
    public bool MetadataLoaded { get; init; } = true;

    public SnapshotKind Kind { get; init; } = SnapshotKind.Full;

    /// <summary>
    /// What the second parse of the C++ files depended on (the macros blanked out and the names of the workspace):
    /// when it changes, every C++ file is parsed again.
    /// </summary>
    public string CppParseKey { get; init; } = "";

    /// <summary>The directory relative paths and the graph cache are relative to.</summary>
    public string Directory => SolutionDiscovery.WorkspaceDirectory(RootPath);

    /// <summary>The files parsed to make this snapshot.</summary>
    public int ParsedFiles { get; init; }

    /// <summary>The files whose references were found again to make this snapshot.</summary>
    public int BoundFiles { get; init; }

    public required DateTimeOffset LoadedAt { get; init; }

    public required TimeSpan Elapsed { get; init; }

    internal WorkspaceSnapshot With(TimeSpan elapsed) => new()
    {
        RootPath = RootPath,
        Configuration = Configuration,
        Platform = Platform,
        ReadReferences = ReadReferences,
        Projects = Projects,
        Documents = Documents,
        Index = Index,
        Problems = Problems,
        Metadata = Metadata,
        References = References,
        Files = Files,
        ProjectPrints = ProjectPrints,
        MetadataKey = MetadataKey,
        MetadataProblems = MetadataProblems,
        MetadataLoaded = MetadataLoaded,
        Kind = Kind,
        CppParseKey = CppParseKey,
        ParsedFiles = ParsedFiles,
        BoundFiles = BoundFiles,
        LoadedAt = LoadedAt,
        Elapsed = elapsed,
    };

    /// <summary>The document at <paramref name="path"/>, which may be relative to the workspace directory.</summary>
    public SourceDocument? FindDocument(string path)
    {
        if (Documents.TryGetValue(Path.GetFullPath(path), out var document))
        {
            return document;
        }

        var relative = Path.GetFullPath(Path.Combine(Directory, path));
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

/// <summary>
/// Loads a workspace: finds its projects, reads them, parses their sources in parallel and indexes the declarations.
/// An update does the same from an earlier snapshot, parsing only the files that changed and binding again only
/// the files that can bind to a changed declaration.
/// </summary>
public static class WorkspaceLoader
{
    /// <param name="readReferences">Read the assemblies the projects reference, so that their types and members bind.</param>
    public static WorkspaceSnapshot Load(string path, string configuration = "Debug", string platform = "AnyCPU", CancellationToken cancellationToken = default, bool readReferences = true) =>
        Build(path, configuration, platform, readReferences, null, cancellationToken);

    /// <summary>
    /// Loads a workspace from the graph cache at <paramref name="cacheFile"/> when there is one for it, updating
    /// what changed on disk since it was written, else in full; then writes the cache when the graph changed.
    /// </summary>
    /// <param name="cacheStatus">What was done: read from the cache, updated from it, or why it was not used.</param>
    public static WorkspaceSnapshot LoadCached(string path, string cacheFile, out string cacheStatus, string configuration = "Debug", string platform = "AnyCPU", CancellationToken cancellationToken = default, bool readReferences = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var root = SolutionDiscovery.Discover(path).RootPath;
        var cached = GraphCache.TryRead(cacheFile, root, configuration, platform, readReferences, out var reason);
        var snapshot = Build(root, configuration, platform, readReferences, cached, cancellationToken);
        snapshot = snapshot.With(stopwatch.Elapsed);
        cacheStatus = cached == null ? $"loaded in full ({reason})" : snapshot.Kind == SnapshotKind.Cache ? "read from the cache" : $"read from the cache, {snapshot.ParsedFiles} files parsed and {snapshot.BoundFiles} bound again";
        if (snapshot.Kind != SnapshotKind.Cache)
        {
            TrySave(snapshot, cacheFile);
        }

        return snapshot;
    }

    /// <summary>Writes the graph cache; a cache that cannot be written only costs the next start its speed.</summary>
    public static bool TrySave(WorkspaceSnapshot snapshot, string cacheFile)
    {
        try
        {
            GraphCache.Save(snapshot, cacheFile);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Brings <paramref name="previous"/> up to date with the files on disk: projects and the solution are read
    /// again, changed files parsed again, and the references of a file found again when it changed or mentions
    /// the name of a declaration that changed. A change to a project's options, to the referenced assemblies, to a
    /// type's bases or to members that code uses without naming them binds every file again.
    /// </summary>
    public static WorkspaceSnapshot Update(WorkspaceSnapshot previous, CancellationToken cancellationToken = default) =>
        Build(previous.RootPath, previous.Configuration, previous.Platform, previous.ReadReferences, previous, cancellationToken);

    private static WorkspaceSnapshot Build(string path, string configuration, string platform, bool readReferences, WorkspaceSnapshot? previous, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var discovery = SolutionDiscovery.Discover(path);
        var problems = new List<LoadProblem>(discovery.Problems);
        var projects = ReadProjects(discovery, configuration, platform, problems, cancellationToken);
        var projectPrints = projects.ToDictionary(p => p.Path, ProjectPrint, SymbolIndexBuilder.PathComparer);

        // A file compiled by several projects is parsed once, with the options of the first
        var files = new Dictionary<string, ProjectModel>(SymbolIndexBuilder.PathComparer);
        foreach (var project in projects)
        {
            foreach (var source in project.Sources)
            {
                files.TryAdd(source, project);
            }
        }

        // Sources made from other files (the C# of XAML) are not read from disk
        var generated = new Dictionary<string, string>(SymbolIndexBuilder.PathComparer);
        foreach (var project in projects)
        {
            foreach (var (file, code) in project.GeneratedSources)
            {
                generated.TryAdd(file, code);
            }
        }

        var contents = new ConcurrentDictionary<string, (ReadOnlyMemory<byte> Utf8, string Hash, string? Failure)>(SymbolIndexBuilder.PathComparer);
        Parallel.ForEach(
            files.Keys,
            new ParallelOptions { CancellationToken = cancellationToken },
            file => contents[file] = generated.TryGetValue(file, out var code) ? Generated(code) : ReadSource(file));

        // The referenced assemblies of all projects, in one catalog
        var referencesByProject = new Dictionary<string, IReadOnlyList<string>>(SymbolIndexBuilder.PathComparer);
        var metadataKey = "";
        if (readReferences)
        {
            var paths = new List<string>();
            foreach (var project in projects.Where(p => p.Language == SourceLanguage.CSharp))
            {
                var resolved = ReferenceAssemblies.Resolve(project, problems);
                referencesByProject[project.Path] = resolved;
                paths.AddRange(resolved);
            }

            metadataKey = MetadataKey(paths);
        }

        var sameProjects = previous != null
            && previous.ProjectPrints.Count == projectPrints.Count
            && projectPrints.All(p => previous.ProjectPrints.TryGetValue(p.Key, out var print) && print == p.Value);
        var sameMetadata = previous != null && previous.MetadataKey == metadataKey;
        var metadata = MetadataCatalog.Empty;
        IReadOnlyList<LoadProblem> metadataProblems = [];
        if (sameMetadata && previous!.MetadataLoaded)
        {
            metadata = previous.Metadata;
            metadataProblems = previous.MetadataProblems;
        }
        else if (readReferences)
        {
            var unreadable = new List<string>();
            metadata = MetadataCatalog.Load(referencesByProject.Values.SelectMany(r => r), unreadable);
            metadataProblems = unreadable.Select(u => LoadProblem.Warning(u)).ToList();
        }

        problems.AddRange(metadataProblems);

        var rebindAll = previous == null || !sameProjects || !sameMetadata;
        var changed = new HashSet<string>(SymbolIndexBuilder.PathComparer);
        foreach (var (file, project) in files)
        {
            if (previous == null
                || previous.Documents.GetValueOrDefault(file) is not { } document
                || document.Project != project.Name
                || previous.Files.GetValueOrDefault(file)?.Hash != contents[file].Hash)
            {
                changed.Add(file);
            }
        }

        var removed = previous?.Documents.Keys.Count(k => !files.ContainsKey(k)) ?? 0;
        if (!rebindAll && changed.Count == 0 && removed == 0)
        {
            return Unchanged(previous!, discovery, projects, problems, metadata, metadataProblems, referencesByProject, contents, stopwatch);
        }

        // Files that changed are parsed again, and so are files read from the cache, whose trees binding needs
        var options = projects.Where(p => p.Language == SourceLanguage.CSharp).ToDictionary(
            p => p,
            p => new CSharpParseOptions(p.LanguageVersion, p.PreprocessorSymbols, errorRecovery: true));
        var documents = new ConcurrentDictionary<string, SourceDocument>(SymbolIndexBuilder.PathComparer);
        var parsed = 0;
        bool Kept(string file, ProjectModel project, out SourceDocument document)
        {
            document = null!;
            if (changed.Contains(file)
                || previous!.ProjectPrints.GetValueOrDefault(project.Path) != projectPrints[project.Path]
                || previous.Documents[file] is not { IsParsed: true } kept)
            {
                return false;
            }

            var content = contents[file];
            document = kept.Utf8.IsEmpty && !content.Utf8.IsEmpty ? kept with { Utf8 = content.Utf8, Lines = Lines(content.Utf8, kept.Language) } : kept;
            return true;
        }

        Parallel.ForEach(
            files.Where(f => f.Value.Language == SourceLanguage.CSharp),
            new ParallelOptions { CancellationToken = cancellationToken },
            file =>
            {
                if (Kept(file.Key, file.Value, out var kept))
                {
                    documents[file.Key] = kept;
                    return;
                }

                documents[file.Key] = Parse(file.Key, file.Value.Name, contents[file.Key], options[file.Value]);
                Interlocked.Increment(ref parsed);
            });

        var cpp = ParseCpp(files, contents, changed, previous, projectPrints, documents, Kept, ref parsed, cancellationToken);
        rebindAll |= cpp.ParsedAgain;

        var builder = new SymbolIndexBuilder();
        var binder = new CSharpBinder(builder) { Metadata = metadata };
        foreach (var project in projects)
        {
            var scope = binder.ProjectScope(project.Name);
            binder.SetLanguageVersion(project.Name, project.LanguageVersion);
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

        var cppSources = cpp.Sources(documents);
        var cppBinder = new CppBinder(builder);
        CppSymbolCollector.Declare(cppSources, builder, cppBinder);

        var declarations = IncrementalState.Declarations(builder.Symbols);
        foreach (var source in sources)
        {
            foreach (var directive in source.Unit.Usings ?? [])
            {
                if (directive.IsGlobal)
                {
                    if (!declarations.TryGetValue(source.Path, out var list))
                    {
                        declarations[source.Path] = list = [];
                    }

                    list.Add(IncrementalState.GlobalUsing(directive.Span.GetText(source.Utf8.Span)));
                }
            }
        }

        foreach (var source in cppSources)
        {
            // The using-directives a file sees through its includes change lookup under names it need not mention
            if (cppBinder.FileUsings(source.Path) is { Count: > 0 } usings)
            {
                if (!declarations.TryGetValue(source.Path, out var list))
                {
                    declarations[source.Path] = list = [];
                }

                list.Add(IncrementalState.FileUsings(string.Join(",", usings.Select(u => u.Id))));
            }
        }

        var changedNames = rebindAll
            ? null
            : IncrementalState.ChangedNames(
                previous!.Files.ToDictionary(f => f.Key, f => f.Value.Declarations, SymbolIndexBuilder.PathComparer),
                declarations.ToDictionary(d => d.Key, d => (IReadOnlyList<DeclarationPrint>)d.Value, SymbolIndexBuilder.PathComparer));

        var names = new Dictionary<string, IReadOnlySet<string>>(SymbolIndexBuilder.PathComparer);
        foreach (var file in files.Keys)
        {
            names[file] = !changed.Contains(file) && previous!.Files.TryGetValue(file, out var state)
                ? state.Names
                : IncrementalState.Names(contents[file].Utf8.Span);
        }

        var sourcePaths = sources.Select(s => s.Path).Concat(cppSources.Select(s => s.Path)).ToList();
        var references = new IReadOnlyList<(string TargetId, SymbolReference Reference)>[sourcePaths.Count];
        var bound = 0;
        Parallel.For(
            0,
            sourcePaths.Count,
            new ParallelOptions { CancellationToken = cancellationToken },
            i =>
            {
                var file = sourcePaths[i];
                if (changedNames != null
                    && !changed.Contains(file)
                    && previous!.Files.TryGetValue(file, out var state)
                    && !changedNames.Overlaps(state.Names))
                {
                    references[i] = state.References;
                    return;
                }

                references[i] = i < sources.Count
                    ? CSharpSymbolCollector.Collect(sources[i], builder, binder, CollectPass.References)
                    : CppSymbolCollector.References(cppSources[i - sources.Count], builder, cppBinder);
                Interlocked.Increment(ref bound);
            });

        var fileStates = new Dictionary<string, FileState>(SymbolIndexBuilder.PathComparer);
        foreach (var file in files.Keys)
        {
            fileStates[file] = new FileState
            {
                Hash = contents[file].Hash,
                Declarations = declarations.TryGetValue(file, out var list) ? list : [],
                Names = names[file],
                References = [],
                CppMacros = cpp.Macros.GetValueOrDefault(file) ?? [],
                CppNames = cpp.Names.GetValueOrDefault(file) ?? [],
            };
        }

        for (var i = 0; i < sourcePaths.Count; i++)
        {
            foreach (var (target, reference) in references[i])
            {
                builder.AddReference(target, reference);
            }

            var state = fileStates[sourcePaths[i]];
            fileStates[sourcePaths[i]] = new FileState
            {
                Hash = state.Hash,
                Declarations = state.Declarations,
                Names = state.Names,
                References = references[i],
                CppMacros = state.CppMacros,
                CppNames = state.CppNames,
            };
        }

        return new WorkspaceSnapshot
        {
            RootPath = discovery.RootPath,
            Configuration = configuration,
            Platform = platform,
            ReadReferences = readReferences,
            Projects = projects,
            Documents = new Dictionary<string, SourceDocument>(documents, SymbolIndexBuilder.PathComparer),
            Index = builder.Build(),
            Problems = problems,
            Metadata = metadata,
            MetadataProblems = metadataProblems,
            References = referencesByProject,
            Files = fileStates,
            ProjectPrints = projectPrints,
            MetadataKey = metadataKey,
            Kind = previous == null ? SnapshotKind.Full : SnapshotKind.Incremental,
            CppParseKey = cpp.Key,
            ParsedFiles = parsed,
            BoundFiles = bound,
            LoadedAt = DateTimeOffset.UtcNow,
            Elapsed = stopwatch.Elapsed,
        };
    }

    /// <summary>The snapshot before, with the documents read now and the problems found now.</summary>
    private static WorkspaceSnapshot Unchanged(
        WorkspaceSnapshot previous,
        DiscoveryResult discovery,
        List<ProjectModel> projects,
        List<LoadProblem> problems,
        MetadataCatalog metadata,
        IReadOnlyList<LoadProblem> metadataProblems,
        Dictionary<string, IReadOnlyList<string>> referencesByProject,
        ConcurrentDictionary<string, (ReadOnlyMemory<byte> Utf8, string Hash, string? Failure)> contents,
        Stopwatch stopwatch)
    {
        var documents = new Dictionary<string, SourceDocument>(SymbolIndexBuilder.PathComparer);
        foreach (var (file, document) in previous.Documents)
        {
            // A document read from the cache gets its text here
            documents[file] = document.Utf8.IsEmpty && !contents[file].Utf8.IsEmpty
                ? document with { Utf8 = contents[file].Utf8, Lines = Lines(contents[file].Utf8, document.Language) }
                : document;
        }

        return new WorkspaceSnapshot
        {
            RootPath = discovery.RootPath,
            Configuration = previous.Configuration,
            Platform = previous.Platform,
            ReadReferences = previous.ReadReferences,
            Projects = projects,
            Documents = documents,
            Index = previous.Index,
            Problems = problems,
            Metadata = metadata,
            MetadataProblems = metadataProblems,
            References = referencesByProject,
            Files = previous.Files,
            ProjectPrints = previous.ProjectPrints,
            MetadataKey = previous.MetadataKey,
            Kind = previous.Kind == SnapshotKind.Cache ? SnapshotKind.Cache : SnapshotKind.Unchanged,
            CppParseKey = previous.CppParseKey,
            LoadedAt = DateTimeOffset.UtcNow,
            Elapsed = stopwatch.Elapsed,
        };
    }

    /// <summary>The listed projects and, transitively, the projects they reference.</summary>
    private static List<ProjectModel> ReadProjects(DiscoveryResult discovery, string configuration, string platform, List<LoadProblem> problems, CancellationToken cancellationToken)
    {
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
                project = System.IO.Directory.Exists(projectPath) ? CppFiles.ReadFolder(projectPath)
                    : Path.GetFileName(projectPath).Equals("compile_commands.json", StringComparison.OrdinalIgnoreCase)
                        ? CppFiles.ReadCompilationDatabase(projectPath, SolutionDiscovery.WorkspaceDirectory(discovery.RootPath))
                        : ProjectFileReader.Read(projectPath, configuration, platform);
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
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

        return projects;
    }

    /// <summary>Everything of a project that binding depends on, except its list of sources.</summary>
    public static string ProjectPrint(ProjectModel project) => string.Join(
        "\n",
        project.Name,
        project.TargetFramework,
        project.LanguageVersion,
        string.Join(';', project.PreprocessorSymbols),
        string.Join(';', project.ProjectReferences),
        string.Join(';', project.Usings),
        project.Sdk,
        string.Join(';', project.PackageReferences.Select(p => $"{p.Id}/{p.Version}")),
        string.Join(';', project.FrameworkReferences),
        string.Join(';', project.AssemblyReferences),
        project.IntermediateOutputPath,
        project.Language,
        string.Join(';', project.Macros.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + "=" + m.Value)),
        string.Join(';', project.IncludeDirectories),
        project.CppLanguageVersion,
        string.Join(';', project.FileOptions.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + ">" + f.Value.Print())));

    private static string MetadataKey(IEnumerable<string> paths)
    {
        var key = new StringBuilder();
        foreach (var path in paths.Distinct(SymbolIndexBuilder.PathComparer).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            key.Append(path).Append('|').Append(info.Exists ? info.Length : -1).Append('|').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }

        return IncrementalState.Hash(Encoding.UTF8.GetBytes(key.ToString()));
    }

    private static (ReadOnlyMemory<byte> Utf8, string Hash, string? Failure) ReadSource(string path)
    {
        try
        {
            var utf8 = CSharpParser.GetUtf8Source(File.ReadAllBytes(path));
            return (utf8, IncrementalState.Hash(utf8.Span), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (ReadOnlyMemory<byte>.Empty, "", e.Message);
        }
    }

    private static (ReadOnlyMemory<byte> Utf8, string Hash, string? Failure) Generated(string code)
    {
        var utf8 = Encoding.UTF8.GetBytes(code);
        return (utf8, IncrementalState.Hash(utf8), null);
    }

    /// <summary>Reads and parses one file, with error recovery.</summary>
    public static SourceDocument ParseFile(string path, string project, CSharpParseOptions options) =>
        Parse(path, project, ReadSource(path), options);

    private static SourceDocument Parse(string path, string project, (ReadOnlyMemory<byte> Utf8, string Hash, string? Failure) content, CSharpParseOptions options)
    {
        if (content.Failure != null)
        {
            return new SourceDocument(path, project, ReadOnlyMemory<byte>.Empty, new LineMap([]), null, content.Failure);
        }

        var utf8 = content.Utf8;
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

    /// <summary>A parse error of a text with macros expanded, at its position in the file.</summary>
    private static string Describe(ParseError? error, CppOffsetMap? map, LineMap lines)
    {
        if (error != null && map != null && error.Position >= 0)
        {
            var (line, column) = lines.GetLineAndColumn(Math.Min(map.Original(error.Position), lines.GetLineSpan(lines.LineCount).End));
            return $"{error.Message} at ({line}:{column})";
        }

        return Describe(error);
    }
    private static LineMap Lines(ReadOnlyMemory<byte> utf8, SourceLanguage language) =>
        new(utf8.Span, unicodeLineBreaks: language == SourceLanguage.CSharp);

    // ========================================
    // C++
    // ========================================

    /// <summary>What the C++ files of a build were parsed with, per file, and the sources for binding.</summary>
    private sealed class CppParse
    {
        public Dictionary<string, IReadOnlyList<string>> Macros { get; } = new(SymbolIndexBuilder.PathComparer);

        public Dictionary<string, IReadOnlyList<string>> Names { get; } = new(SymbolIndexBuilder.PathComparer);

        public Dictionary<string, ProjectModel> Projects { get; } = new(SymbolIndexBuilder.PathComparer);

        public CppMacroPlan Plan { get; set; } = CppMacroPlan.Empty;

        public string Key { get; set; } = "";

        /// <summary>Every C++ file was parsed again: their references must be found again.</summary>
        public bool ParsedAgain { get; set; }

        /// <summary>The parsed C++ files for binding, in path order, with the text the parser read and the files they include.</summary>
        public List<CppSource> Sources(IReadOnlyDictionary<string, SourceDocument> documents)
        {
            var files = new HashSet<string>(Projects.Keys, SymbolIndexBuilder.PathComparer);
            var byName = CppFiles.ByName(files);

            var sources = new List<CppSource>();
            foreach (var (path, project) in Projects.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (documents.GetValueOrDefault(path) is not { CppUnit: { } unit } document)
                {
                    continue;
                }

                var includes = CppFiles.ResolveIncludes(path, unit.Directives, FileOptions(project, path).IncludeDirectories, files, byName);
                var (text, map) = Plan.Prepare(document.Utf8, document.CppExpandedFunctions);
                sources.Add(new CppSource(path, document.Project, text, document.Lines, unit, includes) { Map = map });
            }

            return sources;
        }
    }

    private static CppFileOptions FileOptions(ProjectModel project, string file) =>
        project.FileOptions.GetValueOrDefault(file) ?? new CppFileOptions(project.Macros, project.IncludeDirectories, project.CppLanguageVersion);

    /// <summary>The options of a file with the macros of the headers it includes (those of the project take precedence).</summary>
    private static CppFileOptions FileOptions(ProjectModel project, string file, IReadOnlyDictionary<string, IReadOnlyList<(string Name, string Replacement)>> headerMacros)
    {
        var options = FileOptions(project, file);
        if (headerMacros.GetValueOrDefault(file) is not { Count: > 0 } included)
        {
            return options;
        }

        var macros = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, replacement) in included)
        {
            macros.TryAdd(name, replacement);
        }

        foreach (var (name, value) in options.Macros)
        {
            // A macro of the project replaces a header's, function-like or not
            var paren = name.IndexOf('(');
            var plain = paren < 0 ? name : name[..paren];
            foreach (var key in macros.Keys.Where(k => k == plain || k.StartsWith(plain + "(", StringComparison.Ordinal)).ToList())
            {
                macros.Remove(key);
            }

            macros[name] = value;
        }

        return options with { Macros = macros };
    }

    /// <summary>
    /// The macros the workspace headers a file includes define, transitively, in include order: a header's includes
    /// before its own definitions, and the first definition of a macro wins.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<(string Name, string Replacement)>> HeaderMacros(CppParse parse)
    {
        var files = new HashSet<string>(parse.Projects.Keys, SymbolIndexBuilder.PathComparer);
        var byName = CppFiles.ByName(files);
        var includes = new Dictionary<string, List<string>>(SymbolIndexBuilder.PathComparer);
        foreach (var (file, project) in parse.Projects)
        {
            var directories = FileOptions(project, file).IncludeDirectories;
            includes[file] = CppMacroPlan.Includes(parse.Macros[file])
                .Select(name => CppFiles.ResolveInclude(file, name, directories, files, byName))
                .OfType<string>()
                .Where(f => !SymbolIndexBuilder.PathComparer.Equals(f, file))
                .ToList();
        }

        var result = new Dictionary<string, IReadOnlyList<(string Name, string Replacement)>>(SymbolIndexBuilder.PathComparer);
        foreach (var file in parse.Projects.Keys)
        {
            var macros = new List<(string, string)>();
            var seen = new HashSet<string>(SymbolIndexBuilder.PathComparer) { file };
            void Visit(string header)
            {
                if (!seen.Add(header))
                {
                    return;
                }

                foreach (var included in includes[header])
                {
                    Visit(included);
                }

                macros.AddRange(CppMacroPlan.Macros(parse.Macros[header]));
            }

            foreach (var included in includes[file])
            {
                Visit(included);
            }

            result[file] = macros;
        }

        return result;
    }

    private delegate bool KeptDocument(string file, ProjectModel project, out SourceDocument document);

    /// <summary>
    /// Parses the C++ files that changed, in two parses (see <see cref="CppParsing"/>): the first finds the macros
    /// to blank out and the names each file declares, the second is given the names of all files. When those change,
    /// every C++ file is parsed again.
    /// </summary>
    private static CppParse ParseCpp(
        Dictionary<string, ProjectModel> files,
        ConcurrentDictionary<string, (ReadOnlyMemory<byte> Utf8, string Hash, string? Failure)> contents,
        HashSet<string> changed,
        WorkspaceSnapshot? previous,
        Dictionary<string, string> projectPrints,
        ConcurrentDictionary<string, SourceDocument> documents,
        KeptDocument kept,
        ref int parsed,
        CancellationToken cancellationToken)
    {
        var result = new CppParse();
        foreach (var (file, project) in files)
        {
            if (project.Language == SourceLanguage.Cpp)
            {
                result.Projects[file] = project;
            }
        }

        if (result.Projects.Count == 0)
        {
            return result;
        }

        bool Unchanged(string file) => previous != null && !changed.Contains(file)
            && previous.ProjectPrints.GetValueOrDefault(result.Projects[file].Path) == projectPrints[result.Projects[file].Path]
            && previous.Files.ContainsKey(file);

        // The macros of every file, and those that are decorations only
        foreach (var file in result.Projects.Keys)
        {
            result.Macros[file] = Unchanged(file) ? previous!.Files[file].CppMacros : CppMacroPlan.ScanDefinitions(contents[file].Utf8.Span);
        }

        var predefined = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in result.Projects.Values.Distinct())
        {
            foreach (var (name, value) in project.Macros)
            {
                predefined.TryAdd(name, value);
            }
        }

        result.Plan = CppMacroPlan.From(result.Macros.Values.SelectMany(m => m), predefined);

        // Each file sees the macros of the workspace headers it includes; a change to any directive parses every file again
        var headerMacros = HeaderMacros(result);
        var blankKey = IncrementalState.Hash(Encoding.UTF8.GetBytes(result.Plan.Key + "\n" + string.Join("\n", result.Macros.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + "\n" + string.Join("\n", m.Value)))));
        var sameBlanks = previous != null && previous.CppParseKey.StartsWith(blankKey + "|", StringComparison.Ordinal);

        // The first parse: the names each file declares
        var names = new ConcurrentDictionary<string, IReadOnlyList<string>>(SymbolIndexBuilder.PathComparer);
        Parallel.ForEach(
            result.Projects,
            new ParallelOptions { CancellationToken = cancellationToken },
            entry =>
            {
                var (file, project) = entry;
                if (sameBlanks && Unchanged(file))
                {
                    names[file] = previous!.Files[file].CppNames;
                    return;
                }

                var content = contents[file];
                var options = FileOptions(project, file, headerMacros);
                var unit = content.Failure == null
                    ? ParseWithPlan(content.Utf8, result.Plan, CppParsing.Options(options.LanguageVersion, options.Macros, [.. options.IncludeDirectories, .. CppFiles.SystemIncludeDirectories], Path.GetDirectoryName(file), null), out _, out _, out _)
                    : null;
                names[file] = unit != null ? CppParsing.Names(unit) : [];
            });

        foreach (var (file, list) in names)
        {
            result.Names[file] = list;
        }

        var workspaceNames = CppNames.From(result.Names.Values.SelectMany(n => n));
        result.Key = blankKey + "|" + workspaceNames.Key;
        var sameKey = previous != null && previous.CppParseKey == result.Key;
        result.ParsedAgain = previous != null && !sameKey;

        // The second parse, with the names of the workspace
        var secondParses = 0;
        Parallel.ForEach(
            result.Projects,
            new ParallelOptions { CancellationToken = cancellationToken },
            entry =>
            {
                var (file, project) = entry;
                if (sameKey && kept(file, project, out var document))
                {
                    documents[file] = document;
                    return;
                }

                var options = FileOptions(project, file, headerMacros);
                documents[file] = ParseCpp(file, project.Name, contents[file], result.Plan,
                    CppParsing.Options(options.LanguageVersion, options.Macros, [.. options.IncludeDirectories, .. CppFiles.SystemIncludeDirectories], Path.GetDirectoryName(file), workspaceNames));
                Interlocked.Increment(ref secondParses);
            });

        parsed += secondParses;
        return result;
    }

    private static TranslationUnit? TryParseCpp(ReadOnlyMemory<byte> utf8, CppParseOptions options, out ParseError? error)
    {
        try
        {
            if (CppParser.TryParse(utf8, options, out var unit, out error))
            {
                return unit;
            }

            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A parser bug must not take the whole workspace down
            error = new ParseError { Message = $"Parser failure: {e.GetType().Name}: {e.Message}" };
            return null;
        }
    }

    /// <summary>Reads and parses one C++ file, with its macros prepared by the workspace's plan.</summary>
    private static SourceDocument ParseCpp(string path, string project, (ReadOnlyMemory<byte> Utf8, string Hash, string? Failure) content, CppMacroPlan plan, CppParseOptions options)
    {
        if (content.Failure != null)
        {
            return new SourceDocument(path, project, ReadOnlyMemory<byte>.Empty, new LineMap([], unicodeLineBreaks: false), null, content.Failure) { Language = SourceLanguage.Cpp };
        }

        var lines = Lines(content.Utf8, SourceLanguage.Cpp);
        var unit = ParseWithPlan(content.Utf8, plan, options, out var expandedFunctions, out var error, out var map);
        return new SourceDocument(path, project, content.Utf8, lines, null, unit == null ? Describe(error, map, lines) : null)
        {
            CppUnit = unit,
            Language = SourceLanguage.Cpp,
            CppExpandedFunctions = expandedFunctions,
            CppMap = unit != null ? map : null,
        };
    }

    /// <summary>
    /// Parses a C++ file with its macros prepared by the plan, and when that fails, with the function-like macros
    /// that stand for syntax expanded too; the error and map are those of the first parse when both fail.
    /// </summary>
    private static TranslationUnit? ParseWithPlan(ReadOnlyMemory<byte> utf8, CppMacroPlan plan, CppParseOptions options, out bool expandedFunctions, out ParseError? error, out CppOffsetMap? map)
    {
        expandedFunctions = false;
        var (text, first) = plan.Prepare(utf8);
        map = first;
        var unit = TryParseCpp(text, options, out error);
        if (unit != null || plan.FunctionExpansions.Count == 0)
        {
            return unit;
        }

        var (expanded, second) = plan.Prepare(utf8, expandFunctions: true);
        if (expanded.Length == text.Length && second?.Equals(first) != false && expanded.Span.SequenceEqual(text.Span))
        {
            return null;
        }

        unit = TryParseCpp(expanded, options, out _);
        if (unit != null)
        {
            expandedFunctions = true;
            map = second;
        }

        return unit;
    }
}
