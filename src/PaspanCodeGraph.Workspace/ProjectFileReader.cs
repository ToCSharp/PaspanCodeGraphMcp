using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PaspanParsers.CSharp;
using PaspanParsers.Cpp;

namespace PaspanCodeGraph.Workspace;

/// <summary>What a C# or C++ project compiles, as read from its project file.</summary>
/// <param name="Sources">Full paths of the files it compiles (for C++ also the headers), in the order found.</param>
/// <param name="ProjectReferences">Full paths of the projects it references.</param>
public sealed record ProjectModel(
    string Name,
    string Path,
    string TargetFramework,
    CSharpLanguageVersion LanguageVersion,
    IReadOnlyList<string> PreprocessorSymbols,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<LoadProblem> Problems)
{
    /// <summary>Namespaces imported in every file: the SDK's implicit usings and <c>&lt;Using&gt;</c> items.</summary>
    public IReadOnlyList<string> Usings { get; init; } = [];

    /// <summary>The project SDK (<c>Microsoft.NET.Sdk.Web</c>); empty for a non-SDK project.</summary>
    public string Sdk { get; init; } = "";

    /// <summary><c>PackageReference</c> items with their versions (null when the version is centrally managed or missing).</summary>
    public IReadOnlyList<(string Id, string? Version)> PackageReferences { get; init; } = [];

    /// <summary><c>FrameworkReference</c> items (<c>Microsoft.AspNetCore.App</c>).</summary>
    public IReadOnlyList<string> FrameworkReferences { get; init; } = [];

    /// <summary>Assemblies of <c>Reference</c> items with a <c>HintPath</c>, as full paths.</summary>
    public IReadOnlyList<string> AssemblyReferences { get; init; } = [];

    /// <summary>The intermediate output directory, where NuGet restore writes <c>project.assets.json</c>.</summary>
    public string IntermediateOutputPath { get; init; } = "";

    public string Directory => System.IO.Directory.Exists(Path) ? Path : System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>The language of the sources: C# for .csproj projects, C++ for .vcxproj, compile_commands.json and folders.</summary>
    public SourceLanguage Language { get; init; } = SourceLanguage.CSharp;

    /// <summary>C++: the macros defined for every file (predefined ones and <c>-D</c> options), by name.</summary>
    public IReadOnlyDictionary<string, string> Macros { get; init; } = new Dictionary<string, string>();

    /// <summary>C++: the include directories (<c>-I</c> options), as full paths.</summary>
    public IReadOnlyList<string> IncludeDirectories { get; init; } = [];

    /// <summary>C++: the language standard.</summary>
    public CppLanguageVersion CppLanguageVersion { get; init; } = CppLanguageVersion.Latest;

    /// <summary>C++: options of files compiled with other ones than the project's (from compile_commands.json), by path.</summary>
    public IReadOnlyDictionary<string, CppFileOptions> FileOptions { get; init; } = new Dictionary<string, CppFileOptions>();
}

/// <summary>How one C++ file is compiled: its macros, include directories and standard.</summary>
public sealed record CppFileOptions(IReadOnlyDictionary<string, string> Macros, IReadOnlyList<string> IncludeDirectories, CppLanguageVersion LanguageVersion)
{
    /// <summary>A stable text of the options, for comparing them between loads.</summary>
    public string Print() => string.Join(";", Macros.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + "=" + m.Value))
        + "|" + string.Join(";", IncludeDirectories) + "|" + LanguageVersion;
}

/// <summary>
/// Reads a .csproj without MSBuild. It evaluates properties, conditions of the common forms
/// (<c>'$(Configuration)|$(Platform)' == 'Debug|AnyCPU'</c>, <c>Exists(...)</c>, <c>and</c>, <c>or</c>), imports of
/// files next to the project (Directory.Build.props, .props and .projitems files) and <c>Compile</c> and
/// <c>ProjectReference</c> items with wildcards. SDK projects compile <c>**/*.cs</c> except under bin/, obj/ and
/// folders starting with '.'. Targets, tasks, SDK and NuGet imports are not evaluated, so sources a build
/// generates are missing.
/// </summary>
public sealed partial class ProjectFileReader
{
    private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _compile = [];
    private readonly HashSet<string> _compileSet = new(SymbolIndexBuilder.PathComparer);
    private readonly List<string> _references = [];
    private readonly List<(string Id, string? Version)> _packages = [];
    private readonly List<string> _frameworkReferences = [];
    private readonly List<string> _assemblyReferences = [];
    private readonly List<string> _usings = [];
    private readonly List<string> _removedUsings = [];
    private readonly List<string> _cppSources = [];
    private readonly HashSet<string> _cppSourceSet = new(SymbolIndexBuilder.PathComparer);
    private readonly Dictionary<string, string> _clCompile = new(StringComparer.OrdinalIgnoreCase);
    private string _sdk = "";
    private readonly List<LoadProblem> _problems = [];
    private readonly HashSet<string> _imported = new(SymbolIndexBuilder.PathComparer);
    private readonly string _projectPath;

