using System.Security.Cryptography;
using System.Text;

namespace PaspanCodeGraph.Workspace;

/// <summary>
/// What a declaration looks like to other files: its id, the name they would use for it and every detail that
/// binding in other files depends on (not its location or documentation).
/// </summary>
/// <param name="Global">A change to it can change binding anywhere, so every file is bound again.</param>
public sealed record DeclarationPrint(string Id, string Name, string? ContainerName, SymbolKind Kind, string Details, bool Global);

/// <summary>
/// What an update keeps of a file between snapshots: its content hash, the declarations it contributes, the
/// identifiers it mentions and the references found in it.
/// </summary>
public sealed class FileState
{
    public required string Hash { get; init; }

    public required IReadOnlyList<DeclarationPrint> Declarations { get; init; }

    /// <summary>
    /// The identifiers in the file's text, comments and strings included: a file can only bind to a changed
    /// declaration whose name it mentions (with the exceptions of <see cref="IncrementalState.RebindsEverything"/>).
    /// </summary>
    public required IReadOnlySet<string> Names { get; init; }

    public required IReadOnlyList<(string TargetId, SymbolReference Reference)> References { get; init; }
}

/// <summary>Decides which files an update must bind again.</summary>
public static class IncrementalState
{
    /// <summary>Stands in <see cref="FileState.Names"/> for a target-typed <c>new(...)</c>, which names no type.</summary>
    public const string TargetTypedNew = "new(";

    /// <summary>Members that code uses without naming them: foreach, await, using, deconstruction, queries, ranges.</summary>
    private static readonly HashSet<string> PatternNames = new(StringComparer.Ordinal)
    {
        "GetEnumerator", "MoveNext", "Current", "GetAsyncEnumerator", "MoveNextAsync",
        "GetAwaiter", "GetResult", "IsCompleted", "OnCompleted", "UnsafeOnCompleted",
        "Dispose", "DisposeAsync", "Deconstruct", "Add", "GetPinnableReference",
        "Select", "SelectMany", "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "GroupBy", "Join", "GroupJoin", "Cast", "Slice", "Substring", "Length", "Count", "Invoke",
        "AppendLiteral", "AppendFormatted", "Create",
    };

    public static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>The identifier-like words of a UTF-8 text (non-ASCII bytes count as letters), plus <see cref="TargetTypedNew"/>.</summary>
    public static HashSet<string> Names(ReadOnlySpan<byte> text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        while (i < text.Length)
        {
            var b = text[i];
            if (!IsIdentifierStart(b))
            {
                // Skip a whole number, so that "0x1F" does not give "x1F"
                if (b is >= (byte)'0' and <= (byte)'9')
                {
                    while (i < text.Length && IsIdentifierPart(text[i]))
                    {
                        i++;
                    }

                    continue;
                }

                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && IsIdentifierPart(text[i]))
            {
                i++;
            }

            var word = text[start..i];
            if (word.SequenceEqual("new"u8))
            {
                var next = i;
                while (next < text.Length && text[next] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    next++;
                }

                if (next < text.Length && text[next] == (byte)'(')
                {
                    names.Add(TargetTypedNew);
                }
            }

            names.Add(Encoding.UTF8.GetString(word));
        }

        return names;
    }

