using PaspanCodeGraph.Workspace;
using PaspanParsers.CSharp;

namespace PaspanCodeGraph.Tests.Workspace;

[TestClass]
public sealed class ProjectFileReaderTests
{
    [TestMethod]
    public void SdkProject_CompilesDefaultItems_ExceptBinObjAndHiddenFolders()
    {
        using var workspace = new TempWorkspace();
        var project = workspace.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        workspace.Write("App/Program.cs", "class Program {}");
        workspace.Write("App/Sub/Helper.cs", "class Helper {}");
        workspace.Write("App/bin/Debug/Generated.cs", "class Bin {}");
        workspace.Write("App/obj/Debug/AssemblyInfo.cs", "class Obj {}");
        workspace.Write("App/.hidden/Secret.cs", "class Hidden {}");
        workspace.Write("App/readme.txt", "not code");

        var model = ProjectFileReader.Read(project);

        CollectionAssert.AreEquivalent(
            new[] { workspace.PathOf("App/Program.cs"), workspace.PathOf("App/Sub/Helper.cs") },
            model.Sources.ToArray());
        Assert.AreEqual("App", model.Name);
        Assert.AreEqual("net10.0", model.TargetFramework);
    }

    [TestMethod]
    public void CompileRemoveAndInclude_WithWildcards()
    {
        using var workspace = new TempWorkspace();
        var project = workspace.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Compile Remove="Legacy/**" />
                <Compile Include="../Shared/**/*.cs" />
              </ItemGroup>
            </Project>
            """);
        workspace.Write("App/Program.cs", "class Program {}");
        workspace.Write("App/Legacy/Old.cs", "class Old {}");
        workspace.Write("Shared/Deep/Common.cs", "class Common {}");

        var model = ProjectFileReader.Read(project);

        CollectionAssert.AreEquivalent(
            new[] { workspace.PathOf("App/Program.cs"), workspace.PathOf("Shared/Deep/Common.cs") },
            model.Sources.ToArray());
    }

    [TestMethod]
    public void DisabledDefaultItems_CompileOnlyExplicitFiles()
    {
        using var workspace = new TempWorkspace();
        var project = workspace.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup><Compile Include="A.cs" /></ItemGroup>
            </Project>
            """);
        workspace.Write("App/A.cs", "class A {}");
        workspace.Write("App/B.cs", "class B {}");

        var model = ProjectFileReader.Read(project);

        CollectionAssert.AreEqual(new[] { workspace.PathOf("App/A.cs") }, model.Sources.ToArray());
    }

    [TestMethod]
    public void SharedProjectImport_AddsItsCompileItems()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Common/Common.projitems", """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Compile Include="$(MSBuildThisFileDirectory)Shared.cs" />
              </ItemGroup>
            </Project>
            """);
        workspace.Write("Common/Shared.cs", "class Shared {}");
        var project = workspace.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Import Project="..\Common\Common.projitems" Label="Shared" />
            </Project>
            """);
        workspace.Write("App/Program.cs", "class Program {}");

        var model = ProjectFileReader.Read(project);

        CollectionAssert.AreEquivalent(
            new[] { workspace.PathOf("App/Program.cs"), workspace.PathOf("Common/Shared.cs") },
            model.Sources.ToArray());
    }

    [TestMethod]
    public void DefineConstants_FromDirectoryBuildPropsAndConditions()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <DefineConstants>$(DefineConstants);FROM_PROPS</DefineConstants>
                <LangVersion>12.0</LangVersion>
              </PropertyGroup>
            </Project>
            """);
        var project = workspace.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|AnyCPU'">
                <DefineConstants>$(DefineConstants);DEBUG_ONLY</DefineConstants>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'">
                <DefineConstants>$(DefineConstants);RELEASE_ONLY</DefineConstants>
              </PropertyGroup>
              <PropertyGroup Condition="Exists('$(MSBuildProjectDirectory)/missing.txt') or '$(TargetFramework)' == 'net8.0'">
                <DefineConstants>$(DefineConstants);NET8_BRANCH</DefineConstants>
              </PropertyGroup>
            </Project>
            """);

        var debug = ProjectFileReader.Read(project);
        var release = ProjectFileReader.Read(project, "Release");

        CollectionAssert.IsSubsetOf(new[] { "FROM_PROPS", "DEBUG_ONLY", "NET8_BRANCH", "DEBUG", "TRACE", "NET", "NET8_0", "NET8_0_OR_GREATER", "NET5_0_OR_GREATER", "NETCOREAPP" }, debug.PreprocessorSymbols.ToArray());
        CollectionAssert.DoesNotContain(debug.PreprocessorSymbols.ToArray(), "RELEASE_ONLY");
        CollectionAssert.DoesNotContain(debug.PreprocessorSymbols.ToArray(), "NET9_0_OR_GREATER");
        CollectionAssert.Contains(release.PreprocessorSymbols.ToArray(), "RELEASE_ONLY");
        CollectionAssert.DoesNotContain(release.PreprocessorSymbols.ToArray(), "DEBUG");
        Assert.AreEqual(CSharpLanguageVersion.CSharp12, debug.LanguageVersion);
    }

    [TestMethod]
    public void TargetFrameworkSymbols_ForOtherFrameworks()
    {
        CollectionAssert.AreEquivalent(
            new[] { "NETSTANDARD", "NETSTANDARD2_0", "NETSTANDARD1_0_OR_GREATER", "NETSTANDARD1_1_OR_GREATER", "NETSTANDARD1_2_OR_GREATER", "NETSTANDARD1_3_OR_GREATER", "NETSTANDARD1_4_OR_GREATER", "NETSTANDARD1_5_OR_GREATER", "NETSTANDARD1_6_OR_GREATER", "NETSTANDARD2_0_OR_GREATER" },
            ProjectFileReader.TargetFrameworkSymbols("netstandard2.0").ToArray());
        var framework = ProjectFileReader.TargetFrameworkSymbols("net472").ToArray();
        CollectionAssert.IsSubsetOf(new[] { "NETFRAMEWORK", "NET472", "NET45_OR_GREATER", "NET472_OR_GREATER" }, framework);
        CollectionAssert.DoesNotContain(framework, "NET48_OR_GREATER");
        CollectionAssert.Contains(ProjectFileReader.TargetFrameworkSymbols("net10.0-windows").ToArray(), "WINDOWS");
    }

    [TestMethod]
    public void ProjectReferences_AreFullPaths()
    {
        using var workspace = new TempWorkspace();
        var project = workspace.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """);

        var model = ProjectFileReader.Read(project);

        CollectionAssert.AreEqual(new[] { workspace.PathOf("Lib/Lib.csproj") }, model.ProjectReferences.ToArray());
    }
}
