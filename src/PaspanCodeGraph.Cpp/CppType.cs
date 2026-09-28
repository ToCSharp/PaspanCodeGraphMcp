namespace PaspanCodeGraph.Cpp;

/// <summary>
/// A C++ type as far as binding needs it: the workspace type it names (a class, struct, union, enum or alias),
/// or the name it is written with (<c>std::vector</c>), its template arguments and how many pointers
/// (or arrays) are around it. References and cv-qualifiers are dropped: they do not change what a member access
/// finds. A type parameter of the class template around a member is <see cref="TemplateParameter"/>.
/// </summary>
public sealed record CppType(CodeSymbol? Symbol, string Name, IReadOnlyList<CppType> Arguments, int Pointers = 0, int TemplateParameter = -1)
{
    public static CppType Unknown { get; } = new(null, "", []);

    public static CppType Named(string name) => new(null, name, []);

    public static CppType Of(CodeSymbol symbol, IReadOnlyList<CppType>? arguments = null) => new(symbol, symbol.Id[2..], arguments ?? []);

    /// <summary>The type was deduced (<c>auto</c>) or guessed (a container of the standard library), not declared.</summary>
    public bool IsInferred { get; init; }

    /// <summary>The value is a temporary or moved (<c>std::move(x)</c>, <c>T(...)</c>): it binds to <c>T&amp;&amp;</c>, not to <c>T&amp;</c>.</summary>
    public bool IsRvalue { get; init; }

    /// <summary>An argument that is a pack expansion (<c>args...</c>), which stands for any number of arguments.</summary>
    public bool IsPack { get; init; }

    public bool IsUnknown => Symbol == null && Name.Length == 0 && TemplateParameter < 0;

    public CppType Pointer() => this with { Pointers = Pointers + 1 };

    /// <summary>The type <c>*p</c> or <c>p[i]</c> has; unknown for a type that is not a pointer.</summary>
    public CppType Deref() => Pointers > 0 ? this with { Pointers = Pointers - 1 } : Unknown;

    /// <summary>The written name without a leading <c>::</c> or <c>std::</c>: <c>unique_ptr</c>.</summary>
    public string StandardName => Name.StartsWith("::", StringComparison.Ordinal) ? Name[2..] is var n && n.StartsWith("std::", StringComparison.Ordinal) ? n[5..] : n
        : Name.StartsWith("std::", StringComparison.Ordinal) ? Name[5..] : Name;

    /// <summary>Replaces the type parameters of a class template by the arguments of <paramref name="receiver"/>.</summary>
    public CppType Substitute(IReadOnlyList<CppType> arguments)
    {
        if (TemplateParameter >= 0)
        {
            return TemplateParameter < arguments.Count ? arguments[TemplateParameter] with { Pointers = arguments[TemplateParameter].Pointers + Pointers } : Unknown;
        }

        if (Arguments.Count == 0 || arguments.Count == 0)
        {
            return this;
        }

        return this with { Arguments = Arguments.Select(a => a.Substitute(arguments)).ToList() };
    }

    public override string ToString() => (Symbol?.Id[2..] ?? (TemplateParameter >= 0 ? "`" + TemplateParameter : Name))
        + (Arguments.Count > 0 ? "<" + string.Join(",", Arguments) + ">" : "") + new string('*', Pointers);
}

/// <summary>A parameter of a function as binding sees it.</summary>
/// <param name="Reference">0 for a parameter by value, 1 for an lvalue reference, 2 for an rvalue reference.</param>
/// <param name="IsConst">The parameter is const (a <c>const T&amp;</c> binds to rvalues too).</param>
public sealed record CppParameter(string Name, CppType Type, bool HasDefault, bool IsPack, int Reference = 0, bool IsConst = false);

/// <summary>What binding keeps of a declared symbol besides the <see cref="CodeSymbol"/>.</summary>
public sealed class CppSymbolInfo
{
    /// <summary>The names of the template parameters of a class template, in order.</summary>
    public List<string> TemplateParameters { get; } = [];

    /// <summary>A function template: overload resolution accepts arguments of any type for its parameters.</summary>
    public int FunctionTemplateArity { get; set; }

    /// <summary>The template arguments of an explicit specialization of a function template: <c>&lt;int&gt;</c>.</summary>
    public string? SpecializationKey { get; set; }

    /// <summary>The type of a field or variable, the return type of a function, the type an alias names.</summary>
    public CppType Type { get; set; } = CppType.Unknown;

    public List<CppParameter> Parameters { get; } = [];

    public bool IsVariadic { get; set; }

    public bool IsVirtual { get; set; }

    public bool IsStatic { get; set; }

    /// <summary>A function returns a reference: its calls are lvalues (xvalues for <c>&amp;&amp;</c>).</summary>
    public int ReturnsReference { get; set; }

    /// <summary>The part of the id after the name: <c>(int)const</c>, which an override repeats.</summary>
    public string SignatureKey { get; set; } = "";

    /// <summary>The bases of a class, resolved.</summary>
    public List<(CodeSymbol? Symbol, CppType Type)> Bases { get; } = [];

    /// <summary>Set when a definition (with a body or members) gave the symbol its signature.</summary>
    public bool SignatureFromDefinition { get; set; }

    public int RequiredParameters => Parameters.Count(p => !p.HasDefault && !p.IsPack);

    public bool Accepts(int arguments) =>
        arguments >= RequiredParameters && (arguments <= Parameters.Count || IsVariadic || Parameters.Any(p => p.IsPack));
}
