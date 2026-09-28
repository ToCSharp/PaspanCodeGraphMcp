using PaspanCodeGraph.Cpp;
using PaspanCodeGraph.Tests.Workspace;
using PaspanCodeGraph.Workspace;
using PaspanParsers.Cpp;

namespace PaspanCodeGraph.Tests.Cpp;

/// <summary>Loading C++ workspaces: folders, compilation databases and .vcxproj projects; updates and the graph cache.</summary>
[TestClass]
public sealed class CppWorkspaceTests
{
    private static string Fixture => CppOracleTests.FixtureDirectory;

    [TestMethod]
    public void Folder_IsOneCppProject()
    {
        var discovery = SolutionDiscovery.Discover(Fixture);
        Assert.AreEqual(Fixture, discovery.RootPath);
        CollectionAssert.AreEqual(new[] { Fixture }, discovery.Projects.ToArray());

        var snapshot = WorkspaceLoader.Load(Fixture, readReferences: false);
        var project = snapshot.Projects.Single();
        Assert.AreEqual(SourceLanguage.Cpp, project.Language);
        Assert.AreEqual("Fixture", project.Name);
        Assert.AreEqual(8, snapshot.Documents.Count);
        Assert.IsTrue(snapshot.Documents.Values.All(d => d.CppUnit != null && d.Failure == null), string.Join("\n", snapshot.Documents.Values.Select(d => d.Failure).OfType<string>()));
        Assert.AreEqual(Fixture, snapshot.Directory);
        Assert.IsNotNull(snapshot.FindDocument("src/main.cpp"));
        Assert.IsNotNull(snapshot.FindDocument("circle.h"));
    }

    [TestMethod]
    public void Symbols_OfHeadersAndSources_AreMerged()
    {
        var index = WorkspaceLoader.Load(Fixture, readReferences: false).Index;

        // Declared in the header, defined in the source file: one symbol, the definition first
        var area = index.Get("M:geo::Circle::area()const");
        Assert.IsNotNull(area);
        Assert.AreEqual(2, area.Declarations.Count);
        StringAssert.EndsWith(area.Declarations[0].File, "circle.cpp");
        Assert.AreEqual("M:geo::Shape::area()const", area.Overrides?.Id);
        CollectionAssert.Contains(index.Get("T:geo::Shape").DerivedTypes.Select(d => d.Id).ToArray(), "T:geo::Circle");
        Assert.AreEqual("The area of the shape.", index.Get("M:geo::Shape::area()const").Documentation);
        Assert.AreEqual("double geo::Circle::area() const", area.Signature);
        CollectionAssert.AreEquivalent(new[] { "override", "const" }, area.Modifiers);
        Assert.AreEqual("public", area.Accessibility);

        // Lookup with '::' and dotted names
        Assert.AreEqual("T:geo::Rect::Corner", index.Search("geo::Rect::Corner", exact: true)[0].Id);
        Assert.AreEqual("T:geo::Rect::Corner", index.Search("Rect.Corner", exact: true)[0].Id);

        // Internal linkage: the file is part of the id
        Assert.IsNotNull(index.Get("M:core::twice(int)@registry.cpp"));
        Assert.AreEqual(SymbolKind.Macro, index.Get("D:CORE_MAX").Kind);

        // Constructors are called where objects are made
        var constructed = index.ReferencesTo("M:geo::Circle::Circle(double)").Select(r => Path.GetFileName(r.File) + ":" + r.Line).ToList();
        CollectionAssert.IsSubsetOf(new[] { "main.cpp:41", "circle.cpp:11", "circle.cpp:28" }, constructed);
        Assert.AreEqual(1, index.ReferencesTo("M:geo::Square::Square(double)").Count);
        Assert.IsTrue(index.ReferencesTo("F:geo::Circle::radius_").Any(r => r.Line == 9 && Path.GetFileName(r.File) == "circle.cpp"), "the ctor-initializer names the field");
        Assert.IsTrue(index.ReferencesTo("T:geo::Shape").Count > 10);
        Assert.IsTrue(index.ReferencesTo("D:CORE_MAX").Count == 2);
    }

    [TestMethod]
    public void CompilationDatabase_GivesTheOptionsOfEachFile()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.Write("src/main.cpp", "#include \"lib.h\"\n#ifdef FEATURE\nint feature() { return lib(); }\n#endif\n");
        workspace.Write("include/lib.h", "#pragma once\nint lib();\n");
        var other = workspace.Write("src/other.cpp", "#ifdef FEATURE\nint unexpected();\n#endif\nint other();\n");
        var database = workspace.Write("build/compile_commands.json", $$"""
            [
              { "directory": "{{Esc(workspace.PathOf("build"))}}", "file": "{{Esc(source)}}", "arguments": ["clang++", "-DFEATURE", "-I../include", "-std=c++20", "-c", "{{Esc(source)}}"] },
              { "directory": "{{Esc(workspace.PathOf("build"))}}", "file": "{{Esc(other)}}", "command": "g++ -std=gnu++17 -c {{Esc(other)}}" }
            ]
            """);
        workspace.Write("build/CMakeCache.txt", "");
        workspace.Write("CMakeLists.txt", "project(x)\n");

