namespace PaspanCodeGraph.Cpp;

/// <summary>
/// Links the C++ class hierarchy after the Members pass: the derived types of each class, and the virtual
/// functions each member function overrides (a function with the same name, parameter types and qualifiers in a
/// base, virtual there or overriding in turn; a destructor overrides a virtual destructor of a base).
/// </summary>
public static class CppHierarchy
{
    public static void Link(SymbolIndexBuilder builder, CppBinder binder)
    {
        var types = builder.Symbols.Where(s => s.Language == SourceLanguage.Cpp && s.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Union).ToList();
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

        // Bases first, so that a function of a derived class sees whether the one it overrides is virtual
        var ordered = new List<CodeSymbol>();
        var visited = new HashSet<CodeSymbol>(ReferenceEqualityComparer.Instance);
        foreach (var type in types)
        {
            Order(type, binder, visited, ordered);
        }

        var isVirtual = new HashSet<CodeSymbol>(ReferenceEqualityComparer.Instance);
        foreach (var type in ordered)
        {
            foreach (var member in type.Members)
            {
                if (member.Kind is not (SymbolKind.Method or SymbolKind.Operator or SymbolKind.Destructor) || binder.FindInfo(member) is not { } info || info.IsStatic)
                {
                    continue;
                }

                var overridden = Overridden(type, member, info, binder, isVirtual);
                if (overridden != null)
                {
                    member.Overrides = overridden;
                    if (!overridden.OverriddenBy.Contains(member))
                    {
                        overridden.OverriddenBy.Add(member);
                    }

                    isVirtual.Add(member);
                }
                else if (info.IsVirtual)
                {
                    isVirtual.Add(member);
                }
            }
        }
    }

    private static void Order(CodeSymbol type, CppBinder binder, HashSet<CodeSymbol> visited, List<CodeSymbol> ordered)
    {
        if (!visited.Add(type))
        {
            return;
        }

        foreach (var baseType in binder.FindInfo(type)?.Bases ?? [])
        {
            if (baseType.Symbol != null && binder.ClassOf(baseType.Symbol) is { } resolved)
            {
                Order(resolved, binder, visited, ordered);
            }
        }

        ordered.Add(type);
    }

    /// <summary>The nearest virtual function of a base that <paramref name="member"/> overrides, or null.</summary>
    private static CodeSymbol? Overridden(CodeSymbol type, CodeSymbol member, CppSymbolInfo info, CppBinder binder, HashSet<CodeSymbol> isVirtual)
    {
        foreach (var baseType in binder.AllBases(type))
        {
            foreach (var candidate in baseType.Members)
            {
                if (candidate.Kind != member.Kind || !isVirtual.Contains(candidate))
                {
                    continue;
                }

                if (member.Kind == SymbolKind.Destructor
                    || (candidate.Name == member.Name && binder.FindInfo(candidate)?.SignatureKey == info.SignatureKey))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
