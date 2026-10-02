using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaspanCodeGraph.Workspace;

/// <summary>
/// Finds the assemblies a project compiles against, without MSBuild: the framework reference assemblies of the
/// targeting packs under <c>dotnet/packs</c>, the package assemblies <c>project.assets.json</c> lists (after a
/// restore) or, without it, those of the <c>PackageReference</c> items found in the NuGet cache, and the
/// assemblies of <c>Reference</c> items with a <c>HintPath</c>.
/// </summary>
public static partial class ReferenceAssemblies
{
    public static IReadOnlyList<string> Resolve(ProjectModel project, List<LoadProblem> problems)
    {
        var result = new List<string>();
        var assets = Path.Combine(project.IntermediateOutputPath, "project.assets.json");
        var frameworkReferences = new List<string>(project.FrameworkReferences);
        if (project.Sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) || project.Sdk.StartsWith("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase))
        {
            frameworkReferences.Add("Microsoft.AspNetCore.App");
        }

        if (File.Exists(assets))
        {
            try
            {
                var (packages, fromAssets) = ReadAssets(assets, project.TargetFramework, problems);
                result.AddRange(packages);
                frameworkReferences.AddRange(fromAssets);
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
            {
                problems.Add(LoadProblem.Warning($"Cannot read {assets}: {e.Message}", project.Path));
                result.AddRange(FromPackageCache(project, problems));
            }
        }
        else
        {
            result.AddRange(FromPackageCache(project, problems));
        }

        result.AddRange(Framework(project, frameworkReferences, problems));
        foreach (var reference in project.AssemblyReferences)
        {
            if (File.Exists(reference))
            {
                result.Add(reference);
            }
            else
            {
                problems.Add(LoadProblem.Warning($"{project.Name}: referenced assembly does not exist: {reference}", project.Path));
            }
        }

        return result;
    }

    /// <summary>The reference assemblies of the targeting packs: <c>packs/Microsoft.NETCore.App.Ref/10.0.x/ref/net10.0</c>.</summary>
    private static IEnumerable<string> Framework(ProjectModel project, List<string> frameworkReferences, List<LoadProblem> problems)
    {
        var (family, major) = ParseFramework(project.TargetFramework);
        var packs = DotnetLocator.PacksDirectory;
        if (packs == null)
        {
            problems.Add(LoadProblem.Warning("No .NET SDK found (set DOTNET_ROOT); types of the framework are not known.", project.Path));
            return [];
        }

        if (family == "netstandard")
        {
            return Pack(packs, "NETStandard.Library.Ref", "netstandard2.1", null, project, problems)
                ?? FromPackage("netstandard.library", "build/netstandard2.0/ref", project, problems)
                ?? [];
        }

        if (family != "net" || major < 5)
        {
            // .NET Framework and unrecognized targets: the newest .NET pack is a close enough approximation
            problems.Add(LoadProblem.Warning($"{project.Name}: target '{project.TargetFramework}' is approximated with the .NET reference assemblies.", project.Path));
            major = 0;
        }

        // A platform's framework (net10.0-android): its workload's reference pack, which the SDK references implicitly
        var result = new List<string>();
        if (PlatformPack(packs, project.TargetFramework, major) is { } platform)
        {
            frameworkReferences.RemoveAll(f => f.Equals(platform.Framework, StringComparison.OrdinalIgnoreCase));
            result.AddRange(platform.Assemblies);
        }

        var names = frameworkReferences.Prepend("Microsoft.NETCore.App").Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var packName in names.Select(PackFor).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            result.AddRange(Pack(packs, packName, null, major, project, problems) ?? []);
        }

        return result;
    }

    /// <summary>
    /// The reference assemblies of an Android target (<c>net10.0-android</c>, <c>net10.0-android35.0</c>): the
    /// <c>Microsoft.Android.Ref.&lt;API level&gt;</c> pack of the platform version, else the newest one installed with
    /// assemblies for the .NET version.
    /// </summary>
    private static (string Framework, IReadOnlyList<string> Assemblies)? PlatformPack(string packs, string targetFramework, int major)
    {
        var match = PlatformPattern().Match(targetFramework.ToLowerInvariant());
        if (!match.Success || match.Groups[1].Value != "android" || !Directory.Exists(packs))
        {
            return null;
        }

        var apiLevel = int.TryParse(match.Groups[2].Value.Split('.')[0], out var level) ? level : 0;
        var folder = $"net{major}.0";
        var candidates = Directory.EnumerateDirectories(packs, "Microsoft.Android.Ref.*")
            .Select(d => (Path: d, Level: int.TryParse(Path.GetFileName(d)["Microsoft.Android.Ref.".Length..], out var l) ? l : -1))
            .Where(c => c.Level >= 0 && (apiLevel == 0 || c.Level == apiLevel))
            .OrderByDescending(c => c.Level);
        foreach (var (path, _) in candidates)
        {
            var versions = Directory.EnumerateDirectories(path)
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version())
                .Select(d => Path.Combine(d, "ref", folder))
                .Where(Directory.Exists);
            if (versions.FirstOrDefault() is { } refDirectory)
            {
                return ("Microsoft.Android", Directory.EnumerateFiles(refDirectory, "*.dll").Order(StringComparer.Ordinal).ToList());
            }
        }

        return null;
    }

