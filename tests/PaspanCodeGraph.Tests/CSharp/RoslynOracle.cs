using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.CSharp;

/// <summary>
/// The PaspanParsers submodule's solution loaded twice: through <see cref="WorkspaceLoader"/> and as Roslyn
/// compilations with the same files, preprocessor symbols and project references, for comparing the two.
/// </summary>
internal static class RoslynOracle
{
    private static readonly Lazy<(WorkspaceSnapshot Snapshot, List<CSharpCompilation> Compilations)> Loaded =
        new(() => Load(Path.Combine(TestPaths.RepositoryRoot, "external", "PaspanParsers", "PaspanParsers.slnx")));

    private static readonly Lazy<(WorkspaceSnapshot Snapshot, List<CSharpCompilation> Compilations)> LoadedSelf =
        new(() => Load(Path.Combine(TestPaths.RepositoryRoot, "PaspanCodeGraphMcp.slnx")));

    public static WorkspaceSnapshot Snapshot => Loaded.Value.Snapshot;

    public static IReadOnlyList<CSharpCompilation> Compilations => Loaded.Value.Compilations;

    /// <summary>This repository's solution (with the PaspanParsers projects it builds), loaded the same way.</summary>
    public static WorkspaceSnapshot SelfSnapshot => LoadedSelf.Value.Snapshot;

    public static IReadOnlyList<CSharpCompilation> SelfCompilations => LoadedSelf.Value.Compilations;

    private static (WorkspaceSnapshot, List<CSharpCompilation>) Load(string solution)
    {
        var snapshot = WorkspaceLoader.Load(solution);

        // The framework, and the packages the test binaries carry (MSTest, Roslyn, the MCP SDK) for projects that use them
        var projectNames = snapshot.Projects.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "Microsoft.CSharp.dll")
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Where(p => !projectNames.Contains(Path.GetFileNameWithoutExtension(p)) && !Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal)))
            .DistinctBy(Path.GetFileName)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        // Referenced projects first, so that their compilations can be referenced
        var byPath = snapshot.Projects.ToDictionary(p => p.Path, SymbolIndexBuilder.PathComparer);
        var compilations = new Dictionary<string, CSharpCompilation>(SymbolIndexBuilder.PathComparer);
        CSharpCompilation Compile(ProjectModel project)
        {
            if (compilations.TryGetValue(project.Path, out var known))
            {
                return known;
            }

            // Project references are transitive, as in MSBuild
            var referenced = new List<ProjectModel>();
            var pending = new Stack<string>(project.ProjectReferences);
            while (pending.Count > 0)
            {
                if (byPath.TryGetValue(pending.Pop(), out var next) && !referenced.Contains(next))
                {
                    referenced.Add(next);
                    next.ProjectReferences.ToList().ForEach(pending.Push);
                }
            }

            var projectReferences = referenced
                .Select(r => (MetadataReference)Compile(r).ToMetadataReference())
                .ToList();
            var options = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: project.PreprocessorSymbols);
            var trees = project.Sources
                .Where(s => snapshot.Documents.TryGetValue(s, out var d) && d.Project == project.Name)
                .Select(s => CSharpSyntaxTree.ParseText(File.ReadAllText(s), options, s))
                .ToList();
            var global = project.Usings.Count == 0
                ? []
                : new[] { CSharpSyntaxTree.ParseText(string.Concat(project.Usings.Select(u => $"global using {u};\n")), options, Path.Combine(project.Directory, "obj", "GlobalUsings.g.cs")) };
            var compilation = CSharpCompilation.Create(
                project.Name,
                trees.Concat(global),
                references.Concat(projectReferences),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            compilations[project.Path] = compilation;
            return compilation;
        }

        var list = snapshot.Projects.Select(Compile).ToList();
        return (snapshot, list);
    }

    /// <summary>Every symbol declared in the sources, with the file of the declaration.</summary>
    public static IEnumerable<(ISymbol Symbol, string File)> Declarations()
    {
        foreach (var compilation in Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees.Where(t => !t.FilePath.EndsWith("GlobalUsings.g.cs", StringComparison.Ordinal)))
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes(n => n is not (BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax)))
                {
                    var declared = node switch
                    {
                        BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or EnumMemberDeclarationSyntax
                            or BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax => model.GetDeclaredSymbol(node),
                        VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } => model.GetDeclaredSymbol(node),
                        _ => null,
                    };

                    if (declared is null)
                    {
                        continue;
                    }

                    // Each part of a dotted namespace name is a namespace
                    if (declared is INamespaceSymbol ns)
                    {
                        for (var n = ns; n is { IsGlobalNamespace: false }; n = n.ContainingNamespace)
                        {
                            yield return (n, tree.FilePath);
                        }

                        continue;
                    }

                    yield return (declared, tree.FilePath);
                }
            }
        }
    }

    /// <summary>Whether a symbol is declared in the workspace sources.</summary>
    public static bool IsSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    /// <summary>
    /// Whether the id of a type can be computed without references: predefined types, type parameters, workspace
    /// types, and arrays, pointers, nullables and tuples of those.
    /// </summary>
    public static bool IsExact(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => IsExact(array.ElementType),
        IPointerTypeSymbol pointer => IsExact(pointer.PointedAtType),
        { TypeKind: TypeKind.Dynamic } => true,
        INamedTypeSymbol { IsTupleType: true } tuple => tuple.TupleElements.All(e => IsExact(e.Type)),
        INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable => IsExact(nullable.TypeArguments[0]),
        INamedTypeSymbol { TypeKind: TypeKind.Error } => false,
        INamedTypeSymbol named when IsSource(named) => AllTypeArguments(named).All(IsExact),
        _ => type.SpecialType != SpecialType.None && type.SpecialType != SpecialType.System_Nullable_T,
    };

    private static IEnumerable<ITypeSymbol> AllTypeArguments(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
        {
            foreach (var argument in t.TypeArguments)
            {
                yield return argument;
            }
        }
    }
}
