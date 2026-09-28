namespace PaspanCodeGraph.Cpp;

internal enum CppScopeKind
{
    Global,
    Namespace,
    Class,
    Template,
    Block,
}

/// <summary>
/// A lexical scope of a C++ file: the global scope, a namespace or class body, template parameters, or a block
/// of a function with its locals. Out-of-class member definitions see the class and its namespaces through
/// scopes built for them (<see cref="CppSymbolCollector"/>).
/// </summary>
internal sealed class CppScope(CppScopeKind kind, CppScope? parent, CodeSymbol? symbol = null)
{
    public CppScopeKind Kind { get; } = kind;

    public CppScope? Parent { get; } = parent;

    /// <summary>The namespace or class of a namespace or class scope; null for the global scope.</summary>
    public CodeSymbol? Symbol { get; } = symbol;

    /// <summary>The namespaces of the using-directives in this scope.</summary>
    public List<CodeSymbol>? Usings { get; set; }

    /// <summary>Names brought in by namespace aliases and using-declarations.</summary>
    public Dictionary<string, List<CodeSymbol>>? Aliases { get; set; }

    /// <summary>Template parameter names and their id forms (<c>`0</c> for a class template, <c>``0</c> for a function template).</summary>
    public Dictionary<string, string>? TemplateParameters { get; set; }

    /// <summary>Local variables and parameters with their types.</summary>
    public Dictionary<string, CppType>? Locals { get; set; }

    /// <summary>The current access of a class body.</summary>
    public string Access { get; set; } = "public";

    /// <summary>The type of <c>this</c> in a member function body.</summary>
    public CppType? This { get; set; }

    /// <summary>The namespace or class declarations go into: null for the global namespace.</summary>
    public CodeSymbol? Container
    {
        get
        {
            for (var s = this; s != null; s = s.Parent)
            {
                if (s.Kind is CppScopeKind.Namespace or CppScopeKind.Class)
                {
                    return s.Symbol;
                }

                if (s.Kind == CppScopeKind.Global)
                {
                    return null;
                }
            }

            return null;
        }
    }

    /// <summary>The innermost enclosing class, or null.</summary>
    public CodeSymbol? Class
    {
        get
        {
            for (var s = this; s != null; s = s.Parent)
            {
                if (s.Kind == CppScopeKind.Class)
                {
                    return s.Symbol;
                }

                if (s.Kind is CppScopeKind.Namespace or CppScopeKind.Global)
                {
                    return null;
                }
            }

            return null;
        }
    }

    /// <summary>The innermost enclosing namespace (null for the global one), skipping classes.</summary>
    public CodeSymbol? Namespace
    {
        get
        {
            for (var s = this; s != null; s = s.Parent)
            {
                if (s.Kind == CppScopeKind.Namespace)
                {
                    return s.Symbol;
                }

                if (s.Kind == CppScopeKind.Global)
                {
                    return null;
                }
            }

            return null;
        }
    }

    /// <summary>The type of <c>this</c> in the innermost member function around this scope.</summary>
    public CppType? ThisType
    {
        get
        {
            for (var s = this; s != null; s = s.Parent)
            {
                if (s.This != null)
                {
                    return s.This;
                }
            }

            return null;
        }
    }

    public void AddLocal(string name, CppType type) => (Locals ??= new Dictionary<string, CppType>(StringComparer.Ordinal))[name] = type;

    public void AddAlias(string name, CodeSymbol symbol)
    {
        Aliases ??= new Dictionary<string, List<CodeSymbol>>(StringComparer.Ordinal);
        if (!Aliases.TryGetValue(name, out var list))
        {
            Aliases[name] = list = [];
        }

        if (!list.Contains(symbol))
        {
            list.Add(symbol);
        }
    }
}
