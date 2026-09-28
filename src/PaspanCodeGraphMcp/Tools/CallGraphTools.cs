using System.ComponentModel;
using ModelContextProtocol.Server;
using PaspanCodeGraph;
using PaspanCodeGraph.Workspace;
using PaspanCodeGraphMcp.Symbols;

namespace PaspanCodeGraphMcp.Tools;

/// <param name="Caller">The member the call is in; null for a call outside any member (top-level statements).</param>
/// <param name="IsDirect">False when the call is to a member this one overrides or implements, and may dispatch here.</param>
/// <param name="Via">For an indirect call, the id of the member called.</param>
public sealed record CallSiteDto(SymbolDto? Caller, LocationDto Location, bool IsDirect, string Confidence, string? Via = null);

/// <param name="NameOnlyCallSites">Calls on receivers of unknown type that may reach this member by its name and parameter count; listed only with includeNameOnly=true.</param>
public sealed record FindCallersResult(
    SymbolDto Symbol,
    int TotalCallSites,
    int DistinctCallers,
    int Offset,
    int Returned,
    bool Truncated,
    IReadOnlyList<CallSiteDto> CallSites,
    int NameOnlyCallSites);

/// <param name="Symbol">The workspace member; null for a member of a type from a referenced assembly (see <see cref="Id"/>).</param>
/// <param name="Id">The member's id; for a member of a type from a referenced assembly, its documentation id (the type is written as in source when the assembly was not found).</param>
/// <param name="Implementations">Known overrides or implementations when the call is virtual or through an interface.</param>
public sealed record CalleeDto(
    string Id,
    SymbolDto? Symbol,
    int Calls,
    int FirstLine,
    string Confidence,
    bool IsVirtual,
    bool IsAbstract,
    bool IsInterfaceMember,
    bool IsOverride,
    int? Implementations);

/// <param name="Unresolved">Calls whose target was not found in the workspace nor recognized as a member of a referenced type, by name.</param>
/// <param name="NameOnly">Calls on receivers of unknown type, bound only by name; listed as callees with includeNameOnly=true.</param>
public sealed record FindCalleesResult(
    SymbolDto Symbol,
    int Internal,
    int External,
    int NameOnly,
    IReadOnlyList<CalleeDto> Callees,
    IReadOnlyList<string> Unresolved);

