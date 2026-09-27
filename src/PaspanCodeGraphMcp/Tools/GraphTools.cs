using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using PaspanCodeGraph;
using PaspanCodeGraph.Search;
using PaspanCodeGraph.Workspace;
using PaspanCodeGraphMcp.Symbols;

namespace PaspanCodeGraphMcp.Tools;

/// <param name="Terms">The search terms the symbol matched (a completion when only the start of a term matched).</param>
/// <param name="Summary">The summary of its documentation comment.</param>
/// <param name="Note">The note kept with annotate, if any.</param>
public sealed record SearchItem(SymbolDto Symbol, double Score, IReadOnlyList<string> Terms, string? Summary, string? Note);

public sealed record SearchCodeResult(string Query, int Returned, IReadOnlyList<SearchItem> Items);

/// <param name="Relation">How it relates to the seed: calls, called by, uses, used by, uses type, referenced by, base class, derived class, interface, implemented by, overrides, overridden by, implements, declared in; "seed" for a seed.</param>
/// <param name="Distance">Edges from the nearest seed.</param>
/// <param name="Code">Source of the declaration (for a type, its header and its members' signatures), cut to fit the budget.</param>
/// <param name="CodeLine">1-based line <see cref="Code"/> starts at.</param>
public sealed record ContextItem(SymbolDto Symbol, string Relation, int Distance, string? Summary, string? Note, string? Code, int? CodeLine);

/// <param name="TokensUsed">An estimate (characters / 4) of the tokens the items take.</param>
/// <param name="Omitted">Related symbols found but left out for the budget.</param>
public sealed record GetContextResult(IReadOnlyList<ContextItem> Seeds, IReadOnlyList<ContextItem> Related, int TokensUsed, int TokenBudget, bool Truncated, int Omitted);

/// <param name="Via">The edge it was reached by: calls, uses, references type, inherits, implements, overrides or implements member.</param>
/// <param name="Through">The id of the symbol it depends on, one step closer to the changed one.</param>
public sealed record ImpactItem(SymbolDto Symbol, int Depth, string Via, string Through, bool IsTest);

public sealed record ImpactGroup(string Project, int Members, int TestMembers);

public sealed record ImpactAnalysisResult(
    SymbolDto Symbol,
    int Total,
    int Tests,
    IReadOnlyDictionary<int, int> ByDepth,
    IReadOnlyList<ImpactGroup> ByProject,
    IReadOnlyList<SymbolDto> AffectedTests,
    IReadOnlyList<ImpactItem> Items,
    bool Truncated);

/// <param name="Kind">calls, uses, uses type, or dispatch (a call to a base or interface member that can run this override or implementation).</param>
/// <param name="Location">Where the first reference of the step is, when there is one.</param>
public sealed record PathStep(SymbolDto From, SymbolDto To, string Kind, string Confidence, LocationDto? Location);

/// <param name="Direction">"forward" when From reaches To, "reverse" when only To reaches From (the steps then go from To to From).</param>
public sealed record FindPathResult(bool Found, string? Direction, int Length, IReadOnlyList<PathStep> Steps, string? Hint);

public sealed record NamespaceSummary(string Namespace, int Types, string? Note);

public sealed record ProjectSummary(string Name, int Files, int Types, IReadOnlyList<string> References, IReadOnlyList<NamespaceSummary> Namespaces, string? Note);

/// <param name="Label">The dominant namespace and the most central types.</param>
/// <param name="Links">The communities it uses or is used by the most, by number, with the weight of those uses.</param>
/// <param name="Notes">Notes kept with annotate on its types and namespaces.</param>
public sealed record CommunitySummary(
    int Number,
    string Label,
    int Types,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<string> Projects,
    IReadOnlyList<SymbolDto> TopTypes,
    IReadOnlyDictionary<int, double> Links,
    IReadOnlyList<Annotation> Notes);

