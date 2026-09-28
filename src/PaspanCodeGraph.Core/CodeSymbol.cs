namespace PaspanCodeGraph;

/// <summary>What a declaration declares.</summary>
public enum SymbolKind
{
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    Record,
    RecordStruct,
    Delegate,
    Extension,
    Method,
    Constructor,
    Destructor,
    Property,
    Indexer,
    Field,
    Event,
    Operator,
    EnumMember,

    // C++
    Union,

    /// <summary>A function outside classes (a free function of a namespace).</summary>
    Function,

    /// <summary>A variable of a namespace.</summary>
    Variable,

    /// <summary>A <c>typedef</c> or an alias declaration (<c>using Name = Type;</c>).</summary>
    TypeAlias,

    Concept,

    /// <summary>A macro defined with <c>#define</c>.</summary>
    Macro,
}

/// <summary>The language a symbol is declared in.</summary>
public enum SourceLanguage
{
    CSharp,
    Cpp,
}

public static class SymbolKinds
{
    public static bool IsType(this SymbolKind kind) => kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface
        or SymbolKind.Enum or SymbolKind.Record or SymbolKind.RecordStruct or SymbolKind.Delegate or SymbolKind.Union or SymbolKind.TypeAlias;

    /// <summary>Kinds that code calls: methods, constructors, functions, operators and the like.</summary>
    public static bool IsCallable(this SymbolKind kind) => kind is SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Destructor
        or SymbolKind.Operator or SymbolKind.Function or SymbolKind.Macro;

    public static bool IsMember(this SymbolKind kind) => !kind.IsType() && kind is not (SymbolKind.Namespace or SymbolKind.Extension);

    /// <summary>Parses a kind name case-insensitively; null when it is not a kind.</summary>
    public static SymbolKind? Parse(string? name) =>
        Enum.TryParse<SymbolKind>(name, ignoreCase: true, out var kind) && !int.TryParse(name, out _) ? kind : null;
}

/// <summary>
/// A declaration in source: where it is and, for the parts of the file around it, its byte span.
/// </summary>
/// <param name="File">Full path of the file.</param>
/// <param name="Start">Byte offset of the declaration in the file's UTF-8 source (without byte order mark).</param>
/// <param name="End">Byte offset after the declaration.</param>
/// <param name="Line">1-based line of the declared name.</param>
/// <param name="Column">1-based column of the declared name, in UTF-16 code units.</param>
public sealed record SourceLocation(string File, int Start, int End, int Line, int Column);

/// <summary>How sure a relation found without a compiler is.</summary>
public enum Confidence
{
    /// <summary>Bound by name lookup as the compiler does it.</summary>
    Exact,

    /// <summary>
    /// Most likely right: the receiver's type or the overload was inferred (a lambda parameter from a LINQ
    /// operator, arguments of unknown types), or a same-named local could hide a type.
    /// </summary>
    Inferred,

    /// <summary>
    /// Only the name matches: the receiver's type is unknown, and this is one of the workspace members with that
    /// name and a matching number of parameters.
    /// </summary>
    NameOnly,
}

/// <summary>A place in source that refers to a symbol.</summary>
/// <param name="Start">Byte offset of the name in the file's UTF-8 source.</param>
/// <param name="Line">1-based line of the name.</param>
/// <param name="Column">1-based column of the name, in UTF-16 code units.</param>
/// <param name="InMember">Id of the declaration the reference is in (a member, or a type for its base list and attributes).</param>
public sealed record SymbolReference(string File, int Start, int Line, int Column, string? InMember, Confidence Confidence);

/// <summary>Prefixes of reference targets that are not workspace symbols.</summary>
public static class ReferenceTargets
{
    /// <summary>A member of a type from a referenced assembly, by the id it would have: <c>external:M:System.String.Trim</c>.</summary>
    public const string External = "external:";

    /// <summary>A call that could not be bound, by its name: <c>unresolved:Name</c>.</summary>
    public const string Unresolved = "unresolved:";
}