    [GeneratedRegex(@"^net\d+\.\d+-([a-z]+)([\d.]*)$")]
    private static partial Regex PlatformPattern();

    /// <summary>The dlls of a pack's ref folder, for <paramref name="major"/> (the newest version when missing or 0).</summary>
    private static IReadOnlyList<string>? Pack(string packs, string packName, string? folder, int? major, ProjectModel project, List<LoadProblem> problems)
    {
        var packDirectory = Path.Combine(packs, packName);
        if (!Directory.Exists(packDirectory))
        {
            if (folder == null)
            {
                problems.Add(LoadProblem.Warning($"{project.Name}: targeting pack not installed: {packDirectory}", project.Path));
            }

            return null;
        }

        var versions = Directory.EnumerateDirectories(packDirectory)
            .Select(d => (Path: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
            .Where(v => v.Version != null)
            .OrderByDescending(v => v.Version)
            .ToList();
        var chosen = versions.FirstOrDefault(v => major is null or 0 || v.Version!.Major == major);
        if (chosen.Path == null && versions.Count > 0)
        {
            chosen = versions[0];
            problems.Add(LoadProblem.Warning($"{project.Name}: no {packName} for {project.TargetFramework}; using {Path.GetFileName(chosen.Path)}.", project.Path));
        }

        if (chosen.Path == null)
        {
            return null;
        }

        var refDirectory = folder != null ? Path.Combine(chosen.Path, "ref", folder) : Path.Combine(chosen.Path, "ref", $"net{chosen.Version!.Major}.0");
        if (!Directory.Exists(refDirectory))
        {
            refDirectory = Directory.Exists(Path.Combine(chosen.Path, "ref"))
                ? Directory.EnumerateDirectories(Path.Combine(chosen.Path, "ref")).OrderDescending(StringComparer.Ordinal).FirstOrDefault() ?? refDirectory
                : refDirectory;
        }

        return Directory.Exists(refDirectory) ? Directory.EnumerateFiles(refDirectory, "*.dll").Order(StringComparer.Ordinal).ToList() : null;
    }

    private static string PackFor(string frameworkReference) =>
        frameworkReference.StartsWith("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase) ? "Microsoft.WindowsDesktop.App.Ref"
        : frameworkReference.StartsWith("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase) ? "Microsoft.AspNetCore.App.Ref"
        : "Microsoft.NETCore.App.Ref";

    /// <summary><c>net10.0-windows</c> → (net, 10); <c>netstandard2.0</c> → (netstandard, 2); <c>net48</c> → (netframework, 4).</summary>
    internal static (string Family, int Major) ParseFramework(string targetFramework)
    {
        var match = FrameworkPattern().Match(targetFramework.ToLowerInvariant());
        if (!match.Success)
        {
            return ("", 0);
        }

        var family = match.Groups[1].Value;
        var digits = match.Groups[2].Value;
        if (family is "netcoreapp" or "netstandard")
        {
            return (family == "netcoreapp" ? "net" : family, int.TryParse(digits.Split('.')[0], out var m) ? m : 0);
        }

        if (digits.Contains('.'))
        {
            return ("net", int.TryParse(digits.Split('.')[0], out var major) ? major : 0);
        }

        return ("netframework", digits.Length > 0 ? digits[0] - '0' : 0);
    }

    [GeneratedRegex(@"^(netcoreapp|netstandard|net)(\d+(?:\.\d+)?)")]
    private static partial Regex FrameworkPattern();

    /// <summary>Package compile assemblies and framework references from <c>project.assets.json</c>.</summary>
    private static (List<string> Assemblies, List<string> FrameworkReferences) ReadAssets(string path, string targetFramework, List<LoadProblem> problems)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var assemblies = new List<string>();
        var frameworkReferences = new List<string>();
        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
        {
            return (assemblies, frameworkReferences);
        }

        // Target keys are frameworks ("net10.0"), with "/rid" for runtime-specific ones
        string? targetKey = null;
        foreach (var target in targets.EnumerateObject())
        {
            if (target.Name.Contains('/'))
            {
                continue;
            }

            if (string.Equals(target.Name, targetFramework, StringComparison.OrdinalIgnoreCase)
                || target.Name.StartsWith(targetFramework + " ", StringComparison.OrdinalIgnoreCase)
                || target.Name.Replace(".NETCoreApp,Version=v", "net", StringComparison.OrdinalIgnoreCase) == targetFramework)
            {
                targetKey = target.Name;
                break;
            }

            targetKey ??= target.Name;
        }

        if (targetKey == null)
        {
            return (assemblies, frameworkReferences);
        }

        var folders = root.TryGetProperty("packageFolders", out var packageFolders) ? packageFolders.EnumerateObject().Select(f => f.Name).ToList() : [];
        var libraries = root.TryGetProperty("libraries", out var libs) ? libs : default;
        foreach (var library in targets.GetProperty(targetKey).EnumerateObject())
        {
            if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "package"
                || libraries.ValueKind != JsonValueKind.Object || !libraries.TryGetProperty(library.Name, out var info)
                || !info.TryGetProperty("path", out var libraryPath) || libraryPath.GetString() is not { } relative)
            {
                continue;
            }

            if (folders.Select(f => Path.GetFullPath(Path.Combine(f, relative))).FirstOrDefault(Directory.Exists) is { } packageRoot)
            {
                assemblies.AddRange(BuildReferences(packageRoot, targetFramework));
            }

            if (!library.Value.TryGetProperty("compile", out var compile) || compile.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var assembly in compile.EnumerateObject())
            {
                if (assembly.Name.EndsWith("_._", StringComparison.Ordinal))
                {
                    continue;
                }

                var found = folders
                    .Select(f => Path.GetFullPath(Path.Combine(f, relative, assembly.Name)))
                    .FirstOrDefault(File.Exists);
                if (found != null)
                {
                    assemblies.Add(found);
                }
                else
                {
                    problems.Add(LoadProblem.Warning($"Package assembly not found: {library.Name} {assembly.Name}", path));
                }
            }

            if (library.Value.TryGetProperty("frameworkReferences", out var references) && references.ValueKind == JsonValueKind.Array)
            {
                frameworkReferences.AddRange(references.EnumerateArray().Select(r => r.GetString()).OfType<string>());
            }
        }

        if (root.TryGetProperty("project", out var project) && project.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind == JsonValueKind.Object)
        {
            foreach (var framework in frameworks.EnumerateObject())
            {
                if (framework.Value.TryGetProperty("frameworkReferences", out var references) && references.ValueKind == JsonValueKind.Object)
                {
                    frameworkReferences.AddRange(references.EnumerateObject().Select(r => r.Name));
                }
            }
        }

        return (assemblies, frameworkReferences);
    }

    /// <summary>
    /// Without a restore: the assemblies of the <c>PackageReference</c> items found in the NuGet cache and of their
    /// dependencies, as their .nuspec files list them (the version asked for when it is there, else the newest).
    /// </summary>
    private static IEnumerable<string> FromPackageCache(ProjectModel project, List<LoadProblem> problems)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, string? Version, bool Direct)>(project.PackageReferences.Select(p => (p.Id, p.Version, true)));
        while (queue.Count > 0 && seen.Count < 500)
        {
            var (id, version, direct) = queue.Dequeue();
            if (!seen.Add(id))
            {
                continue;
            }

            var directory = PackageDirectory(id.ToLowerInvariant(), version);
            if (directory == null)
            {
                if (direct)
                {
                    problems.Add(LoadProblem.Warning($"{project.Name}: package {id} {version} is not restored; its types are not known.", project.Path));
                }

                continue;
            }

            result.AddRange(FromPackage(id.ToLowerInvariant(), null, project, problems, Path.GetFileName(directory)) ?? []);
            result.AddRange(BuildReferences(directory, project.TargetFramework));
            foreach (var dependency in Dependencies(directory, project.TargetFramework))
            {
                queue.Enqueue((dependency.Id, dependency.Version, false));
            }
        }

        return result;
    }

