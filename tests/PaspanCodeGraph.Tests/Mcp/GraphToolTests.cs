using Microsoft.Extensions.Logging.Abstractions;
using PaspanCodeGraph.Search;
using PaspanCodeGraphMcp;
using PaspanCodeGraphMcp.Symbols;
using PaspanCodeGraphMcp.Tools;

namespace PaspanCodeGraph.Tests.Mcp;

[TestClass]
public sealed class GraphToolTests
{
    private static TempWorkspace _workspace;
    private static WorkspaceHost _host;

    [ClassInitialize]
    public static async Task Load(TestContext context)
    {
        _workspace = SampleWorkspace.Create();
        _host = new WorkspaceHost(new ServerOptions { Watch = false }, NullLogger<WorkspaceHost>.Instance);
        await _host.LoadAsync(_workspace.PathOf("Sample.slnx"), "Debug", "AnyCPU", CancellationToken.None);
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        _host.Dispose();
        _workspace.Dispose();
    }

    [TestMethod]
    public async Task SearchCode_FindsByWordsAndFilters()
    {
        var result = await GraphTools.SearchCode(_host, "parses text");
        CollectionAssert.IsSubsetOf(new[] { "Parse", "TryParse" }, result.Items.Take(3).Select(i => i.Symbol.Name).ToArray());

        var classes = await GraphTools.SearchCode(_host, "tokenizer", kind: "Class");
        Assert.IsTrue(classes.Items.All(i => i.Symbol.Kind == "Class"));
        CollectionAssert.IsSubsetOf(new[] { "Tokenizer", "WordTokenizer", "JsonTokenizer" }, classes.Items.Select(i => i.Symbol.Name).ToArray());

        var byName = await GraphTools.SearchCode(_host, "word tokenizer");
        Assert.AreEqual("WordTokenizer", byName.Items[0].Symbol.Name, "the name written in parts ranks first");
    }

    [TestMethod]
    public async Task GetContext_HasSourceAndNeighborsWithinBudget()
    {
        var result = (GetContextResult)await GraphTools.GetContext(_host, "M:Lib.Parser.Parse(System.String)");

        StringAssert.Contains(result.Seeds[0].Code, "=> Parse(text, 10);");
        var relations = result.Related.Select(r => $"{r.Relation} {r.Symbol.Id}").ToList();
        CollectionAssert.Contains(relations, "declared in T:Lib.Parser");
        CollectionAssert.Contains(relations, "calls M:Lib.Parser.Parse(System.String,System.Int32)");
        CollectionAssert.Contains(relations, "called by M:App.Runner.Run(Lib.ITokenizer,Lib.WordTokenizer,System.String)");
        Assert.AreEqual("declared in", result.Related[0].Relation, "the containing type comes first");
        Assert.IsTrue(result.TokensUsed <= result.TokenBudget);

        var type = (GetContextResult)await GraphTools.GetContext(_host, "T:Lib.Parser");
        StringAssert.Contains(type.Seeds[0].Code, "bool Parser.TryParse(string text, out int value)", "a type shows its members' signatures");

        var small = (GetContextResult)await GraphTools.GetContext(_host, "T:Lib.ITokenizer", depth: 2, tokenBudget: 200);
        Assert.IsTrue(small.Truncated && small.Omitted > 0, "a small budget leaves symbols out");

        var byQuery = (GetContextResult)await GraphTools.GetContext(_host, query: "json parser");
        Assert.AreEqual("T:App.JsonParser", byQuery.Seeds[0].Symbol.Id);
    }

    [TestMethod]
    public async Task ImpactAnalysis_FollowsUsesAndImplementations()
    {
        var result = (ImpactAnalysisResult)await GraphTools.ImpactAnalysis(_host, "M:Lib.ITokenizer.Next");

        var byId = result.Items.ToDictionary(i => i.Symbol.Id);
        Assert.AreEqual(1, byId["M:Lib.Tokenizer.Next"].Depth, "an implementation must follow the interface");
        Assert.AreEqual("implements member", byId["M:Lib.Tokenizer.Next"].Via);
        Assert.AreEqual("calls", byId["M:App.Runner.Run(Lib.ITokenizer,Lib.WordTokenizer,System.String)"].Via);
        Assert.AreEqual(2, byId["M:Lib.WordTokenizer.Next"].Depth, "overrides of the implementation");
        Assert.AreEqual(result.Total, result.ByDepth.Values.Sum());
        Assert.AreEqual(result.Total, result.ByProject.Sum(p => p.Members));

        var external = (ImpactAnalysisResult)await GraphTools.ImpactAnalysis(_host, "M:System.String.Trim");
        CollectionAssert.Contains(external.Items.Select(i => i.Symbol.Id).ToArray(), "M:App.Runner.Run(Lib.ITokenizer,Lib.WordTokenizer,System.String)");
    }

