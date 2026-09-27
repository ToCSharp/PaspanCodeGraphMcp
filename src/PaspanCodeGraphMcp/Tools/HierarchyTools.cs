using System.ComponentModel;
using ModelContextProtocol.Server;
using PaspanCodeGraph;
using PaspanCodeGraph.Workspace;
using PaspanCodeGraphMcp.Symbols;

namespace PaspanCodeGraphMcp.Tools;

/// <summary>A base type or interface as written in a base list, and the workspace type it binds to (null for types from references).</summary>
public sealed record BaseTypeDto(string Written, string Id, SymbolDto? Symbol);

/// <param name="Depth">1 for direct derived types, 2 for theirs, and so on.</param>
public sealed record DerivedTypeDto(SymbolDto Symbol, int Depth);

public sealed record TypeHierarchyResult(
    SymbolDto Symbol,
    IReadOnlyList<BaseTypeDto> BaseTypes,
    IReadOnlyList<BaseTypeDto> Interfaces,
    IReadOnlyList<DerivedTypeDto> Derived,
    bool DerivedTruncated);

/// <param name="Relation">implements, overrides or derives.</param>
public sealed record RelatedSymbolDto(string Relation, SymbolDto Symbol);

public sealed record FindImplementationsResult(SymbolDto Symbol, int Count, IReadOnlyList<RelatedSymbolDto> Items, bool Truncated);

/// <param name="InMember">The id of the declaration the reference is in.</param>
/// <param name="Confidence">Exact where only a type can be written (signatures, base lists, 'new T()'); Inferred for names in expressions, which a local of the same name would hide.</param>
public sealed record ReferenceDto(int Line, int Column, string? LineText, string? InMember, string Confidence);

public sealed record FileReferences(string File, bool IsTest, int Count, IReadOnlyList<ReferenceDto> Items);

public sealed record FindReferencesResult(SymbolDto Symbol, int Total, int Offset, int Returned, bool Truncated, IReadOnlyList<FileReferences> Files);

