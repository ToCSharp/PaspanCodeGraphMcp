using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PaspanCodeGraph.Workspace;
using PaspanCodeGraphMcp;
using PaspanCodeGraphMcp.Tools;

namespace PaspanCodeGraph.Tests.Mcp;

[TestClass]
public sealed class WatchTests
{
    [TestMethod]
    public async Task ChangedFiles_UpdateTheGraph()
    {
        using var workspace = SampleWorkspace.Create();
        using var host = new WorkspaceHost(new ServerOptions { WatchDelay = TimeSpan.FromMilliseconds(50) }, NullLogger<WorkspaceHost>.Instance);
        var solution = workspace.PathOf("Sample.slnx");
        await host.LoadAsync(solution, "Debug", "AnyCPU", CancellationToken.None);
        Assert.IsTrue(WorkspaceTools.Status(host).Watching);
        StringAssert.StartsWith(host.CacheStatus, "loaded in full");

        // A new file with a caller of an existing method
        workspace.Write("App/Added.cs", """
            namespace App
            {
                public static class Added
                {
                    public static int ParseTwice(string text) => Lib.Parser.Parse(text) * 2;
                }
            }
            """);
        var snapshot = await WaitFor(host, s => s.Index.Search("ParseTwice", exact: true).Count == 1);
        Assert.AreEqual(SnapshotKind.Incremental, snapshot.Kind);
        var callers = (FindCallersResult)await CallGraphTools.FindCallers(host, "M:Lib.Parser.Parse(System.String)");
        CollectionAssert.Contains(callers.CallSites.Select(c => c.Caller?.Name).ToArray(), "ParseTwice");

        // A removed file
        File.Delete(workspace.PathOf("App/Added.cs"));
        await WaitFor(host, s => s.Index.Search("ParseTwice", exact: true).Count == 0);

        // A tool call made while a change waits for the quiet time sees the change
        using var slow = new WorkspaceHost(new ServerOptions { WatchDelay = TimeSpan.FromSeconds(30) }, NullLogger<WorkspaceHost>.Instance);
        await slow.LoadAsync(solution, "Debug", "AnyCPU", CancellationToken.None);
        Assert.AreEqual("read from the cache", slow.CacheStatus);
        workspace.Write("Lib/Extra.cs", "namespace Lib { public class Extra { } }");
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10) && (await NavigationTools.FindSymbol(slow, "Extra", exact: true)).Count == 0)
        {
            await Task.Delay(20);
        }

        Assert.AreEqual(1, (await NavigationTools.FindSymbol(slow, "Extra", exact: true)).Count);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "the update ran before the quiet time was over");
    }

    private static async Task<WorkspaceSnapshot> WaitFor(WorkspaceHost host, Func<WorkspaceSnapshot, bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
        {
            var snapshot = await host.RequireSnapshotAsync(CancellationToken.None);
            if (condition(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(20);
        }

        Assert.Fail("The graph was not updated after the change.");
        return null;
    }
}
