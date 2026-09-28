using Microsoft.Extensions.Logging.Abstractions;
using PaspanCodeGraphMcp;
using PaspanCodeGraphMcp.Symbols;
using PaspanCodeGraphMcp.Tools;

namespace PaspanCodeGraph.Tests.Cpp;

/// <summary>The MCP tools over the C++ fixture.</summary>
[TestClass]
public sealed class CppToolTests
{
    private static WorkspaceHost _host;

    [ClassInitialize]
    public static async Task Load(TestContext context)
    {
        _host = new WorkspaceHost(new ServerOptions { Cache = false, Watch = false }, NullLogger<WorkspaceHost>.Instance);
        await _host.LoadAsync(CppOracleTests.FixtureDirectory, "Debug", "AnyCPU", CancellationToken.None);
    }

    [TestMethod]
    public async Task WorkspaceLoad_ReportsTheCppProject()
    {
        var report = await WorkspaceTools.Load(_host, CppOracleTests.FixtureDirectory);
        var project = report.Projects.Single();
        Assert.AreEqual("Cpp", project.Language);
        Assert.AreEqual("Cpp23", project.LanguageVersion);
        Assert.AreEqual(8, report.Documents);
        Assert.AreEqual(0, report.FilesWithParseErrors);
        CollectionAssert.Contains(project.PreprocessorSymbols.ToArray(), "__cplusplus=202302L");

        var diagnostics = await WorkspaceTools.Diagnostics(_host);
        Assert.AreEqual(0, diagnostics.Total);
        Assert.IsTrue(diagnostics.Binding.Exact > 50);
    }

    [TestMethod]
    public async Task FindSymbol_AndSymbolInfo()
    {
        var found = await NavigationTools.FindSymbol(_host, "geo::Shape::area", exact: true);
        Assert.AreEqual("M:geo::Shape::area()const", found.Items.Single().Id);

        var info = (SymbolInfoResult)await NavigationTools.SymbolInfo(_host, "M:geo::Circle::area()const");
        Assert.AreEqual("M:geo::Shape::area()const", info.Relations.Overrides);
        Assert.AreEqual(2, info.Declarations.Count);
        Assert.AreEqual("geo::Circle", info.Symbol.ContainingType is { } type ? "geo::" + type : null);

        var shape = (SymbolInfoResult)await NavigationTools.SymbolInfo(_host, "geo::Shape");
        Assert.AreEqual("A shape on the plane.", shape.Summary);
        Assert.AreEqual(2, shape.Relations.DerivedTypes);
        Assert.IsTrue(shape.Relations.References > 10);

        var macro = (SymbolInfoResult)await NavigationTools.SymbolInfo(_host, "D:CORE_MAX");
        Assert.AreEqual("Macro", macro.Symbol.Kind);
        Assert.AreEqual("#define CORE_MAX(a, b) ((a) > (b) ? (a) : (b))", macro.Symbol.Signature);
    }

    [TestMethod]
    public async Task Positions_ResolveToDeclarationsAndReferences()
    {
        var definition = (DefinitionResult)await NavigationTools.GoToDefinition(_host, "src/main.cpp:22:27");
        Assert.AreEqual("M:geo::Shape::area()const", definition.Symbol.Id);

        var declared = (DefinitionResult)await NavigationTools.GoToDefinition(_host, "include/geo/circle.h:21:7");
        Assert.AreEqual("T:geo::Rect", declared.Symbol.Id);

        var outline = await NavigationTools.FileOutline(_host, "src/main.cpp");
        CollectionAssert.IsSubsetOf(new[] { "N:app", "T:app::Scene", "M:app::Scene::totalArea()const", "M:main()" }, outline.Items.Select(i => i.Id).ToArray());
    }

    [TestMethod]
    public async Task Hierarchy_CallersAndReferences()
    {
        var hierarchy = (TypeHierarchyResult)await HierarchyTools.TypeHierarchy(_host, "T:geo::Square", transitive: true);
        CollectionAssert.AreEqual(new[] { "T:geo::Rect", "T:geo::Shape" }, hierarchy.BaseTypes.Select(b => b.Symbol?.Id).ToArray());
        Assert.AreEqual(0, hierarchy.Interfaces.Count);

        var derived = (TypeHierarchyResult)await HierarchyTools.TypeHierarchy(_host, "geo::Shape", transitive: true);
        CollectionAssert.AreEquivalent(new[] { "T:geo::Circle", "T:geo::Rect", "T:geo::Square" }, derived.Derived.Select(d => d.Symbol.Id).ToArray());

        var implementations = (FindImplementationsResult)await HierarchyTools.FindImplementations(_host, "M:geo::Shape::area()const");
        CollectionAssert.IsSubsetOf(new[] { "M:geo::Circle::area()const", "M:geo::Rect::area()const", "M:geo::Square::area()const" }, implementations.Items.Select(i => i.Symbol.Id).ToArray());

        var callers = (FindCallersResult)await CallGraphTools.FindCallers(_host, "M:geo::Shape::move(const geo::Point&)");
        CollectionAssert.AreEquivalent(new[] { "M:main()", "M:geo::Shape::move(double,double)" }, callers.CallSites.Select(c => c.Caller?.Id).Distinct().ToArray());

        var functionCallers = (FindCallersResult)await CallGraphTools.FindCallers(_host, "M:geo::distance(const geo::Point&,const geo::Point&)");
        Assert.AreEqual("M:main()", functionCallers.CallSites.Single().Caller?.Id);

        var references = (FindReferencesResult)await HierarchyTools.FindReferences(_host, "F:geo::Point::x");
        Assert.IsTrue(references.Files.Count >= 3, string.Join(", ", references.Files.Select(f => f.File)));
    }

    [TestMethod]
    public async Task Retrieval_FindsCppSymbols()
    {
        var search = await GraphTools.SearchCode(_host, "area of a circle");
        Assert.IsTrue(search.Items.Take(5).Any(i => i.Symbol.Id == "M:geo::Circle::area()const"), string.Join(", ", search.Items.Select(i => i.Symbol.Id)));

        var context = (GetContextResult)await GraphTools.GetContext(_host, "T:geo::Circle");
        StringAssert.Contains(context.Seeds.Single().Code, "class Circle final : public Shape");
        Assert.IsTrue(context.Related.Any(r => r.Symbol.Id == "T:geo::Shape"));

        var impact = await GraphTools.ImpactAnalysis(_host, "M:geo::Point::operator+=(const geo::Point&)");
        StringAssert.Contains(System.Text.Json.JsonSerializer.Serialize(impact, ToolJson.Options), "M:main()");

        var map = await GraphTools.ModuleMap(_host);
        CollectionAssert.Contains(map.EntryPoints.Select(e => e.Id).ToArray(), "M:main()");
    }
}