[McpServerToolType]
public sealed class HierarchyTools
{
    [McpServerTool(Name = "type_hierarchy", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Type hierarchy")]
    [Description("Base class chain, interfaces and derived types of a type. Base types are bound like the compiler binds them (usings, aliases, nested types); ones from referenced assemblies have no symbol. transitive=true gives the whole base chain, all interfaces (inherited ones included) and all derived types at any depth.")]
    public static async Task<object> TypeHierarchy(
        WorkspaceHost host,
        [Description("Type id (T:...), 'file.cs:line:col', or a (dotted) type name")] string symbol,
        [Description("Whole chains instead of direct bases and derived types (default false)")] bool transitive = false,
        [Description("Maximum derived types (default 100)")] int maxDerived = 100,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var (type, ambiguous) = NavigationTools.Resolve(snapshot, symbol);
        if (type is null)
        {
            return ambiguous!;
        }

        if (!type.Kind.IsType())
        {
            throw new ArgumentException($"type_hierarchy needs a type; '{type.Signature}' is a {type.Kind}.");
        }

        var baseTypes = new List<BaseTypeDto>();
        var interfaces = new List<BaseTypeDto>();
        var seenInterfaces = new HashSet<string>(StringComparer.Ordinal);
        var seenTypes = new HashSet<CodeSymbol> { type };
        void AddInterfaces(CodeSymbol from, bool includeFirst)
        {
            for (var i = 0; i < from.Bases.Count; i++)
            {
                var link = from.Bases[i];
                if (IsBaseClass(from, i) && !includeFirst)
                {
                    continue;
                }

                if (!IsBaseClass(from, i) && seenInterfaces.Add(link.Id))
                {
                    interfaces.Add(ToDto(link, snapshot));
                    if (transitive && link.Symbol is { } face && seenTypes.Add(face))
                    {
                        AddInterfaces(face, true);
                    }
                }
            }
        }

        AddInterfaces(type, false);
        for (var current = type; ;)
        {
            if (current.Bases.Count == 0 || !IsBaseClass(current, 0))
            {
                break;
            }

            var link = current.Bases[0];
            baseTypes.Add(ToDto(link, snapshot));
            if (!transitive || link.Symbol is not { } next || !seenTypes.Add(next))
            {
                break;
            }

            AddInterfaces(next, false);
            current = next;
        }

        var derived = new List<DerivedTypeDto>();
        var truncated = false;
        var queue = new Queue<(CodeSymbol Type, int Depth)>([(type, 0)]);
        var seenDerived = new HashSet<CodeSymbol> { type };
        while (queue.Count > 0 && !truncated)
        {
            var (current, depth) = queue.Dequeue();
            foreach (var child in current.DerivedTypes.OrderBy(d => d.Id, StringComparer.Ordinal))
            {
                if (!seenDerived.Add(child))
                {
                    continue;
                }

                if (derived.Count == maxDerived)
                {
                    truncated = true;
                    break;
                }

                derived.Add(new DerivedTypeDto(SymbolFormatter.ToDto(child, snapshot), depth + 1));
                if (transitive)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return new TypeHierarchyResult(SymbolFormatter.ToDto(type, snapshot), baseTypes, interfaces, derived, truncated);
    }

    [McpServerTool(Name = "find_implementations", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Find implementations")]
    [Description("What implements or specializes a symbol. For an interface: the types implementing it (directly or through a base or derived type) and the interfaces deriving from it. For a class: its derived types at any depth. For an interface member: the members implementing it, matched by name and parameter types (explicit implementations included), and their overrides. For a virtual or abstract member: its overrides at any depth.")]
    public static async Task<object> FindImplementations(
        WorkspaceHost host,
        [Description("Type or member id, 'file.cs:line:col', or a (dotted) name")] string symbol,
        [Description("Maximum results (default 100)")] int maxResults = 100,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var (resolved, ambiguous) = NavigationTools.Resolve(snapshot, symbol);
        if (resolved is null)
        {
            return ambiguous!;
        }

        var items = new List<(string Relation, CodeSymbol Symbol)>();
        var seen = new HashSet<CodeSymbol> { resolved };
        void Add(string relation, CodeSymbol related)
        {
            if (seen.Add(related))
            {
                items.Add((relation, related));
            }
        }

        if (resolved.Kind.IsType())
        {
            // Every type below, in breadth-first order: a type deriving from an implementing class implements the interface too
            var queue = new Queue<CodeSymbol>([resolved]);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var child in current.DerivedTypes.OrderBy(d => d.Id, StringComparer.Ordinal))
                {
                    if (seen.Contains(child))
                    {
                        continue;
                    }

                    var relation = resolved.Kind == SymbolKind.Interface && child.Kind != SymbolKind.Interface ? "implements" : "derives";
                    Add(relation, child);
                    queue.Enqueue(child);
                }
            }
        }
        else
        {
            var queue = new Queue<CodeSymbol>([resolved]);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var implementation in current.ImplementedBy.OrderBy(d => d.Id, StringComparer.Ordinal))
                {
                    Add(implementation.ContainingType?.Kind == SymbolKind.Interface ? "overrides" : "implements", implementation);
                    queue.Enqueue(implementation);
                }

                foreach (var overriding in current.OverriddenBy.OrderBy(d => d.Id, StringComparer.Ordinal))
                {
                    Add("overrides", overriding);
                    queue.Enqueue(overriding);
                }
            }
        }

        return new FindImplementationsResult(
            SymbolFormatter.ToDto(resolved, snapshot),
            items.Count,
            items.Take(maxResults).Select(i => new RelatedSymbolDto(i.Relation, SymbolFormatter.ToDto(i.Symbol, snapshot))).ToList(),
            items.Count > maxResults);
    }

    [McpServerTool(Name = "find_references", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Find references")]
    [Description("References to a type across the workspace, grouped by file: in signatures, base lists, attributes, usings, generic arguments, 'new', casts, typeof, patterns and static member access. Names in expressions are Inferred (a local variable of the same name would hide the type). Only types for now; members come later.")]
    public static async Task<object> FindReferences(
        WorkspaceHost host,
        [Description("Type id (T:...), 'file.cs:line:col', or a (dotted) type name")] string symbol,
        [Description("Maximum references (default 200)")] int maxResults = 200,
        [Description("References to skip, for paging (default 0)")] int offset = 0,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var (resolved, ambiguous) = NavigationTools.Resolve(snapshot, symbol);
        if (resolved is null)
        {
            return ambiguous!;
        }

        if (!resolved.Kind.IsType())
        {
            throw new ArgumentException($"find_references supports types for now; '{resolved.Signature}' is a {resolved.Kind}.");
        }

        var all = snapshot.Index.ReferencesTo(resolved.Id);
        var page = all.Skip(Math.Max(0, offset)).Take(Math.Max(0, maxResults)).ToList();
        var files = page
            .GroupBy(r => r.File, SymbolIndexBuilder.PathComparer)
            .Select(g =>
            {
                var items = g.Select(r => new ReferenceDto(r.Line, r.Column, LineText(snapshot, r), r.InMember, r.Confidence.ToString())).ToList();
                return new FileReferences(g.Key, SymbolFormatter.IsTestPath(g.Key), items.Count, items);
            })
            .ToList();
        return new FindReferencesResult(SymbolFormatter.ToDto(resolved, snapshot), all.Count, offset, page.Count, offset + page.Count < all.Count, files);
    }

    /// <summary>Whether the base at <paramref name="index"/> is the base class (only the first one can be, and only of a class or record).</summary>
    internal static bool IsBaseClass(CodeSymbol type, int index) =>
        index == 0 && type.Kind is SymbolKind.Class or SymbolKind.Record
        && type.Bases[0] is var link && (link.Symbol is { } bound ? bound.Kind is SymbolKind.Class or SymbolKind.Record : !LooksLikeInterface(link.Written));

    /// <summary>For a base from a referenced assembly the kind is unknown: 'IDisposable' is taken for an interface by the naming convention.</summary>
    private static bool LooksLikeInterface(string written)
    {
        var name = written.Split('<')[0];
        name = name[(name.LastIndexOf('.') + 1)..];
        return name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);
    }

    private static BaseTypeDto ToDto(TypeLink link, WorkspaceSnapshot snapshot) =>
        new(link.Written, link.Id, link.Symbol is { } symbol ? SymbolFormatter.ToDto(symbol, snapshot, withLineText: false) : null);

    private static string? LineText(WorkspaceSnapshot snapshot, SymbolReference reference) =>
        snapshot.Documents.TryGetValue(reference.File, out var document) && reference.Line <= document.Lines.LineCount
            ? document.Lines.GetLineSpan(reference.Line).GetText(document.Utf8.Span).Trim()
            : null;
}
