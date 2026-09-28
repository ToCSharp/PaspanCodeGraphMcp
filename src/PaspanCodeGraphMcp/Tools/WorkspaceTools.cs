using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraphMcp.Tools;

public sealed record ProblemDto(string Severity, string Message, string? File, int Line, int Column)
{
    public static ProblemDto From(LoadProblem problem) =>
        new(problem.Severity.ToString(), problem.Message, problem.File, problem.Line, problem.Column);
}

/// <param name="Language">CSharp or Cpp.</param>
/// <param name="LanguageVersion">The C# language version, or the C++ standard.</param>
/// <param name="PreprocessorSymbols">C#: the defined symbols; C++: the macros given to every file (-D options and the predefined ones).</param>
public sealed record ProjectReport(
    string Name,
    string Path,
    string Language,
    string TargetFramework,
    string LanguageVersion,
    int Documents,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PreprocessorSymbols,
    int ReferencedAssemblies);

public sealed record LoadReport(
    string WorkspacePath,
    string Configuration,
    DateTimeOffset LoadedAt,
    double ElapsedSeconds,
    int Documents,
    int Symbols,
    int FilesWithParseErrors,
    int ReferencedAssemblies,
    int ExternalTypes,
    string? Cache,
    IReadOnlyList<ProjectReport> Projects,
    IReadOnlyList<ProblemDto> Problems);

public sealed record WorkspaceStatus(
    bool Loaded,
    bool Loading,
    string? WorkspacePath,
    string Configuration,
    int Projects,
    int Documents,
    int Symbols,
    int ReferencedAssemblies,
    int ExternalTypes,
    DateTimeOffset? LoadedAt,
    double? LoadSeconds,
    UpdateReport? LastChange,
    bool Watching,
    string? Cache,
    long WorkingSetMb,
    IReadOnlyList<string> ProjectNames,
    int ProblemCount,
    string? LastError);

/// <summary>How the current snapshot was made from the one before.</summary>
/// <param name="Kind">Full, Incremental (changed files parsed, affected files bound), Unchanged, or Cache (read from the graph cache).</param>
public sealed record UpdateReport(string Kind, int ParsedFiles, int BoundFiles, double Seconds);

public sealed record DiagnosticDto(string Severity, string Kind, string Message, string? File, int Line, int Column, string? Project);

/// <summary>How the references to workspace members were bound, for the whole workspace.</summary>
/// <param name="NameOnlyShare">The share of name-only references among exact, inferred and name-only ones: how much of the call graph rests on names alone.</param>
/// <param name="External">References to members of types from referenced assemblies.</param>
/// <param name="Unresolved">Calls that could not be bound at all.</param>
public sealed record BindingSummary(int Exact, int Inferred, int NameOnly, double NameOnlyShare, int External, int Unresolved);

public sealed record DiagnosticsResult(int Total, IReadOnlyDictionary<string, int> ByProject, IReadOnlyList<DiagnosticDto> Items, bool Truncated, BindingSummary Binding);

