using System.Text.RegularExpressions;

namespace PaspanCodeGraph.CSharp;

/// <summary>
/// Links the types and members of a workspace once all are collected: base types and derived types, overridden
/// members, and the interface members each member implements, matched by name and parameter types as C# does.
/// </summary>
public static partial class CSharpHierarchy
{
    public static void Link(SymbolIndexBuilder builder, CSharpBinder binder)
    {
        var types = builder.Symbols.Where(s => s.Kind.IsType()).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        foreach (var type in types)
        {
            type.Bases.Clear();
            type.Bases.AddRange(binder.Bases(type));
        }

        foreach (var type in types)
        {
            foreach (var link in type.Bases)
            {
                if (link.Symbol is { } baseType && !baseType.DerivedTypes.Contains(type))
                {
                    baseType.DerivedTypes.Add(type);
                }
            }
        }

        foreach (var type in types)
        {
            LinkOverrides(type);
            if (type.Kind is not (SymbolKind.Interface or SymbolKind.Enum or SymbolKind.Delegate))
            {
                LinkImplementations(type);
            }
        }
    }

    /// <summary>The base class of a type, when it is a workspace class.</summary>
    public static CodeSymbol? BaseClass(CodeSymbol type) =>
        type.Kind is SymbolKind.Class or SymbolKind.Record && type.Bases.Count > 0 && type.Bases[0].Symbol is { Kind: SymbolKind.Class or SymbolKind.Record } baseClass
            ? baseClass
            : null;

    /// <summary>The members of the base classes with their type parameters mapped to the derived type's.</summary>
    private static IEnumerable<(CodeSymbol Type, string[] Mapping)> BaseClasses(CodeSymbol type)
    {
        var mapping = Identity(type);
        var seen = new HashSet<CodeSymbol> { type };
        for (var current = type; ;)
        {
            if (current.Kind is not (SymbolKind.Class or SymbolKind.Record) || current.Bases.Count == 0 || current.Bases[0] is not { Symbol: { Kind: SymbolKind.Class or SymbolKind.Record } next } link || !seen.Add(next))
            {
                yield break;
            }

            mapping = Compose(link.TypeArguments, mapping, next);
            yield return (next, mapping);
            current = next;
        }
    }

    /// <summary>A type's own type parameters, as id forms: <c>`0</c>, <c>`1</c>... after those of its containing types.</summary>
    private static string[] Identity(CodeSymbol type)
    {
        var offset = 0;
        for (var container = type.ContainingType; container != null; container = container.ContainingType)
        {
            offset += container.TypeParameters.Count;
        }

        return Enumerable.Range(0, offset + type.TypeParameters.Count).Select(i => "`" + i).ToArray();
    }

    /// <summary>
    /// The id forms, in the derived type's type parameters, of the base type's type parameters: the base's
    /// arguments with the derived mapping applied.
    /// </summary>
    private static string[] Compose(IReadOnlyList<string> arguments, string[] mapping, CodeSymbol baseType)
    {
        var offset = Identity(baseType).Length - baseType.TypeParameters.Count;
        var result = new string[offset + baseType.TypeParameters.Count];
        for (var i = 0; i < offset; i++)
        {
            // Type parameters of the base's containing types are not tracked
            result[i] = "`" + i;
        }

        for (var i = 0; i < baseType.TypeParameters.Count; i++)
        {
            result[offset + i] = i < arguments.Count ? Substitute(arguments[i], mapping) : "`" + (offset + i);
        }

        return result;
    }

    /// <summary>Replaces the type parameters of a type (<c>`0</c>, not the <c>``0</c> of methods) in an id part.</summary>
    internal static string Substitute(string idPart, string[] mapping) =>
        TypeParameterRegex().Replace(idPart, m => int.TryParse(m.Groups[1].Value, out var i) && i < mapping.Length ? mapping[i] : m.Value);

    [GeneratedRegex(@"(?<!`)`(\d+)")]
    private static partial Regex TypeParameterRegex();

    /// <summary>What must match for one member to override or implement another: kind, name, arity and parameter types.</summary>
    private static string? SignatureKey(CodeSymbol member, string[]? mapping)
    {
        if (member.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Indexer or SymbolKind.Event or SymbolKind.Operator))
        {
            return null;
        }