    [TestMethod]
    public async Task FindPath_ThroughDispatch()
    {
        var result = (FindPathResult)await GraphTools.FindPath(_host, "App.Runner.Run", "M:App.JsonTokenizer.Next");

        Assert.IsTrue(result.Found);
        Assert.AreEqual("forward", result.Direction);
        Assert.AreEqual("calls", result.Steps[0].Kind);
        Assert.AreEqual("dispatch", result.Steps[^1].Kind, "the override runs through a call to the base member");
        Assert.AreEqual(9, result.Steps[0].Location.Line);

        var reverse = (FindPathResult)await GraphTools.FindPath(_host, "M:Lib.Parser.Parse(System.String,System.Int32)", "App.Runner.Run");
        Assert.AreEqual("reverse", reverse.Direction);

        var none = (FindPathResult)await GraphTools.FindPath(_host, "M:App.JsonParser.ParseJson(System.String)", "App.Runner.Run");
        Assert.IsFalse(none.Found);
    }

    [TestMethod]
    public async Task ModuleMap_And_Annotations()
    {
        await GraphTools.Annotate(_host, "project:lib", "The parsing library.");
        var annotated = await GraphTools.Annotate(_host, "Lib.Parser", "Entry point for number parsing.");
        Assert.AreEqual("T:Lib.Parser", annotated.Target);
        Assert.IsTrue(File.Exists(_host.Annotations.File));

        var map = await GraphTools.ModuleMap(_host);
        CollectionAssert.AreEquivalent(new[] { "App", "Lib" }, map.Projects.Select(p => p.Name).ToArray());
        Assert.AreEqual("The parsing library.", map.Projects.Single(p => p.Name == "Lib").Note);
        Assert.IsTrue(map.Communities.Count > 0);
        Assert.IsTrue(map.Communities.Any(c => c.Notes.Any(n => n.Target == "T:Lib.Parser")));
        CollectionAssert.Contains(map.EntryPoints.Select(e => e.Id).ToArray(), "M:App.Runner.Run(Lib.ITokenizer,Lib.WordTokenizer,System.String)");

        var context = (GetContextResult)await GraphTools.GetContext(_host, "M:Lib.Parser.Parse(System.String)");
        Assert.AreEqual("Entry point for number parsing.", context.Related.Single(r => r.Symbol.Id == "T:Lib.Parser").Note);

        var removed = await GraphTools.Annotate(_host, "T:Lib.Parser", "");
        Assert.IsNull(removed.Note);
        Assert.AreEqual(1, removed.Total);
        Assert.AreEqual("The parsing library.", (await GraphTools.Annotate(_host, "project:Lib")).Note, "reading a note");
    }

    [TestMethod]
    public void Communities_SplitTwoCliques()
    {
        // Two triangles joined by one edge
        var edges = new List<(int, int, double)> { (0, 1, 1), (1, 2, 1), (0, 2, 1), (3, 4, 1), (4, 5, 1), (3, 5, 1), (2, 3, 1) };
        var membership = Communities.Louvain(6, edges);

        Assert.AreEqual(membership[0], membership[1]);
        Assert.AreEqual(membership[0], membership[2]);
        Assert.AreEqual(membership[3], membership[5]);
        Assert.AreNotEqual(membership[0], membership[3]);
        Assert.IsTrue(Communities.Modularity(6, edges, membership) > 0.3);
    }

    [TestMethod]
    public void Tokenizer_SplitsAndStems()
    {
        CollectionAssert.AreEqual(new[] { "pars", "http", "request", "2", "parsehttprequest2" }, Tokenizer.Terms("ParseHTTPRequest2").ToArray());
        CollectionAssert.AreEqual(new[] { "parser", "pars", "fil" }, Tokenizer.Terms("the parser parses files").ToArray());
        Assert.AreEqual(Tokenizer.Stem("entries"), Tokenizer.Stem("entry"));
        Assert.AreEqual(Tokenizer.Stem("cached"), Tokenizer.Stem("cache"));
    }
}
