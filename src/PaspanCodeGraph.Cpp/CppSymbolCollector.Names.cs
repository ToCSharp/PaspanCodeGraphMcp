using System.Text;
using PaspanParsers.Cpp;

namespace PaspanCodeGraph.Cpp;

/// <summary>What an unqualified name finds: symbols, a local variable or parameter, or a template parameter.</summary>
internal readonly record struct LookupResult(IReadOnlyList<CodeSymbol> Symbols, CppType? Local = null, string? TemplateParameter = null)
{
    public static LookupResult None { get; } = new([]);

    public bool Found => Symbols.Count > 0 || Local != null || TemplateParameter != null;
}

// Name lookup and the canonical spelling of types in ids
public sealed partial class CppSymbolCollector
{
    private static readonly HashSet<string> FundamentalKeywords = new(StringComparer.Ordinal)
    {
        "void", "bool", "char", "char8_t", "char16_t", "char32_t", "wchar_t", "short", "int", "long", "signed", "unsigned",
        "float", "double", "auto", "__int128", "__int64", "__int32", "__int16", "__int8", "_Float16", "__bf16", "__float128",
        "_Bool", "__signed", "__signed__", "__unsigned", "__int128_t", "__uint128_t", "_Complex", "__complex__",
    };

    /// <summary>Keywords that do not change a type: storage classes and function specifiers.</summary>
    private static readonly HashSet<string> ModifierKeywords = new(StringComparer.Ordinal)
    {
        "static", "extern", "inline", "virtual", "explicit", "constexpr", "consteval", "constinit", "friend", "typedef",
        "mutable", "thread_local", "register", "__inline", "__inline__", "__forceinline", "_Thread_local", "__thread",
        "__extension__",
    };

    // ========================================
    // Lookup
    // ========================================

    /// <summary>Looks an unqualified name up from <paramref name="scope"/> outward, as C++ does.</summary>
    internal LookupResult Lookup(string name, CppScope scope, bool typesOnly = false)
    {
        for (var s = scope; s != null; s = s.Parent)
        {
            if (!typesOnly && s.Locals != null && s.Locals.TryGetValue(name, out var local))
            {
                return new LookupResult([], local);
            }

            if (s.TemplateParameters != null && s.TemplateParameters.TryGetValue(name, out var parameter))
            {
                return new LookupResult([], TemplateParameter: parameter);
            }

            // Using-directives of a block (those of namespaces are looked in below, with the namespace)
            if (s.Kind is CppScopeKind.Block or CppScopeKind.Class && s.Usings != null)
            {
                var used = new List<CodeSymbol>();
                foreach (var ns in s.Usings)
                {
                    AddNew(used, Filter(_binder.DeclaredIn(ns, name), typesOnly));
                }

                if (used.Count > 0)
                {
                    return new LookupResult(used);
                }
            }

            if (s.Aliases != null && s.Aliases.TryGetValue(name, out var aliased))
            {
                var filtered = Filter(aliased, typesOnly);
                if (filtered.Count > 0)
                {
                    return new LookupResult(filtered);
                }
            }

            switch (s.Kind)
            {
                case CppScopeKind.Class when s.Symbol != null:
                {
                    var found = Filter(_binder.LookupInClass(s.Symbol, name), typesOnly);
                    if (found.Count > 0)
                    {
                        return new LookupResult(found);
                    }

                    // The injected class name
                    if (s.Symbol.Name == name)
                    {
                        return new LookupResult([s.Symbol]);
                    }

                    break;
                }

                case CppScopeKind.Namespace or CppScopeKind.Global:
                {
                    var found = new List<CodeSymbol>(Filter(_binder.DeclaredIn(s.Kind == CppScopeKind.Global ? null : s.Symbol, name), typesOnly));
                    foreach (var ns in s.Usings ?? [])
                    {
                        AddNew(found, Filter(_binder.DeclaredIn(ns, name), typesOnly));
                    }

                    if (s.Kind == CppScopeKind.Global)
                    {
                        foreach (var ns in _binder.FileUsings(_source.Path))
                        {
                            AddNew(found, Filter(_binder.DeclaredIn(ns, name), typesOnly));
                        }
                    }

                    if (found.Count > 0)
                    {
                        return new LookupResult(found);
                    }

                    break;
                }
            }
        }

        return LookupResult.None;
    }

