using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.Workspace;

/// <summary>
/// Edits a copy of this repository's solution and checks that an update gives the same graph as a full load,
/// while binding only the files it has to.
/// </summary>
[TestClass]
public sealed class IncrementalUpdateTests
{
    [TestMethod]
    public void Update_MatchesFullLoad_AfterEdits()
    {
        using var workspace = new TempWorkspace();
        CopyRepository(workspace.Root);
        var solution = Path.Combine(workspace.Root, "PaspanCodeGraphMcp.slnx");
        var symbolIndex = Path.Combine(workspace.Root, "src", "PaspanCodeGraph.Core", "SymbolIndex.cs");
        var loadProblem = Path.Combine(workspace.Root, "src", "PaspanCodeGraph.Workspace", "LoadProblem.cs");
        var dotnetLocator = Path.Combine(workspace.Root, "src", "PaspanCodeGraph.Workspace", "DotnetLocator.cs");

        var snapshot = WorkspaceLoader.Load(solution);
        var files = snapshot.Documents.Count;

        var unchanged = WorkspaceLoader.Update(snapshot);
        Assert.AreEqual(SnapshotKind.Unchanged, unchanged.Kind);
        Assert.AreSame(snapshot.Index, unchanged.Index);

        // A body edit binds only the edited file
        snapshot = Step(snapshot, solution, "a body edit", () => Replace(symbolIndex, "var parts = query.Replace(", "var parts = (query + \"\").Replace("));
        Assert.AreEqual(1, snapshot.ParsedFiles);
        Assert.AreEqual(1, snapshot.BoundFiles);

        // A renamed member binds the files that mention either name
        snapshot = Step(snapshot, solution, "a renamed member", () => Replace(symbolIndex, "public int ReferenceCount =>", "public int ReferenceTotal =>"));
        Assert.IsTrue(snapshot.BoundFiles < files / 2, $"{snapshot.BoundFiles} of {files} files bound again");

        // A new parameter changes the member's id
        snapshot = Step(snapshot, solution, "a new parameter", () => Replace(loadProblem, "Warning(string message, string? file = null)", "Warning(string message, string? file = null, int line = 0)"));

        // A new type that hides one of a referenced assembly
        snapshot = Step(snapshot, solution, "a new file", () => File.WriteAllText(
            Path.Combine(workspace.Root, "src", "PaspanCodeGraph.Workspace", "Added.cs"),
            "namespace PaspanCodeGraph.Workspace;\n\npublic sealed class StringComparer { public static StringComparer Ordinal => new(); }\n\ninternal static class AddedExtensions\n{\n    public static int Twice(this LoadProblem problem) => 2;\n}\n"));
        Assert.AreEqual(files + 1, snapshot.Documents.Count);

        // A removed file
        snapshot = Step(snapshot, solution, "a removed file", () => File.Delete(dotnetLocator));
        Assert.AreEqual(files, snapshot.Documents.Count);

        // A changed base list binds everything again
        snapshot = Step(snapshot, solution, "a new base type", () => Replace(symbolIndex, "public sealed class SymbolIndexBuilder", "public sealed class SymbolIndexBuilder : IDisposable"));
        Assert.AreEqual(snapshot.Documents.Count(d => d.Value.Unit != null), snapshot.BoundFiles);

        // A project option binds everything again
        var project = Path.Combine(workspace.Root, "src", "PaspanCodeGraph.Core", "PaspanCodeGraph.Core.csproj");
        snapshot = Step(snapshot, solution, "a project option", () => Replace(project, "</Project>", "  <PropertyGroup><DefineConstants>$(DefineConstants);EXTRA</DefineConstants></PropertyGroup>\n</Project>"));
        Assert.AreEqual(SnapshotKind.Incremental, snapshot.Kind);
    }

