using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.Workspace;

/// <summary>
/// What the build generates that a project's code uses: the global usings it wrote, the C# of an Android binding
/// project, and the fields and InitializeComponent the XAML compiler makes of named elements.
/// </summary>
[TestClass]
public sealed class BuildOutputTests
{
    private const string Page = """
        <?xml version="1.0" encoding="utf-8" ?>
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                     xmlns:local="clr-namespace:App.Controls"
                     x:Class="App.LogsPage">
            <Grid>
                <CollectionView x:Name="FilesList">
                    <CollectionView.ItemTemplate>
                        <DataTemplate>
                            <Label x:Name="InTemplate" />
                        </DataTemplate>
                    </CollectionView.ItemTemplate>
                </CollectionView>
                <Label x:Name="ContentLabel" Text="?" /> <local:Gauge x:Name="Meter" x:FieldModifier="public" />
            </Grid>
        </ContentPage>
        """;

    [TestMethod]
    public void MauiXaml_NamedElementsAreFieldsDeclaredInTheXaml()
    {
        using var workspace = new TempWorkspace();
        var project = workspace.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <UseMaui>true</UseMaui>
              </PropertyGroup>
            </Project>
            """);
        var xaml = workspace.Write("App/LogsPage.xaml", Page);
        workspace.Write("App/LogsPage.xaml.cs", """
            namespace App
            {
                public partial class LogsPage
                {
                    public LogsPage() => InitializeComponent();
                    public object Show() => ContentLabel ?? FilesList ?? (object)Meter;
                }
            }
            """);
        workspace.Write("App/Controls/Gauge.cs", "namespace App.Controls { public class Gauge { } }");
        workspace.Write("App/Resources/Colors.xaml", """<ResourceDictionary xmlns="http://schemas.microsoft.com/dotnet/2021/maui" />""");

        var snapshot = WorkspaceLoader.Load(project, readReferences: false);
        var index = snapshot.Index;

        // A XAML file without a class adds nothing
        Assert.IsFalse(snapshot.Projects[0].Sources.Any(s => s.EndsWith("Colors.xaml", StringComparison.Ordinal)));

        // The fields are declared where their names are in the XAML; names in templates make no fields
        var label = index.Get("F:App.LogsPage.ContentLabel");
        Assert.IsNotNull(label);
        Assert.AreEqual(xaml, label.Declarations[0].File);
        var lines = Page.Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains("\"ContentLabel\"", StringComparison.Ordinal));
        Assert.AreEqual(line + 1, label.Declarations[0].Line);
        Assert.AreEqual(lines[line].IndexOf("ContentLabel", StringComparison.Ordinal) + 1, label.Declarations[0].Column);
        Assert.IsNull(index.Get("F:App.LogsPage.InTemplate"));
        Assert.AreEqual("Public", index.Get("F:App.LogsPage.Meter")!.Accessibility);
        Assert.AreEqual("Private", index.Get("F:App.LogsPage.FilesList")!.Accessibility);

        // The code-behind binds to them, and to InitializeComponent
        foreach (var id in new[] { "F:App.LogsPage.ContentLabel", "F:App.LogsPage.FilesList", "F:App.LogsPage.Meter", "M:App.LogsPage.InitializeComponent" })
        {
            Assert.IsTrue(index.ReferencesTo(id).Any(r => r.Confidence == Confidence.Exact && r.File.EndsWith("LogsPage.xaml.cs", StringComparison.Ordinal)), id);
        }

        // A clr-namespace prefix names a workspace type
        Assert.IsTrue(index.ReferencesTo("T:App.Controls.Gauge").Any(r => r.File == xaml));
    }

    [TestMethod]
    public void WpfXaml_NameAttributeMakesAnInternalField()
    {
        using var workspace = new TempWorkspace();
        var project = workspace.Write("Wpf/Wpf.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows</TargetFramework>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);
        workspace.Write("Wpf/MainWindow.xaml", """
            <Window x:Class="Wpf.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <StackPanel>
                    <TextBlock Name="StatusText" />
                    <Button x:Name="Go" />
                </StackPanel>
            </Window>
            """);
        workspace.Write("Wpf/MainWindow.xaml.cs", "namespace Wpf { public partial class MainWindow { public MainWindow() { InitializeComponent(); StatusText.ToString(); Go.ToString(); } } }");

        var index = WorkspaceLoader.Load(project, readReferences: false).Index;

        Assert.AreEqual("Internal", index.Get("F:Wpf.MainWindow.StatusText")!.Accessibility);
        Assert.AreEqual("Public", index.Get("M:Wpf.MainWindow.InitializeComponent")!.Accessibility);
        Assert.IsTrue(index.ReferencesTo("F:Wpf.MainWindow.StatusText").Any());
        Assert.IsTrue(index.ReferencesTo("F:Wpf.MainWindow.Go").Any());
    }

    [TestMethod]
    public void BuiltGlobalUsings_ReplaceTheSdkImplicitOnes()
    {
        using var workspace = new TempWorkspace();
        var project = workspace.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-android</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup><Using Include="Added.Since.Build" /></ItemGroup>
            </Project>
            """);
        workspace.Write("App/obj/Debug/net10.0-android/App.GlobalUsings.g.cs", """
            // <auto-generated/>
            global using global::Microsoft.Maui.Hosting;
            global using System;
            global using static System.Math;
            """);

        var model = ProjectFileReader.Read(project);

        CollectionAssert.AreEquivalent(new[] { "Microsoft.Maui.Hosting", "System", "Added.Since.Build" }, model.Usings.ToArray());
    }

    [TestMethod]
    public void AndroidBinding_CompilesTheGeneratedCSharp()
    {
        using var workspace = new TempWorkspace();
        var project = workspace.Write("Binding/Binding.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0-android</TargetFramework></PropertyGroup>
              <ItemGroup>
                <AndroidLibrary Include="Jars\bridge.jar" />
                <AndroidLibrary Include="Jars\internal.jar" Bind="false" />
              </ItemGroup>
            </Project>
            """);
        var generated = workspace.Write("Binding/obj/Debug/net10.0-android/generated/src/Com.Example.Bridge.cs", "namespace Com.Example { public class Bridge { } }");

        var model = ProjectFileReader.Read(project);
        CollectionAssert.Contains(model.Sources.ToList(), generated);

        // Without a library to bind, what is under generated/src is not compiled
        workspace.Write("Binding/Binding.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0-android</TargetFramework></PropertyGroup>
              <ItemGroup><AndroidLibrary Include="Jars\internal.jar" Bind="false" /></ItemGroup>
            </Project>
            """);
        CollectionAssert.DoesNotContain(ProjectFileReader.Read(project).Sources.ToList(), generated);
    }
}
