using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using PaspanCodeGraph;
using PaspanCodeGraph.Workspace;
using PaspanCodeGraphMcp.Symbols;

namespace PaspanCodeGraphMcp.Tools;

public sealed record FindSymbolResult(string Query, int Count, IReadOnlyList<SymbolDto> Items, bool Truncated);

public sealed record ParameterDto(string Name, string? Type, string? Modifier, string? Default);

public sealed record SymbolInfoResult(
    SymbolDto Symbol,
    string? Summary,
    string Accessibility,
    IReadOnlyList<string> Modifiers,
    string? Type,
    IReadOnlyList<ParameterDto> Parameters,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<LocationDto> Declarations);

public sealed record DefinitionResult(SymbolDto Symbol, IReadOnlyList<LocationDto> Declarations);

public sealed record MemberDto(SymbolDto Symbol, string Accessibility, bool IsStatic, string? InheritedFrom);

public sealed record TypeMembersResult(SymbolDto Type, int Count, IReadOnlyList<MemberDto> Members, bool Truncated, IReadOnlyList<string> UnresolvedBaseTypes);

public sealed record OutlineItem(string Id, string Kind, string Name, string Signature, int Line, int Column, int EndLine, int Depth);

public sealed record FileOutlineResult(string File, string? Project, int SyntaxErrors, IReadOnlyList<OutlineItem> Items);

[McpServerToolType]
public sealed partial class NavigationTools
{
    /// <summary>Ambiguity lists are meant to be scanned by an agent; beyond this it should narrow the query.</summary>
    private const int MaxCandidates = 20;