    /// <summary>Properties set from outside the project, which its own assignments do not change.</summary>
    private readonly HashSet<string> _global = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set when the framework was taken from <c>TargetFrameworks</c> after the evaluation.</summary>
    private bool _frameworkChosenLate;

    private ProjectFileReader(string projectPath, string configuration, string platform, string? targetFramework = null)
    {
        _projectPath = projectPath;
        if (targetFramework != null)
        {
            // As in the inner build of a multi-targeting project
            _properties["TargetFramework"] = targetFramework;
            _global.Add("TargetFramework");
        }

        var directory = Path.GetDirectoryName(projectPath)!;
        _properties["Configuration"] = configuration;
        _properties["Platform"] = platform;
        _properties["MSBuildProjectFullPath"] = projectPath;
        _properties["MSBuildProjectDirectory"] = directory;
        _properties["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(projectPath);
        _properties["MSBuildProjectFile"] = Path.GetFileName(projectPath);
        _properties["MSBuildProjectExtension"] = Path.GetExtension(projectPath);
        _properties["DefineConstants"] = "";
    }

    public static ProjectModel Read(string projectPath, string configuration = "Debug", string platform = "AnyCPU")
    {
        projectPath = Path.GetFullPath(projectPath);
        if (projectPath.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
        {
            // C++ projects have no AnyCPU platform
            return new ProjectFileReader(projectPath, configuration, platform == "AnyCPU" ? "x64" : platform).EvaluateCpp();
        }

        var reader = new ProjectFileReader(projectPath, configuration, platform);
        var model = reader.Evaluate();

        // A multi-targeting project is built once per framework with TargetFramework set from the start, so
        // that conditions on it (DefineConstants per framework) hold: evaluate again for the framework chosen
        return reader._frameworkChosenLate && model.TargetFramework.Length != 0
            ? new ProjectFileReader(projectPath, configuration, platform, model.TargetFramework).Evaluate()
            : model;
    }

    private ProjectModel Evaluate()
    {
        var document = XDocument.Load(_projectPath);
        var root = document.Root ?? throw new InvalidDataException($"Empty project file: {_projectPath}");
        var isSdk = root.Attribute("Sdk") != null || root.Elements().Any(e => e.Name.LocalName == "Sdk");
        _sdk = (string?)root.Attribute("Sdk") ?? (string?)root.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name") ?? "";
        var directory = Path.GetDirectoryName(_projectPath)!;

        if (isSdk)
        {
            // What Microsoft.NET.Sdk's props define before the project body
            SetDefault("EnableDefaultItems", "true");
            SetDefault("EnableDefaultCompileItems", "true");
            SetDefault("ImportDirectoryBuildProps", "true");
            SetDefault("BaseOutputPath", "bin" + Path.DirectorySeparatorChar);
            SetDefault("BaseIntermediateOutputPath", "obj" + Path.DirectorySeparatorChar);
            SetDefault("AssemblyName", _properties["MSBuildProjectName"]);
            if (string.Equals(Expand("$(ImportDirectoryBuildProps)"), "true", StringComparison.OrdinalIgnoreCase)
                && FindFileAbove(directory, "Directory.Build.props") is { } props)
            {
                Import(props);
            }
        }

        EvaluateElements(root, _projectPath, isSdk);

        var targetFramework = Expand("$(TargetFramework)");
        if (targetFramework.Length == 0)
        {
            targetFramework = Expand("$(TargetFrameworks)").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            if (targetFramework.Length != 0)
            {
                _frameworkChosenLate = true;
            }
        }

        if (targetFramework.Length == 0 && Expand("$(TargetFrameworkVersion)") is { Length: > 0 } version)
        {
            targetFramework = "net" + version.TrimStart('v', 'V').Replace(".", "");
        }

        return new ProjectModel(
            Expand("$(AssemblyName)") is { Length: > 0 } assemblyName ? assemblyName : _properties["MSBuildProjectName"],
            _projectPath,
            targetFramework,
            ParseLanguageVersion(Expand("$(LangVersion)")),
            PreprocessorSymbols(isSdk, targetFramework),
            _compile,
            _references,
            _problems)
        {
            Usings = Usings(isSdk),
            Sdk = _sdk,
            PackageReferences = _packages,
            FrameworkReferences = _frameworkReferences,
            AssemblyReferences = _assemblyReferences,
            IntermediateOutputPath = Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(Expand("$(BaseIntermediateOutputPath)") is { Length: > 0 } obj ? obj : "obj"))),
        };
    }

