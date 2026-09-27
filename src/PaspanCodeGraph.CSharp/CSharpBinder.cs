using System.Text;
using PaspanParsers.CSharp;

namespace PaspanCodeGraph.CSharp;

/// <summary>
/// The names a namespace declaration, a file or a project brings into scope: the namespace whose members are
/// visible, and its using directives. Scopes chain outward: a namespace declaration, the enclosing ones, the file,
/// the project's global usings.
/// </summary>
public sealed class ImportScope
{
    private readonly List<NameExpression> _namespaces = [];
    private readonly List<(string Alias, CSharpNode Target)> _aliases = [];
    private readonly List<CSharpNode> _staticTypes = [];
    private List<string>? _boundNamespaces;
    private Dictionary<string, BoundName?>? _boundAliases;
    private List<CodeSymbol>? _boundStaticTypes;

    public ImportScope(ImportScope? parent, string? namespaceName)
    {
        Parent = parent;
        NamespaceName = namespaceName;
    }

    public ImportScope? Parent { get; }

    /// <summary>The dotted namespace whose members are in scope; "" for the global namespace, null for none (the project's global usings).</summary>
    public string? NamespaceName { get; }

    /// <summary>A scope where the using directives are looked up: the file's for its global usings.</summary>
    internal ImportScope? Globals { get; set; }

    public bool HasUsings => _namespaces.Count != 0 || _aliases.Count != 0 || _staticTypes.Count != 0;

    public void AddUsings(IEnumerable<UsingDirective>? usings)
    {
        foreach (var directive in usings ?? [])
        {
            AddUsing(directive);
        }
    }

    public void AddUsing(UsingDirective directive)
    {
        lock (_namespaces)
        {
            switch (directive)
            {
                case UsingNamespaceDirective ns:
                    _namespaces.Add(ns.Namespace);
                    break;
                case UsingAliasDirective alias:
                    _aliases.Add((alias.Alias, (CSharpNode?)alias.Target ?? alias.TargetType));
                    break;
                case UsingStaticDirective type:
                    _staticTypes.Add((CSharpNode?)type.Type ?? type.TargetType);
                    break;
            }

            _boundNamespaces = null;
            _boundAliases = null;
            _boundStaticTypes = null;
        }
    }

    /// <summary>A using of a namespace name, from a project's <c>&lt;Using&gt;</c> items or the SDK's implicit usings.</summary>
    public void AddNamespace(string name) => AddUsing(new UsingNamespaceDirective(new NameExpression(name.Split('.'))));

    private ImportScope? _withoutUsings;

    // A using target is looked up as if the declaration that contains it had no using directives; the
    // project's global usings are bound in the global namespace
    private ImportScope TargetScope => _withoutUsings ??= new ImportScope(Parent, NamespaceName ?? "");

