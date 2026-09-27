using System.Text.RegularExpressions;

namespace PaspanCodeGraph;

/// <summary>
/// Collects the symbols of a workspace. Declarations with the same id (partial types and members, namespaces
/// declared in several files) are merged into one symbol.
/// </summary>
public sealed class SymbolIndexBuilder
{
    private readonly Dictionary<string, CodeSymbol> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(CodeSymbol Symbol, SourceLocation Location)>> _byFile = new(PathComparer);
    private readonly Dictionary<string, List<SymbolReference>> _references = new(StringComparer.Ordinal);

    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The symbol with <paramref name="id"/>, created with <paramref name="kind"/> and <paramref name="name"/>
    /// under <paramref name="container"/> when it is new.
    /// </summary>
    public CodeSymbol GetOrAdd(string id, SymbolKind kind, string name, CodeSymbol? container, out bool created)
    {
        if (_byId.TryGetValue(id, out var existing))
        {
            created = false;
            return existing;
        }

        var symbol = new CodeSymbol(id, kind, name) { Container = container };
        container?.Members.Add(symbol);
        _byId.Add(id, symbol);
        created = true;
        return symbol;
    }

    public CodeSymbol? Get(string id) => _byId.GetValueOrDefault(id);

    public IEnumerable<CodeSymbol> Symbols => _byId.Values;

    /// <summary>Records a reference to the symbol with <paramref name="id"/>.</summary>
    public void AddReference(string id, SymbolReference reference)
    {
        if (!_references.TryGetValue(id, out var list))
        {
            _references[id] = list = [];
        }

        list.Add(reference);
    }

    /// <summary>Records a declaration of <paramref name="symbol"/>.</summary>
    public void AddDeclaration(CodeSymbol symbol, SourceLocation location)
    {
        symbol.Declarations.Add(location);
        if (!_byFile.TryGetValue(location.File, out var list))
        {
            _byFile[location.File] = list = [];
        }

        list.Add((symbol, location));
    }

    public SymbolIndex Build() => new(_byId, _byFile, _references);
}

/// <summary>The declared symbols of a workspace, by id, by file and by name.</summary>
public sealed class SymbolIndex
{
    private readonly Dictionary<string, CodeSymbol> _byId;
    private readonly Dictionary<string, List<(CodeSymbol Symbol, SourceLocation Location)>> _byFile;
    private readonly Dictionary<string, List<SymbolReference>> _references;
    private readonly Dictionary<string, List<(CodeSymbol Target, SymbolReference Reference)>> _referencesByFile = new(SymbolIndexBuilder.PathComparer);
    private readonly Dictionary<string, List<(string TargetId, SymbolReference Reference)>> _referencesFrom = new(StringComparer.Ordinal);

    internal SymbolIndex(
        Dictionary<string, CodeSymbol> byId,
        Dictionary<string, List<(CodeSymbol Symbol, SourceLocation Location)>> byFile,
        Dictionary<string, List<SymbolReference>> references)
    {
        _byId = byId;
        _byFile = byFile;
        _references = references;
        foreach (var (id, list) in _references)
        {
            list.Sort((a, b) => a.File == b.File ? a.Start.CompareTo(b.Start) : string.CompareOrdinal(a.File, b.File));
            foreach (var reference in list)
            {
                if (reference.InMember is { } from)
                {
                    if (!_referencesFrom.TryGetValue(from, out var fromList))
                    {
                        _referencesFrom[from] = fromList = [];
                    }

                    fromList.Add((id, reference));
                }
            }

            if (_byId.TryGetValue(id, out var target))
            {
                foreach (var reference in list)
                {
                    if (!_referencesByFile.TryGetValue(reference.File, out var inFile))
                    {
                        _referencesByFile[reference.File] = inFile = [];
                    }

                    inFile.Add((target, reference));
                }
            }
        }

        // At one position the surest reference comes first
        foreach (var list in _referencesByFile.Values)
        {
            list.Sort((a, b) => a.Reference.Start != b.Reference.Start ? a.Reference.Start.CompareTo(b.Reference.Start) : a.Reference.Confidence.CompareTo(b.Reference.Confidence));
        }

        foreach (var list in _referencesFrom.Values)
        {
            list.Sort((a, b) => a.Reference.File == b.Reference.File ? a.Reference.Start.CompareTo(b.Reference.Start) : string.CompareOrdinal(a.Reference.File, b.Reference.File));
        }

        foreach (var list in _byFile.Values)
        {
            list.Sort((a, b) => a.Location.Start.CompareTo(b.Location.Start));
        }
    }

    public static SymbolIndex Empty { get; } = new SymbolIndexBuilder().Build();

    public int Count => _byId.Count;

    public IEnumerable<CodeSymbol> Symbols => _byId.Values;

