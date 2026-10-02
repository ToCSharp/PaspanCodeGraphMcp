using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.CSharp;

/// <summary>
/// Top-level statements, bound as the one method the compiler makes of them, and invocations of a name whose member
/// lookup finds a property that is not a delegate (<c>list.Count(x => ...)</c>), compared with Roslyn.
/// </summary>
[TestClass]
public sealed class TopLevelAndInvocationTests
{
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string Program = """
        using Fixture;

        var items = new List<Item> { new Item("a", 1) };
        var big = items.Where(i => i.Size > 0).ToList();
        var count = items.Count(i => i.Name.Length > 0) + big.Count(i => i.Size > 1);
        var store = new Store();
        store.Add(items[0]);
        IReadOnlyList<Item> view = store.Items;
        var named = view.Count(i => i.Name == "a") + store.Count + store.Matching();
        var first = view.First(i => i.Size > 0);
        Console.WriteLine(Describe(first) + count + named + args.Length);

        static string Describe(Item item) => item.Name + Helper(item);
        static int Helper(Item item) => item.Size;
        """;

    private const string Types = """
        namespace Fixture
        {
            public record Item(string Name, int Size);

            public class Store
            {
                public List<Item> Items { get; } = [];
                public int Count => Items.Count;
                public Func<Item, bool> Filter { get; set; } = _ => true;
                public void Add(Item item) { if (Filter(item)) Items.Add(item); }
                public int Matching() => Items.Count(Filter) + Items.Count(i => i.Size > Count);
            }
        }
        """;

    private static SymbolIndex _index;
    private static List<CSharpCompilation> _compilations;
    private static TempWorkspace _workspace;
    private static string _programPath;
    private static string _typesPath;

    [ClassInitialize]
    public static void Load(TestContext context)
    {
        _workspace = new TempWorkspace();
        var project = _workspace.Write("TopLevel/TopLevel.csproj", Project);
        _programPath = _workspace.Write("TopLevel/Program.cs", Program);
        _typesPath = _workspace.Write("TopLevel/Types.cs", Types);
        var snapshot = WorkspaceLoader.Load(project);
        _index = snapshot.Index;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) == "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var global = string.Concat(snapshot.Projects[0].Usings.Select(u => $"global using {u};\n"));
        var trees = new[] { _programPath, _typesPath }.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), options, f))
            .Append(CSharpSyntaxTree.ParseText(global, options, _workspace.PathOf("TopLevel/obj/GlobalUsings.g.cs")));
        var compilation = CSharpCompilation.Create("TopLevel", trees, references, new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
        _compilations = [compilation];
    }

    [ClassCleanup]
    public static void Cleanup() => _workspace?.Dispose();

    [TestMethod]
    public void MemberReferences_MatchRoslyn()
    {
        var result = HierarchyComparison.MemberReferences(_index, _compilations);
        Assert.IsTrue(result.Result.Compared > 15, result.Report());
        Assert.AreEqual(0, result.Result.Differences.Count, result.Report());
        Assert.AreEqual(0, result.NameOnly, result.Report());
    }

    [TestMethod]
    public void TopLevelStatements_ShareLocalsAndHaveAnOwner()
    {
        // 'items', 'big' and 'view' are declared by earlier statements; their lambdas' parameters are Items
        var name = _index.ReferencesTo("P:Fixture.Item.Name").Where(r => r.File == _programPath).ToList();
        Assert.AreEqual(3, name.Count, string.Join("\n", name));
        Assert.IsTrue(name.All(r => r.Confidence != Confidence.NameOnly && r.InMember == "M:Program.<Main>$(System.String[])"), string.Join("\n", name));

        // A local function declared after the statements that call it
        Assert.IsTrue(_index.ReferencesTo("P:Fixture.Item.Size").Any(r => r.File == _programPath && r.Line == 14));

        // References to members of referenced assemblies are recorded in top-level statements too
        Assert.IsTrue(ExternalAt(_programPath, 11).Any(t => t.StartsWith("M:System.Console.WriteLine", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void InvokedNonDelegateProperty_LeavesTheExtensionMethods()
    {
        foreach (var (file, line) in new[] { (_programPath, 5), (_programPath, 9), (_typesPath, 11) })
        {
            var targets = ExternalAt(file, line);
            Assert.IsTrue(targets.Any(t => t.StartsWith("M:System.Linq.Enumerable.Count``1(", StringComparison.Ordinal)), $"{Path.GetFileName(file)}:{line}: " + string.Join(", ", targets));
            Assert.IsFalse(targets.Any(t => t.Contains("Collection", StringComparison.Ordinal) && t.EndsWith(".Count", StringComparison.Ordinal)), $"{Path.GetFileName(file)}:{line}: " + string.Join(", ", targets));
        }

        // A property of a delegate type is still invoked, and a property that is not invoked is still a property
        Assert.IsTrue(_index.ReferencesTo("P:Fixture.Store.Filter").Any(r => r.Line == 10 && r.Confidence == Confidence.Exact));
        Assert.IsTrue(_index.ReferencesTo("P:Fixture.Store.Count").Any(r => r.File == _programPath && r.Line == 9 && r.Confidence == Confidence.Exact));
    }

    private static List<string> ExternalAt(string file, int line) =>
        _index.ReferenceTargetIds
            .Where(t => t.StartsWith(ReferenceTargets.External, StringComparison.Ordinal))
            .Where(t => _index.ReferencesTo(t).Any(r => r.File == file && r.Line == line))
            .Select(t => t[ReferenceTargets.External.Length..])
            .ToList();
}
