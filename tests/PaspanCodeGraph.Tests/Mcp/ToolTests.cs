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
        Assert.AreEqual(6, report.Documents);
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

        Assert.IsTrue(result.Binding.Exact > 0);
        Assert.IsTrue(result.Binding.NameOnly > 0 && result.Binding.NameOnlyShare is > 0 and < 1, "value.Next() on a dynamic receiver");
        Assert.AreEqual(1, result.Binding.Unresolved, "Missing(1)");
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
        // "        public static int Parse(string text) => Parse(text, 10);" is line 12 of Lib/Parser.cs
        var onName = (DefinitionResult)await NavigationTools.GoToDefinition(_host, "Lib/Parser.cs:12:25");
        Assert.AreEqual("M:Lib.Parser.Parse(System.String)", onName.Symbol.Id);

        // On a call: the overload called
        var onCall = (DefinitionResult)await NavigationTools.GoToDefinition(_host, "Parser.cs:12:50");
        Assert.AreEqual("M:Lib.Parser.Parse(System.String,System.Int32)", onCall.Symbol.Id);

        // Elsewhere in a body: the member containing it
        var inBody = (DefinitionResult)await NavigationTools.GoToDefinition(_host, "Parser.cs:12:61");
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
    public async Task GoToDefinition_OnTypeReference_GoesToTheType()
    {
        // "public sealed class JsonParser : Parser, System.IDisposable" is line 5 of App/JsonParser.cs
        var result = (DefinitionResult)await NavigationTools.GoToDefinition(_host, "App/JsonParser.cs:5:36");
        Assert.AreEqual("T:Lib.Parser", result.Symbol.Id);
    }

    [TestMethod]
    public async Task SymbolInfo_HasRelations()
    {
        var type = (SymbolInfoResult)await NavigationTools.SymbolInfo(_host, "T:App.JsonParser");
        Assert.AreEqual("T:Lib.Parser", type.Relations.BaseType);
        CollectionAssert.AreEqual(new[] { "System.IDisposable" }, type.Relations.Interfaces.ToArray());

        var member = (SymbolInfoResult)await NavigationTools.SymbolInfo(_host, "M:Lib.Tokenizer.Next");
        CollectionAssert.AreEqual(new[] { "M:Lib.ITokenizer.Next" }, member.Relations.Implements.ToArray());
        Assert.AreEqual(1, member.Relations.Implementations, "only WordTokenizer.Next overrides it directly");

        var overriding = (SymbolInfoResult)await NavigationTools.SymbolInfo(_host, "M:Lib.WordTokenizer.Next");
        Assert.AreEqual("M:Lib.Tokenizer.Next", overriding.Relations.Overrides);

        var parser = (SymbolInfoResult)await NavigationTools.SymbolInfo(_host, "T:Lib.Parser");
        Assert.AreEqual(1, parser.Relations.DerivedTypes);
        Assert.AreEqual(2, parser.Relations.References);
    }

    [TestMethod]
    public async Task TypeHierarchy_BasesInterfacesAndDerived()
    {
        var direct = (TypeHierarchyResult)await HierarchyTools.TypeHierarchy(_host, "JsonTokenizer");
        CollectionAssert.AreEqual(new[] { "T:Lib.WordTokenizer" }, direct.BaseTypes.Select(b => b.Symbol.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "T:Lib.ITokenizer" }, direct.Interfaces.Select(b => b.Symbol.Id).ToArray());

        var chain = (TypeHierarchyResult)await HierarchyTools.TypeHierarchy(_host, "T:App.JsonTokenizer", transitive: true);
        CollectionAssert.AreEqual(new[] { "T:Lib.WordTokenizer", "T:Lib.Tokenizer" }, chain.BaseTypes.Select(b => b.Symbol.Id).ToArray());

        var derived = (TypeHierarchyResult)await HierarchyTools.TypeHierarchy(_host, "ITokenizer", transitive: true);
        CollectionAssert.AreEqual(
            new[] { "T:App.JsonTokenizer:1", "T:Lib.Tokenizer:1", "T:Lib.WordTokenizer:2" },
            derived.Derived.Select(d => $"{d.Symbol.Id}:{d.Depth}").ToArray());

        var truncated = (TypeHierarchyResult)await HierarchyTools.TypeHierarchy(_host, "ITokenizer", transitive: true, maxDerived: 1);
        Assert.AreEqual(1, truncated.Derived.Count);
        Assert.IsTrue(truncated.DerivedTruncated);
    }

    [TestMethod]
    public async Task FindImplementations_OfInterfaceMemberAndType()
    {
        var member = (FindImplementationsResult)await HierarchyTools.FindImplementations(_host, "M:Lib.ITokenizer.Next");
        CollectionAssert.AreEqual(
            new[] { "implements M:App.JsonTokenizer.Lib#ITokenizer#Next", "implements M:Lib.Tokenizer.Next", "overrides M:App.JsonTokenizer.Next", "overrides M:Lib.WordTokenizer.Next" },
            member.Items.Select(i => $"{i.Relation} {i.Symbol.Id}").Order().ToArray());

        var type = (FindImplementationsResult)await HierarchyTools.FindImplementations(_host, "ITokenizer");
        CollectionAssert.AreEquivalent(new[] { "T:App.JsonTokenizer", "T:Lib.Tokenizer", "T:Lib.WordTokenizer" }, type.Items.Select(i => i.Symbol.Id).ToArray());
        Assert.IsTrue(type.Items.All(i => i.Relation == "implements"));
    }

    [TestMethod]
    public async Task FindReferences_OfType_GroupedByFile()
    {
        var result = (FindReferencesResult)await HierarchyTools.FindReferences(_host, "T:Lib.Tokenizer");
        Assert.AreEqual(2, result.Total);
        CollectionAssert.AreEquivalent(new[] { "JsonParser.cs", "Tokens.cs" }, result.Files.Select(f => Path.GetFileName(f.File)).ToArray());
        var inApp = result.Files.Single(f => f.File.EndsWith("JsonParser.cs", StringComparison.Ordinal)).Items.Single();
        Assert.AreEqual("public static Tokenizer Create() => new WordTokenizer();", inApp.LineText);
        Assert.AreEqual("M:App.JsonTokenizer.Create", inApp.InMember);
        Assert.AreEqual("Exact", inApp.Confidence);

        var page = (FindReferencesResult)await HierarchyTools.FindReferences(_host, "T:Lib.Tokenizer", maxResults: 1, offset: 1);
        Assert.AreEqual(1, page.Returned);
        Assert.IsFalse(page.Truncated);
    }

    [TestMethod]
    public async Task FindReferences_OfMember_WithImplementations()
    {
        var own = (FindReferencesResult)await HierarchyTools.FindReferences(_host, "M:Lib.ITokenizer.Next", includeImplementations: false);
        var call = own.Files.Single().Items.Single();
        Assert.AreEqual("M:App.Runner.Run(Lib.ITokenizer,Lib.WordTokenizer,System.String)", call.InMember);
        Assert.AreEqual("Exact", call.Confidence);
        Assert.AreEqual(1, own.NameOnly, "value.Next() on a dynamic receiver");

        var family = (FindReferencesResult)await HierarchyTools.FindReferences(_host, "M:Lib.ITokenizer.Next");
        var targets = family.Files.SelectMany(f => f.Items).Select(i => i.Target).ToList();
        CollectionAssert.Contains(targets, null);
        CollectionAssert.Contains(targets, "M:Lib.WordTokenizer.Next");
    }

    [TestMethod]
    public async Task FindCallers_DirectAndThroughInterface()
    {
        var parse = (FindCallersResult)await CallGraphTools.FindCallers(_host, "M:Lib.Parser.Parse(System.String)");
        var site = parse.CallSites.Single();
        Assert.AreEqual("M:App.Runner.Run(Lib.ITokenizer,Lib.WordTokenizer,System.String)", site.Caller!.Id);
        Assert.IsTrue(site.IsDirect);
        Assert.AreEqual("Exact", site.Confidence);

        var overload = (FindCallersResult)await CallGraphTools.FindCallers(_host, "M:Lib.Parser.Parse(System.String,System.Int32)");
        Assert.AreEqual("M:Lib.Parser.Parse(System.String)", overload.CallSites.Single().Caller!.Id);

        var next = (FindCallersResult)await CallGraphTools.FindCallers(_host, "M:Lib.WordTokenizer.Next");
        Assert.IsTrue(next.CallSites.Any(c => c.IsDirect && c.Location.LineText!.Contains("word.Next()")));
        Assert.IsTrue(next.CallSites.Any(c => !c.IsDirect && c.Via == "M:Lib.ITokenizer.Next"));
        Assert.IsTrue(next.NameOnlyCallSites >= 1);
        Assert.IsFalse(next.CallSites.Any(c => c.Confidence == "NameOnly"));

        var withNameOnly = (FindCallersResult)await CallGraphTools.FindCallers(_host, "M:Lib.WordTokenizer.Next", includeNameOnly: true);
        Assert.IsTrue(withNameOnly.CallSites.Any(c => c.Confidence == "NameOnly" && c.Caller!.Name == "Loose"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => CallGraphTools.FindCallers(_host, "T:Lib.Parser"));
    }

    [TestMethod]
    public async Task FindCallees_OfMethod()
    {
        var result = (FindCalleesResult)await CallGraphTools.FindCallees(_host, "M:App.Runner.Run(Lib.ITokenizer,Lib.WordTokenizer,System.String)");
        var parse = result.Callees.Single(c => c.Id == "M:Lib.Parser.Parse(System.String)");
        Assert.AreEqual("Exact", parse.Confidence);
        Assert.IsNull(parse.Implementations);

        var face = result.Callees.Single(c => c.Id == "M:Lib.ITokenizer.Next");
        Assert.IsTrue(face.IsInterfaceMember);
        Assert.AreEqual(4, face.Implementations, "Tokenizer.Next, its two overrides and the explicit implementation");

        var word = result.Callees.Single(c => c.Id == "M:Lib.WordTokenizer.Next");
        Assert.IsTrue(word.IsOverride);

        CollectionAssert.IsSubsetOf(new[] { "M:System.String.Trim", "P:System.String.Length" }, result.Callees.Where(c => c.Symbol == null).Select(c => c.Id).ToArray());
        Assert.AreEqual(3, result.Internal);
        CollectionAssert.AreEqual(new[] { "Missing" }, result.Unresolved.ToArray());

        var internalOnly = (FindCalleesResult)await CallGraphTools.FindCallees(_host, "M:App.Runner.Run(Lib.ITokenizer,Lib.WordTokenizer,System.String)", includeExternal: false);
        Assert.IsTrue(internalOnly.Callees.All(c => c.Symbol != null));

        var loose = (FindCalleesResult)await CallGraphTools.FindCallees(_host, "Runner.Loose");
        Assert.AreEqual(0, loose.Callees.Count);
        Assert.IsTrue(loose.NameOnly >= 3);
    }

    [TestMethod]
    public async Task UnknownFile_IsAnError()
    {
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => NavigationTools.FileOutline(_host, "Missing.cs"));
    }
}

/// <summary>Two projects, a partial class, inheritance and interface implementations across projects, and a file with a syntax error.</summary>
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

            public sealed class JsonTokenizer : WordTokenizer, ITokenizer
            {
                public override int Next() => 2;

                int ITokenizer.Next() => 3;

                public static Tokenizer Create() => new WordTokenizer();
            }
            """);
        workspace.Write("App/Runner.cs", """
            using Lib;

            namespace App;

            public static class Runner
            {
                public static int Run(ITokenizer tokenizer, WordTokenizer word, string text)
                {
                    var total = Parser.Parse(text) + tokenizer.Next() + word.Next();
                    total += text.Trim().Length;
                    return total + Missing(1);
                }

                public static int Loose(dynamic value) => value.Next();
            }
            """);
        workspace.Write("Lib/Tokens.cs", """
            namespace Lib;

            public interface ITokenizer { int Next(); }

            public abstract class Tokenizer : ITokenizer { public abstract int Next(); }

            public class WordTokenizer : Tokenizer { public override int Next() => 1; }
            """);
        return workspace;
    }
}
