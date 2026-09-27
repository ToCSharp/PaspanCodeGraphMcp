using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using PaspanCodeGraphMcp;
using PaspanCodeGraphMcp.Tools;

var options = ServerOptions.Parse(args, Environment.GetEnvironmentVariable);

// The arguments are parsed by ServerOptions; keep them away from the configuration binder
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });

// stdout is the MCP transport: every log line must go to stderr
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("PASPAN_LOG_LEVEL"), true, out var level) ? level : LogLevel.Information);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WorkspaceHost>();
builder.Services.AddHostedService<StartupLoader>();

builder.Services
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "paspan-code-graph-mcp", Version = "0.1.0" })
    .WithStdioServerTransport()

    // The SDK hides exception text behind "An error occurred invoking '<tool>'"; the agent needs the reason
    // (unknown id, file not in the workspace) to recover, so return it as an error result
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
    {
        try
        {
            return await next(context, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var root = e is AggregateException aggregate ? aggregate.GetBaseException() : e;
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"{root.GetType().Name}: {root.Message}" }],
            };
        }
    }))
    .WithTools<WorkspaceTools>()
    .WithTools<NavigationTools>();

await builder.Build().RunAsync();

/// <summary>Starts loading the workspace given on the command line without holding up MCP initialization.</summary>
internal sealed class StartupLoader(WorkspaceHost host, ServerOptions options, ILogger<StartupLoader> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.WorkspacePath is null)
        {
            log.LogInformation("Started without --workspace; call workspace_load to load a solution.");
        }
        else
        {
            host.StartBackgroundLoad(options.WorkspacePath, stoppingToken);
        }

        return Task.CompletedTask;
    }
}