    [TestMethod]
    public void Cache_GivesTheSameGraph()
    {
        using var workspace = new TempWorkspace();
        CopyRepository(workspace.Root);
        var solution = Path.Combine(workspace.Root, "PaspanCodeGraphMcp.slnx");
        var cache = GraphCache.DefaultPath(solution);

        var first = WorkspaceLoader.LoadCached(solution, cache, out var status);
        StringAssert.StartsWith(status, "loaded in full");
        Assert.IsTrue(File.Exists(cache));

        var cached = WorkspaceLoader.LoadCached(solution, cache, out status);
        Assert.AreEqual("read from the cache", status);
        Assert.AreEqual(SnapshotKind.Cache, cached.Kind);
        Assert.AreEqual(first.Metadata.TypeCount, cached.Metadata.TypeCount);
        SnapshotComparison.AssertSame(first, cached, "the graph read from the cache");
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "incremental-report.txt"), $"cache: {new FileInfo(cache).Length / 1024} KB, read in {cached.Elapsed.TotalMilliseconds:0} ms (full {first.Elapsed.TotalMilliseconds:0} ms)\n");

        // An update from the cache parses every file, since it keeps no trees, but binds only what changed
        var loadProblem = Path.Combine(workspace.Root, "src", "PaspanCodeGraph.Workspace", "LoadProblem.cs");
        Replace(loadProblem, "Warning(string message, string? file = null)", "Warning(string message, string? file = null, int line = 0)");
        var updated = WorkspaceLoader.LoadCached(solution, cache, out status);
        Assert.AreEqual(SnapshotKind.Incremental, updated.Kind, status);
        Assert.IsTrue(updated.BoundFiles < updated.Documents.Count / 2, status);
        SnapshotComparison.AssertSame(WorkspaceLoader.Load(solution), updated, "the graph updated from the cache");

        // The update was written back, and a damaged cache is loaded in full
        Assert.AreEqual(SnapshotKind.Cache, WorkspaceLoader.LoadCached(solution, cache, out _).Kind);
        File.WriteAllBytes(cache, [1, 2, 3]);
        var reloaded = WorkspaceLoader.LoadCached(solution, cache, out status);
        Assert.AreEqual(SnapshotKind.Full, reloaded.Kind, status);
        SnapshotComparison.AssertSame(updated, reloaded, "the graph loaded again");
    }

    private static WorkspaceSnapshot Step(WorkspaceSnapshot previous, string solution, string edit, Action change)
    {
        change();
        var updated = WorkspaceLoader.Update(previous);
        var full = WorkspaceLoader.Load(solution);
        SnapshotComparison.AssertSame(full, updated, edit);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "incremental-report.txt"), $"{edit}: parsed {updated.ParsedFiles}, bound {updated.BoundFiles} of {updated.Documents.Count} in {updated.Elapsed.TotalMilliseconds:0} ms (full {full.Elapsed.TotalMilliseconds:0} ms), {SnapshotComparison.Describe(full).Count} lines\n");
        Assert.AreEqual(SnapshotKind.Incremental, updated.Kind, edit);
        return updated;
    }

    private static void Replace(string path, string old, string replacement)
    {
        var text = File.ReadAllText(path);
        Assert.IsTrue(text.Contains(old, StringComparison.Ordinal), $"{path} contains {old}");
        File.WriteAllText(path, text.Replace(old, replacement, StringComparison.Ordinal));
    }

    /// <summary>The solution's sources and project files, with the restore output but without binaries or the parser's test corpus.</summary>
    internal static void CopyRepository(string target)
    {
        var root = TestPaths.RepositoryRoot;
        File.Copy(Path.Combine(root, "PaspanCodeGraphMcp.slnx"), Path.Combine(target, "PaspanCodeGraphMcp.slnx"));
        foreach (var directory in new[] { "src", "tests", Path.Combine("external", "PaspanParsers") })
        {
            Copy(Path.Combine(root, directory), Path.Combine(target, directory));
        }
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            var inObj = Path.GetFileName(source) == "obj";
            if (!inObj || name == "project.assets.json")
            {
                File.Copy(file, Path.Combine(target, name));
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name is "bin" or ".git" or "Corpus" or "PaspanParsers.Tests" || (Path.GetFileName(source) == "obj"))
            {
                continue;
            }

            Copy(directory, Path.Combine(target, name));
        }
    }
}