    private static bool IsIdentifierStart(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_' or >= 0x80;

    private static bool IsIdentifierPart(byte b) => IsIdentifierStart(b) || b is >= (byte)'0' and <= (byte)'9';

    /// <summary>The declarations of each file, from the symbols after their hierarchy is linked.</summary>
    public static Dictionary<string, List<DeclarationPrint>> Declarations(IEnumerable<CodeSymbol> symbols)
    {
        var byFile = new Dictionary<string, List<DeclarationPrint>>(SymbolIndexBuilder.PathComparer);
        foreach (var symbol in symbols)
        {
            if (symbol.Declarations.Count == 0)
            {
                continue;
            }

            var print = Print(symbol);
            foreach (var file in symbol.Declarations.Select(d => d.File).Distinct(SymbolIndexBuilder.PathComparer))
            {
                if (!byFile.TryGetValue(file, out var list))
                {
                    byFile[file] = list = [];
                }

                list.Add(print);
            }
        }

        foreach (var list in byFile.Values)
        {
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        }

        return byFile;
    }

    /// <summary>A <c>global using</c> directive of a file: changing it changes name lookup in the whole project.</summary>
    public static DeclarationPrint GlobalUsing(string text) => new("global using " + text, "", null, SymbolKind.Namespace, text, Global: true);

    private static DeclarationPrint Print(CodeSymbol symbol)
    {
        var details = new StringBuilder()
            .Append(symbol.Kind).Append('|')
            .Append(symbol.Accessibility).Append('|')
            .AppendJoin(' ', symbol.Modifiers).Append('|')
            .Append(symbol.Type).Append('|')
            .AppendJoin(',', symbol.Parameters.Select(p => $"{p.Modifier} {p.Type} {p.Name}={p.DefaultValue}")).Append('|')
            .AppendJoin(',', symbol.TypeParameters).Append('|')
            .AppendJoin(',', symbol.Bases.Select(b => b.Id)).Append('|')
            .Append(symbol.ExplicitInterface?.Id).Append('|')
            .Append(symbol.Overrides?.Id).Append('|')
            .Append(symbol.Signature)
            .ToString();
        var kind = symbol.Kind;
        var global = kind is SymbolKind.Operator or SymbolKind.Indexer or SymbolKind.Destructor or SymbolKind.Extension
            || (kind.IsMember() && PatternNames.Contains(symbol.Name));
        return new DeclarationPrint(symbol.Id, symbol.Name, symbol.ContainingType?.Name, kind, details, global);
    }

    /// <summary>
    /// Compares the declarations of every file before and after an update. Returns null when a change can affect
    /// binding anywhere (a type's bases or modifiers, an operator, a member used by a language pattern, a global
    /// using), else the names that files must mention to be bound again.
    /// </summary>
    public static HashSet<string>? ChangedNames(
        IReadOnlyDictionary<string, IReadOnlyList<DeclarationPrint>> before,
        IReadOnlyDictionary<string, IReadOnlyList<DeclarationPrint>> after)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in before.Keys.Union(after.Keys, SymbolIndexBuilder.PathComparer))
        {
            var old = before.GetValueOrDefault(file) ?? [];
            var current = after.GetValueOrDefault(file) ?? [];
            if (old.SequenceEqual(current))
            {
                continue;
            }

            var oldSet = old.ToHashSet();
            var currentSet = current.ToHashSet();
            var oldIds = old.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var currentIds = current.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var print in old.Where(p => !currentSet.Contains(p)).Concat(current.Where(p => !oldSet.Contains(p))))
            {
                if (print.Global)
                {
                    return null;
                }

                var isType = print.Kind.IsType() || print.Kind == SymbolKind.Namespace;
                if (isType && oldIds.Contains(print.Id) && currentIds.Contains(print.Id))
                {
                    // The same type with other bases, modifiers or type parameters: its members' lookup changes
                    // under names no file needs to mention
                    return null;
                }

                AddName(names, print.Name);
                if (print.Kind == SymbolKind.Constructor)
                {
                    names.Add(TargetTypedNew);
                    if (print.ContainerName is { } type)
                    {
                        AddName(names, type);
                    }
                }
            }
        }

        return names;
    }

    private static void AddName(HashSet<string> names, string name)
    {
        names.Add(name);

        // [Obsolete] names ObsoleteAttribute
        if (name.Length > "Attribute".Length && name.EndsWith("Attribute", StringComparison.Ordinal))
        {
            names.Add(name[..^"Attribute".Length]);
        }
    }
}