    internal IReadOnlyList<string> Namespaces(CSharpBinder binder)
    {
        lock (_namespaces)
        {
            return _boundNamespaces ??= _namespaces
                .Select(n => binder.BindNamespaceOrTypeName(n, new BindingContext(TargetScope, null, BindingContext.NoTypeParameters)))
                .Where(b => b is { Namespace: not null })
                .Select(b => b!.Namespace!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }

    internal BoundName? Alias(string name, CSharpBinder binder)
    {
        lock (_namespaces)
        {
            if (_boundAliases == null)
            {
                _boundAliases = new Dictionary<string, BoundName?>(StringComparer.Ordinal);
                foreach (var (alias, target) in _aliases)
                {
                    // Placeholder while binding: an alias cannot refer to itself
                    _boundAliases[alias] = null;
                }

                foreach (var (alias, target) in _aliases)
                {
                    var context = new BindingContext(TargetScope, null, BindingContext.NoTypeParameters);
                    _boundAliases[alias] = target switch
                    {
                        NameExpression n => binder.BindNamespaceOrTypeName(n, context),
                        NamedTypeReference t => binder.BindType(t, context) is { } bound ? new BoundName(null, bound) : null,
                        _ => null,
                    };
                }
            }

            return _boundAliases.GetValueOrDefault(name);
        }
    }

    internal IReadOnlyList<CodeSymbol> StaticTypes(CSharpBinder binder)
    {
        lock (_namespaces)
        {
            return _boundStaticTypes ??= _staticTypes
                .Select(t => t switch
                {
                    NameExpression n => binder.BindNamespaceOrTypeName(n, new BindingContext(TargetScope, null, BindingContext.NoTypeParameters))?.Type?.Symbol,
                    NamedTypeReference r => binder.BindType(r, new BindingContext(TargetScope, null, BindingContext.NoTypeParameters))?.Symbol,
                    _ => null,
                })
                .OfType<CodeSymbol>()
                .ToList();
        }
    }
}

/// <summary>Where a name is looked up: the import scopes, the containing type and the type parameters in scope.</summary>
public sealed record BindingContext(ImportScope Imports, CodeSymbol? Type, IReadOnlyDictionary<string, string> TypeParameters)
{
    public static IReadOnlyDictionary<string, string> NoTypeParameters { get; } = new Dictionary<string, string>();
}

/// <summary>A workspace type a name refers to, with its id form (<c>Ns.List{System.Int32}</c>).</summary>
/// <param name="Arguments">Id forms of the type arguments of each generic level, outermost first.</param>
public sealed record BoundType(CodeSymbol Symbol, string Id, IReadOnlyList<IReadOnlyList<string>> Arguments)
{
    /// <summary>The type arguments of the type itself (its last level).</summary>
    public IReadOnlyList<string> OwnArguments => Arguments.Count == 0 || Symbol.TypeParameters.Count == 0 ? [] : Arguments[^1];
}

/// <summary>A namespace (dotted name) or a type.</summary>
public sealed record BoundName(string? Namespace, BoundType? Type);

/// <summary>
/// Binds type names without a compiler, as C# looks them up: type parameters, nested types of the containing
/// types and their base types, then for each enclosing namespace its types, the aliases, the types of the
/// imported namespaces and the nested types of <c>using static</c> types. Only workspace types are found;
/// other names stay unbound.
/// </summary>
public sealed partial class CSharpBinder
{
    private readonly SymbolIndexBuilder _builder;
    private readonly Dictionary<string, ImportScope> _projectScopes = new(StringComparer.Ordinal);
    private readonly Dictionary<CodeSymbol, List<(BindingContext Context, TypeReference Type, string Written)>> _baseReferences = [];
    private readonly Dictionary<CodeSymbol, List<TypeLink>?> _bases = [];
    private readonly object _lock = new();

    public CSharpBinder(SymbolIndexBuilder builder)
    {
        _builder = builder;
    }

    /// <summary>The scope of the global usings of <paramref name="project"/>.</summary>
    public ImportScope ProjectScope(string project)
    {
        lock (_projectScopes)
        {
            if (!_projectScopes.TryGetValue(project, out var scope))
            {
                _projectScopes[project] = scope = new ImportScope(null, null);
            }

            return scope;
        }
    }

    /// <summary>Remembers the base list of one declaration of a type, to bind once every type is known.</summary>
    public void AddBaseReferences(CodeSymbol type, BindingContext context, IEnumerable<(TypeReference Type, string Written)> bases)
    {
        lock (_lock)
        {
            if (!_baseReferences.TryGetValue(type, out var list))
            {
                _baseReferences[type] = list = [];
            }

            list.AddRange(bases.Select(b => (context, b.Type, b.Written)));
        }
    }

    /// <summary>The base types of a type from all its declarations, bound on first use.</summary>
    public IReadOnlyList<TypeLink> Bases(CodeSymbol type)
    {
        lock (_lock)
        {
            if (_bases.TryGetValue(type, out var known))
            {
                // Null while being bound: a base list that depends on itself
                return known ?? [];
            }

            _bases[type] = null;
        }

        var links = new List<TypeLink>();
        List<(BindingContext Context, TypeReference Type, string Written)> references;
        lock (_lock)
        {
            references = _baseReferences.TryGetValue(type, out var list) ? [.. list] : [];
        }

        foreach (var (context, reference, written) in references)
        {
            // A base type is looked up outside the type's own members
            var outside = context with { Type = type.ContainingType };
            var bound = reference is NamedTypeReference named ? BindType(named, outside) : null;
            var id = DocumentationIds.TypeId(reference, context.TypeParameters, Resolver(outside));
            if (!links.Any(l => l.Id == id))
            {
                links.Add(new TypeLink(written, bound?.Symbol, id, bound?.OwnArguments ?? []));
            }
        }

        lock (_lock)
        {
            _bases[type] = links;
        }

        return links;
    }

    /// <summary>A <see cref="DocumentationIds"/> resolver that writes workspace types with their full names.</summary>
    public TypeResolver Resolver(BindingContext context) => named => BindType(named, context)?.Id;

    /// <summary>The id form of a type in <paramref name="context"/>.</summary>
    public string TypeId(TypeReference type, BindingContext context) =>
        DocumentationIds.TypeId(type, context.TypeParameters, Resolver(context));

    /// <summary>The workspace type a named type reference refers to, or null.</summary>
    public BoundType? BindType(NamedTypeReference reference, BindingContext context)
    {
        var parts = reference.Name.Parts;
        var arity = reference.TypeArguments?.Count ?? 0;
        if (parts.Count == 0)
        {
            return null;
        }

        BoundName? current;
        int next;
        if (reference.Qualifier is NamedTypeReference qualifier)
        {
            current = BindType(qualifier, context) is { } q ? new BoundName(null, q) : null;
            next = 0;
        }
        else if (reference.Qualifier != null)
        {
            return null;
        }
        else if (reference.Alias != null)
        {
            current = reference.Alias == "global" ? new BoundName("", null) : FindAlias(reference.Alias, context);
            next = 0;
        }
        else
        {
            if (parts.Count == 1 && arity == 0 && context.TypeParameters.ContainsKey(parts[0]))
            {
                return null;
            }

            current = LookupSimple(parts[0], parts.Count == 1 ? arity : 0, context, typeOnly: parts.Count == 1);
            next = 1;
        }

        for (var i = next; i < parts.Count && current != null; i++)
        {
            current = MemberOf(current, parts[i], i == parts.Count - 1 ? arity : 0, context);
        }

        if (current?.Type is not { } bound)
        {
            return null;
        }

        // The type arguments of the last part
        if (arity != 0)
        {
            var arguments = reference.TypeArguments!.Select(a => DocumentationIds.TypeId(a, context.TypeParameters, Resolver(context))).ToList();
            bound = WithArguments(bound, arguments);
        }

        if (reference.IsNullable && bound.Symbol.Kind is SymbolKind.Struct or SymbolKind.RecordStruct or SymbolKind.Enum)
        {
            bound = bound with { Id = $"System.Nullable{{{bound.Id}}}" };
        }

        return bound;
    }

    /// <summary>A namespace or type named by a dotted name (a using target, an attribute, an expression chain).</summary>
    public BoundName? BindNamespaceOrTypeName(NameExpression name, BindingContext context)
    {
        if (name.Parts.Count == 0)
        {
            return null;
        }

        var arity = name.TypeArguments?.Count ?? 0;
        var current = name.Alias switch
        {
            null => LookupSimple(name.Parts[0], name.Parts.Count == 1 ? arity : 0, context, typeOnly: false),
            "global" => MemberOf(new BoundName("", null), name.Parts[0], name.Parts.Count == 1 ? arity : 0, context),
            _ => FindAlias(name.Alias, context) is { } alias ? MemberOf(alias, name.Parts[0], name.Parts.Count == 1 ? arity : 0, context) : null,
        };

        for (var i = 1; i < name.Parts.Count && current != null; i++)
        {
            current = MemberOf(current, name.Parts[i], i == name.Parts.Count - 1 ? arity : 0, context);
        }

        if (current?.Type is { } type && arity != 0)
        {
            current = new BoundName(null, WithArguments(type, name.TypeArguments!.Select(a => TypeId(a, context)).ToList()));
        }

        return current;
    }

    /// <summary>A simple name looked up in scope; with <paramref name="typeOnly"/> namespaces are skipped.</summary>
    public BoundName? LookupSimple(string name, int arity, BindingContext context, bool typeOnly, bool expression = false)
    {
        // Nested types of the containing types and of their base types; in an expression, a method, property,
        // field or event of those types hides the types further out
        for (var type = context.Type; type != null; type = type.ContainingType)
        {
            if (FindNestedType(type, name, arity) is { } nested)
            {
                return new BoundName(null, Implicit(nested, context));
            }

            if (expression && HasValueMember(type, name, 0))
            {
                return null;
            }
        }

        for (var scope = context.Imports; scope != null; scope = scope.Parent)
        {
            if (scope.NamespaceName is { } ns)
            {
                var full = ns.Length == 0 ? name : $"{ns}.{name}";
                if (FindType(full, arity) is { } type)
                {
                    return new BoundName(null, Implicit(type, context));
                }

                if (!typeOnly && arity == 0 && _builder.Get("N:" + full) != null)
                {
                    return new BoundName(full, null);
                }
            }

            if (FindInUsings(scope, name, arity, context) is { } imported)
            {
                return imported;
            }

            if (scope.Globals is { } globals && FindInUsings(globals, name, arity, context) is { } global)
            {
                return global;
            }
        }

        return null;
    }

    private BoundName? FindInUsings(ImportScope scope, string name, int arity, BindingContext context)
    {
        if (!scope.HasUsings)
        {
            return null;
        }

        if (arity == 0 && scope.Alias(name, this) is { } alias)
        {
            return alias;
        }

        foreach (var ns in scope.Namespaces(this))
        {
            if (FindType(ns.Length == 0 ? name : $"{ns}.{name}", arity) is { } type)
            {
                return new BoundName(null, Implicit(type, context));
            }
        }

        foreach (var staticType in scope.StaticTypes(this))
        {
            if (FindNestedType(staticType, name, arity) is { } nested)
            {
                return new BoundName(null, Implicit(nested, context));
            }
        }

        return null;
    }

    private bool HasValueMember(CodeSymbol type, string name, int depth) => FindValueMember(type, name, depth) != null;

    /// <summary>A member other than a type named <paramref name="name"/> in a type or one of its base types.</summary>
    private CodeSymbol? FindValueMember(CodeSymbol type, string name, int depth)
    {
        if (type.Members.FirstOrDefault(m => m.Name == name && m.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event or SymbolKind.EnumMember) is { } member)
        {
            return member;
        }

        foreach (var link in Bases(type))
        {
            if (depth < 16 && link.Symbol is { } baseType && FindValueMember(baseType, name, depth + 1) is { } inherited)
            {
                return inherited;
            }
        }

        return null;
    }

    /// <summary>
    /// The type in <c>Color.Red</c> when a property or field <c>Color</c> of type <c>Color</c> hides it: C# then
    /// binds the name as the type when the member accessed is static.
    /// </summary>
    public BoundName? ColorColor(string name, string memberName, BindingContext context)
    {
        CodeSymbol? member = null;
        for (var type = context.Type; type != null && member == null; type = type.ContainingType)
        {
            member = FindValueMember(type, name, 0);
        }

        if (member is not { Kind: SymbolKind.Property or SymbolKind.Field } || member.Type?.TrimEnd('?') is not { } written
            || written != name && !written.EndsWith("." + name, StringComparison.Ordinal))
        {
            return null;
        }

        var bound = LookupSimple(name, 0, context, typeOnly: true);
        return bound?.Type?.Symbol.Members.Any(m => m.Name == memberName
            && (m.Kind is SymbolKind.EnumMember || m.Kind.IsType() || m.Modifiers.Contains("static") || m.Modifiers.Contains("const"))) == true
            ? bound
            : null;
    }

    private BoundName? FindAlias(string alias, BindingContext context)
    {
        for (var scope = context.Imports; scope != null; scope = scope.Parent)
        {
            if (scope.Alias(alias, this) is { } bound)
            {
                return bound;
            }

            if (scope.Globals?.Alias(alias, this) is { } global)
            {
                return global;
            }
        }

        return null;
    }

    /// <summary>A namespace or nested type named <paramref name="name"/> in a namespace or type.</summary>
    public BoundName? MemberOf(BoundName container, string name, int arity, BindingContext context)
    {
        if (container.Namespace is { } ns)
        {
            var full = ns.Length == 0 ? name : $"{ns}.{name}";
            if (FindType(full, arity) is { } type)
            {
                return new BoundName(null, Plain(type, context));
            }

            return arity == 0 && _builder.Get("N:" + full) != null ? new BoundName(full, null) : null;
        }

        if (container.Type is { } outer && FindNestedType(outer.Symbol, name, arity) is { } nested)
        {
            var arguments = new List<IReadOnlyList<string>>(outer.Arguments);
            if (nested.ContainingType != outer.Symbol)
            {
                // Inherited from a base type: the type arguments of the base are not tracked
                return new BoundName(null, Plain(nested, context));
            }

            return new BoundName(null, Format(nested, arguments));
        }

        return null;
    }

    /// <summary>A workspace type by metadata name (<c>Ns.Outer`1.Inner</c> without arity) and arity.</summary>
    private CodeSymbol? FindType(string dottedName, int arity)
    {
        var symbol = _builder.Get("T:" + (arity == 0 ? dottedName : $"{dottedName}`{arity}"));
        return symbol is { Kind: var kind } && kind.IsType() ? symbol : null;
    }

    /// <summary>A type nested in <paramref name="type"/> or inherited from its base types.</summary>
    public CodeSymbol? FindNestedType(CodeSymbol type, string name, int arity) => FindNestedType(type, name, arity, 0);

    private CodeSymbol? FindNestedType(CodeSymbol type, string name, int arity, int depth)
    {
        if (depth > 16)
        {
            return null;
        }

        var nested = _builder.Get($"{type.Id}.{(arity == 0 ? name : $"{name}`{arity}")}");
        if (nested is { Kind: var kind } && kind.IsType())
        {
            return nested;
        }

        foreach (var link in Bases(type))
        {
            if (link.Symbol is { } baseType && FindNestedType(baseType, name, arity, depth + 1) is { } inherited)
            {
                return inherited;
            }
        }

        return null;
    }

    /// <summary>
    /// A type found through the containing scopes, whose containing generic types have the type arguments in
    /// scope: <c>Inner</c> inside <c>Outer&lt;T&gt;</c> is <c>Outer{`0}.Inner</c>.
    /// </summary>
    private BoundType Implicit(CodeSymbol type, BindingContext context)
    {
        var levels = new List<IReadOnlyList<string>>();
        var chain = Chain(type);
        foreach (var level in chain)
        {
            if (level == type)
            {
                // The type's own arguments are added by the caller
                levels.Add([]);
                continue;
            }

            var arguments = new List<string>();
            foreach (var parameter in level.TypeParameters)
            {
                arguments.Add(context.TypeParameters.TryGetValue(parameter, out var id) ? id : parameter);
            }

            levels.Add(arguments);
        }

        return Format(type, levels);
    }

    /// <summary>A type named with its containers explicitly: its containing types have no type arguments of their own here.</summary>
    private BoundType Plain(CodeSymbol type, BindingContext context) => Implicit(type, context);

    private static List<CodeSymbol> Chain(CodeSymbol type)
    {
        var chain = new List<CodeSymbol>();
        for (var t = type; t != null; t = t.ContainingType)
        {
            chain.Insert(0, t);
        }

        return chain;
    }

    private static BoundType WithArguments(BoundType bound, IReadOnlyList<string> arguments)
    {
        var levels = new List<IReadOnlyList<string>>(bound.Arguments);
        var chainLength = Chain(bound.Symbol).Count;
        while (levels.Count < chainLength)
        {
            levels.Add([]);
        }

        levels[^1] = arguments;
        return Format(bound.Symbol, levels);
    }

    /// <summary>The id form of a type with the type arguments of each level: <c>Ns.Outer{`0}.Inner{System.Int32}</c>.</summary>
    private static BoundType Format(CodeSymbol type, IReadOnlyList<IReadOnlyList<string>> levels)
    {
        var chain = Chain(type);
        var builder = new StringBuilder();
        if (chain[0].Namespace is { } ns)
        {
            builder.Append(ns).Append('.');
        }

        var normalized = new List<IReadOnlyList<string>>();
        for (var i = 0; i < chain.Count; i++)
        {
            var level = chain[i];
            if (i != 0)
            {
                builder.Append('.');
            }

            builder.Append(level.Name);
            var arguments = i < levels.Count ? levels[i] : [];
            if (level.TypeParameters.Count != 0)
            {
                if (arguments.Count == level.TypeParameters.Count)
                {
                    builder.Append('{').Append(string.Join(",", arguments)).Append('}');
                }
                else
                {
                    // Unknown arguments: the open type
                    builder.Append('`').Append(level.TypeParameters.Count);
                }
            }

            normalized.Add(arguments);
        }

        return new BoundType(type, builder.ToString(), normalized);
    }
}
