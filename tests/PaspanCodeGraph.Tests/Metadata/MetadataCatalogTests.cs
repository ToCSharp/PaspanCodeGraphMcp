using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PaspanCodeGraph.Metadata;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.Metadata;

/// <summary>The types and members read from reference assemblies, compared with Roslyn reading the same files.</summary>
[TestClass]
public sealed class MetadataCatalogTests
{
    private static readonly Lazy<List<string>> ReferencePack = new(() =>
    {
        var project = new ProjectModel("App", Path.Combine(Path.GetTempPath(), "App.csproj"), "net10.0", PaspanParsers.CSharp.CSharpLanguageVersion.Latest, [], [], [], []);
        return ReferenceAssemblies.Resolve(project, []).ToList();
    });

    private static string Assembly(string name) => ReferencePack.Value.First(p => Path.GetFileName(p) == name);

    [TestMethod]
    public void ReferencePack_IsFound()
    {
        Assert.IsTrue(ReferencePack.Value.Count > 100, $"{ReferencePack.Value.Count} reference assemblies");
        Assert.IsTrue(ReferencePack.Value.Any(p => Path.GetFileName(p) == "System.Runtime.dll"));
    }

    [TestMethod]
    [DataRow("System.Runtime.dll")]
    [DataRow("System.Collections.dll")]
    [DataRow("System.Linq.dll")]
    [DataRow("System.Collections.Immutable.dll")]
    public void DocumentationIds_MatchRoslyn(string file)
    {
        var path = Assembly(file);
        var catalog = MetadataCatalog.Load([path]);
        var ours = new HashSet<string>(StringComparer.Ordinal);
        void AddType(MetadataType type)
        {
            ours.Add(type.DocId);
            foreach (var member in type.Members)
            {
                ours.Add(member.DocId);
            }

            type.NestedTypes.ForEach(AddType);
        }

        foreach (var ns in AllTypes(catalog))
        {
            AddType(ns);
        }

        var reference = MetadataReference.CreateFromFile(path);
        var compilation = CSharpCompilation.Create("Oracle", references: ReferencePack.Value.Select(p => MetadataReference.CreateFromFile(p)));
        var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(compilation.References.First(r => r.Display == path))!;
        var roslyn = new HashSet<string>(StringComparer.Ordinal);
        void Visit(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
            {
                if (member is INamespaceSymbol ns)
                {
                    Visit(ns);
                    continue;
                }

                if (member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal) || member.IsImplicitlyDeclared && member is not IMethodSymbol { MethodKind: MethodKind.Constructor })
                {
                    continue;
                }

                // Accessors, finalizers and explicit interface implementations (their names are dotted) are not members to look up
                if (member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.StaticConstructor or MethodKind.Destructor }
                    || member.Name.Contains('.') && member is not IMethodSymbol { MethodKind: MethodKind.Constructor } || member is IFieldSymbol { AssociatedSymbol: not null })
                {
                    continue;
                }

                if (member.GetDocumentationCommentId() is { } id)
                {
                    roslyn.Add(id);
                }

                if (member is INamedTypeSymbol type)
                {
                    Visit(type);
                }
            }
        }

        Visit(assembly.GlobalNamespace);
        var missing = roslyn.Except(ours).Order(StringComparer.Ordinal).ToList();
        var extra = ours.Except(roslyn).Order(StringComparer.Ordinal).ToList();
        Assert.IsTrue(roslyn.Count > 100, $"{roslyn.Count} Roslyn ids");
        Assert.AreEqual(0, missing.Count + extra.Count,
            $"{roslyn.Count} Roslyn ids; missing {missing.Count}:\n{string.Join("\n", missing.Take(400))}\nextra {extra.Count}:\n{string.Join("\n", extra.Take(400))}");
    }

    [TestMethod]
    public void Members_HaveSignatures()
    {
        var catalog = MetadataCatalog.Load(ReferencePack.Value);
        var list = catalog.FindType("System.Collections.Generic", "List", 1)!;
        Assert.AreEqual(MetadataTypeKind.Class, list.Kind);
        Assert.AreEqual("System.Object", list.BaseType?.DocId);
        Assert.IsTrue(list.Interfaces.Any(i => i.DocId == "System.Collections.Generic.IList{`0}"));
        var add = list.Members.Single(m => m.DocId == "M:System.Collections.Generic.List`1.Add(`0)");
        Assert.AreEqual(MetadataMemberKind.Method, add.Kind);
        Assert.AreEqual("System.Void", add.Type.DocId);

        var select = catalog.ExtensionMethods("Select").First(m => m.DocId == "M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})");
        Assert.IsTrue(select.IsExtension && select.Parameters[0].IsThis);
        Assert.AreEqual("System.Collections.Generic.IEnumerable{``1}", select.Type.DocId);

        var tryGetValue = catalog.FindByDocId("M:System.Collections.Generic.Dictionary`2.TryGetValue(`0,`1@)").Member!;
        Assert.AreEqual(MetaRefKind.Out, tryGetValue.Parameters[1].RefKind);

        var split = catalog.FindType("System", "String", 0)!.Members.Single(m => m.DocId == "M:System.String.Split(System.Char[])");
        Assert.IsTrue(split.Parameters[0].IsParams);
        Assert.IsTrue(catalog.IsNamespace("System.Collections") && catalog.IsNamespace("System"));
    }

    private static IEnumerable<MetadataType> AllTypes(MetadataCatalog catalog) =>
        typeof(MetadataCatalog).GetField("_types", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(catalog) is Dictionary<string, MetadataType> types
            ? types.Values
            : [];
}