/// <summary>A base type or interface of a type as written, and the workspace type it names when it was found.</summary>
/// <param name="Id">Id form of the type (<c>Ns.Base{System.Int32}</c>), written as in source when it was not found.</param>
/// <param name="TypeArguments">Id forms of the type arguments of the last part, for mapping type parameters.</param>
public sealed record TypeLink(string Written, CodeSymbol? Symbol, string Id, IReadOnlyList<string> TypeArguments);

/// <summary>A parameter as written in source.</summary>
public sealed record ParameterInfo(string Name, string? Type, string? Modifier, string? DefaultValue);

/// <summary>
/// A declared symbol: a type, member, namespace or enum member. Partial declarations of one symbol are merged
/// into one <see cref="CodeSymbol"/> with several <see cref="Declarations"/>.
/// </summary>
public sealed class CodeSymbol
{
    public CodeSymbol(string id, SymbolKind kind, string name)
    {
        Id = id;
        Kind = kind;
        Name = name;
    }

    /// <summary>
    /// Documentation-comment id (<c>T:Ns.Type`1</c>, <c>M:Ns.Type.Method(System.Int32)</c>). Parameter types are
    /// fully qualified when they are workspace types or types of the referenced assemblies, else written as in source.
    /// </summary>
    public string Id { get; }

    public SymbolKind Kind { get; }

    public string Name { get; }

    /// <summary>A readable declaration: <c>int Parser.Parse(string text, int start = 0)</c>.</summary>
    public string Signature { get; set; } = "";

    /// <summary>The containing type or namespace; null for the global namespace.</summary>
    public CodeSymbol? Container { get; set; }

    /// <summary>The containing namespace, dotted; null for the global namespace.</summary>
    public string? Namespace { get; set; }

    /// <summary>The project that declares the symbol (the first one for files linked into several projects).</summary>
    public string? Project { get; set; }

    /// <summary>For a type or member of a referenced assembly, the assembly's name; null for a workspace symbol.</summary>
    public string? Assembly { get; set; }

    public bool IsExternal => Assembly != null;

    /// <summary>The language of the declaration.</summary>
    public SourceLanguage Language { get; set; }

    public string Accessibility { get; set; } = "";

    public List<string> Modifiers { get; } = [];

    public List<SourceLocation> Declarations { get; } = [];

    public List<CodeSymbol> Members { get; } = [];

    /// <summary>Base class and interfaces as written.</summary>
    public List<string> BaseTypes { get; } = [];

    public List<string> TypeParameters { get; } = [];

    public List<ParameterInfo> Parameters { get; } = [];

    /// <summary>Return type of methods, delegates and operators; type of properties, indexers, fields and events.</summary>
    public string? Type { get; set; }

    /// <summary>The documentation comment XML, without the comment markers.</summary>
    public string? Documentation { get; set; }

    /// <summary>The base class and interfaces, resolved where they are workspace types.</summary>
    public List<TypeLink> Bases { get; } = [];

    /// <summary>The types that list this one among their <see cref="Bases"/>.</summary>
    public List<CodeSymbol> DerivedTypes { get; } = [];

    /// <summary>The interface of an explicit interface implementation, when it was found.</summary>
    public TypeLink? ExplicitInterface { get; set; }

    /// <summary>The member of a base class this one overrides.</summary>
    public CodeSymbol? Overrides { get; set; }

    public List<CodeSymbol> OverriddenBy { get; } = [];

    /// <summary>The interface members this member implements, in this type or in types derived from it.</summary>
    public List<CodeSymbol> Implements { get; } = [];

    public List<CodeSymbol> ImplementedBy { get; } = [];

    /// <summary>The containing type, if any.</summary>
    public CodeSymbol? ContainingType => Container is { Kind: var kind } && (kind.IsType() || kind == SymbolKind.Extension) ? Container : null;

    /// <summary>The dotted name from the global namespace: <c>Ns.Outer.Inner</c>.</summary>
    public string QualifiedName => Container is null ? Name : $"{Container.QualifiedName}.{Name}";

    public override string ToString() => Id;
}