/// <param name="Modularity">How clearly the types split into communities, from 0 (not at all) to 1.</param>
/// <param name="EntryPoints">Main and public methods nothing in the workspace calls that reach much of it (handlers, tools, public API), tests last.</param>
public sealed record ModuleMapResult(
    IReadOnlyList<ProjectSummary> Projects,
    int CommunityCount,
    double Modularity,
    IReadOnlyList<CommunitySummary> Communities,
    IReadOnlyList<SymbolDto> EntryPoints);

public sealed record AnnotateResult(string Target, string? Note, DateTimeOffset? UpdatedAt, int Total);

[McpServerToolType]
public sealed class GraphTools
{
    private const int CharsPerToken = 4;

    [McpServerTool(Name = "search_code", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Search code")]
    [Description("Finds types and members by what they are about, from words rather than exact names: \"where are project files read\", \"graph cache\", \"parse error recovery\". BM25 over each symbol's name (split at case changes), containing type, namespace, documentation, signature, file name and, for members, the identifiers, strings and comments of its body, with stemming and prefix completion; central symbols (by PageRank over the reference graph) rank higher. Use find_symbol for exact names.")]
    public static async Task<SearchCodeResult> SearchCode(
        WorkspaceHost host,
        [Description("Words describing the code, identifiers, or both")] string query,
        [Description("Only this kind (Class, Method, Property...)")] string? kind = null,
        [Description("Only this project (exact, case-insensitive)")] string? project = null,
        [Description("Maximum results (default 20)")] int maxResults = 20,
        [Description("Include symbols in test files (default true; they rank a little lower)")] bool includeTests = true,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var rag = GraphRag.For(snapshot);
        var kindFilter = SymbolKinds.Parse(kind);
        var hits = rag.Search.Search(query, Math.Clamp(maxResults, 1, 200), symbol =>
        {
            if ((kindFilter is { } k && symbol.Kind != k) || (project != null && !string.Equals(symbol.Project, project, StringComparison.OrdinalIgnoreCase)))
            {
                return 0;
            }

            return IsTest(symbol, snapshot) ? includeTests ? 0.8 : 0 : 1;
        });
        var annotations = host.Annotations;
        var items = hits.Select(h =>
        {
            var symbol = rag.Graph.Nodes[h.Node];
            return new SearchItem(SymbolFormatter.ToDto(symbol, snapshot), Math.Round(h.Score, 3), h.Terms, SymbolFormatter.Summary(symbol), annotations.NoteOf(symbol.Id));
        }).ToList();
        return new SearchCodeResult(query, items.Count, items);
    }

    [McpServerTool(Name = "get_context", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get context")]
    [Description("Collects what an agent needs to understand or change a symbol, within a token budget: the symbol's source (a type's header and member signatures), then the symbols around it in the graph, nearest and most relevant first (containing type, base types and interfaces, overridden and implemented members, callers, callees, overrides and implementations, used and using members, referenced types), each with its signature, summary, notes kept with annotate and, while the budget allows, its code. Give a symbol, or a query to start from the best search_code matches.")]
    public static async Task<object> GetContext(
        WorkspaceHost host,
        [Description("Symbol id, 'file.cs:line:col', or a (dotted) name")] string? symbol = null,
        [Description("Words to search for when no symbol is given; the best matches become the seeds")] string? query = null,
        [Description("How many edges away to look (1 to 3, default 1)")] int depth = 1,
        [Description("Approximate token budget for the whole answer (default 4000)")] int tokenBudget = 4000,
        [Description("Include related symbols in test files (default false)")] bool includeTests = false,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var rag = GraphRag.For(snapshot);
        var graph = rag.Graph;
        var seeds = new List<int>();
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var (resolved, ambiguous) = NavigationTools.Resolve(snapshot, symbol);
            if (resolved is null)
            {
                return ambiguous!;
            }

            seeds.Add(graph.NodeOf(resolved) ?? throw new ArgumentException($"{resolved.Id} is not a type or member of the workspace."));
        }
        else if (!string.IsNullOrWhiteSpace(query))
        {
            seeds.AddRange(rag.Search.Search(query, 3, s => IsTest(s, snapshot) ? 0.8 : 1).Select(h => h.Node));
            if (seeds.Count == 0)
            {
                return new GetContextResult([], [], 0, tokenBudget, false, 0);
            }
        }
        else
        {
            throw new ArgumentException("Give a symbol or a query.");
        }

        depth = Math.Clamp(depth, 1, 3);
        var budget = Math.Max(200, tokenBudget) * CharsPerToken;
        var used = 0;
        var annotations = host.Annotations;

        ContextItem Item(int node, string relation, int distance, int maxCodeChars)
        {
            var s = graph.Nodes[node];
            var (code, line) = maxCodeChars > 0 ? Code(s, snapshot, maxCodeChars) : (null, null);
            return new ContextItem(SymbolFormatter.ToDto(s, snapshot, withLineText: false), relation, distance, SymbolFormatter.Summary(s), annotations.NoteOf(s.Id), code, line);
        }

        static int Size(ContextItem item) =>
            80 + item.Symbol.Id.Length + item.Symbol.Signature.Length + (item.Symbol.Location?.File.Length ?? 0) + (item.Summary?.Length ?? 0) + (item.Note?.Length ?? 0) + (item.Code?.Length ?? 0);

        // The seeds, with most of the budget for their code when there are several
        var seedItems = new List<ContextItem>();
        var seedShare = (int)(budget * 0.6) / seeds.Count;
        foreach (var seed in seeds)
        {
            var item = Item(seed, "seed", 0, seedShare);
            used += Size(item);
            seedItems.Add(item);
        }

        // Around the seeds, breadth first; a type's members count as the type
        var found = new Dictionary<int, (string Relation, int Distance, int Priority, double Weight)>();
        var visited = new HashSet<int>(seeds);
        var frontier = new List<int>();
        foreach (var seed in seeds)
        {
            frontier.Add(seed);
            if (graph.Nodes[seed].Kind.IsType())
            {
                foreach (var edge in graph.Out(seed).Where(e => e.Kind == EdgeKind.Contains))
                {
                    visited.Add(edge.To);
                    frontier.Add(edge.To);
                }
            }
        }

        for (var distance = 1; distance <= depth && frontier.Count > 0; distance++)
        {
            var next = new List<int>();
            foreach (var node in frontier)
            {
                foreach (var (other, relation, priority, edge) in Neighbors(graph, node))
                {
                    if (edge.Confidence == Confidence.NameOnly || visited.Contains(other) || (!includeTests && IsTest(graph.Nodes[other], snapshot)))
                    {
                        continue;
                    }

                    var weight = graph.RankPercentile(other) + Math.Log(1 + edge.Count) / 10;
                    if (!found.TryGetValue(other, out var existing) || existing.Priority > priority || (existing.Priority == priority && existing.Weight < weight))
                    {
                        found[other] = (relation, distance, priority, weight);
                    }
                }
            }

            foreach (var node in found.Where(f => f.Value.Distance == distance).Select(f => f.Key))
            {
                if (visited.Add(node) && visited.Count < 2000)
                {
                    next.Add(node);
                }
            }

            frontier = next;
        }

        var related = new List<ContextItem>();
        var omitted = 0;
        var truncated = false;
        foreach (var (node, (relation, distance, _, _)) in found
            .OrderBy(f => f.Value.Distance)
            .ThenBy(f => f.Value.Priority)
            .ThenByDescending(f => f.Value.Weight)
            .ThenBy(f => graph.Nodes[f.Key].Id, StringComparer.Ordinal))
        {
            var remaining = budget - used;
            var item = Item(node, relation, distance, Math.Min(1200, remaining / 3));
            if (Size(item) > remaining)
            {
                item = item with { Code = null, CodeLine = null };
            }

            if (Size(item) > remaining)
            {
                truncated = true;
                omitted++;
                continue;
            }

            used += Size(item);
            related.Add(item);
        }

        return new GetContextResult(seedItems, related, used / CharsPerToken, tokenBudget, truncated, omitted);
    }

    [McpServerTool(Name = "impact_analysis", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Impact analysis")]
    [Description("What may break if a symbol changes: the members that use it and, transitively, those that use them, up to a depth; for a type also the users of its members and its derived types; for a virtual or interface member also its overrides and implementations. Grouped by depth and by project, with the affected tests listed. Works for members of referenced assemblies too (by documentation id).")]
    public static async Task<object> ImpactAnalysis(
        WorkspaceHost host,
        [Description("Symbol id, 'file.cs:line:col', a (dotted) name, or the documentation id of a member of a referenced assembly")] string symbol,
        [Description("How many steps of use to follow (default 3)")] int depth = 3,
        [Description("Also follow uses bound only by name (default false)")] bool includeNameOnly = false,
        [Description("Maximum items to list (default 100); the counts cover all")] int maxResults = 100,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var graph = GraphRag.For(snapshot).Graph;
        var (resolved, ambiguous) = NavigationTools.ResolveExternal(snapshot, symbol) is { } external ? (external, null) : NavigationTools.Resolve(snapshot, symbol);
        if (resolved is null)
        {
            return ambiguous!;
        }

        depth = Math.Clamp(depth, 1, 10);
        List<(int Node, int Depth, string Via, string Through)> dependents;
        if (graph.NodeOf(resolved) is { } node)
        {
            dependents = graph.Dependents(node, depth, includeNameOnly)
                .Select(d => (d.Node, d.Depth, Via(d.Edge.Kind), graph.Nodes[d.Edge.To].Id))
                .ToList();
        }
        else
        {
            // A member of a referenced assembly: the members that reference it, then what depends on them
            dependents = [];
            var seen = new HashSet<int>();
            var id = NavigationTools.ReferenceId(resolved);
            foreach (var reference in snapshot.Index.ReferencesTo(id))
            {
                if ((includeNameOnly || reference.Confidence != Confidence.NameOnly) && reference.InMember is { } inMember && graph.NodeOf(inMember) is { } user && seen.Add(user))
                {
                    dependents.Add((user, 1, resolved.Kind.IsType() ? "references type" : "calls", resolved.Id));
                }
            }

            foreach (var first in dependents.ToList())
            {
                foreach (var d in graph.Dependents(first.Node, depth - 1, includeNameOnly))
                {
                    if (seen.Add(d.Node))
                    {
                        dependents.Add((d.Node, d.Depth + 1, Via(d.Edge.Kind), graph.Nodes[d.Edge.To].Id));
                    }
                }
            }
        }

        var ordered = dependents
            .OrderBy(d => d.Depth)
            .ThenByDescending(d => graph.Rank[d.Node])
            .ThenBy(d => graph.Nodes[d.Node].Id, StringComparer.Ordinal)
            .ToList();
        var tests = ordered.Where(d => IsTest(graph.Nodes[d.Node], snapshot)).ToList();
        var byProject = ordered
            .GroupBy(d => graph.Nodes[d.Node].Project ?? "(none)")
            .Select(g => new ImpactGroup(g.Key, g.Count(), g.Count(d => IsTest(graph.Nodes[d.Node], snapshot))))
            .OrderByDescending(g => g.Members)
            .ToList();
        var items = ordered
            .Take(Math.Max(1, maxResults))
            .Select(d => new ImpactItem(SymbolFormatter.ToDto(graph.Nodes[d.Node], snapshot, withLineText: false), d.Depth, d.Via, d.Through, IsTest(graph.Nodes[d.Node], snapshot)))
            .ToList();
        return new ImpactAnalysisResult(
            SymbolFormatter.ToDto(resolved, snapshot),
            ordered.Count,
            tests.Count,
            ordered.GroupBy(d => d.Depth).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            byProject,
            tests.Where(t => graph.Nodes[t.Node].Kind.IsMember()).Take(50).Select(t => SymbolFormatter.ToDto(graph.Nodes[t.Node], snapshot, withLineText: false)).ToList(),
            items,
            ordered.Count > items.Count);
    }

    [McpServerTool(Name = "find_path", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Find path")]
    [Description("How one symbol reaches another: the shortest chain of calls, uses and type references from 'from' to 'to' (a call to a base or interface member continues into its overrides and implementations), each step with the place of the call. When 'from' does not reach 'to', the reverse direction is tried.")]
    public static async Task<object> FindPath(
        WorkspaceHost host,
        [Description("Where the path starts: symbol id, 'file.cs:line:col', or a (dotted) name")] string from,
        [Description("Where the path ends")] string to,
        [Description("Maximum steps (default 8)")] int maxDepth = 8,
        [Description("Also follow calls bound only by name (default false)")] bool includeNameOnly = false,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var graph = GraphRag.For(snapshot).Graph;
        var (start, ambiguousStart) = NavigationTools.Resolve(snapshot, from);
        if (start is null)
        {
            return ambiguousStart!;
        }

        var (end, ambiguousEnd) = NavigationTools.Resolve(snapshot, to);
        if (end is null)
        {
            return ambiguousEnd!;
        }

        if (graph.NodeOf(start) is not { } a || graph.NodeOf(end) is not { } b)
        {
            throw new ArgumentException("Both symbols must be types or members of the workspace.");
        }

        maxDepth = Math.Clamp(maxDepth, 1, 20);
        var direction = "forward";
        var path = graph.ShortestPath(a, b, maxDepth, includeNameOnly);
        if (path == null)
        {
            direction = "reverse";
            path = graph.ShortestPath(b, a, maxDepth, includeNameOnly);
        }

        if (path == null)
        {
            return new FindPathResult(false, null, 0, [], $"Neither symbol reaches the other within {maxDepth} steps of calls, uses and type references. Try a larger maxDepth, includeNameOnly=true, or impact_analysis on one of them.");
        }

        var steps = path.Select(e => new PathStep(
            SymbolFormatter.ToDto(graph.Nodes[e.From], snapshot, withLineText: false),
            SymbolFormatter.ToDto(graph.Nodes[e.To], snapshot, withLineText: false),
            e.Kind switch
            {
                EdgeKind.Calls => "calls",
                EdgeKind.Uses => "uses",
                EdgeKind.ReferencesType => "uses type",
                _ => "dispatch",
            },
            e.Confidence.ToString(),
            e.First is { } r ? SymbolFormatter.ToLocation(new SourceLocation(r.File, r.Start, r.Start, r.Line, r.Column), snapshot) : null)).ToList();
        return new FindPathResult(true, direction, steps.Count, steps, null);
    }

    [McpServerTool(Name = "module_map", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Module map")]
    [Description("A map of the workspace for orientation: projects with their references and namespaces; communities of types that use each other more than the rest (Louvain over the reference graph), each labeled by its dominant namespace and most central types, with the communities it is most linked to; and entry points (members nothing in the workspace calls that reach much of it). Notes kept with annotate on projects, namespaces and types are included.")]
    public static async Task<ModuleMapResult> ModuleMap(
        WorkspaceHost host,
        [Description("Only this project's namespaces and the communities with its types (exact, case-insensitive)")] string? project = null,
        [Description("Maximum communities (default 20)")] int maxCommunities = 20,
        [Description("Types listed per community (default 5)")] int typesPerCommunity = 5,
        [Description("Include test projects (default true)")] bool includeTests = true,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var rag = GraphRag.For(snapshot);
        var graph = rag.Graph;
        var communities = rag.Communities;
        var annotations = host.Annotations;
        bool ProjectMatches(string? name) => project is null || string.Equals(name, project, StringComparison.OrdinalIgnoreCase);

        var typesByProject = snapshot.Index.Symbols
            .Where(s => s.Kind.IsType() && !s.IsExternal && s.Declarations.Count > 0)
            .GroupBy(s => s.Project ?? "")
            .ToDictionary(g => g.Key, g => g.ToList());
        var filesByProject = snapshot.Documents.Values.GroupBy(d => d.Project).ToDictionary(g => g.Key, g => g.Count());
        var projects = snapshot.Projects
            .Where(p => ProjectMatches(p.Name) && (includeTests || !SymbolFormatter.IsTestPath(Path.GetRelativePath(Path.GetDirectoryName(snapshot.RootPath)!, p.Path))))
            .Select(p =>
            {
                var types = typesByProject.GetValueOrDefault(p.Name) ?? [];
                var namespaces = types
                    .GroupBy(t => t.Namespace ?? "(global)")
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Take(15)
                    .Select(g => new NamespaceSummary(g.Key, g.Count(), annotations.NoteOf("N:" + g.Key)))
                    .ToList();
                return new ProjectSummary(
                    p.Name,
                    filesByProject.GetValueOrDefault(p.Name),
                    types.Count,
                    p.ProjectReferences.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList(),
                    namespaces,
                    annotations.NoteOf("project:" + p.Name));
            })
            .ToList();

        var summaries = new List<CommunitySummary>();
        foreach (var community in communities.All)
        {
            var types = community.Types.Select(t => graph.Nodes[t]).ToList();
            if ((project != null && !types.Any(t => ProjectMatches(t.Project))) || (!includeTests && types.All(t => IsTest(t, snapshot))))
            {
                continue;
            }

            if (summaries.Count >= Math.Max(1, maxCommunities))
            {
                break;
            }

            var namespaces = types.GroupBy(t => t.Namespace ?? "(global)").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
            var top = types.Take(Math.Max(1, typesPerCommunity)).ToList();
            var label = $"{namespaces[0]}: {string.Join(", ", top.Take(3).Select(t => t.Name))}";
            var links = communities.All
                .Where(other => other.Number != community.Number)
                .Select(other => (other.Number, Weight: communities.Link(community.Number, other.Number)))
                .Where(l => l.Weight > 0)
                .OrderByDescending(l => l.Weight)
                .Take(5)
                .ToDictionary(l => l.Number, l => Math.Round(l.Weight, 1));
            var notes = types.Select(t => annotations.Get(t.Id))
                .Concat(namespaces.Select(n => annotations.Get("N:" + n)))
                .OfType<Annotation>()
                .DistinctBy(a => a.Target)
                .Take(10)
                .ToList();
            summaries.Add(new CommunitySummary(
                community.Number,
                label,
                types.Count,
                namespaces.Take(5).ToList(),
                types.Select(t => t.Project ?? "").Distinct().Order(StringComparer.Ordinal).ToList(),
                top.Select(t => SymbolFormatter.ToDto(t, snapshot, withLineText: false)).ToList(),
                links,
                notes));
        }

        return new ModuleMapResult(projects, communities.All.Count, Math.Round(communities.Modularity, 3), summaries, EntryPoints(graph, snapshot, project, includeTests));
    }

    [McpServerTool(Name = "annotate", ReadOnly = false, Idempotent = true, OpenWorld = false, Title = "Annotate")]
    [Description("Keeps a short note about a symbol, namespace or project (what it is for, how a flow works, a pitfall), so that later search_code, get_context and module_map answers carry it: summaries of modules written once by the agent instead of re-derived. Notes are stored in .paspan/annotations.json next to the solution and survive reloads. Give an empty note to remove one; omit the note to read it.")]
    public static async Task<AnnotateResult> Annotate(
        WorkspaceHost host,
        [Description("Symbol id, 'file.cs:line:col', a (dotted) name, 'N:Namespace', or 'project:Name'")] string target,
        [Description("The note; empty removes it; omitted reads it")] string? note = null,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        string key;
        if (target.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
        {
            var name = target["project:".Length..].Trim();
            var projectModel = snapshot.Projects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No project named '{name}'.");
            key = "project:" + projectModel.Name;
        }
        else if (target.StartsWith("N:", StringComparison.Ordinal))
        {
            key = snapshot.Index.Get(target.Trim()) is { Kind: SymbolKind.Namespace } ns ? ns.Id : throw new ArgumentException($"No namespace {target[2..]} in the workspace.");
        }
        else
        {
            var (resolved, ambiguous) = NavigationTools.Resolve(snapshot, target);
            key = resolved?.Id ?? throw new ArgumentException(ambiguous!.Hint + " Candidates: " + string.Join(", ", ambiguous.Candidates.Select(c => c.Id)));
        }

        var annotations = host.Annotations;
        var annotation = note is null ? annotations.Get(key) : annotations.Set(key, note);
        return new AnnotateResult(key, annotation?.Note, annotation?.UpdatedAt, annotations.Count);
    }

    /// <summary>Members that nothing in the workspace calls or uses, ranked by how much of it they reach in three steps.</summary>
    private static List<SymbolDto> EntryPoints(CodeGraph graph, WorkspaceSnapshot snapshot, string? project, bool includeTests)
    {
        var candidates = new List<(int Node, int OutDegree)>();
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var s = graph.Nodes[i];
            if (s.Kind != SymbolKind.Method || s.Overrides != null || s.Implements.Count > 0 || (!string.Equals(s.Accessibility, "public", StringComparison.OrdinalIgnoreCase) && s.Name != "Main")
                || (!includeTests && IsTest(s, snapshot)) || (project != null && !string.Equals(s.Project, project, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (graph.In(i).Any(e => e.Kind is EdgeKind.Calls or EdgeKind.Uses))
            {
                continue;
            }

            var outDegree = graph.Out(i).Count(e => e.Kind is EdgeKind.Calls or EdgeKind.Uses);
            if (outDegree > 0)
            {
                candidates.Add((i, outDegree));
            }
        }

        int Reach(int node)
        {
            var seen = new HashSet<int> { node };
            var frontier = new List<int> { node };
            for (var d = 0; d < 3; d++)
            {
                frontier = frontier.SelectMany(n => graph.Out(n))
                    .Where(e => e.Kind is EdgeKind.Calls or EdgeKind.Uses && e.Confidence != Confidence.NameOnly && seen.Add(e.To))
                    .Select(e => e.To)
                    .ToList();
            }

            return seen.Count - 1;
        }

        return candidates
            .OrderByDescending(c => c.OutDegree)
            .Take(200)
            .Select(c => (c.Node, Reach: Reach(c.Node), Main: graph.Nodes[c.Node].Name == "Main"))
            .OrderByDescending(c => c.Main)
            .ThenBy(c => IsTest(graph.Nodes[c.Node], snapshot))
            .ThenByDescending(c => c.Reach)
            .ThenBy(c => graph.Nodes[c.Node].Id, StringComparer.Ordinal)
            .Take(10)
            .Select(c => SymbolFormatter.ToDto(graph.Nodes[c.Node], snapshot, withLineText: false))
            .ToList();
    }

    /// <summary>The neighbors of a node with the relation seen from it and a priority (lower comes first).</summary>
    private static IEnumerable<(int Other, string Relation, int Priority, GraphEdge Edge)> Neighbors(CodeGraph graph, int node)
    {
        foreach (var edge in graph.Out(node))
        {
            var (relation, priority) = edge.Kind switch
            {
                EdgeKind.Inherits => ("base class", 1),
                EdgeKind.Implements => ("interface", 1),
                EdgeKind.Overrides => ("overrides", 1),
                EdgeKind.ImplementsMember => ("implements", 1),
                EdgeKind.Calls => ("calls", 2),
                EdgeKind.Uses => ("uses", 3),
                EdgeKind.ReferencesType => ("uses type", 4),
                _ => ("member", 5),
            };
            yield return (edge.To, relation, priority, edge);
        }

        foreach (var edge in graph.In(node))
        {
            var (relation, priority) = edge.Kind switch
            {
                EdgeKind.Contains => ("declared in", 0),
                EdgeKind.Calls => ("called by", 2),
                EdgeKind.Overrides => ("overridden by", 2),
                EdgeKind.ImplementsMember => ("implemented by", 2),
                EdgeKind.Inherits => ("derived class", 3),
                EdgeKind.Implements => ("implemented by", 3),
                EdgeKind.Uses => ("used by", 3),
                _ => ("referenced by", 4),
            };
            yield return (edge.From, relation, priority, edge);
        }
    }

    private static string Via(EdgeKind kind) => kind switch
    {
        EdgeKind.Calls => "calls",
        EdgeKind.Uses => "uses",
        EdgeKind.ReferencesType => "references type",
        EdgeKind.Inherits => "inherits",
        EdgeKind.Implements => "implements",
        EdgeKind.Overrides => "overrides",
        EdgeKind.ImplementsMember => "implements member",
        _ => kind.ToString(),
    };

    /// <summary>Whether the symbol is in a test file, judged by the path below the solution's directory.</summary>
    private static bool IsTest(CodeSymbol symbol, WorkspaceSnapshot snapshot) =>
        symbol.Declarations.Count > 0 && SymbolFormatter.IsTestPath(Path.GetRelativePath(Path.GetDirectoryName(snapshot.RootPath)!, symbol.Declarations[0].File));

    /// <summary>
    /// The source of a declaration, dedented and cut at <paramref name="maxChars"/>; for a type, the lines before
    /// its first member and then its members' signatures.
    /// </summary>
    internal static (string? Code, int? Line) Code(CodeSymbol symbol, WorkspaceSnapshot snapshot, int maxChars)
    {
        if (symbol.Declarations.Count == 0 || !snapshot.Documents.TryGetValue(symbol.Declarations[0].File, out var document) || document.Utf8.IsEmpty)
        {
            return (null, null);
        }

        var location = symbol.Declarations[0];
        var text = document.Utf8.Span;
        var start = Math.Clamp(location.Start, 0, text.Length);
        var end = Math.Clamp(location.End, start, text.Length);
        var startLine = document.Lines.GetLineAndColumn(start).Line;
        string code;
        if (symbol.Kind.IsType() && symbol.Members.Count > 0)
        {
            var firstMember = symbol.Members
                .SelectMany(m => m.Declarations)
                .Where(d => SymbolIndexBuilder.PathComparer.Equals(d.File, location.File) && d.Start > start && d.Start < end)
                .Select(d => d.Start)
                .DefaultIfEmpty(end)
                .Min();
            var builder = new StringBuilder(Dedent(Encoding.UTF8.GetString(text[start..LineStart(text, firstMember)])));
            foreach (var member in symbol.Members)
            {
                builder.Append("    ").Append(member.Signature.Length > 0 ? member.Signature : member.Name);
                if (member.Declarations.Count > 0)
                {
                    builder.Append("  // line ").Append(member.Declarations[0].Line);
                }

                builder.Append('\n');
            }

            code = builder.ToString();
        }
        else
        {
            code = Dedent(Encoding.UTF8.GetString(text[LineStart(text, start)..end]));
        }

        if (code.Length > maxChars)
        {
            var cut = code.LastIndexOf('\n', Math.Max(0, maxChars - 1));
            code = code[..(cut > 0 ? cut : maxChars)] + "\n    …";
        }

        return (code, startLine);
    }

    private static int LineStart(ReadOnlySpan<byte> text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        while (offset > 0 && text[offset - 1] != (byte)'\n')
        {
            offset--;
        }

        return offset;
    }

    private static string Dedent(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())).TrimEnd() + "\n";
    }
}