    /// <summary>A .vcxproj: its ClCompile and ClInclude items and the ClCompile item definition of the configuration.</summary>
    private ProjectModel EvaluateCpp()
    {
        var document = XDocument.Load(_projectPath);
        var root = document.Root ?? throw new InvalidDataException($"Empty project file: {_projectPath}");
        var directory = Path.GetDirectoryName(_projectPath)!;
        _properties["ProjectDir"] = directory + Path.DirectorySeparatorChar;
        _properties["ProjectName"] = _properties["MSBuildProjectName"];
        _properties["SolutionDir"] = directory + Path.DirectorySeparatorChar;
        EvaluateElements(root, _projectPath, isSdk: false);

        var platform = _properties["Platform"];
        var macros = CppFiles.PredefinedMacros(msvc: true, platform);
        if (string.Equals(_properties["Configuration"], "Debug", StringComparison.OrdinalIgnoreCase))
        {
            macros["_DEBUG"] = "1";
        }

        foreach (var definition in _clCompile.GetValueOrDefault("PreprocessorDefinitions", "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!definition.StartsWith("%(", StringComparison.Ordinal) && !definition.Contains("$(", StringComparison.Ordinal))
            {
                CppFiles.Define(macros, definition);
            }
        }

        var includes = new List<string>();
        foreach (var include in _clCompile.GetValueOrDefault("AdditionalIncludeDirectories", "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!include.Contains("%(", StringComparison.Ordinal) && !include.Contains("$(", StringComparison.Ordinal))
            {
                includes.Add(Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(include))));
            }
        }

        var standard = CppFiles.ParseStandard(_clCompile.GetValueOrDefault("LanguageStandard", "")) ?? CppLanguageVersion.Cpp20;
        macros["__cplusplus"] = CppFiles.CplusplusValue(standard);
        return new ProjectModel(_properties["ProjectName"], _projectPath, "", CSharpLanguageVersion.Latest, [], _cppSources, _references, _problems)
        {
            Language = SourceLanguage.Cpp,
            Macros = macros,
            IncludeDirectories = includes,
            CppLanguageVersion = standard,
        };
    }

    private void SetDefault(string name, string value)
    {
        if (!_properties.ContainsKey(name))
        {
            _properties[name] = value;
        }
    }

    private void EvaluateElements(XElement root, string file, bool isSdk)
    {
        var directory = Path.GetDirectoryName(file)!;
        var previousThisFile = _properties.GetValueOrDefault("MSBuildThisFileDirectory");
        _properties["MSBuildThisFileDirectory"] = directory + Path.DirectorySeparatorChar;
        _properties["MSBuildThisFileFullPath"] = file;
        _properties["MSBuildThisFile"] = Path.GetFileName(file);

        var defaultsAdded = false;
        foreach (var element in root.Elements())
        {
            if (!IsTrue(element))
            {
                continue;
            }

            switch (element.Name.LocalName)
            {
                case "PropertyGroup":
                    foreach (var property in element.Elements().Where(IsTrue))
                    {
                        if (!_global.Contains(property.Name.LocalName))
                        {
                            _properties[property.Name.LocalName] = Expand(property.Value.Trim());
                        }
                    }

                    break;

                case "ItemGroup":
                    // The SDK adds its default items before the project's own ones
                    if (isSdk && !defaultsAdded)
                    {
                        AddDefaultCompileItems();
                        defaultsAdded = true;
                    }

                    foreach (var item in element.Elements().Where(IsTrue))
                    {
                        EvaluateItem(item, directory);
                    }

                    break;

                case "Import":
                    if (element.Attribute("Sdk") is null && (string?)element.Attribute("Project") is { } project)
                    {
                        var expanded = Expand(project);
                        if (expanded.Length != 0)
                        {
                            var full = Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(expanded)));
                            if (File.Exists(full))
                            {
                                Import(full);
                            }
                            else if (expanded.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase))
                            {
                                _problems.Add(LoadProblem.Warning($"Imported shared project does not exist: {full}", file));
                            }
                        }
                    }

                    break;

                case "ItemDefinitionGroup":
                    foreach (var definition in element.Elements().Where(e => e.Name.LocalName == "ClCompile" && IsTrue(e)))
                    {
                        foreach (var metadata in definition.Elements().Where(IsTrue))
                        {
                            // %(PreprocessorDefinitions) is the value defined before
                            var previous = _clCompile.GetValueOrDefault(metadata.Name.LocalName, "");
                            _clCompile[metadata.Name.LocalName] = Expand(metadata.Value.Trim()).Replace($"%({metadata.Name.LocalName})", previous, StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    break;

                case "Choose":
                    foreach (var branch in element.Elements())
                    {
                        if (branch.Name.LocalName == "Otherwise" || (branch.Name.LocalName == "When" && IsTrue(branch)))
                        {
                            EvaluateElements(new XElement("Project", branch.Elements()), file, isSdk: false);
                            break;
                        }
                    }

                    break;
            }
        }

        if (isSdk && !defaultsAdded)
        {
            AddDefaultCompileItems();
        }

        if (previousThisFile != null)
        {
            _properties["MSBuildThisFileDirectory"] = previousThisFile;
        }
    }

    private void Import(string file)
    {
        if (!_imported.Add(file))
        {
            return;
        }

        try
        {
            if (XDocument.Load(file).Root is { } root)
            {
                EvaluateElements(root, file, isSdk: false);
            }
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            _problems.Add(LoadProblem.Warning($"Could not read imported file {file}: {e.Message}", _projectPath));
        }
    }

    private void EvaluateItem(XElement item, string directory)
    {
        var include = Expand((string?)item.Attribute("Include") ?? "");
        var remove = Expand((string?)item.Attribute("Remove") ?? "");
        switch (item.Name.LocalName)
        {
            case "Compile":
                if (include.Length != 0)
                {
                    var exclude = Expand((string?)item.Attribute("Exclude") ?? "");
                    var excluded = Glob(exclude, directory).ToHashSet(SymbolIndexBuilder.PathComparer);
                    foreach (var path in Glob(include, directory))
                    {
                        if (!excluded.Contains(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && _compileSet.Add(path))
                        {
                            _compile.Add(path);
                        }
                    }
                }

                if (remove.Length != 0)
                {
                    var removed = MatchPatterns(remove, directory);
                    _compile.RemoveAll(path => removed(path) && _compileSet.Remove(path));
                }

                break;

            case "ClCompile" or "ClInclude" or "None" when _projectPath.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) && include.Length != 0:
                foreach (var path in Glob(include, directory))
                {
                    if ((item.Name.LocalName != "None" || CppFiles.IsCppFile(path)) && _cppSourceSet.Add(path))
                    {
                        _cppSources.Add(path);
                    }
                }

                break;

            case "Using" when (string?)item.Attribute("Alias") == null && (string?)item.Attribute("Static") is null or "false":
                foreach (var ns in Split(include))
                {
                    _usings.Add(ns);
                }

                foreach (var ns in Split(remove))
                {
                    _removedUsings.Add(ns);
                }

                break;

            case "PackageReference":
            {
                var version = Expand((string?)item.Attribute("Version") ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value ?? "").Trim();
                foreach (var id in Split(include))
                {
                    _packages.RemoveAll(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    _packages.Add((id, version.Length == 0 ? null : version));
                }

                break;
            }

            case "FrameworkReference":
                foreach (var name in Split(include))
                {
                    if (!_frameworkReferences.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        _frameworkReferences.Add(name);
                    }
                }

                break;

            case "Reference":
                if (item.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath") is { } hint && Expand(hint.Value.Trim()) is { Length: > 0 } hintPath)
                {
                    var full = Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(hintPath)));
                    if (!_assemblyReferences.Contains(full, SymbolIndexBuilder.PathComparer))
                    {
                        _assemblyReferences.Add(full);
                    }
                }

                break;

            case "ProjectReference":
                foreach (var path in Split(include))
                {
                    var full = Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(path)));
                    if (!_references.Contains(full, SymbolIndexBuilder.PathComparer))
                    {
                        _references.Add(full);
                    }
                }

                break;
        }
    }

    private void AddDefaultCompileItems()
    {
        if (!string.Equals(Expand("$(EnableDefaultItems)"), "true", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Expand("$(EnableDefaultCompileItems)"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = _properties["MSBuildProjectDirectory"];
        var excluded = new List<string>
        {
            Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(Expand("$(BaseOutputPath)")))),
            Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(Expand("$(BaseIntermediateOutputPath)")))),
        };

        foreach (var path in EnumerateSources(directory, excluded))
        {
            if (_compileSet.Add(path))
            {
                _compile.Add(path);
            }
        }
    }

    /// <summary>The .cs files under <paramref name="directory"/>, skipping excluded and hidden ('.') directories.</summary>
    private static IEnumerable<string> EnumerateSources(string directory, List<string> excludedDirectories)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files, subdirectories;
            try
            {
                files = System.IO.Directory.GetFiles(current, "*.cs");
                subdirectories = System.IO.Directory.GetDirectories(current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                yield return file;
            }

            Array.Sort(subdirectories, StringComparer.Ordinal);
            for (var i = subdirectories.Length - 1; i >= 0; i--)
            {
                var subdirectory = subdirectories[i];
                if (Path.GetFileName(subdirectory).StartsWith('.')
                    || excludedDirectories.Any(e => SymbolIndexBuilder.PathComparer.Equals(subdirectory.TrimEnd(Path.DirectorySeparatorChar), e.TrimEnd(Path.DirectorySeparatorChar))))
                {
                    continue;
                }

                pending.Push(subdirectory);
            }
        }
    }