[McpServerToolType]
public sealed class WorkspaceTools
{
    [McpServerTool(Name = "workspace_load", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Load workspace")]
    [Description("Load a C# or C++ workspace (replaces the current one): a .sln/.slnx/.csproj; for C++ a .vcxproj (also in a solution), a compile_commands.json (CMake, Meson, Bear: the macros, include directories and standard of each file; the headers under the sources are added), or a directory of C++ sources. Project files are read without MSBuild: properties, simple conditions, Directory.Build.props, .projitems imports, Compile/ClCompile/ClInclude items with wildcards, ProjectReference, PackageReference and FrameworkReference, the ClCompile item definition of the configuration. The referenced assemblies of C# projects (the SDK's reference packs, the packages of obj/project.assets.json or, without a restore, of the NuGet cache) are read with System.Reflection.Metadata so that their types and members bind. Every file is parsed by PaspanParsers, C# with error recovery; C++ files are parsed without reading headers or expanding macros, with the names of types and templates of the whole workspace and of the standard library, and with the macros that only decorate declarations (FOO_API) blanked out; a C++ file that does not parse has no symbols (see diagnostics). The graph is kept in .paspan/graph.bin next to the solution (or in the directory), so a later load reads it and parses and binds again only what changed (unless the server runs with --no-cache). Returns per-project file and assembly counts, preprocessor symbols or macros and the problems found.")]
    public static async Task<LoadReport> Load(
        WorkspaceHost host,
        [Description("Path to a .sln, .slnx, .csproj, .vcxproj or compile_commands.json, or a directory containing one (or containing C++ sources, or a build directory with compile_commands.json). Defaults to the --workspace the server was started with.")] string? path = null,
        [Description("MSBuild Configuration (default: server option, usually Debug)")] string? configuration = null,
        CancellationToken ct = default)
    {
        var target = path ?? host.Options.WorkspacePath
            ?? throw new ArgumentException("No path given and the server was started without --workspace.");
        var snapshot = await host.LoadAsync(target, configuration ?? host.Options.Configuration, host.Options.Platform, ct);
        return Report(snapshot, host.CacheStatus);
    }

    internal static LoadReport Report(WorkspaceSnapshot snapshot, string? cache = null)
    {
        var documentsByProject = snapshot.Documents.Values.GroupBy(d => d.Project).ToDictionary(g => g.Key, g => g.Count());
        return new LoadReport(
            snapshot.RootPath,
            snapshot.Configuration,
            snapshot.LoadedAt,
            Math.Round(snapshot.Elapsed.TotalSeconds, 2),
            snapshot.Documents.Count,
            snapshot.Index.Count,
            snapshot.Documents.Values.Count(d => d.Failure is not null || d.Errors.Count != 0),
            snapshot.Metadata.Assemblies.Count,
            snapshot.Metadata.TypeCount,
            cache,
            snapshot.Projects.Select(p => new ProjectReport(
                p.Name,
                p.Path,
                p.Language.ToString(),
                p.TargetFramework,
                p.Language == PaspanCodeGraph.SourceLanguage.Cpp ? p.CppLanguageVersion.ToString() : p.LanguageVersion.ToString(),
                documentsByProject.GetValueOrDefault(p.Name),
                p.ProjectReferences.Select(r => Path.GetFileNameWithoutExtension(r)).ToList(),
                p.Language == PaspanCodeGraph.SourceLanguage.Cpp ? p.Macros.Select(m => m.Value == "1" ? m.Key : $"{m.Key}={m.Value}").Order(StringComparer.Ordinal).ToList() : p.PreprocessorSymbols,
                snapshot.References.GetValueOrDefault(p.Path)?.Count ?? 0)).ToList(),
            snapshot.Problems.Select(ProblemDto.From).ToList());
    }

    [McpServerTool(Name = "workspace_status", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Workspace status")]
    [Description("Current workspace: loaded projects, file, symbol, referenced assembly and external type counts, load time, how the last change was applied (the server watches the files and updates the graph when they change: changed files are parsed again and the files that can bind to what changed are bound again), whether it is watching, how the graph cache was used, process memory, number of load problems.")]
    public static WorkspaceStatus Status(WorkspaceHost host)
    {
        var snapshot = host.Current;
        return new WorkspaceStatus(
            Loaded: snapshot is not null,
            Loading: host.IsLoading,
            WorkspacePath: snapshot?.RootPath ?? host.Options.WorkspacePath,
            Configuration: snapshot?.Configuration ?? host.Options.Configuration,
            Projects: snapshot?.Projects.Count ?? 0,
            Documents: snapshot?.Documents.Count ?? 0,
            Symbols: snapshot?.Index.Count ?? 0,
            ReferencedAssemblies: snapshot?.Metadata.Assemblies.Count ?? 0,
            ExternalTypes: snapshot?.Metadata.TypeCount ?? 0,
            LoadedAt: snapshot?.LoadedAt,
            LoadSeconds: snapshot is null ? null : Math.Round(snapshot.Elapsed.TotalSeconds, 2),
            LastChange: snapshot is null ? null : new UpdateReport(snapshot.Kind.ToString(), snapshot.ParsedFiles, snapshot.BoundFiles, Math.Round(snapshot.Elapsed.TotalSeconds, 2)),
            Watching: host.WatchedDirectories.Count > 0,
            Cache: host.CacheStatus,
            WorkingSetMb: Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024),
            ProjectNames: snapshot?.Projects.Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            ProblemCount: snapshot?.Problems.Count ?? 0,
            LastError: host.LastError);
    }

    [McpServerTool(Name = "diagnostics", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Load and parse problems")]
    [Description("Problems of the loaded workspace: projects that could not be read, syntax errors found by the parser (there is no semantic analysis, so no type errors) and C++ files that could not be parsed (usually a macro used where the code expects a declaration or a type), with the position. Grouped counts plus the first N items, and how the member references of the whole workspace were bound (exact, inferred, name-only and their share, external, unresolved).")]
    public static async Task<DiagnosticsResult> Diagnostics(
        WorkspaceHost host,
        [Description("Project name filter (exact, case-insensitive)")] string? project = null,
        [Description("Only problems in this file (full path or path suffix)")] string? file = null,
        [Description("Maximum items to return (default 50)")] int maxResults = 50,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var suffix = file?.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        bool FileMatches(string? path) => suffix is null || (path is not null && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        bool ProjectMatches(string? name) => project is null || string.Equals(name, project, StringComparison.OrdinalIgnoreCase);

        var all = new List<DiagnosticDto>();
        var projectOfFile = snapshot.Projects.ToDictionary(p => p.Path, p => p.Name, PaspanCodeGraph.SymbolIndexBuilder.PathComparer);
        foreach (var problem in snapshot.Problems)
        {
            var problemProject = problem.File is not null ? projectOfFile.GetValueOrDefault(problem.File) : null;
            if (FileMatches(problem.File) && ProjectMatches(problemProject))
            {
                all.Add(new DiagnosticDto(problem.Severity.ToString(), "Load", problem.Message, problem.File, problem.Line, problem.Column, problemProject));
            }
        }

        foreach (var document in snapshot.Documents.Values.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            if (!FileMatches(document.Path) || !ProjectMatches(document.Project))
            {
                continue;
            }

            if (document.Failure is { } failure)
            {
                // ParseError's text ends with its position: "... at (12:5)"
                var position = System.Text.RegularExpressions.Regex.Match(failure, @" at \((\d+):(\d+)\)$");
                all.Add(new DiagnosticDto("Error", "Parse", failure, document.Path, position.Success ? int.Parse(position.Groups[1].Value) : 0, position.Success ? int.Parse(position.Groups[2].Value) : 0, document.Project));
            }

            foreach (var error in document.Errors)
            {
                var (line, column) = document.Lines.GetLineAndColumn(Math.Min(error.Span.Start, document.Utf8.Length));
                all.Add(new DiagnosticDto("Error", "Syntax", error.Message, document.Path, line, column, document.Project));
            }
        }

        var byProject = all.GroupBy(d => d.Project ?? "(workspace)").ToDictionary(g => g.Key, g => g.Count());
        var (exact, inferred, nameOnly, external, unresolved) = snapshot.Index.MemberReferenceCounts();
        var bound = exact + inferred + nameOnly;
        var binding = new BindingSummary(exact, inferred, nameOnly, bound == 0 ? 0 : Math.Round((double)nameOnly / bound, 4), external, unresolved);
        return new DiagnosticsResult(all.Count, byProject, all.Take(maxResults).ToList(), all.Count > maxResults, binding);
    }
}