        var discovery = SolutionDiscovery.Discover(workspace.Root);
        CollectionAssert.AreEqual(new[] { database }, discovery.Projects.ToArray());
        Assert.AreEqual(workspace.Root, SolutionDiscovery.WorkspaceDirectory(database));

        var snapshot = WorkspaceLoader.Load(workspace.Root, readReferences: false);
        var project = snapshot.Projects.Single();
        Assert.AreEqual(3, project.Sources.Count, "the header next to the sources is added");
        var options = new[] { source, other }.Select(f => project.FileOptions.GetValueOrDefault(f) ?? new CppFileOptions(project.Macros, project.IncludeDirectories, project.CppLanguageVersion)).ToList();
        Assert.AreEqual("1", options[0].Macros["FEATURE"]);
        Assert.AreEqual(CppLanguageVersion.Cpp20, options[0].LanguageVersion);
        CollectionAssert.Contains(options[0].IncludeDirectories.ToArray(), workspace.PathOf("include"));
        Assert.IsFalse(options[1].Macros.ContainsKey("FEATURE"));
        Assert.AreEqual(CppLanguageVersion.Cpp17, options[1].LanguageVersion);

        Assert.IsNotNull(snapshot.Index.Get("M:feature()"), "FEATURE is defined for main.cpp");
        Assert.IsNull(snapshot.Index.Get("M:unexpected()"), "but not for other.cpp");
        Assert.AreEqual(1, snapshot.Index.ReferencesTo("M:lib()").Count);
    }

    private static string Esc(string path) => path.Replace("\\", "\\\\");

    [TestMethod]
    public void Vcxproj_ReadsItemsAndTheConfigurationsDefinitions()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("App/main.cpp", "#include \"app.h\"\n#if defined(APP_DEBUG) && defined(_WIN64)\nint debug_only();\n#endif\nint main() { return run(); }\n");
        workspace.Write("App/app.h", "#pragma once\nint run();\n");
        var project = workspace.Write("App/App.vcxproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
              <ItemDefinitionGroup Condition="'$(Configuration)|$(Platform)'=='Debug|x64'">
                <ClCompile>
                  <PreprocessorDefinitions>APP_DEBUG;LEVEL=2;%(PreprocessorDefinitions)</PreprocessorDefinitions>
                  <AdditionalIncludeDirectories>$(ProjectDir)include;%(AdditionalIncludeDirectories)</AdditionalIncludeDirectories>
                  <LanguageStandard>stdcpp17</LanguageStandard>
                </ClCompile>
              </ItemDefinitionGroup>
              <ItemGroup>
                <ClCompile Include="main.cpp" />
                <ClInclude Include="app.h" />
              </ItemGroup>
            </Project>
            """);
        workspace.Write("App.sln", "Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}\") = \"App\", \"App\\App.vcxproj\", \"{00000000-0000-0000-0000-000000000001}\"\nEndProject\n");

        var model = ProjectFileReader.Read(project);
        Assert.AreEqual(SourceLanguage.Cpp, model.Language);
        Assert.AreEqual(2, model.Sources.Count);
        Assert.AreEqual("2", model.Macros["LEVEL"]);
        Assert.AreEqual("1", model.Macros["_WIN32"]);
        Assert.AreEqual(CppLanguageVersion.Cpp17, model.CppLanguageVersion);
        CollectionAssert.Contains(model.IncludeDirectories.ToArray(), workspace.PathOf("App/include"));

        var snapshot = WorkspaceLoader.Load(workspace.PathOf("App.sln"), readReferences: false);
        Assert.AreEqual("App", snapshot.Projects.Single().Name);
        Assert.IsNotNull(snapshot.Index.Get("M:debug_only()"));
        Assert.AreEqual(1, snapshot.Index.ReferencesTo("M:run()").Count);
        Assert.IsNull(WorkspaceLoader.Load(workspace.PathOf("App.sln"), configuration: "Release", readReferences: false).Index.Get("M:debug_only()"));
    }

    [TestMethod]
    public void DecorationMacros_AreBlankedOut()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("api.h", """
            #pragma once
            #ifdef _WIN32
            #define API __declspec(dllexport)
            #else
            #define API __attribute__((visibility("default")))
            #endif
            #define INLINE_API API inline
            #define VALUE 42
            namespace lib {
            class API Widget { public: API int size() const; };
            INLINE_API int twice(int x) { return 2 * x + VALUE; }
            }
            """);
        workspace.Write("api.cpp", "#include \"api.h\"\nnamespace lib { int Widget::size() const { return twice(1); } }\n");

        var snapshot = WorkspaceLoader.Load(workspace.Root, readReferences: false);
        Assert.IsTrue(snapshot.Documents.Values.All(d => d.Failure == null), string.Join("\n", snapshot.Documents.Values.Select(d => d.Failure).OfType<string>()));
        var size = snapshot.Index.Get("M:lib::Widget::size()const");
        Assert.AreEqual(2, size.Declarations.Count);
        Assert.AreEqual(1, snapshot.Index.ReferencesTo("M:lib::twice(int)").Count);
        Assert.AreEqual(1, snapshot.Index.ReferencesTo("D:VALUE").Count, "a macro that is not a decoration stays");
        CollectionAssert.AreEquivalent(new[] { "API", "INLINE_API" }, CppMacroPlan.From(CppMacroPlan.ScanDefinitions(File.ReadAllBytes(workspace.PathOf("api.h")))).Blankable.ToArray());
    }

    [TestMethod]
    public void SyntaxMacros_AreExpanded_WithPositionsInTheFile()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("config.h", """
            #pragma once
            #if defined(__has_include)
            #  define LIB_HAS_INCLUDE(x) __has_include(x)
            #else
            #  define LIB_HAS_INCLUDE(x) 0
            #endif
            #define LIB_BEGIN namespace lib { inline namespace v1 {
            #define LIB_END } }
            #define LIB_ENABLE_IF(...) typename enable_if<(__VA_ARGS__), int>::type = 0
            #ifdef LIB_OPTIONAL
            #  define LIB_EXTRA , int extra
            #else
            #  define LIB_EXTRA
            #endif
            """);
        workspace.Write("lib.h", """
            #pragma once
            #include "config.h"
            LIB_BEGIN
            template <bool B, typename T> struct enable_if {};
            template <typename T> struct enable_if<true, T> { using type = T; };
            class Widget {
            public:
                template <typename T, LIB_ENABLE_IF(sizeof(T) > 1)> int take(T value LIB_EXTRA);
                int size() const;
            };
            #if LIB_HAS_INCLUDE("config.h")
            int configured();
            #endif
            LIB_END
            """);
        workspace.Write("lib.cpp", """
            #include "lib.h"
            LIB_BEGIN
            int Widget::size() const { return configured(); }
            LIB_END
            """);

        var snapshot = WorkspaceLoader.Load(workspace.Root, readReferences: false);
        Assert.IsTrue(snapshot.Documents.Values.All(d => d.Failure == null), string.Join("\n", snapshot.Documents.Values.Select(d => d.Failure).OfType<string>()));
        var index = snapshot.Index;

        // Declared in the namespaces the macros open, where the file has them
        var widget = index.Get("T:lib::v1::Widget");
        Assert.AreEqual(6, widget.Declarations.Single().Line);
        Assert.AreEqual(7, widget.Declarations.Single().Column);
        var size = index.Get("M:lib::v1::Widget::size()const");
        Assert.AreEqual(2, size.Declarations.Count);
        Assert.AreEqual((3, 13), (size.Declarations[0].Line, size.Declarations[0].Column));
        Assert.IsNotNull(index.Get("M:lib::v1::Widget::take``2(``0)"), string.Join(", ", index.Symbols.Select(x => x.Id)));
        Assert.AreEqual("T:lib::v1::Widget", index.Search("lib::Widget").Single().Id, "an inline namespace can be left out");

        // A macro of an included header is known in the file's conditions
        var configured = index.Get("M:lib::v1::configured()");
        Assert.IsNotNull(configured, "LIB_HAS_INCLUDE comes from config.h");
        var call = index.ReferencesTo(configured.Id).Single();
        Assert.AreEqual((3, 35), (call.Line, call.Column));
    }

    [TestMethod]
    public void Update_MatchesFullLoad_AfterEdits()
    {
        using var workspace = new TempWorkspace();
        Copy(Fixture, workspace.Root);
        var snapshot = WorkspaceLoader.Load(workspace.Root, readReferences: false);
        var files = snapshot.Documents.Count;
        Assert.AreEqual(SnapshotKind.Unchanged, WorkspaceLoader.Update(snapshot).Kind);

        // A body edit parses and binds only the edited file
        snapshot = Step(snapshot, workspace.Root, "a body edit", () => Replace(workspace.PathOf("src/circle.cpp"), "return pi * radius_ * radius_;", "return radius_ * pi * radius_;"));
        Assert.AreEqual(1, snapshot.ParsedFiles);
        Assert.AreEqual(1, snapshot.BoundFiles);

        // A renamed member binds the files that mention either name
        snapshot = Step(snapshot, workspace.Root, "a renamed member", () =>
        {
            Replace(workspace.PathOf("include/geo/circle.h"), "double radius() const", "double size() const");
            Replace(workspace.PathOf("src/circle.cpp"), "* radius();", "* size();");
        });
        Assert.IsTrue(snapshot.BoundFiles < files, $"{snapshot.BoundFiles} of {files} files bound again");

        // A new class changes the names the files are parsed with: every file is parsed again
        snapshot = Step(snapshot, workspace.Root, "a new class", () => Replace(workspace.PathOf("include/geo/point.h"), "/// The distance", "struct Size { double width, height; };\n\n/// The distance"));
        Assert.AreEqual(files, snapshot.ParsedFiles);

        // A macro that becomes a decoration is blanked out everywhere
        snapshot = Step(snapshot, workspace.Root, "a changed macro", () => Replace(workspace.PathOf("include/core/registry.h"), "#define CORE_API", "#define CORE_API __attribute__((used))"));

        // A new parameter changes the function's id
        snapshot = Step(snapshot, workspace.Root, "a new parameter", () =>
        {
            Replace(workspace.PathOf("include/geo/point.h"), "double distance(const Point& a, const Point& b);", "double distance(const Point& a, const Point& b, int power = 2);");
            Replace(workspace.PathOf("src/shape.cpp"), "double distance(const Point& a, const Point& b)", "double distance(const Point& a, const Point& b, int power)");
        });

        // A changed base binds everything again
        snapshot = Step(snapshot, workspace.Root, "a new base", () => Replace(workspace.PathOf("include/geo/circle.h"), "class Square : public Rect {", "class Square : public Rect, public Visitor {"));

        // A new file and a removed file
        snapshot = Step(snapshot, workspace.Root, "a new file", () => File.WriteAllText(workspace.PathOf("src/extra.cpp"), "#include \"geo/circle.h\"\nnamespace geo { double extra(const Circle& c) { return c.area(); } }\n"));
        Assert.AreEqual(files + 1, snapshot.Documents.Count);
        snapshot = Step(snapshot, workspace.Root, "a removed file", () => File.Delete(workspace.PathOf("src/registry.cpp")));
        Assert.AreEqual(files, snapshot.Documents.Count);

        // A file that does not parse any more
        Step(snapshot, workspace.Root, "a syntax error", () => Replace(workspace.PathOf("src/main.cpp"), "int main()", "int main( {"));
    }

    [TestMethod]
    public void Cache_GivesTheSameGraph()
    {
        using var workspace = new TempWorkspace();
        Copy(Fixture, workspace.Root);
        var cache = GraphCache.DefaultPath(workspace.Root);
        Assert.AreEqual(Path.Combine(workspace.Root, ".paspan", "graph.bin"), cache);

        var first = WorkspaceLoader.LoadCached(workspace.Root, cache, out var status, readReferences: false);
        StringAssert.StartsWith(status, "loaded in full");
        var cached = WorkspaceLoader.LoadCached(workspace.Root, cache, out status, readReferences: false);
        Assert.AreEqual("read from the cache", status);
        SnapshotComparison.AssertSame(first, cached, "the graph read from the cache");

        Replace(workspace.PathOf("src/main.cpp"), "area += box.get().x;", "area += box.get().y;");
        var updated = WorkspaceLoader.LoadCached(workspace.Root, cache, out status, readReferences: false);
        Assert.AreEqual(SnapshotKind.Incremental, updated.Kind, status);
        Assert.AreEqual(1, updated.BoundFiles, status);
        SnapshotComparison.AssertSame(WorkspaceLoader.Load(workspace.Root, readReferences: false), updated, "the graph updated from the cache");
    }

    private static WorkspaceSnapshot Step(WorkspaceSnapshot previous, string root, string edit, Action change)
    {
        change();
        var updated = WorkspaceLoader.Update(previous);
        var full = WorkspaceLoader.Load(root, readReferences: false);
        SnapshotComparison.AssertSame(full, updated, edit);
        Assert.AreEqual(SnapshotKind.Incremental, updated.Kind, edit);
        return updated;
    }

    private static void Replace(string path, string old, string replacement)
    {
        var text = File.ReadAllText(path);
        Assert.IsTrue(text.Contains(old, StringComparison.Ordinal), $"{path} contains {old}");
        File.WriteAllText(path, text.Replace(old, replacement, StringComparison.Ordinal));
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target);
        }
    }
}