        // "M:Ns.Type.Name``1(System.Int32)": the part after the containing type's id; an explicit
        // implementation's name ("Ns#IFace#Name") is compared by its last part
        var rest = member.Id[(2 + member.Container!.Id.Length - 2 + 1)..];
        var nameEnd = rest.IndexOfAny(['(', '~']);
        var name = nameEnd < 0 ? rest : rest[..nameEnd];
        var hash = name.LastIndexOf('#');
        if (hash >= 0)
        {
            rest = rest[(hash + 1)..];
        }

        var key = $"{(member.Kind is SymbolKind.Indexer ? SymbolKind.Property : member.Kind)}:{rest}";
        return mapping == null ? key : Substitute(key, mapping);
    }

    private static void LinkOverrides(CodeSymbol type)
    {
        foreach (var member in type.Members)
        {
            if (!member.Modifiers.Contains("override") || SignatureKey(member, null) is not { } key)
            {
                continue;
            }

            foreach (var (baseClass, mapping) in BaseClasses(type))
            {
                var overridden = baseClass.Members.FirstOrDefault(m =>
                    m.Kind == member.Kind && m.Name == member.Name && SignatureKey(m, mapping) == key
                    && (m.Modifiers.Contains("virtual") || m.Modifiers.Contains("abstract") || m.Modifiers.Contains("override")));
                if (overridden != null)
                {
                    member.Overrides = overridden;
                    overridden.OverriddenBy.Add(member);
                    break;
                }
            }
        }
    }

    /// <summary>The interfaces a type lists and their base interfaces, mapped to the type's type parameters.</summary>
    private static List<(CodeSymbol Interface, string[] Mapping)> AllInterfaces(CodeSymbol type)
    {
        var result = new List<(CodeSymbol, string[])>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(CodeSymbol from, string[] mapping, int depth)
        {
            if (depth > 16)
            {
                return;
            }

            foreach (var link in from.Bases)
            {
                if (link.Symbol is not { Kind: SymbolKind.Interface } face)
                {
                    continue;
                }

                var faceMapping = Compose(link.TypeArguments, mapping, face);
                if (seen.Add(face.Id + "|" + string.Join(",", faceMapping)))
                {
                    result.Add((face, faceMapping));
                    Add(face, faceMapping, depth + 1);
                }
            }
        }

        // An interface only a base class lists keeps the base class's implementations (C# spec, interface
        // implementation inheritance), so the base class links them
        Add(type, Identity(type), 0);
        return result;
    }

    private static void LinkImplementations(CodeSymbol type)
    {
        var interfaces = AllInterfaces(type);
        if (interfaces.Count == 0)
        {
            return;
        }

        var candidates = new List<(CodeSymbol Member, string[]? Mapping)>();
        candidates.AddRange(type.Members.Select(m => (m, (string[]?)null)));
        foreach (var (baseClass, mapping) in BaseClasses(type))
        {
            candidates.AddRange(baseClass.Members.Select(m => (m, (string[]?)mapping)));
        }

        foreach (var (face, faceMapping) in interfaces)
        {
            foreach (var member in face.Members)
            {
                if (member.Modifiers.Contains("static") && !member.Modifiers.Contains("abstract") && !member.Modifiers.Contains("virtual")
                    || SignatureKey(member, faceMapping) is not { } key)
                {
                    continue;
                }

                // An explicit implementation in the type, else a public member of the type or its base classes
                var implementation = type.Members.FirstOrDefault(m =>
                        m.ExplicitInterface?.Symbol == face && m.Kind == member.Kind && SignatureKey(m, null) == key)
                    ?? candidates.FirstOrDefault(c =>
                        c.Member.ExplicitInterface == null && c.Member.Kind == member.Kind && c.Member.Name == member.Name
                        && c.Member.Accessibility == "Public" && SignatureKey(c.Member, c.Mapping) == key).Member;
                if (implementation != null && !implementation.Implements.Contains(member))
                {
                    implementation.Implements.Add(member);
                    member.ImplementedBy.Add(implementation);
                }
            }
        }
    }
}
