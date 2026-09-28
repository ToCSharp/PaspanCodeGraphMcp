using System.Collections.Concurrent;
using PaspanCodeGraph.Metadata;
using PaspanParsers.CSharp;

namespace PaspanCodeGraph.CSharp;

/// <summary>A parameter of a member with its type bound: what overload resolution and lambdas need.</summary>
public sealed record ParameterSem(string Name, SemType Type, ParameterModifier Modifier, bool HasDefault)
{
    public bool IsParams => Modifier == ParameterModifier.Params;

    public bool IsOut => Modifier == ParameterModifier.Out;
}

/// <summary>A member found by member lookup, with how the type parameters of its containing type map for the receiver.</summary>
public sealed record FoundMember(CodeSymbol Symbol, Func<TypeParameterType, SemType?> Map)
{
    public SemType Substitute(SemType type) => SemTypes.Substitute(type, Map);
}

// The declared types of members and the lookup of members in types, for binding expressions
public sealed partial class CSharpBinder
{
    private sealed record MemberSyntax(BindingContext Context, TypeReference? Type, IReadOnlyList<Parameter>? Parameters, IReadOnlyList<TypeParameterConstraint>? Constraints);

    private readonly ConcurrentDictionary<CodeSymbol, MemberSyntax> _memberSyntax = new();
    private readonly ConcurrentDictionary<CodeSymbol, List<(BindingContext Context, IReadOnlyList<TypeParameterConstraint> Constraints)>> _typeConstraints = new();
    private readonly ConcurrentDictionary<CodeSymbol, SemType> _memberTypes = new();
    private readonly ConcurrentDictionary<CodeSymbol, IReadOnlyList<ParameterSem>> _parameters = new();
    private readonly ConcurrentDictionary<CodeSymbol, IReadOnlyList<SemType>> _baseTypes = new();
    private readonly ConcurrentDictionary<(CodeSymbol, int, bool), IReadOnlyList<SemType>> _constraints = new();
    private readonly ConcurrentDictionary<string, List<CodeSymbol>> _extensionMethods = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ImportScope, HashSet<string>> _namespacesInScope = new(ReferenceEqualityComparer.Instance);

    /// <summary>Remembers the declared type, parameters and constraints of a member (or delegate), bound on first use.</summary>
    public void RegisterMember(CodeSymbol member, BindingContext context, TypeReference? type, IReadOnlyList<Parameter>? parameters, IReadOnlyList<TypeParameterConstraint>? constraints = null)
    {
        _memberSyntax.TryAdd(member, new MemberSyntax(context, type, parameters, constraints));
        if (member.Kind == SymbolKind.Method && parameters is { Count: > 0 } && parameters[0].Modifiers.Contains(ParameterModifier.This))
        {
            _extensionMethods.GetOrAdd(member.Name, _ => []).Add(member);
        }
    }

    /// <summary>Whether a method is an extension method (its first parameter is <c>this</c>).</summary>
    public bool IsExtensionMethod(CodeSymbol method) =>
        ExternalMember(method) is { } external ? external.IsExtension : _memberSyntax.TryGetValue(method, out var syntax) && syntax.Parameters is { Count: > 0 } parameters && parameters[0].Modifiers.Contains(ParameterModifier.This);

    /// <summary>Remembers the type parameter constraints of one declaration of a type.</summary>
    public void RegisterTypeConstraints(CodeSymbol type, BindingContext context, IReadOnlyList<TypeParameterConstraint>? constraints)
    {
        if (constraints is { Count: > 0 })
        {
            var list = _typeConstraints.GetOrAdd(type, _ => []);
            lock (list)
            {
                list.Add((context, constraints));
            }
        }
    }

    /// <summary>The type a member declares: a field's, property's or event's type, a method's or delegate's return type.</summary>
    public SemType MemberType(CodeSymbol member)
    {
        if (member.Kind == SymbolKind.EnumMember && member.ContainingType is { } enumType)
        {
            return TypeOf(enumType, []);
        }

        if (member.Kind == SymbolKind.Constructor && member.ContainingType is { } created)
        {
            return SelfType(created);
        }

        return _memberTypes.GetOrAdd(member, m => ExternalMember(m) is { } external ? ExternalMemberType(m, external)
            : m.IsExternal && DefinitionOf(m) is { Kind: MetadataTypeKind.Delegate } && m.Members.FirstOrDefault(i => i.Name == "Invoke") is { } invoke ? MemberType(invoke)
            : _memberSyntax.TryGetValue(m, out var syntax) ? ToSemType(syntax.Type, syntax.Context, m) : SemType.Unknown);
    }

