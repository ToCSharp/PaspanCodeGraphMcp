using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PaspanCodeGraph.Tests.CSharp;

/// <summary>
/// Compares what the binder finds without a compiler with Roslyn: the workspace base types and interfaces of
/// each type, overridden members, interface implementations, and the references to workspace types.
/// </summary>
internal static class HierarchyComparison
{
    /// <param name="Compared">How many Roslyn facts were comparable.</param>
    /// <param name="Differences">Each difference, one line each.</param>
    public sealed record Result(int Compared, List<string> Differences)
    {
        public string Report(string what) => $"{Differences.Count} of {Compared} {what} differ:\n" + string.Join("\n", Differences.Take(200));
    }

    private static bool IsSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    private static IEnumerable<SyntaxTree> Trees(CSharpCompilation compilation) =>
        compilation.SyntaxTrees.Where(t => !t.FilePath.EndsWith("GlobalUsings.g.cs", StringComparison.Ordinal));

    /// <summary>Every type and member declared in the sources of the compilations.</summary>
    private static IEnumerable<ISymbol> Declared(IEnumerable<CSharpCompilation> compilations)
    {
        foreach (var compilation in compilations)
        {
            foreach (var tree in Trees(compilation))
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes(n => n is not (BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax)))
                {
                    if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax
                        or EventFieldDeclarationSyntax && model.GetDeclaredSymbol(node) is { } symbol)
                    {
                        yield return symbol;
                    }
                    else if (node is VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax } && model.GetDeclaredSymbol(node) is { } eventField)
                    {
                        yield return eventField;
                    }
                }
            }
        }
    }

    public static Result BaseTypes(SymbolIndex index, IEnumerable<CSharpCompilation> compilations)
    {
        var wrong = new List<string>();
        var compared = 0;
        foreach (var type in Declared(compilations).OfType<INamedTypeSymbol>().Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>())
        {
            var id = type.GetDocumentationCommentId();
            if (index.Get(id) is not { } ours)
            {
                wrong.Add($"{id}: not in the index");
                continue;
            }

            var expected = new List<string>();
            if (type.TypeKind == TypeKind.Class && type.BaseType is { } baseType && IsSource(baseType))
            {
                expected.Add(baseType.OriginalDefinition.GetDocumentationCommentId());
            }

            expected.AddRange(type.Interfaces.Where(IsSource).Select(i => i.OriginalDefinition.GetDocumentationCommentId()));
            var actual = ours.Bases.Where(b => b.Symbol != null).Select(b => b.Symbol.Id).ToList();
            compared += expected.Count;
            if (!expected.Order().SequenceEqual(actual.Order()))
            {
                wrong.Add($"{id}: Roslyn [{string.Join(", ", expected)}], index [{string.Join(", ", actual)}]");
            }
        }

        return new Result(compared, wrong);
    }

    public static Result Overrides(SymbolIndex index, IEnumerable<CSharpCompilation> compilations)
    {
        var wrong = new List<string>();
        var compared = 0;
        foreach (var symbol in Declared(compilations))
        {
            var overridden = symbol switch
            {
                IMethodSymbol m => (ISymbol?)m.OverriddenMethod,
                IPropertySymbol p => p.OverriddenProperty,
                IEventSymbol e => e.OverriddenEvent,
                _ => null,
            };

            // Ids with parameter types from references are not in the index
            if (index.Get(symbol.GetDocumentationCommentId()) is not { } ours)
            {
                continue;
            }

            var expected = overridden != null && IsSource(overridden) ? overridden.OriginalDefinition.GetDocumentationCommentId() : null;
            if (expected != null)
            {
                compared++;
            }

            var actual = ours.Overrides?.Id;
            if (expected != actual && !(expected == null && overridden != null && actual == null))
            {
                wrong.Add($"{ours.Id}: Roslyn {expected ?? "none"}, index {actual ?? "none"}");
            }
        }

        return new Result(compared, wrong);
    }

    public static Result InterfaceImplementations(SymbolIndex index, IEnumerable<CSharpCompilation> compilations)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in Declared(compilations).OfType<INamedTypeSymbol>().Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>())
        {
            foreach (var face in type.AllInterfaces.Where(IsSource))
            {
                foreach (var member in face.GetMembers().Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol))
                {
                    if (member.IsStatic || type.FindImplementationForInterfaceMember(member) is not { } implementation || !IsSource(implementation)
                        || implementation.ContainingType.TypeKind == TypeKind.Interface)
                    {
                        continue;
                    }

                    expected.Add($"{implementation.OriginalDefinition.GetDocumentationCommentId()} -> {member.OriginalDefinition.GetDocumentationCommentId()}");
                }
            }
        }

        var actual = index.Symbols
            .Where(s => s.Kind.IsMember())
            .SelectMany(s => s.Implements.Select(i => $"{s.Id} -> {i.Id}"))
            .ToHashSet(StringComparer.Ordinal);

        // Pairs whose ids have parameter types from references cannot be in the index
        var comparable = expected.Where(e => e.Split(" -> ").All(id => index.Get(id) != null)).ToHashSet(StringComparer.Ordinal);
        var wrong = comparable.Except(actual).Order().Select(e => "missing " + e)
            .Concat(expected.Except(comparable).Order().Select(e => "not in the index " + e))
            .Concat(actual.Except(expected).Order().Select(e => "extra " + e))
            .ToList();
        return new Result(comparable.Count, wrong);
    }

    /// <summary>
    /// Every identifier Roslyn binds to a workspace type (or to its constructor, in <c>new T()</c> and attributes)
    /// against the index's references; <see cref="Result.Compared"/> is the number of Roslyn references.
    /// </summary>
    public static (Result Result, double Recall, double Precision) TypeReferences(SymbolIndex index, IEnumerable<CSharpCompilation> compilations)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var compilation in compilations)
        {
            foreach (var tree in Trees(compilation))
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    if (name is IdentifierNameSyntax { IsVar: true } || name.Parent is NameColonSyntax or NameEqualsSyntax)
                    {
                        continue;
                    }

                    var info = model.GetSymbolInfo(name);
                    var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
                    if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor && name.Parent is not MemberAccessExpressionSyntax)
                    {
                        symbol = constructor.ContainingType;
                    }

                    if (symbol is INamedTypeSymbol type && IsSource(type) && !type.IsImplicitlyDeclared)
                    {
                        var position = name.Identifier.GetLocation().GetLineSpan().StartLinePosition;
                        expected.Add($"{tree.FilePath}:{position.Line + 1}:{position.Character + 1} {type.OriginalDefinition.GetDocumentationCommentId()}");
                    }
                }
            }
        }

        var actual = index.Symbols
            .Where(s => s.Kind.IsType())
            .SelectMany(s => index.ReferencesTo(s.Id).Select(r => $"{r.File}:{r.Line}:{r.Column} {s.Id}"))
            .ToHashSet(StringComparer.Ordinal);

        var missing = expected.Except(actual).Order().ToList();
        var extra = actual.Except(expected).Order().ToList();
        var recall = expected.Count == 0 ? 1 : 1 - (double)missing.Count / expected.Count;
        var precision = actual.Count == 0 ? 1 : 1 - (double)extra.Count / actual.Count;
        var wrong = missing.Select(m => "missing " + m).Take(200).Concat(extra.Select(e => "extra " + e).Take(200)).ToList();
        return (new Result(expected.Count, wrong), recall, precision);
    }
}
