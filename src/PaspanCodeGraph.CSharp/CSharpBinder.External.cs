using System.Collections.Concurrent;
using PaspanCodeGraph.Metadata;
using PaspanParsers.CSharp;

namespace PaspanCodeGraph.CSharp;

// Types and members of referenced assemblies: found by name like workspace types, and given CodeSymbols (not in
// the index) so that member lookup, overload resolution and conversions treat them like workspace ones
public sealed partial class CSharpBinder
{
    private readonly ConcurrentDictionary<MetadataType, Lazy<CodeSymbol>> _externalTypes = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<CodeSymbol, MetadataType> _externalDefinitions = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<CodeSymbol, MetadataMember> _externalMembers = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<(string FullName, int Arity), MetadataType?> _definitionsByName = new();
    private Dictionary<string, List<CodeSymbol>>? _externalExtensionMethods;

    /// <summary>The types of the referenced assemblies; empty when they are not known.</summary>
    public MetadataCatalog Metadata { get; set; } = MetadataCatalog.Empty;

    /// <summary>Whether a dotted name is a namespace of the workspace or of a referenced assembly.</summary>
    private bool IsNamespace(string full) => _builder.Get("N:" + full) != null || Metadata.IsNamespace(full);

    /// <summary>The symbol of a type from a referenced assembly, created on first use.</summary>
    public CodeSymbol ExternalSymbol(MetadataType type) =>
        _externalTypes.GetOrAdd(type, t => new Lazy<CodeSymbol>(() => CreateExternalType(t))).Value;

    /// <summary>The type read from an assembly for an external type symbol.</summary>
    public MetadataType? DefinitionOf(CodeSymbol symbol) => _externalDefinitions.GetValueOrDefault(symbol);

    /// <summary>The metadata member of an external member symbol.</summary>
    public MetadataMember? ExternalMember(CodeSymbol symbol) => _externalMembers.GetValueOrDefault(symbol);

    /// <summary>The type an external type refers to: its <see cref="ExternalType.Definition"/>, else found by full name.</summary>
    public MetadataType? DefinitionOf(ExternalType type)
    {
        if (type.Definition != null)
        {
            return type.Definition;
        }

        if (Metadata.TypeCount == 0 || !type.FullName.Contains('.'))
        {
            return null;
        }

        return _definitionsByName.GetOrAdd((type.FullName, type.Arguments.Count), key =>
        {
            var dot = key.FullName.LastIndexOf('.');
            return Metadata.FindType(key.FullName[..dot], key.FullName[(dot + 1)..], key.Arity);
        });
    }

    /// <summary>A named type as a <see cref="SemType"/>: an <see cref="ExternalType"/> for a type from a reference.</summary>
    public SemType TypeOf(CodeSymbol type, IReadOnlyList<SemType> arguments)
    {
        if (DefinitionOf(type) is { } definition)
        {
            return ToSemType(definition, arguments);
        }

        return new NamedType(type, arguments);
    }

    private static SemType ToSemType(MetadataType definition, IReadOnlyList<SemType> arguments)
    {
        if (definition.Namespace == "System" && definition.DeclaringType == null)
        {
            if (definition is { Name: "Nullable", Arity: 1 } && arguments.Count == 1)
            {
                return new NullableType(arguments[0]);
            }

            if (definition is { Name: "ValueTuple", Arity: >= 2 and <= 7 } && arguments.Count == definition.Arity)
            {
                return new TupleType(arguments, arguments.Select(_ => (string?)null).ToList());
            }
        }

        return new ExternalType(definition.Name, definition.FullName, arguments) { Definition = definition };
    }

    /// <summary>A type of a signature in metadata, for members of <paramref name="owner"/> (and <paramref name="method"/>).</summary>
    private SemType ToSemType(MetaType type, CodeSymbol owner, CodeSymbol? method)
    {
        switch (type)
        {
            case MetaNamed named:
            {
                var arguments = named.Arguments.Select(a => ToSemType(a, owner, method)).ToList();
                if (Metadata.FindType(named.Key) is { } definition)
                {
                    return ToSemType(definition, arguments);
                }

                return new ExternalType(named.Name, named.FullName, arguments);
            }

            case MetaTypeParameter { IsMethod: false } parameter:
            {
                var definition = DefinitionOf(owner);
                var name = definition != null && parameter.Index < definition.TypeParameters.Count ? definition.TypeParameters[parameter.Index] : "T" + parameter.Index;
                return new TypeParameterType(name, parameter.Index, false, TypeParameterOwner(owner, parameter.Index));
            }

            case MetaTypeParameter parameter:
            {
                var member = method != null ? ExternalMember(method) : null;
                var name = member != null && parameter.Index < member.TypeParameters.Count ? member.TypeParameters[parameter.Index] : "T" + parameter.Index;
                return new TypeParameterType(name, parameter.Index, true, method);
            }

            case MetaArray array:
                return new ArrayType(ToSemType(array.Element, owner, method), array.Rank);
            case MetaByRef byRef:
                return ToSemType(byRef.Element, owner, method);
            default:
                return SemType.Unknown;
        }
    }