    /// <summary>The parameters of a method, constructor, indexer, operator or delegate.</summary>
    public IReadOnlyList<ParameterSem> Parameters(CodeSymbol member) =>
        _parameters.GetOrAdd(member, m => ExternalMember(m) is { } external ? ExternalParameters(m, external)
            : m.IsExternal && DefinitionOf(m) is { Kind: MetadataTypeKind.Delegate } && m.Members.FirstOrDefault(i => i.Name == "Invoke") is { } invoke ? Parameters(invoke)
            : _memberSyntax.TryGetValue(m, out var syntax) && syntax.Parameters != null
            ? syntax.Parameters.Select(p => new ParameterSem(
                p.Name,
                ToSemType(p.Type, syntax.Context, m),
                p.Modifiers.FirstOrDefault(x => x is ParameterModifier.Params or ParameterModifier.Out or ParameterModifier.Ref or ParameterModifier.In or ParameterModifier.This),
                p.DefaultValue != null)).ToList()
            : []);

    /// <summary>A type with its own type parameters as arguments, as <c>this</c> has it inside its declaration.</summary>
    public SemType SelfType(CodeSymbol type)
    {
        var arguments = new List<SemType>();
        var chain = new List<CodeSymbol>();
        for (var t = type; t != null; t = t.ContainingType)
        {
            chain.Insert(0, t);
        }

        foreach (var level in chain)
        {
            foreach (var parameter in level.TypeParameters)
            {
                arguments.Add(new TypeParameterType(parameter, arguments.Count, false, level));
            }
        }

        return TypeOf(type, arguments);
    }

    /// <summary>The base class and interfaces of a type, in terms of its own type parameters.</summary>
    public IReadOnlyList<SemType> BaseTypes(CodeSymbol type) => _baseTypes.GetOrAdd(type, t =>
    {
        if (DefinitionOf(t) is { } definition)
        {
            return ExternalBaseTypes(t, definition);
        }

        List<(BindingContext Context, TypeReference Type, string Written)> references;
        lock (_lock)
        {
            references = _baseReferences.TryGetValue(t, out var list) ? [.. list] : [];
        }

        var result = new List<SemType>();
        foreach (var (context, reference, _) in references)
        {
            var bound = ToSemType(reference, context with { Type = t.ContainingType }, null);
            if (!result.Contains(bound))
            {
                result.Add(bound);
            }
        }

        return result;
    });

    /// <summary>The constraint types of a type parameter.</summary>
    public IReadOnlyList<SemType> Constraints(TypeParameterType parameter)
    {
        if (parameter.Owner is not { } owner)
        {
            return [];
        }

        return _constraints.GetOrAdd((owner, parameter.Ordinal, parameter.IsMethod), key =>
        {
            var result = new List<SemType>();
            void Add(BindingContext context, IReadOnlyList<TypeParameterConstraint>? constraints, CodeSymbol? method)
            {
                foreach (var clause in constraints ?? [])
                {
                    if (clause.TypeParameterName != parameter.Name)
                    {
                        continue;
                    }

                    foreach (var constraint in clause.Constraints)
                    {
                        if (constraint is TypeReferenceConstraint { Type: { } type })
                        {
                            result.Add(ToSemType(type, context, method));
                        }
                    }
                }
            }

            if (owner.IsExternal)
            {
                return ExternalConstraints(owner, parameter);
            }

            if (parameter.IsMethod)
            {
                if (_memberSyntax.TryGetValue(owner, out var syntax))
                {
                    Add(syntax.Context, syntax.Constraints, owner);
                }
            }
            else if (_typeConstraints.TryGetValue(owner, out var declarations))
            {
                lock (declarations)
                {
                    foreach (var (context, constraints) in declarations)
                    {
                        Add(context, constraints, null);
                    }
                }
            }

            return result;
        });
    }

    /// <summary>The type a type reference denotes in <paramref name="context"/>; <paramref name="method"/> owns the <c>``N</c> type parameters.</summary>
    public SemType ToSemType(TypeReference? type, BindingContext context, CodeSymbol? method)
    {
        switch (type)
        {
            case null:
            case OmittedTypeReference:
                return SemType.Unknown;
            case PredefinedTypeReference predefined:
            {
                var semType = Predefined(predefined.Type);
                return predefined.IsNullable ? new NullableType(semType) : semType;
            }

            case NullableTypeReference nullable:
                return new NullableType(ToSemType(nullable.ElementType, context, method));
            case ArrayTypeReference array:
                return new ArrayType(ToSemType(array.ElementType, context, method), array.Rank);
            case TupleTypeReference tuple:
                return new TupleType(tuple.Elements.Select(e => ToSemType(e.Type, context, method)).ToList(), tuple.Elements.Select(e => e.Name).ToList());
            case RefTypeReference reference:
                return ToSemType(reference.Type, context, method);
            case ScopedTypeReference scoped:
                return ToSemType(scoped.Type, context, method);
            case NamedTypeReference named:
            {
                var semType = ToSemType(named, context, method);
                return named.IsNullable ? new NullableType(semType) : semType;
            }

            default:
                return SemType.Unknown;
        }
    }

