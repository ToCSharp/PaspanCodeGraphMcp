using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.Workspace;

[TestClass]
public sealed class WorkspaceLoaderTests
{
    private const string LibProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    private const string AppProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
        </Project>
        """;

    [TestMethod]
    public void Sln_ListsCSharpProjects()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("App/App.csproj", AppProject);
        workspace.Write("Lib/Lib.csproj", LibProject);
        var sln = workspace.Write("All.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Folder", "Folder", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Missing", "Missing\Missing.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            """);

        var result = SolutionDiscovery.Discover(sln);

        CollectionAssert.AreEqual(new[] { workspace.PathOf("App/App.csproj") }, result.Projects.ToArray());
        Assert.AreEqual(1, result.Problems.Count, "the missing project is reported");
    }

    [TestMethod]
    public void Slnx_ListsProjectsInFolders()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("src/App/App.csproj", AppProject);
        workspace.Write("src/Lib/Lib.csproj", LibProject);
        var slnx = workspace.Write("All.slnx", """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/App/App.csproj" />
                <Project Path="src\Lib\Lib.csproj" />
              </Folder>
            </Solution>
            """);

        var result = SolutionDiscovery.Discover(workspace.Root);

        Assert.AreEqual(slnx, result.RootPath);
        CollectionAssert.AreEqual(new[] { workspace.PathOf("src/App/App.csproj"), workspace.PathOf("src/Lib/Lib.csproj") }, result.Projects.ToArray());
    }

    [TestMethod]
    public void Load_FollowsProjectReferences_AndIndexesDeclarations()
    {
        using var workspace = new TempWorkspace();
        var app = workspace.Write("App/App.csproj", AppProject);
        workspace.Write("Lib/Lib.csproj", LibProject);
        workspace.Write("App/Program.cs", """
            namespace App;

            public static class Program
            {
                public static void Main() => Lib.Tools.Helper.Run();
            }
            """);
        workspace.Write("Lib/Helper.cs", """
            namespace Lib.Tools
            {
                /// <summary>Helps.</summary>
                public partial class Helper
                {
                    public static void Run() { }
                }
            }
            """);
        workspace.Write("Lib/Helper.Part.cs", """
            namespace Lib.Tools;

            partial class Helper
            {
                private int _count;
            }
            """);

        var snapshot = WorkspaceLoader.Load(app);

        CollectionAssert.AreEquivalent(new[] { "App", "Lib" }, snapshot.Projects.Select(p => p.Name).ToArray());
        Assert.AreEqual(3, snapshot.Documents.Count);
        var helper = snapshot.Index.Get("T:Lib.Tools.Helper");
        Assert.IsNotNull(helper);
        Assert.AreEqual(2, helper.Declarations.Count, "both parts of the partial class");
        Assert.AreEqual("Lib", helper.Project);
        CollectionAssert.AreEquivalent(new[] { "M:Lib.Tools.Helper.Run", "F:Lib.Tools.Helper._count" }, helper.Members.Select(m => m.Id).ToArray());
        Assert.IsNotNull(snapshot.Index.Get("M:App.Program.Main"));
        Assert.IsNotNull(snapshot.Index.Get("N:Lib.Tools"));
    }

    [TestMethod]
    public void Load_KeepsDeclarationsOfFilesWithSyntaxErrors()
    {
        using var workspace = new TempWorkspace();
        var project = workspace.Write("Lib/Lib.csproj", LibProject);
        workspace.Write("Lib/Broken.cs", """
            namespace Lib;

            public class Broken
            {
                public void Good() { }

                public void Bad( { }

                public int After { get; set; }
            }
            """);

        var snapshot = WorkspaceLoader.Load(project);

        var document = snapshot.Documents.Values.Single();
        Assert.IsNotNull(document.Unit);
        Assert.IsTrue(document.Errors.Count > 0);
        Assert.IsNotNull(snapshot.Index.Get("M:Lib.Broken.Good"));
        Assert.IsNotNull(snapshot.Index.Get("P:Lib.Broken.After"));
    }
}