    /// <summary>The implicit usings of the SDK (when <c>ImplicitUsings</c> is on) and the <c>Using</c> items.</summary>
    private List<string> Usings(bool isSdk)
    {
        var usings = new List<string>();
        var implicitUsings = Expand("$(ImplicitUsings)");
        if (isSdk && (implicitUsings.Equals("enable", StringComparison.OrdinalIgnoreCase) || implicitUsings.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            usings.AddRange(["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks"]);
            if (_sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
            {
                usings.AddRange(["System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing", "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"]);
            }
            else if (_sdk.StartsWith("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase))
            {
                usings.AddRange(["Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"]);
            }
        }

        usings.AddRange(_usings);
        return usings.Where(u => !_removedUsings.Contains(u)).Distinct(StringComparer.Ordinal).ToList();
    }

    // ========================================
    // Wildcards
    // ========================================

    private static IEnumerable<string> Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The files an item specification (paths and wildcards separated by ';') names.</summary>
    private static IEnumerable<string> Glob(string specification, string directory)
    {
        foreach (var part in Split(specification))
        {
            var full = Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(part)));
            if (!HasWildcard(full))
            {
                yield return full;
                continue;
            }

            // Enumerate from the directory before the first wildcard
            var firstWildcard = full.IndexOfAny(['*', '?']);
            var baseDirectory = full[..(full.LastIndexOf(Path.DirectorySeparatorChar, firstWildcard) + 1)];
            if (!System.IO.Directory.Exists(baseDirectory))
            {
                continue;
            }

            var regex = WildcardRegex(full);
            IEnumerable<string> files;
            try
            {
                files = System.IO.Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (regex.IsMatch(file))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>Whether a path matches one of the paths or wildcards of an item specification.</summary>
    private static Func<string, bool> MatchPatterns(string specification, string directory)
    {
        var paths = new HashSet<string>(SymbolIndexBuilder.PathComparer);
        var patterns = new List<Regex>();
        foreach (var part in Split(specification))
        {
            var full = Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(part)));
            if (HasWildcard(full))
            {
                patterns.Add(WildcardRegex(full));
            }
            else
            {
                paths.Add(full);
            }
        }

        return path => paths.Contains(path) || patterns.Any(p => p.IsMatch(path));
    }

    private static bool HasWildcard(string path) => path.Contains('*') || path.Contains('?');

    /// <summary><c>**</c> matches any number of directories, <c>*</c> and <c>?</c> characters within one.</summary>
    internal static Regex WildcardRegex(string pattern)
    {
        var separator = Regex.Escape(Path.DirectorySeparatorChar.ToString());
        var builder = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // "**/" matches zero or more directories
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == Path.DirectorySeparatorChar)
                {
                    i++;
                    builder.Append($"(?:.*{separator})?");
                }
                else
                {
                    builder.Append(".*");
                }
            }
            else if (c == '*')
            {
                builder.Append($"[^{separator}]*");
            }
            else if (c == '?')
            {
                builder.Append($"[^{separator}]");
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        var options = OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None;
        return new Regex(builder.Append('$').ToString(), options | RegexOptions.CultureInvariant);
    }

    // ========================================
    // Properties and conditions
    // ========================================

    /// <summary>
    /// Replaces <c>$(Name)</c> with property values and evaluates the property functions
    /// <c>$([MSBuild]::GetPathOfFileAbove(...))</c> and <c>$([MSBuild]::GetDirectoryNameOfFileAbove(...))</c>;
    /// other property functions expand to nothing.
    /// </summary>
    private string Expand(string value)
    {
        if (!value.Contains("$("))
        {
            return value;
        }

        var result = new StringBuilder();
        var i = 0;
        while (i < value.Length)
        {
            var start = value.IndexOf("$(", i, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(value, i, value.Length - i);
                break;
            }

            result.Append(value, i, start - i);
            var end = FindClosingParenthesis(value, start + 1);
            if (end < 0)
            {
                result.Append(value, start, value.Length - start);
                break;
            }

            var inner = value[(start + 2)..end].Trim();
            result.Append(inner.StartsWith('[') ? PropertyFunction(inner) : _properties.GetValueOrDefault(inner, ""));
            i = end + 1;
        }

        return result.ToString();
    }

    private static int FindClosingParenthesis(string value, int open)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = open; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '\'' or '"' or '`')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private string PropertyFunction(string call)
    {
        var framework = FrameworkFunction().Match(call);
        if (framework.Success)
        {
            var arguments = SplitArguments(framework.Groups["arguments"].Value).Select(a => Expand(Unquote(a.Trim()))).ToList();
            return framework.Groups["function"].Value.ToLowerInvariant() switch
            {
                "istargetframeworkcompatible" when arguments.Count == 2 => IsTargetFrameworkCompatible(arguments[0], arguments[1]) ? "True" : "False",
                "gettargetframeworkidentifier" when arguments.Count >= 1 => ParseTargetFramework(arguments[0])?.Identifier ?? "",
                "gettargetframeworkversion" when arguments.Count >= 1 => ParseTargetFramework(arguments[0]) is { } parsed
                    ? parsed.Version.ToString(arguments.Count > 1 && int.TryParse(arguments[1], out var digits) ? Math.Clamp(digits, 1, 4) : 2)
                    : "",
                _ => "",
            };
        }

        var match = FileAboveFunction.Match(call);
        if (!match.Success)
        {
            return "";
        }

        var file = Expand(match.Groups["file"].Value);
        var from = match.Groups["from"].Success ? Expand(match.Groups["from"].Value) : _properties["MSBuildThisFileDirectory"];
        var directory = Path.GetFullPath(Path.Combine(_properties["MSBuildProjectDirectory"], SolutionDiscovery.NormalizeSeparators(from)));
        var found = FindFileAbove(directory, file);
        return found == null ? "" : match.Groups["function"].Value == "GetPathOfFileAbove" ? found : Path.GetDirectoryName(found)!;
    }

    [GeneratedRegex(@"^\[MSBuild\]::(?<function>IsTargetFrameworkCompatible|GetTargetFrameworkIdentifier|GetTargetFrameworkVersion)\((?<arguments>.*)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameworkFunction();

    /// <summary>Splits the arguments of a property function at commas outside quotes and parentheses.</summary>
    private static List<string> SplitArguments(string arguments)
    {
        var parts = new List<string>();
        var depth = 0;
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case '\'':
                    inQuote = !inQuote;
                    break;
                case '(' when !inQuote:
                    depth++;
                    break;
                case ')' when !inQuote:
                    depth--;
                    break;
                case ',' when !inQuote && depth == 0:
                    parts.Add(arguments[start..i]);
                    start = i + 1;
                    break;
            }
        }

        parts.Add(arguments[start..]);
        return parts;
    }

    /// <summary>
    /// <c>net8.0</c> → (.NETCoreApp, 8.0), <c>netcoreapp3.1</c> → (.NETCoreApp, 3.1), <c>netstandard2.0</c> →
    /// (.NETStandard, 2.0), <c>net472</c> → (.NETFramework, 4.7.2); the platform (<c>-windows</c>) is ignored.
    /// </summary>
    internal static (string Identifier, Version Version)? ParseTargetFramework(string targetFramework)
    {
        var framework = targetFramework.Trim().ToLowerInvariant();
        if (framework.IndexOf('-') is var dash and >= 0)
        {
            framework = framework[..dash];
        }

        var match = TargetFrameworkName().Match(framework);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value;
        var version = match.Groups["version"].Value;
        if (name == "net" && !version.Contains('.'))
        {
            // .NET Framework: net48, net472, net20
            return version.Length is >= 2 and <= 3 && version.All(char.IsDigit)
                ? (".NETFramework", new Version(string.Join('.', version.Select(c => c.ToString())) + (version.Length == 2 ? ".0" : "")))
                : null;
        }

        if (!Version.TryParse(version.Contains('.') ? version : version + ".0", out var parsed))
        {
            return null;
        }

        return (name == "netstandard" ? ".NETStandard" : ".NETCoreApp", parsed);
    }

    /// <summary>
    /// Whether a project for <paramref name="candidate"/> can reference one for <paramref name="target"/>, as
    /// <c>$([MSBuild]::IsTargetFrameworkCompatible(...))</c> answers for the common frameworks.
    /// </summary>
    internal static bool IsTargetFrameworkCompatible(string candidate, string target)
    {
        if (ParseTargetFramework(candidate) is not { } from || ParseTargetFramework(target) is not { } to)
        {
            return false;
        }

        if (from.Identifier == to.Identifier)
        {
            return from.Version >= to.Version;
        }

        if (to.Identifier != ".NETStandard")
        {
            return false;
        }

        // The highest .NET Standard each framework implements
        var standard = from.Identifier switch
        {
            ".NETCoreApp" => from.Version >= new Version(3, 0) ? new Version(2, 1) : from.Version >= new Version(2, 0) ? new Version(2, 0) : new Version(1, 6),
            ".NETFramework" => from.Version >= new Version(4, 6, 1) ? new Version(2, 0)
                : from.Version >= new Version(4, 6) ? new Version(1, 3)
                : from.Version >= new Version(4, 5, 1) ? new Version(1, 2)
                : from.Version >= new Version(4, 5) ? new Version(1, 1)
                : null,
            _ => null,
        };
        return standard != null && standard >= to.Version;
    }

    /// <summary>GetPathOfFileAbove(file, from) and GetDirectoryNameOfFileAbove(from, file).</summary>
    private static readonly Regex FileAboveFunction = new(
        @"^\[MSBuild\]::(?:(?<function>GetPathOfFileAbove)\(\s*'?(?<file>[^',)]*)'?\s*(?:,\s*'?(?<from>[^')]*)'?\s*)?\)|(?<function>GetDirectoryNameOfFileAbove)\(\s*'?(?<from>[^',)]*)'?\s*,\s*'?(?<file>[^')]*)'?\s*\))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The nearest <paramref name="fileName"/> in <paramref name="directory"/> or above it.</summary>
    private static string? FindFileAbove(string directory, string fileName)
    {
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Evaluates an element's Condition; one that cannot be evaluated is false.</summary>
    private bool IsTrue(XElement element)
    {
        var condition = (string?)element.Attribute("Condition");
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        try
        {
            return EvaluateCondition(condition);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal bool EvaluateCondition(string condition)
    {
        condition = condition.Trim();
        foreach (var alternative in SplitTopLevel(condition, " or "))
        {
            if (SplitTopLevel(alternative, " and ").All(EvaluateTerm))
            {
                return true;
            }
        }

        return false;
    }

    private bool EvaluateTerm(string term)
    {
        term = term.Trim();
        if (term.StartsWith('(') && FindClosingParenthesis(term, 0) == term.Length - 1)
        {
            return EvaluateCondition(term[1..^1]);
        }

        var negate = false;
        if (term.StartsWith('!'))
        {
            negate = true;
            term = term[1..].TrimStart();
        }

        var exists = ExistsCall().Match(term);
        if (exists.Success)
        {
            var path = Expand(exists.Groups[1].Value);
            var full = path.Length == 0 ? "" : Path.GetFullPath(Path.Combine(_properties["MSBuildProjectDirectory"], SolutionDiscovery.NormalizeSeparators(path)));
            var result = full.Length != 0 && (File.Exists(full) || System.IO.Directory.Exists(full));
            return result != negate;
        }

        var comparison = Comparison().Match(term);
        if (comparison.Success)
        {
            var left = Expand(comparison.Groups["left"].Value);
            var right = Expand(comparison.Groups["right"].Value);
            var equal = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            return (comparison.Groups["op"].Value == "==" ? equal : !equal) != negate;
        }

        if (SplitComparison(term) is var (leftSide, op, rightSide))
        {
            // A side holding a quoted property function: '$([MSBuild]::GetTargetFrameworkIdentifier('$(TargetFramework)'))'
            var equal = string.Equals(Expand(Unquote(leftSide)), Expand(Unquote(rightSide)), StringComparison.OrdinalIgnoreCase);
            return (op == "==" ? equal : !equal) != negate;
        }

        var literal = Unquote(term);
        if (bool.TryParse(Expand(literal), out var value))
        {
            return value != negate;
        }

        throw new FormatException($"Unsupported condition: {term}");
    }

    /// <summary>Splits <c>left == right</c> at the operator outside parentheses.</summary>
    private static (string Left, string Op, string Right)? SplitComparison(string term)
    {
        var depth = 0;
        for (var i = 0; i + 1 < term.Length; i++)
        {
            var c = term[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (depth == 0 && c is '=' or '!' && term[i + 1] == '=')
            {
                return (term[..i].Trim(), term.Substring(i, 2), term[(i + 2)..].Trim());
            }
        }

        return null;
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' ? value[1..^1] : value;

    /// <summary>Splits on a keyword (case-insensitive) outside quotes and parentheses.</summary>
    private static List<string> SplitTopLevel(string value, string separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\'')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && c == '(')
            {
                depth++;
            }
            else if (!inQuote && c == ')')
            {
                depth--;
            }
            else if (!inQuote && depth == 0 && string.Compare(value, i, separator, 0, separator.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                parts.Add(value[start..i]);
                i += separator.Length - 1;
                start = i + 1;
            }
        }

        parts.Add(value[start..]);
        return parts;
    }

    [GeneratedRegex(@"^Exists\(\s*'([^']*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExistsCall();

    [GeneratedRegex(@"^'(?<left>[^']*)'\s*(?<op>==|!=)\s*'(?<right>[^']*)'$")]
    private static partial Regex Comparison();

    // ========================================
    // Compilation options
    // ========================================

    private static CSharpLanguageVersion ParseLanguageVersion(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "" or "latest" or "latestmajor" or "preview" or "default")
        {
            return CSharpLanguageVersion.Latest;
        }

        if (normalized is "iso-1")
        {
            return CSharpLanguageVersion.CSharp1;
        }

        if (normalized is "iso-2")
        {
            return CSharpLanguageVersion.CSharp2;
        }

        var parts = normalized.Split('.');
        if (int.TryParse(parts[0], out var major))
        {
            var minor = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
            var number = major >= 8 ? major * 100 : major == 7 && minor != 0 ? 700 + minor : major;
            if (Enum.IsDefined(typeof(CSharpLanguageVersion), number))
            {
                return (CSharpLanguageVersion)number;
            }
        }

        return CSharpLanguageVersion.Latest;
    }

    /// <summary>DefineConstants plus the symbols the SDK derives from the configuration and target framework.</summary>
    private List<string> PreprocessorSymbols(bool isSdk, string targetFramework)
    {
        var symbols = new List<string>();
        void Add(string symbol)
        {
            if (symbol.Length != 0 && !symbols.Contains(symbol, StringComparer.Ordinal))
            {
                symbols.Add(symbol);
            }
        }

        foreach (var symbol in Expand("$(DefineConstants)").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Add(symbol);
        }

        if (isSdk)
        {
            if (string.Equals(_properties["Configuration"], "Debug", StringComparison.OrdinalIgnoreCase))
            {
                Add("DEBUG");
            }
            else if (string.Equals(_properties["Configuration"], "Release", StringComparison.OrdinalIgnoreCase))
            {
                Add("RELEASE");
            }

            Add("TRACE");
            foreach (var symbol in TargetFrameworkSymbols(targetFramework))
            {
                Add(symbol);
            }
        }

        return symbols;
    }

    /// <summary>The symbols the SDK defines for a target framework: <c>NET</c>, <c>NET8_0</c>, <c>NET8_0_OR_GREATER</c>...</summary>
    internal static IEnumerable<string> TargetFrameworkSymbols(string targetFramework)
    {
        var framework = targetFramework.ToLowerInvariant();
        var platform = "";
        var dash = framework.IndexOf('-');
        if (dash >= 0)
        {
            platform = framework[(dash + 1)..];
            framework = framework[..dash];
        }

        var match = TargetFrameworkName().Match(framework);
        if (!match.Success)
        {
            yield break;
        }

        var name = match.Groups["name"].Value;
        var version = match.Groups["version"].Value;
        if (name == "net" && !version.Contains('.'))
        {
            // .NET Framework: net48, net472
            var digits = version;
            yield return "NETFRAMEWORK";
            yield return "NET" + digits;
            foreach (var known in (string[])["20", "30", "35", "40", "45", "451", "452", "46", "461", "462", "47", "471", "472", "48", "481"])
            {
                if (string.CompareOrdinal(known.PadRight(3, '0'), digits.PadRight(3, '0')) <= 0)
                {
                    yield return $"NET{known}_OR_GREATER";
                }
            }

            yield break;
        }

        var versionParts = version.Split('.');
        if (!int.TryParse(versionParts[0], out var major))
        {
            yield break;
        }

        var minor = versionParts.Length > 1 && int.TryParse(versionParts[1], out var m) ? m : 0;
        if (name == "netstandard")
        {
            yield return "NETSTANDARD";
            yield return $"NETSTANDARD{major}_{minor}";
            foreach (var (knownMajor, knownMinor) in new (int, int)[] {(1, 0), (1, 1), (1, 2), (1, 3), (1, 4), (1, 5), (1, 6), (2, 0), (2, 1) })
            {
                if (knownMajor < major || (knownMajor == major && knownMinor <= minor))
                {
                    yield return $"NETSTANDARD{knownMajor}_{knownMinor}_OR_GREATER";
                }
            }

            yield break;
        }

        // .NET Core and .NET 5+
        yield return "NETCOREAPP";
        yield return major >= 5 ? $"NET{major}_{minor}" : $"NETCOREAPP{major}_{minor}";
        if (major >= 5)
        {
            yield return "NET";
        }

        foreach (var (knownMajor, knownMinor) in new (int, int)[] {(1, 0), (1, 1), (2, 0), (2, 1), (2, 2), (3, 0), (3, 1) })
        {
            if (knownMajor < major || (knownMajor == major && knownMinor <= minor))
            {
                yield return $"NETCOREAPP{knownMajor}_{knownMinor}_OR_GREATER";
            }
        }

        for (var known = 5; known <= major; known++)
        {
            yield return $"NET{known}_0_OR_GREATER";
        }

        var platformName = PlatformName().Match(platform);
        if (platformName.Success)
        {
            yield return platformName.Groups[1].Value.ToUpperInvariant();
        }
    }

    [GeneratedRegex(@"^(?<name>netcoreapp|netstandard|net)(?<version>[0-9.]+)$")]
    private static partial Regex TargetFrameworkName();

    [GeneratedRegex(@"^([a-z]+)")]
    private static partial Regex PlatformName();
}
