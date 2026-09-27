using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.CSharp;

/// <summary>
/// Checks the declaration ids and name locations of the index against Roslyn on real code: the PaspanParsers
/// submodule, loaded through <see cref="WorkspaceLoader"/>. Ids are compared where they can be computed from
/// syntax and the workspace's own types: namespaces, types, and members whose parameter types are predefined
/// types, type parameters, workspace types, or arrays, pointers, nullables and tuples of those.
/// </summary>
[TestClass]
public sealed class DocumentationIdOracleTests
{
    private static WorkspaceSnapshot Snapshot => RoslynOracle.Snapshot;

    [TestMethod]
    public void Workspace_LoadsAllProjectsAndParsesEveryFile()
    {
        CollectionAssert.IsSubsetOf(new[] { "Paspan", "PaspanParsers", "PaspanParsers.Tests" }, Snapshot.Projects.Select(p => p.Name).ToArray());
        var failed = Snapshot.Documents.Values.Where(d => d.Unit is null || d.Errors.Count != 0).Select(d => $"{d.Path}: {d.Failure ?? d.Errors[0].Message}").ToList();
        Assert.AreEqual(0, failed.Count, string.Join("\n", failed.Take(10)));

        // The shared project's files come in through the .projitems import of Paspan.csproj
        var paspan = Snapshot.Projects.Single(p => p.Name == "Paspan");
        Assert.IsTrue(paspan.Sources.Any(s => s.Contains(Path.Combine("PaspanCommon", ""), StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Ids_MatchRoslyn()
    {
        var missing = new List<string>();
        var compared = 0;
        foreach (var (symbol, file) in RoslynOracle.Declarations())
        {
            if (!IsComparable(symbol))
            {
                continue;
            }

            compared++;
            var id = symbol.GetDocumentationCommentId();
            var ours = Snapshot.Index.Get(id);
            if (ours is null || !ours.Declarations.Any(d => SymbolIndexBuilder.PathComparer.Equals(d.File, file)))
            {
                missing.Add($"{id} ({Path.GetFileName(file)})");
            }
        }

        Assert.IsTrue(compared > 4500, $"only {compared} symbols compared");
        Assert.AreEqual(0, missing.Count, $"{missing.Count} of {compared} Roslyn ids are missing from the index:\n" + string.Join("\n", missing.Distinct().Take(30)));
    }

    [TestMethod]
    public void TypeIds_ExistInRoslyn()
    {
        var roslyn = RoslynOracle.Declarations()
            .Select(d => d.Symbol.GetDocumentationCommentId())
            .ToHashSet(StringComparer.Ordinal);
        var extra = Snapshot.Index.Symbols
            .Where(s => s.Kind.IsType() || s.Kind == SymbolKind.Namespace)
            .Where(s => !roslyn.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();

        Assert.AreEqual(0, extra.Count, "ids Roslyn does not declare:\n" + string.Join("\n", extra.Take(30)));
    }

    [TestMethod]
    public void NameLocations_MatchRoslyn()
    {
        var wrong = new List<string>();
        foreach (var (symbol, file) in RoslynOracle.Declarations())
        {
            if (!IsComparable(symbol) || symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion })
            {
                continue;
            }

            var ours = Snapshot.Index.Get(symbol.GetDocumentationCommentId());
            if (ours is null)
            {
                continue;
            }

            foreach (var location in symbol.Locations.Where(l => l.IsInSource && l.SourceTree.FilePath == file))
            {
                var span = location.GetLineSpan().StartLinePosition;
                if (!ours.Declarations.Any(d => d.File == file && d.Line == span.Line + 1 && d.Column == span.Character + 1))
                {
                    var actual = string.Join(", ", ours.Declarations.Where(d => d.File == file).Select(d => $"{d.Line}:{d.Column}"));
                    wrong.Add($"{ours.Id}: Roslyn {span.Line + 1}:{span.Character + 1}, index {actual} ({Path.GetFileName(file)})");
                }
            }
        }

        Assert.AreEqual(0, wrong.Count, $"{wrong.Count} name locations differ:\n" + string.Join("\n", wrong.Take(30)));
    }

    /// <summary>Whether the id of the symbol can be computed from syntax alone.</summary>
    private static bool IsComparable(ISymbol symbol)
    {
        if (symbol is INamespaceSymbol or INamedTypeSymbol)
        {
            return true;
        }

        var parameters = symbol switch
        {
            IMethodSymbol m when m.ExplicitInterfaceImplementations.All(i => RoslynOracle.IsExact(i.ContainingType)) => m.Parameters,
            IPropertySymbol p when p.ExplicitInterfaceImplementations.All(i => RoslynOracle.IsExact(i.ContainingType)) => p.Parameters,
            IEventSymbol e when e.ExplicitInterfaceImplementations.All(i => RoslynOracle.IsExact(i.ContainingType)) => [],
            IFieldSymbol => [],
            _ => default(System.Collections.Immutable.ImmutableArray<IParameterSymbol>?),
        };

        if (parameters is null)
        {
            return false;
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.Conversion } conversion && !RoslynOracle.IsExact(conversion.ReturnType))
        {
            return false;
        }

        return parameters.Value.All(p => RoslynOracle.IsExact(p.Type));
    }
}