    private static void AddNew(List<CodeSymbol> list, IEnumerable<CodeSymbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (!list.Contains(symbol))
            {
                list.Add(symbol);
            }
        }
    }

    private static IReadOnlyList<CodeSymbol> Filter(IReadOnlyList<CodeSymbol> symbols, bool typesOnly) =>
        typesOnly ? symbols.Where(s => s.Kind.IsType() || s.Kind is SymbolKind.Namespace or SymbolKind.Concept).ToList() : symbols;

    /// <summary>What a qualified name's qualifier names: a namespace or class; <c>Global</c> for <c>::name</c>.</summary>
    internal readonly record struct Qualifier(bool Known, CodeSymbol? Symbol, bool IsGlobal);

    /// <summary>The namespace or class a nested name specifier names; unknown when it is not a workspace one.</summary>
    internal Qualifier ResolveQualifier(Name? qualifier, CppScope scope)
    {
        switch (qualifier)
        {
            case null:
                return new Qualifier(true, null, true);
            case IdentifierName identifier:
            {
                var found = Lookup(identifier.Identifier, scope, typesOnly: true);
                return ScopeSymbol(found.Symbols) is { } symbol ? new Qualifier(true, symbol, false) : default;
            }

            case TemplateIdName templateId:
            {
                var template = ResolveQualifier(templateId.Template, scope);
                return template.Symbol is { } primary ? new Qualifier(true, Specialization(primary, templateId, scope) ?? primary, false) : default;
            }

            case QualifiedName qualified:
            {
                var outer = ResolveQualifier(qualified.Qualifier, scope);
                if (!outer.Known)
                {
                    return default;
                }

                var name = LastName(qualified.Name);
                var found = ScopeSymbol(LookupQualified(outer.Symbol, name).Where(s => s.Kind.IsType() || s.Kind == SymbolKind.Namespace).ToList());
                if (found != null && qualified.Name is TemplateIdName inner)
                {
                    found = Specialization(found, inner, scope) ?? found;
                }

                return found != null ? new Qualifier(true, found, false) : default;
            }

            default:
                return default;
        }
    }

    /// <summary>The namespace or class among symbols (an alias of a class stands for the class).</summary>
    private CodeSymbol? ScopeSymbol(IReadOnlyList<CodeSymbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind == SymbolKind.Namespace)
            {
                return symbol;
            }

            if (symbol.Kind.IsType() && _binder.ClassOf(symbol) is { } type)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>The explicit or partial specialization <c>Box&lt;int&gt;</c> of a class template, when the workspace declares it.</summary>
    private CodeSymbol? Specialization(CodeSymbol primary, TemplateIdName templateId, CppScope scope)
    {
        if (!primary.Kind.IsType())
        {
            return null;
        }

        var id = primary.Id + "<" + string.Join(",", templateId.Arguments.Select(a => TemplateArgumentKey(a, scope))) + ">";
        return _builder.Get(id);
    }

    /// <summary>The members named <paramref name="name"/> of a namespace (null: the global one) or class.</summary>
    internal IReadOnlyList<CodeSymbol> LookupQualified(CodeSymbol? container, string name) =>
        container is { Kind: not SymbolKind.Namespace } type ? _binder.LookupInClass(type, name) : _binder.DeclaredIn(container, name);

    /// <summary>What a name, qualified or not, finds.</summary>
    internal LookupResult ResolveName(Name name, CppScope scope, bool typesOnly = false)
    {
        switch (name)
        {
            case IdentifierName identifier:
                return Lookup(identifier.Identifier, scope, typesOnly);
            case TemplateIdName templateId:
            {
                var found = ResolveName(templateId.Template, scope, typesOnly);
                if (found.Symbols.Count == 1 && found.Symbols[0].Kind.IsType() && Specialization(found.Symbols[0], templateId, scope) is { } specialization)
                {
                    return new LookupResult([specialization]);
                }

                return found;
            }

            case QualifiedName qualified:
            {
                var qualifier = ResolveQualifier(qualified.Qualifier, scope);
                if (!qualifier.Known)
                {
                    return LookupResult.None;
                }

                var found = Filter(LookupQualified(qualifier.Symbol, LastName(qualified.Name)), typesOnly);
                if (qualified.Name is TemplateIdName inner && found.Count == 1 && found[0].Kind.IsType() && Specialization(found[0], inner, scope) is { } specialization)
                {
                    return new LookupResult([specialization]);
                }

                // Class::Class names the constructors
                if (qualifier.Symbol is { Kind: not SymbolKind.Namespace } type && LastName(qualified.Name) == type.Name && !typesOnly)
                {
                    var constructors = _binder.DeclaredIn(type, ConstructorKey);
                    if (constructors.Count > 0)
                    {
                        return new LookupResult(constructors);
                    }
                }

                return new LookupResult(found);
            }

            case OperatorFunctionName or ConversionFunctionName or LiteralOperatorName or DestructorName:
                return Lookup(LastName(name), scope, typesOnly);
            default:
                return LookupResult.None;
        }
    }

    /// <summary>The key constructors are declared under in their class.</summary>
    internal const string ConstructorKey = "#ctor";

    /// <summary>The unqualified name as declarations spell it: <c>f</c>, <c>operator+</c>, <c>~T</c>, <c>operator bool</c>.</summary>
    internal string LastName(Name name) => name switch
    {
        IdentifierName identifier => identifier.Identifier,
        TemplateIdName templateId => LastName(templateId.Template),
        QualifiedName qualified => LastName(qualified.Name),
        OperatorFunctionName op => "operator" + op.Operator,
        DestructorName destructor => "~" + LastName(destructor.Type),
        ConversionFunctionName conversion => "operator " + Text(conversion.Type),
        LiteralOperatorName literal => "operator\"\"" + literal.Suffix,
        _ => name.ToString() ?? "",
    };

    /// <summary>The name node that holds the position of the last identifier of a name.</summary>
    internal static CppNode NameNode(Name name) => name switch
    {
        QualifiedName qualified => NameNode(qualified.Name),
        TemplateIdName templateId => NameNode(templateId.Template),
        _ => name,
    };

    // ========================================
    // Types in ids
    // ========================================

    /// <summary>
    /// The canonical spelling of a type in an id: cv-qualifiers first, fundamental types in one order, workspace types
    /// with their full names, template parameters by position, and the declarator's pointers, references, arrays and
    /// function types after it: <c>const ns::Widget&amp;</c>, <c>unsigned long</c>, <c>`0*</c>. A parameter drops its
    /// top-level const, and its array and function types become pointers.
    /// </summary>
    internal string TypeKey(DeclSpecifierSequence? specifiers, Declarator? declarator, CppScope scope, bool parameter = false, bool canonical = true, Declarator? stopAt = null)
    {
        var (baseType, isConst, isVolatile) = BaseTypeKey(specifiers, scope, canonical);
        var operations = new List<string>();
        CollectOperations(declarator, scope, operations, canonical, stopAt);
        if (parameter && operations.Count > 0)
        {
            var last = operations[^1];
            if (last.StartsWith('['))
            {
                operations[^1] = "*";
            }
            else if (last.StartsWith('('))
            {
                operations.Add("*");
            }
            else if (last.StartsWith('*'))
            {
                operations[^1] = "*";
            }
        }

        var cv = parameter && operations.Count == 0 ? "" : (isConst ? "const " : "") + (isVolatile ? "volatile " : "");
        return cv + baseType + string.Concat(operations);
    }

    private (string Type, bool IsConst, bool IsVolatile) BaseTypeKey(DeclSpecifierSequence? specifiers, CppScope scope, bool canonical)
    {
        var isConst = false;
        var isVolatile = false;
        var keywords = new List<string>();
        string? named = null;
        foreach (var specifier in specifiers?.Specifiers ?? [])
        {
            switch (specifier)
            {
                case KeywordSpecifier { Keyword: "const" or "__const" or "__const__" }:
                    isConst = true;
                    break;
                case KeywordSpecifier { Keyword: "volatile" or "__volatile" or "__volatile__" }:
                    isVolatile = true;
                    break;
                case KeywordSpecifier keyword when FundamentalKeywords.Contains(keyword.Keyword):
                    keywords.Add(keyword.Keyword);
                    break;
                case KeywordSpecifier:
                    break;
                case NamedTypeSpecifier namedType:
                    named = NameKey(namedType.Name, scope, canonical);
                    break;
                case ElaboratedTypeSpecifier elaborated:
                    named = NameKey(elaborated.Name, scope, canonical);
                    break;
                case ClassSpecifier { Name: { } className }:
                    named = NameKey(className, scope, canonical);
                    break;
                case EnumSpecifier { Name: { } enumName }:
                    named = NameKey(enumName, scope, canonical);
                    break;
                case DecltypeSpecifier decltype:
                    named = "decltype(" + (decltype.Expression is { } e ? Text(e) : "auto") + ")";
                    break;
                case PlaceholderTypeSpecifier placeholder:
                    named = (canonical ? "" : NameKey(placeholder.Concept, scope, false) + " ") + (placeholder.IsDecltypeAuto ? "decltype(auto)" : "auto");
                    break;
                case BitIntSpecifier bitInt:
                    named = "_BitInt(" + Text(bitInt.Width) + ")";
                    break;
            }
        }

        return (named ?? Fundamental(keywords), isConst, isVolatile);
    }

    /// <summary>One spelling of each fundamental type: <c>unsigned long long</c>, <c>long double</c>, <c>int</c>.</summary>
    internal static string Fundamental(List<string> keywords)
    {
        if (keywords.Count == 0)
        {
            return "int";
        }

        var unsigned = keywords.Contains("unsigned") || keywords.Contains("__unsigned");
        var signed = keywords.Contains("signed") || keywords.Contains("__signed") || keywords.Contains("__signed__");
        var longs = keywords.Count(k => k == "long");
        var rest = keywords.Where(k => k is not ("unsigned" or "__unsigned" or "signed" or "__signed" or "__signed__" or "long" or "int" or "short")).ToList();
        if (keywords.Contains("short"))
        {
            return unsigned ? "unsigned short" : "short";
        }

        if (rest.Contains("char"))
        {
            return unsigned ? "unsigned char" : signed ? "signed char" : "char";
        }

        if (rest.Contains("double"))
        {
            return longs > 0 ? "long double" : "double";
        }

        if (rest.Count > 0)
        {
            return (unsigned ? "unsigned " : "") + string.Join(" ", rest);
        }

        var core = longs >= 2 ? "long long" : longs == 1 ? "long" : "int";
        return unsigned ? "unsigned " + core : core;
    }

    private void CollectOperations(Declarator? declarator, CppScope scope, List<string> operations, bool canonical, Declarator? stopAt = null)
    {
        while (declarator != null && declarator != stopAt)
        {
            switch (declarator)
            {
                case PointerDeclarator pointer:
                    operations.Add("*" + string.Concat(pointer.Qualifiers.Where(q => q is "const" or "volatile")));
                    declarator = pointer.Inner;
                    break;
                case ReferenceDeclarator reference:
                    operations.Add(reference.IsRvalue ? "&&" : "&");
                    declarator = reference.Inner;
                    break;
                case MemberPointerDeclarator memberPointer:
                    operations.Add(" " + NameKey(memberPointer.Class, scope, canonical) + "::*");
                    declarator = memberPointer.Inner;
                    break;
                case ArrayDeclarator array:
                    operations.Add("[" + (array.Size is { } size ? Text(size) : "") + "]");
                    declarator = array.Inner;
                    break;
                case FunctionDeclarator function:
                    operations.Add(ParameterListKey(function, scope, canonical));
                    declarator = function.Inner;
                    break;
                case ParenthesizedDeclarator parenthesized:
                    declarator = parenthesized.Inner;
                    break;
                case PackDeclarator pack:
                    operations.Add("...");
                    declarator = pack.Inner;
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>The parameter types of a function declarator with its qualifiers: <c>(int,const char*)const</c>.</summary>
    internal string ParameterListKey(FunctionDeclarator function, CppScope scope, bool canonical = true)
    {
        var parameters = function.Parameters;
        var types = new List<string>();
        if (!(parameters.Count == 1 && IsVoidParameter(parameters[0])))
        {
            foreach (var parameter in parameters)
            {
                types.Add(TypeKey(parameter.Specifiers, parameter.Declarator, scope, parameter: true, canonical));
            }
        }

        if (function.IsVariadic)
        {
            types.Add("...");
        }

        var builder = new StringBuilder("(").AppendJoin(",", types).Append(')');
        foreach (var qualifier in function.Qualifiers)
        {
            builder.Append(qualifier);
        }

        builder.Append(function.RefQualifier);
        return builder.ToString();
    }

    internal static bool IsVoidParameter(ParameterDeclaration parameter) =>
        parameter.Declarator == null && parameter.Specifiers?.Specifiers is [KeywordSpecifier { Keyword: "void" }];

    /// <summary>A type name in an id: a workspace type by its full name, a template parameter by position, else as written.</summary>
    internal string NameKey(Name name, CppScope scope, bool canonical = true)
    {
        if (canonical)
        {
            var found = ResolveName(name, scope, typesOnly: true);
            if (found.TemplateParameter is { } parameter)
            {
                return parameter;
            }

            if (found.Symbols.Count > 0 && found.Symbols[0] is { Kind: not SymbolKind.Namespace } type)
            {
                var body = type.Id[2..];

                // The template arguments of a specialization are in its id already
                return name is TemplateIdName templateId && !body.EndsWith('>') ? body + TemplateArgumentsKey(templateId, scope, canonical)
                    : name is QualifiedName { Name: TemplateIdName inner } && !body.EndsWith('>') ? body + TemplateArgumentsKey(inner, scope, canonical)
                    : body;
            }
        }

        return WrittenKey(name, scope, canonical);
    }

    private string WrittenKey(Name name, CppScope scope, bool canonical) => name switch
    {
        IdentifierName identifier => identifier.Identifier,
        TemplateIdName templateId => WrittenKey(templateId.Template, scope, canonical) + TemplateArgumentsKey(templateId, scope, canonical),
        QualifiedName qualified => (qualified.Qualifier is { } q ? WrittenKey(q, scope, canonical) : "") + "::" + WrittenKey(qualified.Name, scope, canonical),
        DecltypeName decltype => "decltype(" + Text(decltype.Expression) + ")",
        _ => LastName(name),
    };

    private string TemplateArgumentsKey(TemplateIdName templateId, CppScope scope, bool canonical) =>
        "<" + string.Join(",", templateId.Arguments.Select(a => TemplateArgumentKey(a, scope, canonical))) + ">";

    internal string TemplateArgumentKey(CppNode argument, CppScope scope, bool canonical = true) => argument switch
    {
        TypeId typeId => TypeKey(typeId.Specifiers, typeId.Declarator, scope, canonical: canonical) + (typeId.IsPackExpansion ? "..." : ""),
        NameExpression { Name: IdentifierName identifier } when canonical && Lookup(identifier.Identifier, scope).TemplateParameter is { } parameter => parameter,
        Expression expression => Text(expression),
        _ => "",
    };

    // ========================================
    // Types for binding
    // ========================================

    /// <summary>The type a declaration declares, for binding member accesses on it.</summary>
    internal CppType BindType(DeclSpecifierSequence? specifiers, Declarator? declarator, CppScope scope, Declarator? stopAt = null)
    {
        var type = BindBaseType(specifiers, scope);
        for (var d = declarator; d != null && d != stopAt;)
        {
            switch (d)
            {
                case PointerDeclarator pointer:
                    type = type.Pointer();
                    d = pointer.Inner;
                    break;
                case ArrayDeclarator array:
                    type = type.Pointer();
                    d = array.Inner;
                    break;
                case ReferenceDeclarator reference:
                    d = reference.Inner;
                    break;
                case ParenthesizedDeclarator parenthesized:
                    d = parenthesized.Inner;
                    break;
                case PackDeclarator pack:
                    d = pack.Inner;
                    break;
                case FunctionDeclarator or MemberPointerDeclarator:
                    return CppType.Unknown;
                default:
                    d = null;
                    break;
            }
        }

        return type;
    }

    internal CppType BindBaseType(DeclSpecifierSequence? specifiers, CppScope scope)
    {
        var keywords = new List<string>();
        foreach (var specifier in specifiers?.Specifiers ?? [])
        {
            switch (specifier)
            {
                case NamedTypeSpecifier named:
                    return BindTypeName(named.Name, scope);
                case ElaboratedTypeSpecifier elaborated:
                    return BindTypeName(elaborated.Name, scope);
                case ClassSpecifier { Name: { } name }:
                    return BindTypeName(name, scope);
                case EnumSpecifier { Name: { } name }:
                    return BindTypeName(name, scope);
                case DecltypeSpecifier { Expression: { } expression }:
                    return TypeOfDecltype(expression, scope);
                case PlaceholderTypeSpecifier:
                    return CppType.Named("auto");
                case KeywordSpecifier keyword when FundamentalKeywords.Contains(keyword.Keyword):
                    keywords.Add(keyword.Keyword);
                    break;
            }
        }

        return keywords.Count == 0 ? CppType.Unknown : CppType.Named(Fundamental(keywords));
    }

    /// <summary>The type of <c>decltype(expression)</c>; the References pass knows expression types.</summary>
    private partial CppType TypeOfDecltype(Expression expression, CppScope scope);

    internal CppType BindTypeName(Name name, CppScope scope)
    {
        var arguments = name switch
        {
            TemplateIdName t => t.Arguments,
            QualifiedName { Name: TemplateIdName t } => t.Arguments,
            _ => [],
        };
        var boundArguments = arguments.Select(a => a is TypeId typeId ? BindType(typeId.Specifiers, typeId.Declarator, scope) : CppType.Unknown).ToList();
        var found = ResolveName(name, scope, typesOnly: true);
        if (found.TemplateParameter is { } parameter)
        {
            return parameter.StartsWith("``", StringComparison.Ordinal) || !int.TryParse(parameter.AsSpan(1), out var index)
                ? CppType.Unknown
                : new CppType(null, parameter, [], TemplateParameter: index);
        }

        if (found.Symbols.Count > 0 && found.Symbols[0] is { Kind: not SymbolKind.Namespace } type)
        {
            return CppType.Of(type, boundArguments);
        }

        return new CppType(null, WrittenBase(name), boundArguments);
    }

    /// <summary>A name as written without its template arguments: <c>std::vector</c>.</summary>
    private string WrittenBase(Name name) => name switch
    {
        IdentifierName identifier => identifier.Identifier,
        TemplateIdName templateId => WrittenBase(templateId.Template),
        QualifiedName qualified => (qualified.Qualifier is { } q ? WrittenBase(q) : "") + "::" + WrittenBase(qualified.Name),
        _ => LastName(name),
    };

    /// <summary>Follows aliases to the class or enum a type names.</summary>
    internal CppType Resolve(CppType type)
    {
        for (var i = 0; i < 8 && type.Symbol is { Kind: SymbolKind.TypeAlias } alias; i++)
        {
            var target = _binder.FindInfo(alias)?.Type ?? CppType.Unknown;
            if (target.IsUnknown)
            {
                return type;
            }

            type = target with { Pointers = target.Pointers + type.Pointers };
        }

        return type;
    }

    // ========================================
    // Text
    // ========================================

    internal string Text(CppNode node) => Collapse(node.Span.GetText(Utf8));

    /// <summary>Source text with runs of white space as single spaces.</summary>
    internal static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