    [McpServerTool(Name = "find_symbol", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Find symbol declarations")]
    [Description("Find type/member declarations by name. Query is a (dotted) name, e.g. 'Trim', 'Pixmap.Trim', 'Connector.Ocr.Pixmap', with optional * and ? wildcards in the last segment. Substring match by default ('Parse' also finds ParseSslr and _parser); exact=true matches the whole name. Container segments always match whole names. Returns ids usable by every other tool.")]
    public static async Task<FindSymbolResult> FindSymbol(
        WorkspaceHost host,
        [Description("Name or dotted path to search, wildcards allowed: 'Parse', 'Parser.Parse', 'Recognize*'")] string query,
        [Description("Kind filter: Class, Interface, Struct, Enum, Record, RecordStruct, Delegate, Method, Constructor, Destructor, Property, Indexer, Field, Event, Operator, EnumMember")] string? kind = null,
        [Description("Restrict to one project by name")] string? project = null,
        [Description("Match the last segment as a whole name instead of a substring (default false)")] bool exact = false,
        [Description("Maximum results (default 50)")] int maxResults = 50,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var kindFilter = ParseKind(kind);
        var symbols = snapshot.Index.Search(query, kindFilter, project, exact, maxResults + 1);
        var items = symbols.Take(maxResults).Select(s => SymbolFormatter.ToDto(s, snapshot)).ToList();
        return new FindSymbolResult(query, items.Count, items, symbols.Count > maxResults);
    }

    [McpServerTool(Name = "symbol_info", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Symbol details")]
    [Description("Signature, documentation summary, accessibility, modifiers, parameters, type parameters, base types as written, and all declaration locations (partial types).")]
    public static async Task<object> SymbolInfo(
        WorkspaceHost host,
        [Description("Symbol id (T:/M:/P:/F:/E:), 'file.cs:line:col', or a (dotted) name")] string symbol,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var (resolved, ambiguous) = Resolve(snapshot, symbol);
        if (resolved is null)
        {
            return ambiguous!;
        }

        return new SymbolInfoResult(
            SymbolFormatter.ToDto(resolved, snapshot),
            SymbolFormatter.Summary(resolved),
            resolved.Accessibility,
            resolved.Modifiers,
            resolved.Type,
            resolved.Parameters.Select(p => new ParameterDto(p.Name, p.Type, p.Modifier, p.DefaultValue)).ToList(),
            resolved.TypeParameters,
            resolved.BaseTypes,
            resolved.Declarations.Select(d => SymbolFormatter.ToLocation(d, snapshot)).ToList());
    }

    [McpServerTool(Name = "go_to_definition", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Go to definition")]
    [Description("Declaration location(s) of a symbol, including all parts of partial types. A position resolves to the declaration whose name is there, else to the innermost declaration containing it (references are not resolved yet).")]
    public static async Task<object> GoToDefinition(
        WorkspaceHost host,
        [Description("Symbol id, 'file.cs:line:col', or a (dotted) name")] string symbol,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var (resolved, ambiguous) = Resolve(snapshot, symbol);
        if (resolved is null)
        {
            return ambiguous!;
        }

        return new DefinitionResult(
            SymbolFormatter.ToDto(resolved, snapshot),
            resolved.Declarations.Select(d => SymbolFormatter.ToLocation(d, snapshot)).ToList());
    }

    [McpServerTool(Name = "type_members", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Type members")]
    [Description("Outline of a type: its declared members with ids, kinds, accessibility. Cheap way to get member ids. includeInherited adds the members of base types declared in the workspace, found by name (base types are not bound, so a name shared by several types may pick the wrong one).")]
    public static async Task<object> TypeMembers(
        WorkspaceHost host,
        [Description("Type id (T:...), 'file.cs:line:col', or a (dotted) type name")] string symbol,
        [Description("Kind filter: Method, Constructor, Property, Field, Event, Class, ...")] string? kind = null,
        [Description("Include members of base types declared in the workspace (default false)")] bool includeInherited = false,
        [Description("Maximum members (default 200)")] int maxResults = 200,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var (type, ambiguous) = Resolve(snapshot, symbol);
        if (type is null)
        {
            return ambiguous!;
        }

        if (!type.Kind.IsType())
        {
            throw new ArgumentException($"type_members needs a type; '{type.Signature}' is a {type.Kind}.");
        }

        var kindFilter = ParseKind(kind);
        var members = new List<MemberDto>();
        var unresolved = new List<string>();
        var visited = new HashSet<CodeSymbol>();
        var pending = new Queue<CodeSymbol>([type]);
        while (pending.Count > 0 && visited.Count < 32)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            var inheritedFrom = current == type ? null : current.Id;
            foreach (var member in current.Members)
            {
                if (kindFilter is null || member.Kind == kindFilter)
                {
                    members.Add(new MemberDto(SymbolFormatter.ToDto(member, snapshot, withLineText: false), member.Accessibility, member.Modifiers.Contains("static"), inheritedFrom));
                }
            }

            if (!includeInherited)
            {
                break;
            }

            foreach (var baseType in current.BaseTypes)
            {
                if (FindBaseType(snapshot.Index, current, baseType) is { } found)
                {
                    pending.Enqueue(found);
                }
                else if (!unresolved.Contains(baseType))
                {
                    unresolved.Add(baseType);
                }
            }
        }

        return new TypeMembersResult(SymbolFormatter.ToDto(type, snapshot), members.Count, members.Take(maxResults).ToList(), members.Count > maxResults, unresolved);
    }

    /// <summary>
    /// A base type by its name as written (<c>Ns.Base&lt;T&gt;</c>): a workspace type with that name and arity,
    /// preferring one in the namespace of <paramref name="derived"/> or an enclosing one.
    /// </summary>
    internal static CodeSymbol? FindBaseType(SymbolIndex index, CodeSymbol derived, string written)
    {
        var withoutArguments = GenericArguments().Replace(written, "");
        var arity = CountTypeArguments(written);
        var name = withoutArguments.Replace("global::", "").Trim().TrimEnd('?');
        var candidates = index.Search(name, exact: true)
            .Where(c => c.Kind.IsType() && c.TypeParameters.Count == arity && c != derived)
            .ToList();
        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        // The closest namespace: the one sharing the longest prefix with the derived type's
        return candidates
            .OrderByDescending(c => CommonPrefixLength(c.Namespace ?? "", derived.Namespace ?? ""))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .First();
    }

    private static int CountTypeArguments(string written)
    {
        var open = written.LastIndexOf('<');
        if (open < 0)
        {
            return 0;
        }

        // Count top-level commas of the last argument list
        var depth = 0;
        var count = 1;
        for (var i = open + 1; i < written.Length; i++)
        {
            switch (written[i])
            {
                case '<' or '(' or '[':
                    depth++;
                    break;
                case '>' or ')' or ']' when depth > 0:
                    depth--;
                    break;
                case ',' when depth == 0:
                    count++;
                    break;
            }
        }

        return count;
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var i = 0;
        while (i < aParts.Length && i < bParts.Length && aParts[i] == bParts[i])
        {
            i++;
        }

        return i;
    }

    [GeneratedRegex(@"<[^<>]*(?:<[^<>]*(?:<[^<>]*>[^<>]*)*>[^<>]*)*>")]
    private static partial Regex GenericArguments();

    [McpServerTool(Name = "file_outline", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "File outline")]
    [Description("The declarations of a file in source order (namespaces, types, members, enum members) with ids, lines and nesting depth, plus the number of syntax errors in the file.")]
    public static async Task<FileOutlineResult> FileOutline(
        WorkspaceHost host,
        [Description("File path: full, relative to the workspace, or a unique path suffix such as 'Parser/Lexer.cs'")] string file,
        CancellationToken ct = default)
    {
        var snapshot = await host.RequireSnapshotAsync(ct);
        var document = snapshot.FindDocument(file)
            ?? throw new FileNotFoundException($"'{file}' is not a file of the loaded workspace (or the name matches several files).");

        var items = new List<OutlineItem>();
        var open = new Stack<int>();
        var declarations = snapshot.Index.InFile(document.Path);
        for (var i = 0; i < declarations.Count; i++)
        {
            var (symbol, location) = declarations[i];

            // 'namespace A.B' declares A and A.B: show only A.B
            if (symbol.Kind == SymbolKind.Namespace && i + 1 < declarations.Count
                && declarations[i + 1].Symbol.Kind == SymbolKind.Namespace && declarations[i + 1].Location.Start == location.Start)
            {
                continue;
            }

            while (open.Count > 0 && location.Start >= open.Peek())
            {
                open.Pop();
            }

            var endLine = document.Lines.GetLineAndColumn(Math.Min(location.End, document.Utf8.Length)).Line;
            items.Add(new OutlineItem(symbol.Id, symbol.Kind.ToString(), symbol.Name, symbol.Signature, location.Line, location.Column, endLine, open.Count));
            open.Push(location.End);
        }

        return new FileOutlineResult(document.Path, document.Project, document.Errors.Count + (document.Failure is null ? 0 : 1), items);
    }

    private static (CodeSymbol? Symbol, AmbiguousSymbol? Ambiguous) Resolve(WorkspaceSnapshot snapshot, string symbol)
    {
        var resolution = SymbolResolver.Resolve(snapshot, symbol);
        if (resolution.Symbol is { } resolved)
        {
            return (resolved, null);
        }

        return (null, new AmbiguousSymbol(
            symbol,
            resolution.Candidates.Count,
            resolution.Candidates.Take(MaxCandidates).Select(c => SymbolFormatter.ToDto(c, snapshot)).ToList()));
    }

    private static SymbolKind? ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        return SymbolKinds.Parse(kind) ?? throw new ArgumentException(
            $"Unknown kind '{kind}'. Use one of: {string.Join(", ", Enum.GetNames<SymbolKind>())}.");
    }
}
