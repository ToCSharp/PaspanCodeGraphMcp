using System.Runtime.InteropServices;

namespace PaspanCodeGraph.Workspace;

/// <summary>Finds the dotnet root that holds <c>packs/</c>: <c>DOTNET_ROOT</c>, then <c>dotnet</c> on the PATH (symbolic links resolved), then the usual install folders.</summary>
public static class DotnetLocator
{
    private static readonly Lazy<string?> s_root = new(Locate);

    /// <summary>The dotnet root; null when no SDK is installed.</summary>
    public static string? DotnetRoot => s_root.Value;

    public static string? PacksDirectory => DotnetRoot is { } root ? Path.Combine(root, "packs") : null;

    private static string? Locate()
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, exeName)))
        {
            return root;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, exeName);
            if (!File.Exists(candidate))
            {
                continue;
            }

            // Package managers link /usr/bin/dotnet to /usr/lib/dotnet/dotnet
            try
            {
                var target = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
                return Path.GetDirectoryName(target);
            }
            catch (IOException)
            {
                return directory;
            }
        }

        foreach (var fallback in new[] { @"C:\Program Files\dotnet", "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/local/share/dotnet", "/usr/local/lib/dotnet" })
        {
            if (File.Exists(Path.Combine(fallback, exeName)))
            {
                return fallback;
            }
        }

        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        return File.Exists(Path.Combine(home, exeName)) ? home : null;
    }
}
