using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using PaspanCodeGraphMcp;

namespace PaspanCodeGraph.Tests.Mcp;

/// <summary>
/// The package metadata agrees with itself, the tool answers --help and --version, and every result type has
/// source-generated JSON metadata (which a NativeAOT build needs).
/// </summary>
[TestClass]
public sealed class PackagingTests
{
    private static string ServerProject => Path.Combine(TestPaths.RepositoryRoot, "src", "PaspanCodeGraphMcp");

    [TestMethod]
    public void ServerJson_MatchesThePackage()
    {
        var project = XDocument.Load(Path.Combine(ServerProject, "PaspanCodeGraphMcp.csproj"));
        string Property(string name) => project.Descendants(name).Single().Value;

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(ServerProject, ".mcp", "server.json")));
        var root = json.RootElement;
        var package = root.GetProperty("packages").EnumerateArray().Single();

        Assert.AreEqual(Property("Version"), root.GetProperty("version").GetString());
        Assert.AreEqual(Property("Version"), package.GetProperty("version").GetString());
        Assert.AreEqual(Property("PackageId"), package.GetProperty("identifier").GetString());
        Assert.AreEqual("stdio", package.GetProperty("transport").GetProperty("type").GetString());
        Assert.AreEqual(Property("Version"), ServerOptions.Version);

        // Every variable the package offers is one the server reads
        var usage = ServerOptions.Usage;
        foreach (var variable in package.GetProperty("environmentVariables").EnumerateArray())
        {
            StringAssert.Contains(usage, variable.GetProperty("name").GetString()!);
        }
    }

    [TestMethod]
    public void HelpAndVersion_AreOptions()
    {
        Func<string, string> none = _ => null;
        Assert.IsTrue(ServerOptions.Parse(["--help"], none).ShowHelp);
        Assert.IsTrue(ServerOptions.Parse(["-h"], none).ShowHelp);
        Assert.IsTrue(ServerOptions.Parse(["--version"], none).ShowVersion);

        var options = ServerOptions.Parse(["--no-watch", "My.slnx"], none);
        Assert.IsFalse(options.ShowHelp || options.ShowVersion);
        Assert.AreEqual(Path.GetFullPath("My.slnx"), options.WorkspacePath);
    }

    [TestMethod]
    public void ResultTypes_HaveGeneratedJson()
    {
        // The records of the tools' namespaces are what tools return; these few are not
        string[] notResults = ["Resolution", "SymbolRef"];
        var records = typeof(ToolJson).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Namespace is "PaspanCodeGraphMcp.Tools" or "PaspanCodeGraphMcp.Symbols"
                && t.GetMethod("<Clone>$") != null && !notResults.Contains(t.Name))
            .ToList();
        Assert.IsTrue(records.Count > 30, string.Join(", ", records.Select(t => t.Name)));

        var missing = records.Where(t => ToolJsonContext.Default.GetTypeInfo(t) == null).Select(t => t.Name).ToList();
        Assert.AreEqual(0, missing.Count, "Add to ToolJsonContext: " + string.Join(", ", missing));
    }
}