    private CodeSymbol CreateExternalType(MetadataType type)
    {
        var container = type.DeclaringType is { } declaring ? ExternalSymbol(declaring) : null;
        var kind = type.Kind switch
        {
            MetadataTypeKind.Struct => SymbolKind.Struct,
            MetadataTypeKind.Interface => SymbolKind.Interface,
            MetadataTypeKind.Enum => SymbolKind.Enum,
            MetadataTypeKind.Delegate => SymbolKind.Delegate,
            _ => SymbolKind.Class,
        };
        var symbol = new CodeSymbol(type.DocId, kind, type.Name)
        {
            Container = container,
            Namespace = type.Namespace.Length == 0 ? null : type.Namespace,
            Assembly = type.Assembly,
            Accessibility = "public",
            Signature = type.FullName,
        };
        if (type.IsStatic)
        {
            symbol.Modifiers.Add("static");
        }
        else if (type.IsAbstract && kind == SymbolKind.Class)
        {
            symbol.Modifiers.Add("abstract");
        }
        else if (type.IsSealed && kind == SymbolKind.Class)
        {
            symbol.Modifiers.Add("sealed");
        }

        symbol.TypeParameters.AddRange(type.TypeParameters.Skip(type.TypeParameters.Count - type.Arity));
        _externalDefinitions[symbol] = type;
        foreach (var member in type.Members)
        {
            var memberKind = member.Kind switch
            {
                MetadataMemberKind.Constructor => SymbolKind.Constructor,
                MetadataMemberKind.Property => SymbolKind.Property,
                MetadataMemberKind.Indexer => SymbolKind.Indexer,
                MetadataMemberKind.Field => kind == SymbolKind.Enum ? SymbolKind.EnumMember : SymbolKind.Field,
                MetadataMemberKind.Event => SymbolKind.Event,
                MetadataMemberKind.Operator => SymbolKind.Operator,
                _ => SymbolKind.Method,
            };
            var memberSymbol = new CodeSymbol(member.DocId, memberKind, memberKind == SymbolKind.Indexer ? "this[]" : member.Name)
            {
                Container = symbol,
                Namespace = symbol.Namespace,
                Assembly = type.Assembly,
                Accessibility = member.IsProtected ? "protected" : "public",
                Signature = member.DocId[2..],
            };
            if (member.IsStatic && memberKind != SymbolKind.EnumMember)
            {
                memberSymbol.Modifiers.Add(member.IsConstant ? "const" : "static");
            }

            if (member.IsAbstract && kind != SymbolKind.Interface)
            {
                memberSymbol.Modifiers.Add("abstract");
            }
            else if (member.IsOverride)
            {
                memberSymbol.Modifiers.Add("override");
            }
            else if (member.IsVirtual && kind != SymbolKind.Interface)
            {
                memberSymbol.Modifiers.Add("virtual");
            }

            memberSymbol.TypeParameters.AddRange(member.TypeParameters);
            foreach (var parameter in member.Parameters)
            {
                memberSymbol.Parameters.Add(new ParameterInfo(parameter.Name, parameter.Type.DocId, Modifier(parameter), parameter.HasDefault ? "default" : null));
            }

            memberSymbol.Type = member.Type.DocId;
            _externalMembers[memberSymbol] = member;
            symbol.Members.Add(memberSymbol);
        }

        return symbol;
    }

    private static string? Modifier(MetadataParameter parameter) =>
        parameter.IsThis ? "this" : parameter.IsParams ? "params" : parameter.RefKind switch
        {
            MetaRefKind.Ref => "ref",
            MetaRefKind.Out => "out",
            MetaRefKind.In => "in",
            _ => null,
        };

    private SemType ExternalMemberType(CodeSymbol symbol, MetadataMember member)
    {
        if (member.Kind == MetadataMemberKind.Constructor)
        {
            return SelfType(symbol.ContainingType!);
        }

        return ToSemType(member.Type, symbol.ContainingType!, symbol);
    }

