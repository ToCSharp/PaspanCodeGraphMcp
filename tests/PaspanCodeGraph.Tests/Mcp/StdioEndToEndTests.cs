using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PaspanCodeGraph.Tests.Mcp;

/// <summary>Starts the server as a process and talks MCP to it over stdio, as an agent would.</summary>
[TestClass]
public sealed class StdioEndToEndTests
{
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Server_LoadsWorkspaceFromArguments_AndAnswersToolCalls()
    {
        using var workspace = SampleWorkspace.Create();
        var server = Path.Combine(AppContext.BaseDirectory, "paspan-code-graph-mcp.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "paspan-code-graph-mcp",
            Command = "dotnet",
            Arguments = [server, "--workspace", workspace.PathOf("Sample.slnx")],
        });

        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        CollectionAssert.IsSubsetOf(
            new[] { "workspace_load", "workspace_status", "diagnostics", "find_symbol", "symbol_info", "go_to_definition", "type_members", "file_outline" },
            tools.Select(t => t.Name).ToArray());

        // The first call waits for the load started from the command line
        var found = await client.CallToolAsync("find_symbol", new Dictionary<string, object> { ["query"] = "TryParse" });
        Assert.IsFalse(found.IsError ?? false, Text(found));
        using (var json = JsonDocument.Parse(Text(found)))
        {
            var item = json.RootElement.GetProperty("items")[0];
            Assert.AreEqual("M:Lib.Parser.TryParse(System.String,System.Int32@)", item.GetProperty("id").GetString());
            Assert.AreEqual(17, item.GetProperty("location").GetProperty("line").GetInt32());
        }

        // Errors come back as error results that name the problem
        var failed = await client.CallToolAsync("symbol_info", new Dictionary<string, object> { ["symbol"] = "T:Lib.Missing" });
        Assert.IsTrue(failed.IsError);
        StringAssert.Contains(Text(failed), "T:Lib.Missing");
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