    public CodeSymbol? Get(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The references to the symbol with <paramref name="id"/>, by file and position.</summary>
    public IReadOnlyList<SymbolReference> ReferencesTo(string id) => _references.TryGetValue(id, out var list) ? list : [];

    /// <summary>
    /// The references made in the declaration with <paramref name="memberId"/> (its body, signature and attributes),
    /// with their targets: workspace symbol ids, or <see cref="ReferenceTargets"/> ids.
    /// </summary>
    public IReadOnlyList<(string TargetId, SymbolReference Reference)> ReferencesFrom(string memberId) =>
        _referencesFrom.TryGetValue(memberId, out var list) ? list : [];

    /// <summary>The number of references to workspace members (not types or namespaces), by confidence, and of unbound calls.</summary>
    public (int Exact, int Inferred, int NameOnly, int External, int Unresolved) MemberReferenceCounts()
    {
        int exact = 0, inferred = 0, nameOnly = 0, external = 0, unresolved = 0;
        foreach (var (id, list) in _references)
        {
            if (id.StartsWith(ReferenceTargets.External, StringComparison.Ordinal))
            {
                // Members only, not the types of references named in the sources
                external += id.AsSpan(ReferenceTargets.External.Length).StartsWith("T:") ? 0 : list.Count;
            }
            else if (id.StartsWith(ReferenceTargets.Unresolved, StringComparison.Ordinal))
            {
                unresolved += list.Count;
            }
            else if (_byId.TryGetValue(id, out var target) && !target.Kind.IsType() && target.Kind != SymbolKind.Namespace)
            {
                foreach (var reference in list)
                {
                    switch (reference.Confidence)
                    {
                        case Confidence.Exact:
                            exact++;
                            break;
                        case Confidence.Inferred:
                            inferred++;
                            break;
                        default:
                            nameOnly++;
                            break;
                    }
                }
            }
        }

        return (exact, inferred, nameOnly, external, unresolved);
    }

    /// <summary>The references in <paramref name="file"/> (a full path) and what they refer to, in source order.</summary>
    public IReadOnlyList<(CodeSymbol Target, SymbolReference Reference)> ReferencesInFile(string file) =>
        _referencesByFile.TryGetValue(file, out var list) ? list : [];

    /// <summary>The reference whose name covers a 1-based line and column (UTF-16) of <paramref name="file"/>, or null.</summary>
    public (CodeSymbol Target, SymbolReference Reference)? ReferenceAt(string file, int line, int column)
    {
        foreach (var entry in ReferencesInFile(file))
        {
            var reference = entry.Reference;
            if (reference.Line == line && reference.Column <= column && column <= reference.Column + entry.Target.Name.Length)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The number of references recorded.</summary>
    public int ReferenceCount => _references.Values.Sum(l => l.Count);

    /// <summary>The declarations in <paramref name="file"/> (a full path), in source order.</summary>
    public IReadOnlyList<(CodeSymbol Symbol, SourceLocation Location)> InFile(string file) =>
        _byFile.TryGetValue(file, out var list) ? list : [];

    /// <summary>
    /// The innermost declaration in <paramref name="file"/> whose span contains <paramref name="offset"/>, or null.
    /// </summary>
    public (CodeSymbol Symbol, SourceLocation Location)? DeclarationAt(string file, int offset)
    {
        (CodeSymbol, SourceLocation)? best = null;
        foreach (var (symbol, location) in InFile(file))
        {
            if (location.Start > offset)
            {
                break;
            }

            // Later starts inside an earlier span are nested in it
            if (offset < location.End && symbol.Kind != SymbolKind.Namespace)
            {
                best = (symbol, location);
            }
        }

        return best;
    }

    /// <summary>
    /// Types and members by (dotted) name. The last segment matches as a substring, a whole name
    /// (<paramref name="exact"/>) or a pattern with <c>*</c> and <c>?</c>; the other segments must appear, in
    /// order, among the containing types and namespaces. Exact matches come first, then shorter names.
    /// </summary>
    public List<CodeSymbol> Search(string query, SymbolKind? kind = null, string? project = null, bool exact = false, int limit = int.MaxValue)
    {
        var parts = query.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return [];
        }

        var last = parts[^1];
        var containers = parts[..^1];
        var wildcard = last.Contains('*') || last.Contains('?');
        var pattern = wildcard ? new Regex("^" + Regex.Escape(last).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase) : null;
        Func<string, bool> matches = wildcard
            ? pattern!.IsMatch
            : exact
                ? name => string.Equals(name, last, StringComparison.OrdinalIgnoreCase)
                : name => name.Contains(last, StringComparison.OrdinalIgnoreCase);

        return _byId.Values
            .Where(s => s.Kind != SymbolKind.Namespace && matches(s.Name))
            .Where(s => kind is null || s.Kind == kind)
            .Where(s => project is null || string.Equals(s.Project, project, StringComparison.OrdinalIgnoreCase))
            .Where(s => ContainersMatch(s, containers))
            .OrderByDescending(s => string.Equals(s.Name, last, StringComparison.Ordinal))
            .ThenByDescending(s => string.Equals(s.Name, last, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.Name.Length)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>"Ns.Type.Member": every given container must appear, in order, in the symbol's containing chain.</summary>
    private static bool ContainersMatch(CodeSymbol symbol, string[] containers)
    {
        if (containers.Length == 0)
        {
            return true;
        }

        // Innermost first; a namespace symbol contributes each part of its dotted name
        var chain = new List<string>();
        for (var c = symbol.Container; c is not null; c = c.Container)
        {
            chain.Add(c.Name);
        }

        var index = 0;
        for (var i = containers.Length - 1; i >= 0; i--)
        {
            while (index < chain.Count && !string.Equals(chain[index], containers[i], StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }

            if (index == chain.Count)
            {
                return false;
            }

            index++;
        }

        return true;
    }
}
