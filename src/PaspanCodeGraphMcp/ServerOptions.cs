namespace PaspanCodeGraphMcp;

/// <summary>Process-wide settings, from command-line arguments and then <c>PASPAN_*</c> environment variables.</summary>
public sealed class ServerOptions
{
    /// <summary>The .sln, .slnx, .csproj or directory to load at startup.</summary>
    public string? WorkspacePath { get; init; }

    /// <summary>The MSBuild Configuration that project conditions and DEBUG/RELEASE depend on.</summary>
    public string Configuration { get; init; } = "Debug";

    public string Platform { get; init; } = "AnyCPU";

    /// <summary>Watch the workspace's files and update the graph when they change.</summary>
    public bool Watch { get; init; } = true;

    /// <summary>How long changes must stop before an update runs.</summary>
    public TimeSpan WatchDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Keep the graph on disk and start from it.</summary>
    public bool Cache { get; init; } = true;

    /// <summary>Where the graph is kept; by default <c>.paspan/graph.bin</c> next to the solution.</summary>
    public string? CachePath { get; init; }

    public static ServerOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        var workspace = environment("PASPAN_WORKSPACE");
        var configuration = environment("PASPAN_CONFIGURATION") ?? "Debug";
        var platform = environment("PASPAN_PLATFORM") ?? "AnyCPU";
        var watch = !IsOff(environment("PASPAN_WATCH"));
        var cache = !IsOff(environment("PASPAN_CACHE"));
        var cachePath = environment("PASPAN_CACHE_PATH");

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (arg)
            {
                case "--workspace" or "-w":
                    workspace = Next();
                    break;
                case "--configuration" or "-c":
                    configuration = Next() ?? configuration;
                    break;
                case "--platform" or "-p":
                    platform = Next() ?? platform;
                    break;
                case "--no-watch":
                    watch = false;
                    break;
                case "--no-cache":
                    cache = false;
                    break;
                case "--cache":
                    cachePath = Next() ?? cachePath;
                    break;
                default:
                    if (!arg.StartsWith('-'))
                    {
                        workspace = arg;
                    }

                    break;
            }
        }

        return new ServerOptions
        {
            WorkspacePath = string.IsNullOrWhiteSpace(workspace) ? null : Path.GetFullPath(workspace),
            Configuration = configuration,
            Platform = platform,
            Watch = watch,
            Cache = cache,
            CachePath = string.IsNullOrWhiteSpace(cachePath) ? null : Path.GetFullPath(cachePath),
        };
    }

    private static bool IsOff(string? value) => value is "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
}