    private IReadOnlyList<ParameterSem> ExternalParameters(CodeSymbol symbol, MetadataMember member) =>
        member.Parameters.Select(p => new ParameterSem(
            p.Name,
            ToSemType(p.Type, symbol.ContainingType!, symbol),
            p.IsThis ? ParameterModifier.This : p.IsParams ? ParameterModifier.Params : p.RefKind switch
            {
                MetaRefKind.Ref => ParameterModifier.Ref,
                MetaRefKind.Out => ParameterModifier.Out,
                MetaRefKind.In => ParameterModifier.In,
                _ => ParameterModifier.None,
            },
            p.HasDefault)).ToList();

    private IReadOnlyList<SemType> ExternalBaseTypes(CodeSymbol symbol, MetadataType definition)
    {
        var result = new List<SemType>();
        if (definition.BaseType != null && definition.Kind is MetadataTypeKind.Class or MetadataTypeKind.Struct or MetadataTypeKind.Enum or MetadataTypeKind.Delegate)
        {
            result.Add(ToSemType(definition.BaseType, symbol, null));
        }

        result.AddRange(definition.Interfaces.Select(i => ToSemType(i, symbol, null)));
        return result;
    }

    private IReadOnlyList<SemType> ExternalConstraints(CodeSymbol owner, TypeParameterType parameter)
    {
        if (parameter.IsMethod)
        {
            return [];
        }

        return DefinitionOf(owner) is { } definition && parameter.Ordinal >= 0 && parameter.Ordinal < definition.Constraints.Count
            ? definition.Constraints[parameter.Ordinal].Select(c => ToSemType(c, owner, null)).ToList()
            : [];
    }

    /// <summary>The symbol and type arguments behind a named type, workspace or external.</summary>
    public (CodeSymbol Symbol, IReadOnlyList<SemType> Arguments)? SymbolOf(SemType type) => type switch
    {
        NamedType named => (named.Symbol, named.Arguments),
        ExternalType external when DefinitionOf(external) is { } definition => (ExternalSymbol(definition), external.Arguments),
        _ => null,
    };

    /// <summary>A nested type of an external type.</summary>
    private CodeSymbol? FindExternalNestedType(CodeSymbol type, string name, int arity, int depth)
    {
        if (DefinitionOf(type) is not { } definition)
        {
            return null;
        }

        for (var current = definition; current != null && depth++ < 16;)
        {
            if (current.NestedTypes.FirstOrDefault(n => n.Name == name && n.Arity == arity) is { } nested)
            {
                return ExternalSymbol(nested);
            }

            current = current.BaseType is MetaNamed baseType ? Metadata.FindType(baseType.Key) : null;
        }

        return null;
    }

    /// <summary>The extension methods of the referenced assemblies named <paramref name="name"/>, as symbols.</summary>
    private IReadOnlyList<CodeSymbol> ExternalExtensionMethods(string name)
    {
        var index = _externalExtensionMethods;
        if (index == null)
        {
            lock (_lock)
            {
                index = _externalExtensionMethods ??= new Dictionary<string, List<CodeSymbol>>(StringComparer.Ordinal);
            }
        }

        lock (index)
        {
            if (index.TryGetValue(name, out var known))
            {
                return known;
            }
        }

        var result = new List<CodeSymbol>();
        foreach (var method in Metadata.ExtensionMethods(name))
        {
            var type = ExternalSymbol(method.DeclaringType);
            if (type.Members.FirstOrDefault(m => m.Id == method.DocId) is { } symbol)
            {
                result.Add(symbol);
            }
        }

        lock (index)
        {
            index[name] = result;
        }

        return result;
    }

    /// <summary>Whether one of the two types declares an implicit conversion from the first to the second.</summary>
    private static bool UserDefinedConversion(MetadataType from, MetadataType to)
    {
        static bool Is(MetaType type, MetadataType definition) => type is MetaNamed named && named.Key == definition.Key;
        return from.Members.Concat(to.Members).Any(m => m.Kind == MetadataMemberKind.Operator && m.Name == "op_Implicit"
            && m.Parameters.Count == 1 && Is(m.Parameters[0].Type, from) && Is(m.Type, to));
    }

    /// <summary>Whether an external type declares an implicit conversion (from or to it).</summary>
    private bool HasExternalConversion(MetadataType definition) =>
        definition.Members.Any(m => m.Kind == MetadataMemberKind.Operator && m.Name == "op_Implicit");
}
