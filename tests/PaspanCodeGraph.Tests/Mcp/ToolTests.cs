using Microsoft.Extensions.Logging.Abstractions;
using PaspanCodeGraphMcp;
using PaspanCodeGraphMcp.Symbols;
using PaspanCodeGraphMcp.Tools;

namespace PaspanCodeGraph.Tests.Mcp;

[TestClass]
public sealed class ToolTests
{
    private static TempWorkspace _workspace;
    private static WorkspaceHost _host;

    [ClassInitialize]
    public static async Task Load(TestContext context)
    {
        _workspace = SampleWorkspace.Create();
        _host = new WorkspaceHost(new ServerOptions(), NullLogger<WorkspaceHost>.Instance);
        await _host.LoadAsync(_workspace.PathOf("Sample.slnx"), "Debug", "AnyCPU", CancellationToken.None);
    }

    [ClassCleanup]
    public static void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public async Task WorkspaceLoad_ReportsProjectsAndProblems()
    {
        var report = await WorkspaceTools.Load(_host, _workspace.PathOf("Sample.slnx"));

        CollectionAssert.AreEquivalent(new[] { "App", "Lib" }, report.Projects.Select(p => p.Name).ToArray());
        Assert.AreEqual(4, report.Documents);
        Assert.AreEqual(1, report.FilesWithParseErrors);
        CollectionAssert.Contains(report.Projects.Single(p => p.Name == "App").ProjectReferences.ToArray(), "Lib");

        var status = WorkspaceTools.Status(_host);
        Assert.IsTrue(status.Loaded);
        Assert.AreEqual(2, status.Projects);
    }

    [TestMethod]
    public async Task Diagnostics_ListsSyntaxErrorsWithPositions()
    {
        var result = await WorkspaceTools.Diagnostics(_host);

        var error = result.Items.Single(i => i.Kind == "Syntax");
        StringAssert.EndsWith(error.File, "Broken.cs");
        Assert.AreEqual(5, error.Line);
        Assert.AreEqual("Lib", error.Project);
    }

    [TestMethod]
    public async Task FindSymbol_BySubstringExactAndDottedName()
    {
        var bySubstring = await NavigationTools.FindSymbol(_host, "Pars");
        CollectionAssert.IsSubsetOf(new[] { "Parser", "Parse", "TryParse" }, bySubstring.Items.Select(i => i.Name).ToArray());
        Assert.AreEqual("Parse", bySubstring.Items[0].Name, "shorter names first");
        Assert.AreEqual("Parse", (await NavigationTools.FindSymbol(_host, "parse")).Items[0].Name, "whole-name matches first");

        var exact = await NavigationTools.FindSymbol(_host, "Parse", exact: true);
        Assert.AreEqual(2, exact.Count, "both overloads");

        var dotted = await NavigationTools.FindSymbol(_host, "Lib.Parser.Parse", kind: "Method", exact: true);
        Assert.AreEqual(2, dotted.Count);
        Assert.IsTrue(dotted.Items.All(i => i.ContainingType == "Parser" && i.Namespace == "Lib"));

        var wildcard = await NavigationTools.FindSymbol(_host, "Try*");
        CollectionAssert.AreEqual(new[] { "TryParse" }, wildcard.Items.Select(i => i.Name).ToArray());
    }

    [TestMethod]
    public async Task SymbolInfo_ById_WithDocumentationAndParameters()
    {
        var info = (SymbolInfoResult)await NavigationTools.SymbolInfo(_host, "M:Lib.Parser.TryParse(System.String,System.Int32@)");

        Assert.AreEqual("Parses text.", info.Summary);
        Assert.AreEqual("Public", info.Accessibility);
        CollectionAssert.Contains(info.Modifiers.ToArray(), "static");
        Assert.AreEqual("bool", info.Type);
        Assert.AreEqual("out", info.Parameters[1].Modifier);
        Assert.AreEqual("public static bool TryParse(string text, out int value)", info.Symbol.Location.LineText);
    }

    [TestMethod]
    public async Task SymbolInfo_AcceptsRoslynIdWithReturnType()
    {
        var info = (SymbolInfoResult)await NavigationTools.SymbolInfo(_host, "M:Lib.Parser.TryParse(System.String,System.Int32@)~System.Boolean");

        Assert.AreEqual("M:Lib.Parser.TryParse(System.String,System.Int32@)", info.Symbol.Id);
    }

    [TestMethod]
    public async Task AmbiguousName_ReturnsCandidates()
    {
        var result = await NavigationTools.GoToDefinition(_host, "Parse");

        var ambiguous = (AmbiguousSymbol)result;
        Assert.AreEqual(2, ambiguous.TotalCandidates);
        Assert.IsFalse(ambiguous.Resolved);
    }

