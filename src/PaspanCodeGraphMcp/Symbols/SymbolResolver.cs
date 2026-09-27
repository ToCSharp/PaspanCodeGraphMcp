using PaspanCodeGraph;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraphMcp.Symbols;

/// <summary>The symbol a <see cref="SymbolRef"/> names, or the candidates when it names several.</summary>
public sealed record Resolution(CodeSymbol? Symbol, IReadOnlyList<CodeSymbol> Candidates);

public static class SymbolResolver
{
    public static Resolution Resolve(WorkspaceSnapshot snapshot, string input) => SymbolRef.Parse(input) switch
    {
        SymbolRef.DeclarationId id => ResolveId(snapshot.Index, id.Id),
        SymbolRef.Position position => ResolvePosition(snapshot, position),
        SymbolRef.Name name => ResolveName(snapshot.Index, name.Query),
        _ => throw new ArgumentException($"Unrecognized symbol: {input}"),
    };

    /// <summary>
    /// The symbol with the id; else the members with that name and any parameters (<c>M:Ns.Type.Method</c> for all
    /// overloads), or with the same parameter types written without namespaces (Roslyn's ids qualify them fully).
    /// </summary>
    private static Resolution ResolveId(SymbolIndex index, string id)
    {
        if (index.Get(id) is { } exact)
        {
            return new Resolution(exact, [exact]);
        }

        // Roslyn's DocumentationCommentId.CreateDeclarationId appends the return type of every method
        // ("M:Ns.Type.Parse(System.String)~System.Int32"); documentation comments have it only for conversions
        var tilde = id.LastIndexOf('~');
        if (tilde > 0 && !id.Contains(".op_Implicit", StringComparison.Ordinal) && !id.Contains(".op_Explicit", StringComparison.Ordinal))
        {
            id = id[..tilde];
            if (index.Get(id) is { } withoutReturnType)
            {
                return new Resolution(withoutReturnType, [withoutReturnType]);
            }
        }

        var parenthesis = id.IndexOf('(');
        var path = parenthesis < 0 ? id : id[..parenthesis];
        var candidates = index.Symbols
            .Where(s => s.Id.Length > path.Length && s.Id.StartsWith(path, StringComparison.Ordinal) && s.Id[path.Length] is '(' or '~' or '`')
            .ToList();
        if (parenthesis >= 0 && candidates.Count > 1)
        {
            var simplified = SimplifyParameters(id[parenthesis..]);
            var matching = candidates.Where(c => SimplifyParameters(c.Id[path.Length..]) == simplified).ToList();
            if (matching.Count > 0)
            {
                candidates = matching;
            }
        }

        if (candidates.Count == 0)
        {
            throw new KeyNotFoundException($"No declaration with id '{id}'. Use find_symbol to look it up by name.");
        }

        return new Resolution(candidates.Count == 1 ? candidates[0] : null, Order(candidates));
    }

    /// <summary>Drops namespaces from the type names of a parameter list: <c>(System.Collections.Generic.List{System.Int32})</c> becomes <c>(List{Int32})</c>.</summary>
    private static string SimplifyParameters(string parameters)
    {
        var builder = new System.Text.StringBuilder(parameters.Length);
        var segmentStart = 0;
        for (var i = 0; i <= parameters.Length; i++)
        {
            var c = i < parameters.Length ? parameters[i] : '\0';
            if (char.IsLetterOrDigit(c) || c is '_' or '`' or '.')
            {
                continue;
            }

            var segment = parameters[segmentStart..i];
            var dot = segment.LastIndexOf('.');
            builder.Append(dot >= 0 ? segment[(dot + 1)..] : segment);
            if (c != '\0')
            {
                builder.Append(c);
            }

            segmentStart = i + 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The declaration whose name is at the position; else the type a reference there names; else the innermost
    /// declaration containing it.
    /// </summary>
    private static Resolution ResolvePosition(WorkspaceSnapshot snapshot, SymbolRef.Position position)
    {
        var document = snapshot.FindDocument(position.FilePath)
            ?? throw new FileNotFoundException($"'{position.FilePath}' is not a file of the loaded workspace.");
        if (position.Line < 1 || position.Line > document.Lines.LineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(position), $"{position.FilePath} has {document.Lines.LineCount} lines; line {position.Line} is out of range.");
        }

        var declarations = snapshot.Index.InFile(document.Path);
        var onName = declarations
            .Where(d => d.Location.Line == position.Line && d.Location.Column <= position.Column && position.Column <= d.Location.Column + NameLength(d.Symbol))
            .Select(d => d.Symbol)
            .LastOrDefault();
        if (onName != null)
        {
            return new Resolution(onName, [onName]);
        }

        if (snapshot.Index.ReferenceAt(document.Path, position.Line, position.Column) is { } reference)
        {
            return new Resolution(reference.Target, [reference.Target]);
        }

        var offset = document.Lines.GetOffset(position.Line, Math.Max(1, position.Column));
        if (snapshot.Index.DeclarationAt(document.Path, offset) is { } declaration)
        {
            return new Resolution(declaration.Symbol, [declaration.Symbol]);
        }

        throw new KeyNotFoundException($"No declaration at {position.FilePath}:{position.Line}:{position.Column}.");
    }

    private static int NameLength(CodeSymbol symbol) => symbol.Kind switch
    {
        SymbolKind.Indexer => "this".Length,
        SymbolKind.Operator => "operator".Length,
        _ => symbol.Name.TrimStart('~').Length,
    };

    /// <summary>Declarations with the whole (last) name first; declarations containing it when there are none.</summary>
    private static Resolution ResolveName(SymbolIndex index, string query)
    {
        var candidates = index.Search(query, exact: true);
        if (candidates.Count == 0)
        {
            candidates = index.Search(query);
        }

        if (candidates.Count == 0)
        {
            throw new KeyNotFoundException($"No declaration named '{query}'.");
        }

        // A type and its constructors share the name: the name means the type
        var types = candidates.Where(c => c.Kind.IsType()).ToList();
        if (types.Count == 1 && candidates.All(c => c == types[0] || (c.Kind == SymbolKind.Constructor && c.Container == types[0])))
        {
            return new Resolution(types[0], [types[0]]);
        }

        return new Resolution(candidates.Count == 1 ? candidates[0] : null, candidates);
    }

    private static List<CodeSymbol> Order(List<CodeSymbol> symbols) => symbols.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
}