    /// <summary>
    /// Assemblies a package's MSBuild files add as <c>Reference</c> items (MSTest adds its extensions this way), from
    /// <c>build/</c> and <c>buildTransitive/</c> and their folder for the closest framework.
    /// </summary>
    private static IEnumerable<string> BuildReferences(string packageRoot, string targetFramework)
    {
        var result = new List<string>();
        foreach (var kind in new[] { "build", "buildTransitive" })
        {
            var root = Path.Combine(packageRoot, kind);
            if (!Directory.Exists(root))
            {
                continue;
            }

            var directories = new List<string> { root };
            if (BestFramework(Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>(), targetFramework) is { } best)
            {
                directories.Add(Path.Combine(root, best));
            }

            foreach (var directory in directories)
            {
                foreach (var file in Directory.EnumerateFiles(directory).Where(f => f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        foreach (var reference in System.Xml.Linq.XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "Reference"))
                        {
                            var hint = reference.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
                            var name = (string?)reference.Attribute("Include");
                            var candidate = hint != null && !hint.Contains("$(", StringComparison.Ordinal)
                                ? Path.GetFullPath(Path.Combine(directory, hint))
                                : name != null ? Path.Combine(directory, name.Split(',')[0].Trim() + ".dll") : null;
                            if (candidate != null && File.Exists(candidate))
                            {
                                result.Add(candidate);
                            }
                        }
                    }
                    catch (Exception e) when (e is System.Xml.XmlException or IOException or UnauthorizedAccessException or ArgumentException)
                    {
                    }
                }
            }
        }