    private SemType ToSemType(NamedTypeReference named, BindingContext context, CodeSymbol? method)
    {
        var parts = named.Name.Parts;
        var arguments = named.TypeArguments?.Select(a => ToSemType(a, context, method)).ToList() ?? [];
        if (parts.Count == 1 && arguments.Count == 0 && named.Qualifier == null && named.Alias == null)
        {
            if (context.TypeParameters.TryGetValue(parts[0], out var form))
            {
                return TypeParameter(parts[0], form, context, method);
            }

            if (parts[0] == "dynamic" && BindType(named, context) == null)
            {
                return SemType.Unknown;
            }
        }

        if (BindType(named, context) is { } bound)
        {
            // The arguments of the containing generic types: from the qualifier, else the type parameters in scope
            var all = new List<SemType>();
            var chain = new List<CodeSymbol>();
            for (var t = bound.Symbol.ContainingType; t != null; t = t.ContainingType)
            {
                chain.Insert(0, t);
            }

            var outer = named.Qualifier != null ? ToSemType(named.Qualifier, context, method) as NamedType : null;
            foreach (var level in chain)
            {
                foreach (var parameter in level.TypeParameters)
                {
                    if (outer != null && all.Count < outer.Arguments.Count)
                    {
                        all.Add(outer.Arguments[all.Count]);
                    }
                    else if (context.TypeParameters.TryGetValue(parameter, out var form))
                    {
                        all.Add(TypeParameter(parameter, form, context, method));
                    }
                    else
                    {
                        all.Add(SemType.Unknown);
                    }
                }
            }

            for (var i = 0; i < bound.Symbol.TypeParameters.Count; i++)
            {
                all.Add(i < arguments.Count ? arguments[i] : SemType.Unknown);
            }

            return TypeOf(bound.Symbol, all);
        }

        var last = parts.Count == 0 ? "" : parts[^1];
        var full = string.Join(".", parts);
        if (named.Qualifier is NamedTypeReference qualifier)
        {
            full = string.Join(".", qualifier.Name.Parts) + "." + full;
        }

        return new ExternalType(last, full, arguments);
    }

    private static SemType TypeParameter(string name, string form, BindingContext context, CodeSymbol? method)
    {
        if (form.StartsWith("``", StringComparison.Ordinal))
        {
            return new TypeParameterType(name, int.Parse(form.AsSpan(2)), true, method);
        }

        if (form.StartsWith('`'))
        {
            var ordinal = int.Parse(form.AsSpan(1));
            return new TypeParameterType(name, ordinal, false, TypeParameterOwner(context.Type, ordinal));
        }

        // A local function's type parameter
        return new TypeParameterType(name, -1, true, null);
    }

    /// <summary>The type among <paramref name="type"/> and its containing types that declares the type parameter <c>`ordinal</c>.</summary>
    private static CodeSymbol? TypeParameterOwner(CodeSymbol? type, int ordinal)
    {
        var chain = new List<CodeSymbol>();
        for (var t = type; t != null; t = t.ContainingType)
        {
            chain.Insert(0, t);
        }

        var offset = 0;
        foreach (var level in chain)
        {
            if (ordinal < offset + level.TypeParameters.Count)
            {
                return level;
            }

            offset += level.TypeParameters.Count;
        }

        return null;
    }

    public static SemType Predefined(PredefinedType type) => type switch
    {
        PredefinedType.Object => SemTypes.Object,
        PredefinedType.String => SemTypes.String,
        PredefinedType.Bool => SemTypes.Boolean,
        PredefinedType.Byte => SemTypes.Predefined("Byte"),
        PredefinedType.SByte => SemTypes.Predefined("SByte"),
        PredefinedType.Short => SemTypes.Predefined("Int16"),
        PredefinedType.UShort => SemTypes.Predefined("UInt16"),
        PredefinedType.Int => SemTypes.Int32,
        PredefinedType.UInt => SemTypes.Predefined("UInt32"),
        PredefinedType.Long => SemTypes.Int64,
        PredefinedType.ULong => SemTypes.Predefined("UInt64"),
        PredefinedType.Float => SemTypes.Predefined("Single"),
        PredefinedType.Double => SemTypes.Double,
        PredefinedType.Decimal => SemTypes.Predefined("Decimal"),
        PredefinedType.Char => SemTypes.Char,
        PredefinedType.Void => SemType.Void,
        _ => SemType.Unknown,
    };

    // ========================================
    // Member lookup
    // ========================================

