using System.Text;

namespace PaspanCodeGraph.Metadata;

/// <summary>A type as a signature in metadata writes it: a named type (possibly constructed), a type parameter, an array, a pointer or a reference.</summary>
public abstract record MetaType
{
    /// <summary>The documentation-comment id form of the type, as in parameter lists: <c>System.Collections.Generic.List{System.Int32}</c>.</summary>
    public abstract string DocId { get; }

    public override string ToString() => DocId;
}

/// <summary>A named type: its namespace, the names of its levels (containing types first) and their own arities, and the type arguments of all levels.</summary>
public sealed record MetaNamed(string Namespace, IReadOnlyList<(string Name, int Arity)> Levels, IReadOnlyList<MetaType> Arguments) : MetaType
{
    /// <summary>The name of the innermost level, without arity.</summary>
    public string Name => Levels[^1].Name;

    public int TotalArity => Levels.Sum(l => l.Arity);

    /// <summary>The dotted name without arities: <c>System.Collections.Generic.Dictionary.KeyCollection</c>.</summary>
    public string FullName => Qualify(string.Join(".", Levels.Select(l => l.Name)));

    /// <summary>The key of the type definition: <c>System.Collections.Generic.Dictionary`2.KeyCollection</c>.</summary>
    public string Key => Qualify(string.Join(".", Levels.Select(l => l.Arity == 0 ? l.Name : $"{l.Name}`{l.Arity}")));

    public override string DocId
    {
        get
        {
            if (Arguments.Count == 0)
            {
                return Key;
            }

            var builder = new StringBuilder();
            var next = 0;
            foreach (var (name, arity) in Levels)
            {
                if (builder.Length != 0)
                {
                    builder.Append('.');
                }

                builder.Append(name);
                if (arity > 0)
                {
                    builder.Append('{');
                    for (var i = 0; i < arity; i++)
                    {
                        builder.Append(i == 0 ? "" : ",").Append(next < Arguments.Count ? Arguments[next].DocId : "?");
                        next++;
                    }

                    builder.Append('}');
                }
            }

            return Qualify(builder.ToString());
        }
    }

    private string Qualify(string name) => Namespace.Length == 0 ? name : $"{Namespace}.{name}";

    public bool Is(string ns, string name, int arity = 0) => Namespace == ns && Levels.Count == 1 && Levels[0].Name == name && Levels[0].Arity == arity;
}

public sealed record MetaTypeParameter(int Index, bool IsMethod) : MetaType
{
    public override string DocId => (IsMethod ? "``" : "`") + Index;
}

public sealed record MetaArray(MetaType Element, int Rank) : MetaType
{
    public override string DocId => Element.DocId + (Rank == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", Rank)) + "]");
}

public sealed record MetaPointer(MetaType Element) : MetaType
{
    public override string DocId => Element.DocId + "*";
}

public sealed record MetaByRef(MetaType Element) : MetaType
{
    public override string DocId => Element.DocId + "@";
}

/// <summary>A function pointer or another type the model does not represent.</summary>
public sealed record MetaUnknown : MetaType
{
    public static readonly MetaUnknown Instance = new();

    public override string DocId => "?";
}

public enum MetadataTypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
}

public enum MetadataMemberKind
{
    Method,
    Constructor,
    Property,
    Indexer,
    Field,
    Event,
    Operator,
}

public enum MetaRefKind
{
    None,
    Ref,
    Out,
    In,
}

public sealed record MetadataParameter(string Name, MetaType Type, MetaRefKind RefKind, bool IsParams, bool HasDefault, bool IsThis);

/// <summary>A public or protected member of a type from a referenced assembly.</summary>
public sealed class MetadataMember
{
    internal MetadataMember(MetadataType declaringType, MetadataMemberKind kind, string name)
    {
        DeclaringType = declaringType;
        Kind = kind;
        Name = name;
    }

    public MetadataType DeclaringType { get; }

    public MetadataMemberKind Kind { get; }

    public string Name { get; }

    public bool IsStatic { get; internal set; }

    public bool IsVirtual { get; internal set; }