        return result;
    }

    /// <summary>The dependencies a package's .nuspec lists for the framework group closest to <paramref name="targetFramework"/>.</summary>
    private static IEnumerable<(string Id, string? Version)> Dependencies(string packageDirectory, string targetFramework)
    {
        var nuspec = Directory.EnumerateFiles(packageDirectory, "*.nuspec").FirstOrDefault();
        if (nuspec == null)
        {
            return [];
        }

        try
        {
            var root = System.Xml.Linq.XDocument.Load(nuspec).Root;
            var dependencies = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "dependencies");
            if (dependencies == null)
            {
                return [];
            }

            var groups = dependencies.Elements().Where(e => e.Name.LocalName == "group").ToList();
            var elements = dependencies.Elements().Where(e => e.Name.LocalName == "dependency");
            if (groups.Count > 0)
            {
                var byFramework = groups.ToDictionary(g => NuGetFramework((string?)g.Attribute("targetFramework") ?? ""), g => g, StringComparer.OrdinalIgnoreCase);
                var best = BestFramework(byFramework.Keys.Where(k => k.Length > 0), targetFramework);
                var group = best != null ? byFramework[best] : byFramework.GetValueOrDefault("");
                elements = group?.Elements().Where(e => e.Name.LocalName == "dependency") ?? [];
            }

            return elements
                .Select(e => ((string?)e.Attribute("id") ?? "", MinimumVersion((string?)e.Attribute("version"))))
                .Where(d => d.Item1.Length > 0)
                .ToList();
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary><c>.NETStandard2.0</c> → <c>netstandard2.0</c>, <c>.NETCoreApp3.1</c> → <c>netcoreapp3.1</c>, <c>.NETFramework4.6.2</c> → <c>net462</c>.</summary>
    private static string NuGetFramework(string name)
    {
        if (name.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase))
        {
            return "netstandard" + name[".NETStandard".Length..];
        }

        if (name.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
        {
            return "netcoreapp" + name[".NETCoreApp".Length..];
        }

        if (name.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase))
        {
            return "net" + name[".NETFramework".Length..].Replace(".", "", StringComparison.Ordinal);
        }

        return name.ToLowerInvariant();
    }

    /// <summary>The lowest version of a NuGet version range: <c>[1.2.0, )</c> → <c>1.2.0</c>.</summary>
    private static string? MinimumVersion(string? range) =>
        range?.Trim('[', '(', ' ').Split(',')[0].Trim(']', ')', ' ') is { Length: > 0 } version ? version : null;

    /// <summary>The cached package folder for a version (the newest one when that version is not there).</summary>
    private static string? PackageDirectory(string id, string? version)
    {
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var packageDirectory = Path.Combine(packages, id);
        if (!Directory.Exists(packageDirectory))
        {
            return null;
        }

        if (version != null && Directory.Exists(Path.Combine(packageDirectory, version.ToLowerInvariant())))
        {
            return Path.Combine(packageDirectory, version.ToLowerInvariant());
        }

        return Directory.EnumerateDirectories(packageDirectory)
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var parsed) ? parsed : new Version())
            .FirstOrDefault();
    }

    private static IReadOnlyList<string>? FromPackage(string id, string? folder, ProjectModel project, List<LoadProblem> problems, string? version = null)
    {
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var packageDirectory = Path.Combine(packages, id);
        if (!Directory.Exists(packageDirectory))
        {
            return null;
        }

        var versions = Directory.EnumerateDirectories(packageDirectory).Select(Path.GetFileName).OfType<string>().ToList();
        var chosen = version != null && versions.Contains(version.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            ? version.ToLowerInvariant()
            : versions.OrderByDescending(v => Version.TryParse(v.Split('-')[0], out var parsed) ? parsed : new Version()).FirstOrDefault();
        if (chosen == null)
        {
            return null;
        }

        var root = Path.Combine(packageDirectory, chosen);
        if (folder != null)
        {
            var directory = Path.Combine(root, folder);
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.dll").ToList() : null;
        }

        // ref/ before lib/, the closest framework the project can use
        foreach (var kind in new[] { "ref", "lib" })
        {
            var directory = Path.Combine(root, kind);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var best = BestFramework(Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>(), project.TargetFramework);
            if (best != null)
            {
                return Directory.EnumerateFiles(Path.Combine(directory, best), "*.dll").ToList();
            }
        }

        return [];
    }

    /// <summary>The framework folder a project targeting <paramref name="targetFramework"/> uses: the highest compatible .NET, then .NET Standard.</summary>
    internal static string? BestFramework(IEnumerable<string> folders, string targetFramework)
    {
        var (family, major) = ParseFramework(targetFramework);
        return folders
            .Select(f => (Folder: f, Framework: ParseFramework(f)))
            .Where(f => f.Framework.Family == "netstandard"
                || (f.Framework.Family == family && (f.Framework.Major <= major || major == 0))
                || (family == "netframework" && f.Framework.Family == "netframework"))
            .OrderBy(f => f.Framework.Family == "netstandard" ? 1 : 0)
            .ThenByDescending(f => f.Framework.Major)
            .ThenByDescending(f => f.Folder, StringComparer.Ordinal)
            .Select(f => f.Folder)
            .FirstOrDefault();
    }
}