    [TestMethod]
    public async Task GoToDefinition_OfPartialType_HasEveryPart()
    {
        var result = (DefinitionResult)await NavigationTools.GoToDefinition(_host, "Parser");

        Assert.AreEqual("T:Lib.Parser", result.Symbol.Id);
        Assert.AreEqual(2, result.Declarations.Count);
    }

    [TestMethod]
    public async Task GoToDefinition_ByPosition_OnNameAndInsideBody()
    {
        // "    public static int Parse(string text) => Parse(text, 10);" is line 12 of Lib/Parser.cs
        var onName = (DefinitionResult)await NavigationTools.GoToDefinition(_host, "Lib/Parser.cs:12:25");
        Assert.AreEqual("M:Lib.Parser.Parse(System.String)", onName.Symbol.Id);

        var inBody = (DefinitionResult)await NavigationTools.GoToDefinition(_host, "Parser.cs:12:50");
        Assert.AreEqual("M:Lib.Parser.Parse(System.String)", inBody.Symbol.Id);
    }

    [TestMethod]
    public async Task TypeMembers_WithInheritedMembers()
    {
        var own = (TypeMembersResult)await NavigationTools.TypeMembers(_host, "T:App.JsonParser");
        CollectionAssert.AreEquivalent(new[] { "ParseJson" }, own.Members.Select(m => m.Symbol.Name).ToArray());

        var all = (TypeMembersResult)await NavigationTools.TypeMembers(_host, "JsonParser", includeInherited: true);
        CollectionAssert.IsSubsetOf(new[] { "ParseJson", "Parse", "TryParse", "_radix" }, all.Members.Select(m => m.Symbol.Name).ToArray());
        Assert.AreEqual("T:Lib.Parser", all.Members.First(m => m.Symbol.Name == "TryParse").InheritedFrom);
        CollectionAssert.AreEqual(new[] { "System.IDisposable" }, all.UnresolvedBaseTypes.ToArray());
    }

    [TestMethod]
    public async Task TypeMembers_OfMethod_IsAnError()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => NavigationTools.TypeMembers(_host, "M:App.JsonParser.ParseJson(System.String)"));
    }

    [TestMethod]
    public async Task FileOutline_InSourceOrderWithDepth()
    {
        var outline = await NavigationTools.FileOutline(_host, "Lib/Parser.cs");

        CollectionAssert.AreEqual(
            new[] { "N:Lib", "T:Lib.Parser", "F:Lib.Parser._radix", "M:Lib.Parser.Parse(System.String)", "M:Lib.Parser.Parse(System.String,System.Int32)", "M:Lib.Parser.TryParse(System.String,System.Int32@)" },
            outline.Items.Select(i => i.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 2, 2, 2 }, outline.Items.Select(i => i.Depth).ToArray());
        Assert.AreEqual(0, outline.SyntaxErrors);
    }

    [TestMethod]
    public async Task UnknownFile_IsAnError()
    {
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => NavigationTools.FileOutline(_host, "Missing.cs"));
    }
}

/// <summary>Two projects, a partial class, inheritance across projects and a file with a syntax error.</summary>
internal static class SampleWorkspace
{
    public static TempWorkspace Create()
    {
        var workspace = new TempWorkspace();
        workspace.Write("Sample.slnx", """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Lib/Lib.csproj" />
            </Solution>
            """);
        workspace.Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        workspace.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """);
        workspace.Write("Lib/Parser.cs", """
            namespace Lib
            {
                /// <summary>Parses numbers.</summary>
                public partial class Parser
                {
                    private readonly int _radix = 10;

                    /// <summary>
                    /// Parses
                    /// text.
                    /// </summary>
                    public static int Parse(string text) => Parse(text, 10);

                    public static int Parse(string text, int radix) => 0;

                    /// <summary>Parses text.</summary>
                    public static bool TryParse(string text, out int value)
                    {
                        value = 0;
                        return true;
                    }
                }
            }
            """);
        workspace.Write("Lib/Parser.Extra.cs", """
            namespace Lib;

            partial class Parser
            {
            }
            """);
        workspace.Write("Lib/Broken.cs", """
            namespace Lib;

            internal class Broken
            {
                void Bad( { }
            }
            """);
        workspace.Write("App/JsonParser.cs", """
            using Lib;

            namespace App;

            public sealed class JsonParser : Parser, System.IDisposable
            {
                public object ParseJson(string json) => null;
            }
            """);
        return workspace;
    }
}