    public bool IsAbstract { get; internal set; }

    public bool IsOverride { get; internal set; }

    public bool IsProtected { get; internal set; }

    /// <summary>An extension method: static, in a static class, with the <c>this</c> parameter first.</summary>
    public bool IsExtension { get; internal set; }

    /// <summary>A constant or <c>static readonly</c> field, an enum member.</summary>
    public bool IsConstant { get; internal set; }

    public IReadOnlyList<string> TypeParameters { get; internal set; } = [];

    public IReadOnlyList<MetadataParameter> Parameters { get; internal set; } = [];

    /// <summary>The return type of a method, the type of a property, indexer, field or event.</summary>
    public MetaType Type { get; internal set; } = MetaUnknown.Instance;

    /// <summary>The documentation-comment id: <c>M:System.String.Split(System.Char[])</c>.</summary>
    public string DocId { get; internal set; } = "";

    public override string ToString() => DocId;
}

/// <summary>A public type of a referenced assembly (or a protected nested one).</summary>
public sealed class MetadataType
{
    private readonly Func<MetadataType, List<MetadataMember>> _readMembers;
    private List<MetadataMember>? _members;
    private readonly object _lock = new();

    internal MetadataType(string ns, string name, int arity, MetadataType? declaringType, MetadataTypeKind kind, string assembly, Func<MetadataType, List<MetadataMember>> readMembers)
    {
        Namespace = ns;
        Name = name;
        Arity = arity;
        DeclaringType = declaringType;
        Kind = kind;
        Assembly = assembly;
        _readMembers = readMembers;
    }

    public string Namespace { get; }

    /// <summary>The name without arity.</summary>
    public string Name { get; }

    /// <summary>The number of type parameters the type declares itself (not those of containing types).</summary>
    public int Arity { get; }

    public MetadataType? DeclaringType { get; }

    public MetadataTypeKind Kind { get; }

    public string Assembly { get; }

    public bool IsStatic { get; internal set; }

    public bool IsAbstract { get; internal set; }

    public bool IsSealed { get; internal set; }

    /// <summary>The names of the type parameters of all levels, containing types first.</summary>
    public IReadOnlyList<string> TypeParameters { get; internal set; } = [];

    public MetaType? BaseType { get; internal set; }

    public IReadOnlyList<MetaType> Interfaces { get; internal set; } = [];

    /// <summary>The base types of each type parameter of all levels.</summary>
    public IReadOnlyList<IReadOnlyList<MetaType>> Constraints { get; internal set; } = [];

    public List<MetadataType> NestedTypes { get; } = [];

    /// <summary>Whether the type holds extension methods (a static class with the extension attribute).</summary>
    public bool HasExtensions { get; internal set; }

    public IReadOnlyList<MetadataMember> Members
    {
        get
        {
            if (_members == null)
            {
                lock (_lock)
                {
                    _members ??= _readMembers(this);
                }
            }

            return _members;
        }
    }

    public IEnumerable<(string Name, int Arity)> Levels =>
        DeclaringType is { } outer ? outer.Levels.Append((Name, Arity)) : [(Name, Arity)];

    public int TotalArity => (DeclaringType?.TotalArity ?? 0) + Arity;

    /// <summary>The key: <c>System.Collections.Generic.Dictionary`2.KeyCollection</c>.</summary>
    public string Key => (Namespace.Length == 0 ? "" : Namespace + ".") + string.Join(".", Levels.Select(l => l.Arity == 0 ? l.Name : $"{l.Name}`{l.Arity}"));

    /// <summary>The dotted name without arities.</summary>
    public string FullName => (Namespace.Length == 0 ? "" : Namespace + ".") + string.Join(".", Levels.Select(l => l.Name));

    public string DocId => "T:" + Key;

    /// <summary>The type itself as a signature type, with its own type parameters as arguments.</summary>
    public MetaNamed SelfType => new(Namespace, Levels.ToList(), Enumerable.Range(0, TotalArity).Select(i => (MetaType)new MetaTypeParameter(i, false)).ToList());

    public override string ToString() => DocId;
}
