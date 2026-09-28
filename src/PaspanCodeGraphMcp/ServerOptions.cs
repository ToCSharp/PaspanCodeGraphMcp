using System.Reflection;

namespace PaspanCodeGraphMcp;

/// <summary>Process-wide settings, from command-line arguments and then <c>PASPAN_*</c> environment variables.</summary>
public sealed class ServerOptions
{
    /// <summary>The .sln, .slnx, .csproj, .vcxproj, compile_commands.json or directory to load at startup.</summary>
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

    /// <summary>Print <see cref="Usage"/> instead of serving.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Print <see cref="Version"/> instead of serving.</summary>
    public bool ShowVersion { get; init; }

    /// <summary>The package version, without the commit a build may append.</summary>
    public static string Version { get; } =
        typeof(ServerOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ServerOptions).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public const string Usage = """
        paspan-code-graph-mcp: a read-only MCP server (stdio) for C# and C++ code analysis by AI agents.

        Usage: paspan-code-graph-mcp [options] [workspace]

          workspace, -w, --workspace <path>  .sln, .slnx, .csproj, .vcxproj, compile_commands.json, or a directory
                                             holding one (or C++ sources)                      (PASPAN_WORKSPACE)
          -c, --configuration <name>         build configuration, default Debug                (PASPAN_CONFIGURATION)
          -p, --platform <name>              build platform, default AnyCPU                    (PASPAN_PLATFORM)
          --no-watch                         do not update the graph when files change         (PASPAN_WATCH=0)
          --no-cache                         do not keep the graph in .paspan/graph.bin        (PASPAN_CACHE=0)
          --cache <file>                     where to keep the graph                           (PASPAN_CACHE_PATH)
          --version                          print the version
          -h, --help                         print this text

        Logs go to stderr (PASPAN_LOG_LEVEL, default Information); stdout carries MCP messages.
        """;

    public static ServerOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        var help = false;
        var version = false;
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
                case "--help" or "-h" or "-?":
                    help = true;
                    break;
                case "--version":
                    version = true;
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
            ShowHelp = help,
            ShowVersion = version,
        };
    }

    private static bool IsOff(string? value) => value is "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
}