    /// <summary>
    /// The members named <paramref name="name"/> of a type and its base types, most derived first. A member that
    /// is not a method hides the ones further up; methods of all levels are candidates, except those overridden
    /// by a member already found. With type arguments (<c>x.Value&lt;string&gt;()</c>) only the methods with that
    /// many type parameters are members, so a property of the name hides nothing.
    /// </summary>
    public List<FoundMember> LookupMembers(SemType receiver, string name, int arity = 0)
    {
        var found = new List<FoundMember>();
        var visited = new HashSet<CodeSymbol>();
        var queue = new Queue<(CodeSymbol Symbol, IReadOnlyList<SemType> Arguments)>();
        void Enqueue(SemType type)
        {
            switch (type.Underlying)
            {
                case TypeParameterType parameter:
                    foreach (var constraint in Constraints(parameter))
                    {
                        Enqueue(constraint);
                    }

                    break;
                case ArrayType when FindExternal("System.Array") is { } array:
                    queue.Enqueue((array, []));
                    break;
                case var other when SymbolOf(other) is { } named:
                    queue.Enqueue(named);
                    break;
            }
        }

        if (receiver is NullableType { Element: var element } && IsValueType(element.Underlying) && FindExternal("System.Nullable`1") is { } nullable)
        {
            // Members of Nullable<T> (HasValue, Value, GetValueOrDefault), not of T
            queue.Enqueue((nullable, [element]));
        }
        else
        {
            Enqueue(receiver);
        }

        var hidden = false;
        while (queue.Count > 0 && visited.Count < 64)
        {
            var type = queue.Dequeue();
            if (!visited.Add(type.Symbol))
            {
                continue;
            }

            var map = ArgumentMap(type.Symbol, type.Arguments);
            if (!hidden)
            {
                foreach (var member in type.Symbol.Members)
                {
                    if (member.Name != name || member.Kind.IsType() || member.ExplicitInterface != null || member.Kind is SymbolKind.Constructor or SymbolKind.Destructor)
                    {
                        continue;
                    }

                    if (arity > 0 && (member.Kind != SymbolKind.Method || member.TypeParameters.Count != arity))
                    {
                        continue;
                    }

                    // Overridden, or hidden by a method with the same signature further down ('new')
                    if (found.Any(f => Overrides(f.Symbol, member)) || found.Any(f => f.Symbol.ContainingType != member.ContainingType && SameSignature(f, member, map)))
                    {
                        continue;
                    }

                    found.Add(new FoundMember(member, map));
                }

                // A property, field or event hides the members further up; methods are overloaded across levels
                if (found.Count > 0 && found.Any(f => f.Symbol.Kind is not SymbolKind.Method))
                {
                    hidden = true;
                }
            }

            if (hidden)
            {
                break;
            }

            var hasBaseClass = false;
            foreach (var baseType in BaseTypes(type.Symbol))
            {
                // A class or struct declares every member of the interfaces it implements, so these add nothing
                var substituted = SemTypes.Substitute(baseType, map);
                var baseSymbol = SymbolOf(substituted.Underlying);
                if (type.Symbol.Kind != SymbolKind.Interface && baseSymbol is { Symbol.Kind: SymbolKind.Interface })
                {
                    continue;
                }

                hasBaseClass |= baseSymbol is { Symbol.Kind: SymbolKind.Class or SymbolKind.Record } || substituted is ExternalType { Definition: null };
                Enqueue(substituted);
            }

            // What every type of its kind derives from: System.Object, System.ValueType, System.Enum, System.MulticastDelegate
            if (!hasBaseClass && ImplicitBase(type.Symbol) is { } implicitBase && implicitBase != type.Symbol)
            {
                queue.Enqueue((implicitBase, []));
            }
        }

        return found;
    }

    private CodeSymbol? FindExternal(string key) => Metadata.TypeCount != 0 && Metadata.FindType(key) is { } definition ? ExternalSymbol(definition) : null;

    /// <summary>The class a type derives from when its declaration names none.</summary>
    private CodeSymbol? ImplicitBase(CodeSymbol type)
    {
        if (type.IsExternal && type.Kind != SymbolKind.Interface)
        {
            return null;
        }

        return type.Kind switch
        {
            SymbolKind.Enum => FindExternal("System.Enum"),
            SymbolKind.Struct or SymbolKind.RecordStruct => FindExternal("System.ValueType"),
            SymbolKind.Delegate => FindExternal("System.MulticastDelegate"),
            _ => FindExternal("System.Object"),
        };
    }

    /// <summary>How the type parameters of a type (and its containing types) map to the type arguments of a constructed type.</summary>
    private static Func<TypeParameterType, SemType?> ArgumentMap(CodeSymbol type, IReadOnlyList<SemType> arguments) =>
        p => !p.IsMethod && p.Ordinal >= 0 && p.Ordinal < arguments.Count && (p.Owner == null || IsInChain(p.Owner, type)) ? arguments[p.Ordinal] : null;

