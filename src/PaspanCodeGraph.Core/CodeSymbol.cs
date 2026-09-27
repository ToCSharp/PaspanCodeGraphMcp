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
}

public static class SymbolKinds
{
    public static bool IsType(this SymbolKind kind) => kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface
        or SymbolKind.Enum or SymbolKind.Record or SymbolKind.RecordStruct or SymbolKind.Delegate;

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
    /// Documentation-comment id (<c>T:Ns.Type`1</c>, <c>M:Ns.Type.Method(System.Int32)</c>). Parameter types
    /// that are not predefined types or type parameters are written as in source, not fully qualified.
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

    /// <summary>The containing type, if any.</summary>
    public CodeSymbol? ContainingType => Container is { Kind: var kind } && (kind.IsType() || kind == SymbolKind.Extension) ? Container : null;

    /// <summary>The dotted name from the global namespace: <c>Ns.Outer.Inner</c>.</summary>
    public string QualifiedName => Container is null ? Name : $"{Container.QualifiedName}.{Name}";

    public override string ToString() => Id;
}
