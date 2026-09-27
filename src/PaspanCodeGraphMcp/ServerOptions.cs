namespace PaspanCodeGraphMcp;

/// <summary>Process-wide settings, from command-line arguments and then <c>PASPAN_*</c> environment variables.</summary>
public sealed class ServerOptions
{
    /// <summary>The .sln, .slnx, .csproj or directory to load at startup.</summary>
    public string? WorkspacePath { get; init; }

    /// <summary>The MSBuild Configuration that project conditions and DEBUG/RELEASE depend on.</summary>
    public string Configuration { get; init; } = "Debug";

    public string Platform { get; init; } = "AnyCPU";

    public static ServerOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        var workspace = environment("PASPAN_WORKSPACE");
        var configuration = environment("PASPAN_CONFIGURATION") ?? "Debug";
        var platform = environment("PASPAN_PLATFORM") ?? "AnyCPU";

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
        };
    }
}