    /// <summary>
    /// Whether a member already found, further down the hierarchy, has the parameter types of
    /// <paramref name="member"/>: it overrides it (overrides of external members are not linked) or hides it.
    /// </summary>
    private bool SameSignature(FoundMember found, CodeSymbol member, Func<TypeParameterType, SemType?> map)
    {
        if (found.Symbol.Kind != member.Kind || found.Symbol.TypeParameters.Count != member.TypeParameters.Count)
        {
            return false;
        }

        var ours = Parameters(found.Symbol);
        var theirs = Parameters(member);
        if (ours.Count != theirs.Count)
        {
            return false;
        }

        for (var i = 0; i < ours.Count; i++)
        {
            var a = found.Substitute(ours[i].Type);
            var b = SemTypes.Substitute(theirs[i].Type, map);
            if (!Equals(a, b) && !(a is TypeParameterType { IsMethod: true } x && b is TypeParameterType { IsMethod: true } y && x.Ordinal == y.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="derived"/> overrides <paramref name="member"/>, directly or through other overrides.</summary>
    private static bool Overrides(CodeSymbol derived, CodeSymbol member)
    {
        for (var current = derived.Overrides; current != null; current = current.Overrides)
        {
            if (current == member)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInChain(CodeSymbol owner, CodeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
        {
            if (t == owner)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The indexers of a type and its base types.</summary>
    public List<FoundMember> LookupIndexers(SemType receiver)
    {
        var found = new List<FoundMember>();
        var visited = new HashSet<CodeSymbol>();
        var queue = new Queue<SemType>([receiver.Underlying]);
        while (queue.Count > 0 && found.Count == 0 && visited.Count < 64)
        {
            var current = queue.Dequeue();
            if (current is TypeParameterType parameter)
            {
                foreach (var constraint in Constraints(parameter))
                {
                    queue.Enqueue(constraint.Underlying);
                }

                continue;
            }

            if (SymbolOf(current) is not { } type || !visited.Add(type.Symbol))
            {
                continue;
            }

            var map = ArgumentMap(type.Symbol, type.Arguments);
            found.AddRange(type.Symbol.Members.Where(m => m.Kind == SymbolKind.Indexer && m.ExplicitInterface == null).Select(m => new FoundMember(m, map)));
            foreach (var baseType in BaseTypes(type.Symbol))
            {
                queue.Enqueue(SemTypes.Substitute(baseType, map).Underlying);
            }
        }

        return found;
    }

    /// <summary>The instance constructors of a workspace type or a type from a reference.</summary>
    public List<FoundMember> Constructors(SemType type)
    {
        if (SymbolOf(type.Underlying) is not { } named)
        {
            return [];
        }

        var map = ArgumentMap(named.Symbol, named.Arguments);
        return named.Symbol.Members.Where(m => m.Kind == SymbolKind.Constructor && !m.Modifiers.Contains("static")).Select(m => new FoundMember(m, map)).ToList();
    }

    /// <summary>All the types a type converts to by an implicit reference conversion: itself, its base types and interfaces.</summary>
    public IEnumerable<SemType> Ancestors(SemType type)
    {
        var visited = new HashSet<CodeSymbol>();
        var queue = new Queue<SemType>([type.Underlying]);
        var count = 0;
        while (queue.Count > 0 && count++ < 64)
        {
            var current = queue.Dequeue();
            yield return current;
            if (current is TypeParameterType parameter)
            {
                foreach (var constraint in Constraints(parameter))
                {
                    queue.Enqueue(constraint.Underlying);
                }
            }
            else if (SymbolOf(current) is { } named && visited.Add(named.Symbol))
            {
                var map = ArgumentMap(named.Symbol, named.Arguments);
                foreach (var baseType in BaseTypes(named.Symbol))
                {
                    queue.Enqueue(SemTypes.Substitute(baseType, map).Underlying);
                }
            }
        }
    }

    /// <summary>The workspace extension methods named <paramref name="name"/> whose static class's namespace is in scope.</summary>
    public IEnumerable<CodeSymbol> ExtensionMethods(string name, BindingContext context)
    {
        var namespaces = NamespacesInScope(context.Imports);
        var workspace = _extensionMethods.TryGetValue(name, out var methods) ? methods.Where(m => namespaces.Contains(m.ContainingType?.Namespace ?? "")) : [];
        var external = Metadata.TypeCount == 0 ? [] : ExternalExtensionMethods(name).Where(m => namespaces.Contains(m.ContainingType?.Namespace ?? ""));
        return workspace.Concat(external);
    }

    private HashSet<string> NamespacesInScope(ImportScope imports) => _namespacesInScope.GetOrAdd(imports, scope =>
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { "" };
        for (var s = scope; s != null; s = s.Parent)
        {
            if (s.NamespaceName is { } ns)
            {
                result.Add(ns);
            }

            result.UnionWith(s.Namespaces(this));
            if (s.Globals != null)
            {
                result.UnionWith(s.Globals.Namespaces(this));
            }
        }

        return result;
    });

    /// <summary>The static types of the <c>using static</c> directives in scope.</summary>
    public IEnumerable<CodeSymbol> StaticImports(BindingContext context)
    {
        for (var scope = context.Imports; scope != null; scope = scope.Parent)
        {
            foreach (var type in scope.StaticTypes(this))
            {
                yield return type;
            }

            if (scope.Globals != null)
            {
                foreach (var type in scope.Globals.StaticTypes(this))
                {
                    yield return type;
                }
            }
        }
    }

    // ========================================
    // Conversions
    // ========================================

    private static readonly Dictionary<string, string[]> NumericWidening = new(StringComparer.Ordinal)
    {
        ["SByte"] = ["Int16", "Int32", "Int64", "Single", "Double", "Decimal", "IntPtr"],
        ["Byte"] = ["Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal", "IntPtr", "UIntPtr"],
        ["Int16"] = ["Int32", "Int64", "Single", "Double", "Decimal", "IntPtr"],
        ["UInt16"] = ["Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal", "IntPtr", "UIntPtr"],
        ["Int32"] = ["Int64", "Single", "Double", "Decimal", "IntPtr"],
        ["UInt32"] = ["Int64", "UInt64", "Single", "Double", "Decimal", "UIntPtr"],
        ["Int64"] = ["Single", "Double", "Decimal"],
        ["UInt64"] = ["Single", "Double", "Decimal"],
        ["Char"] = ["UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal", "IntPtr", "UIntPtr"],
        ["Single"] = ["Double"],
    };

    private static readonly HashSet<string> ValueTypeNames = new(StringComparer.Ordinal)
    {
        "Boolean", "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal", "Char",
        "IntPtr", "UIntPtr", "DateTime", "TimeSpan", "Guid", "DateTimeOffset", "CancellationToken", "KeyValuePair", "Nullable",
        "ValueTuple", "Span", "ReadOnlySpan", "Memory", "ReadOnlyMemory", "ValueTask", "ImmutableArray",
    };

    /// <summary>
    /// How well a value of type <paramref name="from"/> converts to <paramref name="to"/>: 2 identity, 1 implicit,
    /// 0 unknown (a type from a reference, a lambda), -1 no conversion.
    /// </summary>
    public int Conversion(SemType from, SemType to)
    {
        if (from.IsUnknown || to.IsUnknown)
        {
            return 0;
        }

        if (from == SemType.Void)
        {
            return -1;
        }

        // A value is never a type: a name taken for a type is a value whose type is not known
        if (from is TypeExpressionType or NamespaceExpressionType)
        {
            return 0;
        }

        if (to is TypeParameterType { IsMethod: true })
        {
            return 1;
        }

        if (Equals(from, to))
        {
            return 2;
        }

        if (from is NullType)
        {
            return IsValueType(to) ? -1 : 1;
        }

        if (to is NullableType nullableTo)
        {
            var inner = Conversion(from.Underlying, nullableTo.Element);
            return inner == 2 ? 1 : inner;
        }

        from = from.Underlying;
        if (Equals(from, to))
        {
            return 2;
        }

        if (to is ExternalType { Name: "Object", Arguments.Count: 0 })
        {
            return 1;
        }

        if (from is LambdaType)
        {
            return to switch
            {
                NamedType { Symbol.Kind: SymbolKind.Delegate } => 1,
                NamedType => -1,
                ExternalType external when IsDelegateName(external.Name) => 1,
                ExternalType external when DefinitionOf(external) is { } definition => definition.Kind == MetadataTypeKind.Delegate ? 1 : -1,
                _ when SemTypes.IsPredefined(to) => -1,
                _ => 0,
            };
        }

        switch (from, to)
        {
            case (ExternalType a, ExternalType b) when a.Name == b.Name && a.Arguments.Count == b.Arguments.Count:
                return a.Arguments.Zip(b.Arguments).Any(p => Conversion(p.First, p.Second) < 0 && !IsVariant(b.Name)) ? -1 : 1;
            case (ExternalType a, ExternalType b) when a.Arguments.Count == 0 && NumericWidening.TryGetValue(a.Name, out var wider):
                return wider.Contains(b.Name) ? 1 : SemTypes.IsPredefined(b) ? -1 : 0;
            case (ExternalType a, ExternalType b) when SemTypes.IsPredefined(a) && SemTypes.IsPredefined(b):
                return -1;
            case (ExternalType a, ExternalType b) when DefinitionOf(a) is { } definitionA && DefinitionOf(b) is { } definitionB:
                return ExternalConversion(a, b, definitionA, definitionB);
            case (ExternalType a, NamedType b):
                // Only a user-defined conversion of the workspace type
                return HasConversion(b.Symbol) ? 0 : a.Name == "Object" ? 0 : -1;
            case (NamedType a, ExternalType b):
            {
                if (HasConversion(a.Symbol))
                {
                    return 0;
                }

                if (a.Symbol.Kind == SymbolKind.Enum)
                {
                    return b.Name is "Enum" or "ValueType" or "IComparable" or "IConvertible" or "IFormattable" ? 1 : -1;
                }

                if (a.Symbol.Kind == SymbolKind.Delegate)
                {
                    return b.Name is "Delegate" or "MulticastDelegate" ? 1 : -1;
                }

                var sawExternal = false;
                foreach (var ancestor in Ancestors(a))
                {
                    if (ancestor is ExternalType external)
                    {
                        // A type from a reference whose own bases are not known could implement it
                        sawExternal |= DefinitionOf(external) == null;
                        if (external.Name == b.Name && external.Arguments.Count == b.Arguments.Count)
                        {
                            return 1;
                        }
                    }
                }

                if (DefinitionOf(b) is { } target && HasExternalConversion(target))
                {
                    return 0;
                }

                // A type from a reference could implement it; records and structs implement some interfaces
                return sawExternal || b.Name.StartsWith("IEquatable", StringComparison.Ordinal) || b.Name is "ValueType" or "IComparable" ? 0 : -1;
            }

            case (NamedType a, NamedType b):
            {
                foreach (var ancestor in Ancestors(a))
                {
                    if (ancestor is NamedType named && named.Symbol == b.Symbol)
                    {
                        return 1;
                    }
                }

                return HasConversion(a.Symbol) || HasConversion(b.Symbol) ? 0 : -1;
            }

            case (ArrayType a, ArrayType b):
                return a.Rank != b.Rank ? -1 : Math.Min(1, Conversion(a.Element, b.Element));
            case (ArrayType a, ExternalType b) when DefinitionOf(b) is { } arrayTarget:
                return arrayTarget.Namespace switch
                {
                    "System" when b.Name is "Array" or "ICloneable" => 1,
                    "System.Collections" when b.Name is "IEnumerable" or "ICollection" or "IList" or "IStructuralComparable" or "IStructuralEquatable" => 1,
                    "System.Collections.Generic" when b.Arguments.Count == 1 && b.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
                        => a.Rank == 1 ? Math.Min(1, Conversion(a.Element, b.Arguments[0])) : -1,
                    // T[] to Span<T>, ReadOnlySpan<T>, Memory<T>...: a conversion operator of the target taking an array
                    _ => arrayTarget.Members.Any(m => m.Kind == MetadataMemberKind.Operator && m.Name == "op_Implicit" && m.Parameters is [{ Type: MetaArray }])
                        ? (b.Arguments.Count == 1 ? Math.Min(1, Conversion(a.Element, b.Arguments[0])) : 0)
                        : -1,
                };
            case (ArrayType a, ExternalType b):
                return b.Name is "Array" or "ICloneable" ? 1
                    : b.Arguments.Count == 1 && SemTypes.ElementOf(b) != null ? Math.Min(1, Conversion(a.Element, b.Arguments[0]))
                    : b.Arguments.Count == 0 && b.Name is "IEnumerable" or "ICollection" or "IList" ? 1
                    : -1;
            case (ExternalType { Name: "String" }, ArrayType):
                return -1;
            case (TupleType a, TupleType b):
                return a.Elements.Count != b.Elements.Count ? -1 : a.Elements.Zip(b.Elements).Min(p => Math.Min(1, Conversion(p.First, p.Second)));
            case (TupleType, NamedType):
            case (TupleType, ArrayType):
                return -1;
            case (TypeParameterType parameter, _):
            {
                foreach (var constraint in Constraints(parameter))
                {
                    if (Conversion(constraint, to) > 0)
                    {
                        return 1;
                    }
                }

                return 0;
            }

            case (_, TypeParameterType):
                return 0;
            case (NamedType, ArrayType):
            case (NamedType, TupleType):
                return -1;
            case (ExternalType a, ArrayType) when SemTypes.IsPredefined(a):
                return -1;
            default:
                return 0;
        }
    }

    /// <summary>A conversion between two types from references: to a base type or interface, or a user-defined one.</summary>
    private int ExternalConversion(ExternalType from, ExternalType to, MetadataType fromDefinition, MetadataType toDefinition)
    {
        if (fromDefinition == toDefinition)
        {
            return ArgumentsConvert(from.Arguments, to.Arguments, to.Name) ? 1 : -1;
        }

        foreach (var ancestor in Ancestors(from))
        {
            if (ancestor is ExternalType external && DefinitionOf(external) == toDefinition)
            {
                return ArgumentsConvert(external.Arguments, to.Arguments, to.Name) ? 1 : -1;
            }

            if (ancestor is ExternalType { Definition: null } unknown && DefinitionOf(unknown) == null)
            {
                return 0;
            }
        }

        if (UserDefinedConversion(fromDefinition, toDefinition))
        {
            return 1;
        }

        // Enums convert to their base types only; a type parameter's constraints are not known
        return -1;
    }

    /// <summary>
    /// Whether the type arguments of a generic type allow converting to <paramref name="to"/>: identical, or, for a
    /// variant interface or delegate, converting (not failing to convert) one by one.
    /// </summary>
    private bool ArgumentsConvert(IReadOnlyList<SemType> from, IReadOnlyList<SemType> to, string name) =>
        IsVariant(name) ? from.Zip(to).All(p => Conversion(p.First, p.Second) >= 0) : from.Zip(to).All(p => Conversion(p.First, p.Second) >= 0 && (Conversion(p.Second, p.First) >= 0 || p.First.IsUnknown));

    private static bool IsVariant(string name) => name is "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection" or "Func" or "Action" or "IEnumerator" or "IQueryable" or "Predicate" or "IComparer" or "IEqualityComparer" or "IComparable";

    private static bool IsDelegateName(string name) => name is "Func" or "Action" or "Predicate" or "Comparison" or "Converter" or "EventHandler" or "Expression" or "Delegate" or "MulticastDelegate" or "AsyncCallback";

    private static bool HasConversion(CodeSymbol type) => type.Members.Any(m => m.Kind == SymbolKind.Operator && m.Id.Contains(".op_Implicit(", StringComparison.Ordinal));

    private bool IsValueType(SemType type) => type switch
    {
        NamedType named => named.Symbol.Kind is SymbolKind.Struct or SymbolKind.RecordStruct or SymbolKind.Enum,
        ExternalType external when DefinitionOf(external) is { } definition => definition.Kind is MetadataTypeKind.Struct or MetadataTypeKind.Enum,
        ExternalType external => ValueTypeNames.Contains(external.Name),
        TupleType => true,
        _ => false,
    };

    /// <summary>The parameter types and return type of a delegate type; null when it is not a known delegate type.</summary>
    public (IReadOnlyList<SemType> Parameters, SemType Return)? DelegateSignature(SemType type)
    {
        switch (type.Underlying)
        {
            case NamedType { Symbol.Kind: SymbolKind.Delegate } named:
            {
                Func<TypeParameterType, SemType?> map = p => !p.IsMethod && p.Ordinal >= 0 && p.Ordinal < named.Arguments.Count ? named.Arguments[p.Ordinal] : null;
                return (Parameters(named.Symbol).Select(p => SemTypes.Substitute(p.Type, map)).ToList(), SemTypes.Substitute(MemberType(named.Symbol), map));
            }

            case ExternalType { Name: "Func", Arguments.Count: > 0 } func:
                return (func.Arguments.Take(func.Arguments.Count - 1).ToList(), func.Arguments[^1]);
            case ExternalType { Name: "Action" } action:
                return (action.Arguments, SemType.Void);
            case ExternalType { Name: "Predicate", Arguments.Count: 1 } predicate:
                return (predicate.Arguments, SemTypes.Boolean);
            case ExternalType { Name: "Comparison", Arguments.Count: 1 } comparison:
                return ([comparison.Arguments[0], comparison.Arguments[0]], SemTypes.Int32);
            case ExternalType { Name: "Converter", Arguments.Count: 2 } converter:
                return ([converter.Arguments[0]], converter.Arguments[1]);
            case ExternalType { Name: "EventHandler", Arguments.Count: 1 } handler:
                return ([SemTypes.Object, handler.Arguments[0]], SemType.Void);
            case ExternalType { Name: "EventHandler", Arguments.Count: 0 }:
                return ([SemTypes.Object, new ExternalType("EventArgs", "System.EventArgs", [])], SemType.Void);
            case ExternalType { Name: "Expression", Arguments.Count: 1 } expression:
                return DelegateSignature(expression.Arguments[0]);
            case ExternalType external when DefinitionOf(external) is { Kind: MetadataTypeKind.Delegate } definition:
            {
                var symbol = ExternalSymbol(definition);
                var map = ArgumentMap(symbol, external.Arguments);
                return (Parameters(symbol).Select(p => SemTypes.Substitute(p.Type, map)).ToList(), SemTypes.Substitute(MemberType(symbol), map));
            }

            default:
                return null;
        }
    }
}

// Members by name, for calls whose receiver's type is unknown
public sealed partial class CSharpBinder
{
    private Dictionary<string, List<CodeSymbol>>? _membersByName;

    /// <summary>The workspace methods, properties, fields and events named <paramref name="name"/>.</summary>
    public IReadOnlyList<CodeSymbol> MembersNamed(string name)
    {
        var index = _membersByName;
        if (index == null)
        {
            lock (_lock)
            {
                index = _membersByName ??= _builder.Symbols
                    .Where(s => s.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event && s.ExplicitInterface == null)
                    .GroupBy(s => s.Name, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            }
        }

        return index.TryGetValue(name, out var list) ? list : [];
    }
}