[McpServerToolType]
public sealed class CallGraphTools
{
    [McpServerTool(Name = "find_callers", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Find callers")]
    [Description("Who calls a method, constructor, property, indexer or event: each call site with the calling member. Calls are bound like the compiler binds them (receiver type, overloads, extension methods, lambdas); Exact when every type involved is known, Inferred when some were inferred. Calls to a member this one overrides or implements are included with isDirect=false. Calls on receivers of unknown type (bound only by name) are counted in nameOnlyCallSites and listed with includeNameOnly=true.")]
    public static async Task<object> FindCallers(
        WorkspaceHost host,
        [Description("Member id, 'file.cs:line:col', or a (dotted) name such as 'Parser.Parse'; for a member of a referenced assembly, its documentation id (M:System.Console.WriteLine(System.String))")] string symbol,
        [Description("Maximum call sites (default 50)")] int maxResults = 50,
        [Description("Call sites to skip, for paging (default 0)")] int offset = 0,
        [Description("Include call sites in test files (default true; they are flagged isTest)")] bool includeTests = true,
        [Description("Also list the calls bound only by name (default false)")] bool includeNameOnly = false,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var (resolved, ambiguous) = NavigationTools.ResolveExternal(snapshot, symbol) is { } external ? (external, null) : NavigationTools.Resolve(snapshot, symbol);
        if (resolved is null)
        {
            return ambiguous!;
        }

        if (!resolved.Kind.IsCallable() && resolved.Kind is not (SymbolKind.Property or SymbolKind.Indexer or SymbolKind.Event))
        {
            throw new ArgumentException($"find_callers needs a method, function, constructor, property, indexer, event or macro; '{resolved.Signature}' is a {resolved.Kind}. Use find_references for types and fields.");
        }

        var sites = new List<(SymbolReference Reference, CodeSymbol Target)>();
        var nameOnly = 0;
        foreach (var target in CalledAs(resolved))
        {
            foreach (var reference in snapshot.Index.ReferencesTo(NavigationTools.ReferenceId(target)))
            {
                if (!includeTests && SymbolFormatter.IsTestPath(reference.File))
                {
                    continue;
                }

                if (reference.Confidence == Confidence.NameOnly)
                {
                    nameOnly++;
                    if (!includeNameOnly)
                    {
                        continue;
                    }
                }

                sites.Add((reference, target));
            }
        }

        sites.Sort((a, b) =>
        {
            var byFile = SymbolIndexBuilder.PathComparer.Compare(a.Reference.File, b.Reference.File);
            return byFile != 0 ? byFile : a.Reference.Start.CompareTo(b.Reference.Start);
        });

        var page = sites.Skip(Math.Max(0, offset)).Take(Math.Max(0, maxResults))
            .Select(s => new CallSiteDto(
                s.Reference.InMember is { } caller && snapshot.Index.Get(caller) is { } callerSymbol ? SymbolFormatter.ToDto(callerSymbol, snapshot, withLineText: false) : null,
                ToLocation(s.Reference, snapshot),
                s.Target == resolved,
                s.Reference.Confidence.ToString(),
                s.Target == resolved ? null : s.Target.Id))
            .ToList();
        return new FindCallersResult(
            SymbolFormatter.ToDto(resolved, snapshot),
            sites.Count,
            sites.Select(s => s.Reference.InMember).Distinct(StringComparer.Ordinal).Count(),
            offset,
            page.Count,
            offset + page.Count < sites.Count,
            page,
            nameOnly);
    }

    [McpServerTool(Name = "find_callees", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Find callees")]
    [Description("What a member's body calls or uses: methods, constructors, properties, indexers, events and method groups, each bound to its declaration (overloads and extension methods resolved), with the number of calls and the first line. For a type, the callees of all its members. Members of types from referenced assemblies are listed with their documentation ids (external), bound through the reference assemblies of the framework and the NuGet packages. Virtual and interface targets are flagged with their known implementation count. Calls on receivers of unknown type are counted in nameOnly and listed with includeNameOnly=true; calls that could not be bound at all are listed by name in unresolved. Fields and enum members are not callees; use find_references for them.")]
    public static async Task<object> FindCallees(
        WorkspaceHost host,
        [Description("Member or type id, 'file.cs:line:col', or a (dotted) name")] string symbol,
        [Description("Include members of types from referenced assemblies (default true)")] bool includeExternal = true,
        [Description("Also list the callees bound only by name (default false)")] bool includeNameOnly = false,
        [Description("Maximum callees (default 100)")] int maxResults = 100,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var (resolved, ambiguous) = NavigationTools.Resolve(snapshot, symbol);
        if (resolved is null)
        {
            return ambiguous!;
        }

        if (resolved.Kind is SymbolKind.Namespace)
        {
            throw new ArgumentException($"find_callees needs a member with a body or a type; '{resolved.Signature}' is a namespace.");
        }

        var members = resolved.Kind.IsType() ? [resolved, .. resolved.Members.Where(m => !m.Kind.IsType())] : new List<CodeSymbol> { resolved };
        var byId = new Dictionary<string, (int Calls, int FirstLine, Confidence Best)>(StringComparer.Ordinal);
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var nameOnlyCalls = 0;
        foreach (var member in members)
        {
            foreach (var (targetId, reference) in snapshot.Index.ReferencesFrom(member.Id))
            {
                if (targetId.StartsWith(ReferenceTargets.Unresolved, StringComparison.Ordinal))
                {
                    unresolved.Add(targetId[ReferenceTargets.Unresolved.Length..]);
                    continue;
                }

                var isExternal = targetId.StartsWith(ReferenceTargets.External, StringComparison.Ordinal);
                if (isExternal && targetId.Length > ReferenceTargets.External.Length + 1 && targetId[ReferenceTargets.External.Length] is 'T' or 'F')
                {
                    // Types and fields of references are not callees
                    continue;
                }

                if (!isExternal && snapshot.Index.Get(targetId) is not { Kind: SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Property or SymbolKind.Indexer or SymbolKind.Event or SymbolKind.Operator or SymbolKind.Function or SymbolKind.Destructor or SymbolKind.Macro })
                {
                    continue;
                }

                if (isExternal && !includeExternal)
                {
                    continue;
                }

                if (reference.Confidence == Confidence.NameOnly)
                {
                    nameOnlyCalls++;
                    if (!includeNameOnly)
                    {
                        continue;
                    }
                }

                byId[targetId] = byId.TryGetValue(targetId, out var existing)
                    ? (existing.Calls + 1, Math.Min(existing.FirstLine, reference.Line), existing.Best < reference.Confidence ? existing.Best : reference.Confidence)
                    : (1, reference.Line, reference.Confidence);
            }
        }

        var callees = byId
            .Select(e => (Id: e.Key, e.Value.Calls, e.Value.FirstLine, e.Value.Best, Symbol: snapshot.Index.Get(e.Key)))
            .OrderBy(e => e.Symbol == null ? 1 : 0)
            .ThenBy(e => e.FirstLine)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, maxResults))
            .Select(e => ToCallee(e.Id, e.Symbol, e.Calls, e.FirstLine, e.Best, snapshot))
            .ToList();
        var internalCount = byId.Keys.Count(k => !k.StartsWith(ReferenceTargets.External, StringComparison.Ordinal));
        return new FindCalleesResult(SymbolFormatter.ToDto(resolved, snapshot), internalCount, byId.Count - internalCount, nameOnlyCalls, callees, unresolved.ToList());
    }

    private static CalleeDto ToCallee(string id, CodeSymbol? symbol, int calls, int firstLine, Confidence confidence, WorkspaceSnapshot snapshot)
    {
        if (symbol == null)
        {
            return new CalleeDto(id[ReferenceTargets.External.Length..], null, calls, firstLine, confidence.ToString(), false, false, false, false, null);
        }

        var isInterface = symbol.ContainingType?.Kind == SymbolKind.Interface && !symbol.Modifiers.Contains("static");
        var isVirtual = symbol.Modifiers.Contains("virtual");
        var isAbstract = symbol.Modifiers.Contains("abstract");
        var isOverride = symbol.Modifiers.Contains("override");
        int? implementations = isInterface || isVirtual || isAbstract || isOverride ? Implementations(symbol).Count : null;
        return new CalleeDto(symbol.Id, SymbolFormatter.ToDto(symbol, snapshot, withLineText: false), calls, firstLine, confidence.ToString(), isVirtual, isAbstract, isInterface, isOverride, implementations);
    }

    /// <summary>The overrides and implementations of a member, at any depth.</summary>
    internal static HashSet<CodeSymbol> Implementations(CodeSymbol member)
    {
        var result = new HashSet<CodeSymbol>();
        var queue = new Queue<CodeSymbol>([member]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in current.OverriddenBy.Concat(current.ImplementedBy))
            {
                if (result.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return result;
    }

    /// <summary>The member itself and the members it overrides or implements: a call to any of them may run it.</summary>
    internal static List<CodeSymbol> CalledAs(CodeSymbol member)
    {
        var result = new List<CodeSymbol> { member };
        var seen = new HashSet<CodeSymbol> { member };
        for (var i = 0; i < result.Count; i++)
        {
            var current = result[i];
            foreach (var next in current.Implements.Prepend(current.Overrides))
            {
                if (next != null && seen.Add(next))
                {
                    result.Add(next);
                }
            }
        }

        return result;
    }

    internal static LocationDto ToLocation(SymbolReference reference, WorkspaceSnapshot snapshot)
    {
        string? lineText = null;
        if (snapshot.Documents.TryGetValue(reference.File, out var document) && reference.Line <= document.Lines.LineCount)
        {
            lineText = document.Lines.GetLineSpan(reference.Line).GetText(document.Utf8.Span).Trim();
        }

        return new LocationDto(reference.File, reference.Line, reference.Column, lineText, SymbolFormatter.IsTestPath(reference.File));
    }
}
