using Microsoft.Extensions.Logging.Abstractions;
using PaspanCodeGraphMcp;
using PaspanCodeGraphMcp.Tools;

namespace PaspanCodeGraph.Tests.Mcp;

/// <summary>
/// Questions an agent asks about this repository ("where is X done", "what breaks if Y changes"), with the
/// symbols a person would point to. The share found in the first five results is written to
/// search-quality-report.txt and must stay at 80% or more.
/// </summary>
[TestClass]
public sealed class SearchQualityTests
{
    private static readonly (string Query, string Expected)[] Questions =
    [
        ("graph cache", "PaspanCodeGraph.Workspace.GraphCache"),
        ("read project.assets.json", "ReferenceAssemblies."),
        ("where are project files read", "ProjectFileReader"),
        ("watch file changes", "SourceWatcher"),
        ("find the dotnet sdk", "DotnetLocator"),
        ("line and column of an offset", "GetLineAndColumn"),
        ("overload resolution", "CSharpSymbolCollector.Resolution"),
        ("documentation comment id", "DocumentationIds"),
        ("pagerank centrality", "CodeGraph.Rank"),
        ("louvain communities", "Communities.Louvain"),
        ("split identifiers at case changes", "Tokenizer"),
        ("incremental update of changed files", "IncrementalState"),
        ("which files must be bound again", "IncrementalState"),
        ("extension methods in scope", "ExtensionMethods"),
        ("read types from reference assemblies", "MetadataCatalog"),
        ("link overrides and interface implementations", "CSharpHierarchy"),
        ("annotations json", "AnnotationStore"),
        ("list projects of a slnx solution", "SolutionDiscovery"),
        ("context within a token budget", "GetContext"),
        ("shortest path between symbols", "ShortestPath"),
    ];

    private static WorkspaceHost _host;

    [ClassInitialize]
    public static async Task Load(TestContext context)
    {
        _host = new WorkspaceHost(new ServerOptions { Watch = false, Cache = false }, NullLogger<WorkspaceHost>.Instance);
        await _host.LoadAsync(Path.Combine(TestPaths.RepositoryRoot, "PaspanCodeGraphMcp.slnx"), "Debug", "AnyCPU", CancellationToken.None);
    }

    [ClassCleanup]
    public static void Cleanup() => _host.Dispose();

    [TestMethod]
    public async Task SearchCode_FindsWhatAPersonWouldPointTo()
    {
        var report = new List<string>();
        var found = 0;
        var reciprocal = 0.0;
        foreach (var (query, expected) in Questions)
        {
            var result = await GraphTools.SearchCode(_host, query, maxResults: 5, includeTests: false);
            var rank = result.Items.ToList().FindIndex(i => i.Symbol.Id.Contains(expected, StringComparison.Ordinal));
            if (rank >= 0)
            {
                found++;
                reciprocal += 1.0 / (rank + 1);
            }

            report.Add($"{(rank >= 0 ? $"#{rank + 1}" : "--")} {query} -> {expected}: {string.Join(", ", result.Items.Select(i => i.Symbol.Id))}");
        }

        var share = (double)found / Questions.Length;
        report.Insert(0, $"Found in the first five: {found} of {Questions.Length} ({share:P0}), mean reciprocal rank {reciprocal / Questions.Length:0.00}");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "search-quality-report.txt"), report);
        Assert.IsTrue(share >= 0.8, string.Join("\n", report));
    }

    [TestMethod]
    public async Task ImpactAnalysis_ReachesTheKnownCallers()
    {
        // GraphCache.Save is called by WorkspaceLoader.TrySave, which LoadCached and the server's update call
        var result = (ImpactAnalysisResult)await GraphTools.ImpactAnalysis(_host, "M:PaspanCodeGraph.Workspace.GraphCache.Save(PaspanCodeGraph.Workspace.WorkspaceSnapshot,System.String)", maxResults: 500);
        var depths = result.Items.ToDictionary(i => i.Symbol.Name + (i.Symbol.ContainingType is { } t ? "@" + t : ""), i => i.Depth);

        Assert.AreEqual(1, depths["TrySave@WorkspaceLoader"]);
        Assert.AreEqual(2, depths["LoadCached@WorkspaceLoader"]);
        Assert.AreEqual(2, depths["UpdateCoreAsync@WorkspaceHost"]);
        Assert.IsTrue(result.AffectedTests.Any(t => t.Name == "Cache_GivesTheSameGraph"), "the cache test is affected");
    }

    [TestMethod]
    public async Task FindPath_FromAToolToTheTokenizer()
    {
        var result = (FindPathResult)await GraphTools.FindPath(_host, "GraphTools.SearchCode", "Tokenizer.Stem");

        Assert.IsTrue(result.Found);
        Assert.IsTrue(result.Steps.All(s => s.Kind == "calls"), string.Join(" > ", result.Steps.Select(s => $"{s.From.Name} {s.Kind}")));
        Assert.AreEqual("Stem", result.Steps[^1].To.Name);
    }
}
