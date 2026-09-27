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
                IMethodSymbol m => (ISymbol)m.OverriddenMethod,
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

    /// <summary>What the member reference comparison found.</summary>
    /// <param name="Recall">The share of Roslyn's references found as exact or inferred.</param>
    /// <param name="Precision">The share of exact and inferred references that Roslyn agrees with.</param>
    /// <param name="NameOnlyCovered">The share of Roslyn's references missing from exact and inferred that name-only candidates include.</param>
    public sealed record MemberResult(Result Result, double Recall, double Precision, double ExactPrecision, int Exact, int Inferred, int NameOnly, double NameOnlyCovered)
    {
        public string Report() =>
            $"recall {Recall:P2}, precision {Precision:P2} (exact {ExactPrecision:P2}); {Exact} exact, {Inferred} inferred, {NameOnly} name-only (cover {NameOnlyCovered:P1} of the missing); "
            + Result.Report("member references");
    }

    /// <summary>
    /// The references to workspace members (methods, constructors, properties, indexers, fields, events, enum
    /// members): each name Roslyn binds to one, constructor calls at the type's name (or <c>new</c>, <c>base</c>,
    /// <c>this</c>) and indexers at <c>[</c>, compared with the exact and inferred references of the index.
    /// </summary>
    /// <param name="external">Compare the references to members of types from referenced assemblies instead, by their documentation ids.</param>
    public static MemberResult MemberReferences(SymbolIndex index, IEnumerable<CSharpCompilation> compilations, bool external = false)
    {
        // A member of the index by the location of its name: ids with types from references are not Roslyn's
        var byLocation = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var symbol in index.Symbols)
        {
            foreach (var declaration in symbol.Declarations)
            {
                byLocation.TryAdd($"{declaration.File}:{declaration.Line}:{declaration.Column}:{symbol.Kind.IsType()}", symbol.Id);
            }
        }

        string IdOf(ISymbol member)
        {
            foreach (var location in member.Locations.Where(l => l.IsInSource))
            {
                var position = location.GetLineSpan().StartLinePosition;
                if (byLocation.TryGetValue($"{location.SourceTree!.FilePath}:{position.Line + 1}:{position.Character + 1}:{false}", out var id))
                {
                    return id;
                }
            }

            return member.GetDocumentationCommentId()!;
        }

        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var compilation in compilations)
        {
            foreach (var tree in Trees(compilation))
            {
                var model = compilation.GetSemanticModel(tree);
                void Add(ISymbol symbol, SyntaxToken token) => AddAt(symbol, token.GetLocation());
                void AddAt(ISymbol symbol, Location location)
                {
                    if (Normalize(symbol, external) is { } member)
                    {
                        var position = location.GetLineSpan().StartLinePosition;
                        expected.Add($"{tree.FilePath}:{position.Line + 1}:{position.Character + 1} {IdOf(member)}");
                    }
                }

                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    switch (node)
                    {
                        case SimpleNameSyntax name:
                        {
                            var info = model.GetSymbolInfo(name);
                            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
                            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } && name.FirstAncestorOrSelf<AttributeSyntax>() is not { } attribute)
                            {
                                break;
                            }

                            Add(symbol, name.Identifier);
                            break;
                        }

                        case ObjectCreationExpressionSyntax creation when LastName(creation.Type) is { } typeName:
                            Add(model.GetSymbolInfo(creation).Symbol, typeName);
                            break;
                        case ImplicitObjectCreationExpressionSyntax implicitCreation:
                            Add(model.GetSymbolInfo(implicitCreation).Symbol, implicitCreation.NewKeyword);
                            break;
                        case ConstructorInitializerSyntax initializer:
                            Add(model.GetSymbolInfo(initializer).Symbol, initializer.ThisOrBaseKeyword);
                            break;
                        case PrimaryConstructorBaseTypeSyntax baseType when LastName(baseType.Type) is { } baseName:
                            Add(model.GetSymbolInfo(baseType).Symbol, baseName);
                            break;
                        case ElementAccessExpressionSyntax element:
                            Add(model.GetSymbolInfo(element).Symbol, element.ArgumentList.OpenBracketToken);
                            break;
                        case ImplicitElementAccessSyntax implicitElement:
                            Add(model.GetSymbolInfo(implicitElement).Symbol, implicitElement.ArgumentList.OpenBracketToken);
                            break;
                    }
                }
            }
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var nameOnly = new HashSet<string>(StringComparer.Ordinal);
        int exactCount = 0, inferredCount = 0, nameOnlyCount = 0;
        var references = external
            ? index.Symbols
                .SelectMany(s => index.ReferencesFrom(s.Id))
                .Where(r => r.TargetId.StartsWith(ReferenceTargets.External, StringComparison.Ordinal) && !r.TargetId.StartsWith(ReferenceTargets.External + "T:", StringComparison.Ordinal))
                .Select(r => (Id: r.TargetId[ReferenceTargets.External.Length..], r.Reference))
                .Distinct()
            : index.Symbols
                .Where(s => !s.Kind.IsType() && s.Kind != SymbolKind.Namespace)
                .SelectMany(s => index.ReferencesTo(s.Id).Select(r => (s.Id, Reference: r)));
        foreach (var (id, reference) in references)
        {
            {
                var key = $"{reference.File}:{reference.Line}:{reference.Column} {id}";
                switch (reference.Confidence)
                {
                    case Confidence.Exact:
                        exactCount++;
                        exact.Add(key);
                        actual.Add(key);
                        break;
                    case Confidence.Inferred:
                        inferredCount++;
                        actual.Add(key);
                        break;
                    default:
                        nameOnlyCount++;
                        nameOnly.Add(key);
                        break;
                }
            }
        }

        var missing = expected.Except(actual).Order().ToList();
        var extra = actual.Except(expected).Order().ToList();
        var recall = expected.Count == 0 ? 1 : 1 - (double)missing.Count / expected.Count;
        var precision = actual.Count == 0 ? 1 : 1 - (double)extra.Count / actual.Count;
        var exactPrecision = exact.Count == 0 ? 1 : 1 - (double)exact.Except(expected).Count() / exact.Count;
        var covered = missing.Count == 0 ? 1 : (double)missing.Count(nameOnly.Contains) / missing.Count;
        var wrong = missing.Select(m => "missing " + m).Concat(extra.Select(e => (exact.Contains(e) ? "extra exact " : "extra ") + e)).ToList();
        return new MemberResult(new Result(expected.Count, wrong), recall, precision, exactPrecision, exactCount, inferredCount, nameOnlyCount, covered);
    }

    private static SyntaxToken? LastName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier,
        QualifiedNameSyntax qualified => qualified.Right.Identifier,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier,
        PredefinedTypeSyntax predefined => predefined.Keyword,
        _ => null,
    };

    /// <summary>A workspace member as the index records it: the definition, and an extension method's declaration.</summary>
    private static ISymbol Normalize(ISymbol symbol, bool external)
    {
        var member = symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction or MethodKind.BuiltinOperator } => null,
            IMethodSymbol method => (ISymbol)(method.ReducedFrom ?? method).OriginalDefinition,
            IFieldSymbol { ContainingType.IsTupleType: true } => null,
            IPropertySymbol or IFieldSymbol or IEventSymbol => symbol.OriginalDefinition,
            _ => null,
        };
        return member != null && (external ? !IsSource(member) && member.ContainingType is { IsTupleType: false, IsAnonymousType: false } : IsSource(member) && !member.IsImplicitlyDeclared) ? member : null;
    }
}
