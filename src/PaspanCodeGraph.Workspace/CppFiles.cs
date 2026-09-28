using System.Text.Json;
using PaspanParsers.Cpp;

namespace PaspanCodeGraph.Workspace;

/// <summary>
/// C++ workspaces without MSBuild: a folder of sources, or a compilation database (<c>compile_commands.json</c>,
/// written by CMake, Meson, Bear and others) with the macros, include directories and standard of each file.
/// Headers are not listed in a compilation database: those under the directory of the sources are added.
/// </summary>
public static class CppFiles
{
    /// <summary>Extensions of C++ sources and headers; C files are read as C++.</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cpp", ".cc", ".cxx", ".c++", ".cp", ".cppm", ".ixx", ".c",
        ".h", ".hh", ".hpp", ".hxx", ".h++", ".inl", ".ipp", ".tpp", ".tcc",
    };

    /// <summary>Directories that hold build output or tools rather than sources.</summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "builds", "out", "bin", "obj", "node_modules", "_build", "Debug", "Release", "x64", "x86", "CMakeFiles",
    };

    public static bool IsCppFile(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>A compilation database in a directory or in one of the usual build directories under it, or null.</summary>
    public static string? FindCompilationDatabase(string directory)
    {
        var direct = Path.Combine(directory, "compile_commands.json");
        if (File.Exists(direct))
        {
            return direct;
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith("build", StringComparison.OrdinalIgnoreCase) || name.StartsWith("cmake-build", StringComparison.OrdinalIgnoreCase) || name == "out")
                {
                    var candidate = Path.Combine(sub, "compile_commands.json");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    // out/build/<preset>/compile_commands.json
                    foreach (var preset in Directory.EnumerateDirectories(sub).Concat(Directory.Exists(Path.Combine(sub, "build")) ? Directory.EnumerateDirectories(Path.Combine(sub, "build")) : []))
                    {
                        candidate = Path.Combine(preset, "compile_commands.json");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>Whether a directory holds C++ sources, looking a few levels down.</summary>
    public static bool HasSources(string directory) => EnumerateSources(directory, maxDepth: 4).Any();

    /// <summary>The C++ files under a directory, skipping hidden and build directories, in a stable order.</summary>
    public static IEnumerable<string> EnumerateSources(string directory, int maxDepth = int.MaxValue, IReadOnlySet<string>? excluded = null)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((directory, 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (IsCppFile(file))
                {
                    yield return file;
                }
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            Array.Sort(directories, StringComparer.Ordinal);
            for (var i = directories.Length - 1; i >= 0; i--)
            {
                var sub = directories[i];
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkippedDirectories.Contains(name) || name.StartsWith("cmake-build", StringComparison.OrdinalIgnoreCase)
                    || File.Exists(Path.Combine(sub, "CMakeCache.txt")) || (excluded != null && excluded.Contains(sub)))
                {
                    continue;
                }

                pending.Push((sub, depth + 1));
            }
        }
    }

    /// <summary>A folder of C++ sources as one project named after the folder.</summary>
    public static ProjectModel ReadFolder(string directory)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var sources = EnumerateSources(directory).ToList();
        var includes = new List<string> { directory };
        foreach (var name in new[] { "include", "src", "source", "lib" })
        {
            var candidate = Path.Combine(directory, name);
            if (Directory.Exists(candidate))
            {
                includes.Add(candidate);
            }
        }

        var macros = PredefinedMacros(msvc: false, "x64");
        return new ProjectModel(Path.GetFileName(directory), directory, "", PaspanParsers.CSharp.CSharpLanguageVersion.Latest, [], sources, [], [])
        {
            Language = SourceLanguage.Cpp,
            Macros = macros,
            IncludeDirectories = includes,
            CppLanguageVersion = CppLanguageVersion.Latest,
        };
    }

    /// <summary>
    /// A compilation database as one project: the files it lists with their options, and the headers under the
    /// directory of its sources (options of the most common file).
    /// </summary>
    public static ProjectModel ReadCompilationDatabase(string path, string? rootDirectory = null)
    {
        path = Path.GetFullPath(path);
        var problems = new List<LoadProblem>();
        var options = new Dictionary<string, CppFileOptions>(SymbolIndexBuilder.PathComparer);
        using (var stream = File.OpenRead(path))
        using (var json = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
        {
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                var directory = entry.TryGetProperty("directory", out var d) ? d.GetString() ?? "" : Path.GetDirectoryName(path)!;
                if (!entry.TryGetProperty("file", out var f) || f.GetString() is not { } file)
                {
                    continue;
                }

                var full = Path.GetFullPath(Path.Combine(directory, file));
                List<string> arguments;
                if (entry.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array)
                {
                    arguments = a.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                }
                else if (entry.TryGetProperty("command", out var c) && c.GetString() is { } command)
                {
                    arguments = SplitCommand(command);
                }
                else
                {
                    arguments = [];
                }

                if (IsCppFile(full) && !options.ContainsKey(full))
                {
                    options[full] = ParseArguments(arguments, directory);
                }
            }
        }

        var sources = options.Keys.ToList();
        var root = rootDirectory ?? CommonDirectory(sources) ?? Path.GetDirectoryName(path)!;

        // The headers next to the sources, which the database does not list
        var buildDirectory = Path.GetDirectoryName(path)!;
        var listed = new HashSet<string>(sources, SymbolIndexBuilder.PathComparer);
        foreach (var file in EnumerateSources(root, excluded: new HashSet<string>([buildDirectory], SymbolIndexBuilder.PathComparer)))
        {
            if (listed.Add(file) && IsHeader(file))
            {
                sources.Add(file);
            }
        }

        // The options most files have are the project's, for headers; the others are kept per file
        var common = options.Values.GroupBy(o => o.Print()).OrderByDescending(g => g.Count()).FirstOrDefault()?.First()
            ?? new CppFileOptions(PredefinedMacros(msvc: false, "x64"), [], CppLanguageVersion.Latest);
        var perFile = options.Where(o => o.Value.Print() != common.Print()).ToDictionary(o => o.Key, o => o.Value, SymbolIndexBuilder.PathComparer);
        if (options.Count == 0)
        {
            problems.Add(LoadProblem.Warning($"The compilation database lists no C++ files: {path}", path));
        }

        return new ProjectModel(Path.GetFileName(root), path, "", PaspanParsers.CSharp.CSharpLanguageVersion.Latest, [], sources, [], problems)
        {
            Language = SourceLanguage.Cpp,
            Macros = common.Macros,
            IncludeDirectories = common.IncludeDirectories,
            CppLanguageVersion = common.LanguageVersion,
            FileOptions = perFile,
        };
    }

    public static bool IsHeader(string path) => Path.GetExtension(path).ToLowerInvariant() is ".h" or ".hh" or ".hpp" or ".hxx" or ".h++" or ".inl" or ".ipp" or ".tpp" or ".tcc";

    private static string? CommonDirectory(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        var common = Path.GetDirectoryName(files[0])!;
        foreach (var file in files.Skip(1))
        {
            while (!file.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Path.GetDirectoryName(common) is { } parent)
            {
                common = parent;
            }
        }

        return common;
    }

    /// <summary>Splits a command line as a shell does, with quotes and backslashes.</summary>
    internal static List<string> SplitCommand(string command)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var inArgument = false;
        char quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"' && i + 1 < command.Length && command[i + 1] is '"' or '\\')
                {
                    current.Append(command[++i]);
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inArgument)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    inArgument = false;
                }

                continue;
            }

            inArgument = true;
            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '\\' && i + 1 < command.Length)
            {
                current.Append(command[++i]);
            }
            else
            {
                current.Append(c);
            }
        }

        if (inArgument)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    /// <summary>The macros, include directories and standard of a compiler command line (GCC, clang or MSVC style).</summary>
    internal static CppFileOptions ParseArguments(IReadOnlyList<string> arguments, string directory)
    {
        var msvc = arguments.Count > 0 && Path.GetFileNameWithoutExtension(arguments[0]).ToLowerInvariant() is "cl" or "clang-cl";
        var macros = PredefinedMacros(msvc, "x64");
        var includes = new List<string>();
        var standard = CppLanguageVersion.Cpp17;
        string? Value(ref int i, string argument, string prefix)
        {
            if (argument.Length > prefix.Length)
            {
                return argument[prefix.Length..];
            }

            return i + 1 < arguments.Count ? arguments[++i] : null;
        }

        for (var i = 1; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.StartsWith("-D", StringComparison.Ordinal) || argument.StartsWith("/D", StringComparison.Ordinal))
            {
                if (Value(ref i, argument, "-D") is { } definition)
                {
                    Define(macros, definition);
                }
            }
            else if (argument.StartsWith("-U", StringComparison.Ordinal) || argument.StartsWith("/U", StringComparison.Ordinal))
            {
                if (Value(ref i, argument, "-U") is { } name)
                {
                    macros.Remove(name);
                }
            }
            else if (argument is "-isystem" or "-iquote" or "-idirafter" && i + 1 < arguments.Count)
            {
                includes.Add(Path.GetFullPath(Path.Combine(directory, arguments[++i])));
            }
            else if (argument.StartsWith("-I", StringComparison.Ordinal) || argument.StartsWith("/I", StringComparison.Ordinal))
            {
                if (Value(ref i, argument, "-I") is { } include)
                {
                    includes.Add(Path.GetFullPath(Path.Combine(directory, include)));
                }
            }
            else if (argument.StartsWith("-std=", StringComparison.Ordinal) || argument.StartsWith("/std:", StringComparison.Ordinal))
            {
                standard = ParseStandard(argument[5..]) ?? standard;
            }
        }

        macros["__cplusplus"] = CplusplusValue(standard);
        return new CppFileOptions(macros, includes, standard);
    }

    /// <summary>Adds <c>NAME</c>, <c>NAME=VALUE</c> or <c>F(x)=VALUE</c> as <c>-D</c> does.</summary>
    public static void Define(Dictionary<string, string> macros, string definition)
    {
        var equals = definition.IndexOf('=');
        if (equals < 0)
        {
            if (definition.Length > 0)
            {
                macros[definition] = "1";
            }
        }
        else if (equals > 0)
        {
            macros[definition[..equals]] = definition[(equals + 1)..];
        }
    }

    /// <summary>The standard of <c>c++17</c>, <c>gnu++20</c>, <c>c++2b</c>, <c>stdcpp20</c> or <c>c++latest</c>; null for another value.</summary>
    public static CppLanguageVersion? ParseStandard(string value)
    {
        value = value.ToLowerInvariant().Replace("stdcpp", "c++").Replace("gnu++", "c++");
        return value switch
        {
            "c++11" or "c++0x" => CppLanguageVersion.Cpp11,
            "c++14" or "c++1y" => CppLanguageVersion.Cpp14,
            "c++17" or "c++1z" => CppLanguageVersion.Cpp17,
            "c++20" or "c++2a" => CppLanguageVersion.Cpp20,
            "c++23" or "c++2b" or "c++26" or "c++2c" or "c++latest" or "c++23preview" => CppLanguageVersion.Cpp23,
            _ => null,
        };
    }

    public static string CplusplusValue(CppLanguageVersion standard) => standard switch
    {
        CppLanguageVersion.Cpp11 => "201103L",
        CppLanguageVersion.Cpp14 => "201402L",
        CppLanguageVersion.Cpp17 => "201703L",
        CppLanguageVersion.Cpp20 => "202002L",
        _ => "202302L",
    };

    /// <summary>
    /// The macros a compiler predefines, so that conditional code takes the branches of one configuration: MSVC on
    /// Windows for .vcxproj projects and <c>cl</c> commands, clang on Linux otherwise.
    /// </summary>
    public static Dictionary<string, string> PredefinedMacros(bool msvc, string platform)
    {
        var macros = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__cplusplus"] = CplusplusValue(CppLanguageVersion.Latest),
            ["__STDC_HOSTED__"] = "1",
        };
        if (msvc)
        {
            macros["_WIN32"] = "1";
            macros["_MSC_VER"] = "1940";
            macros["_MSVC_LANG"] = macros["__cplusplus"];
            if (platform is "x64" or "X64" or "ARM64")
            {
                macros["_WIN64"] = "1";
                macros[platform == "ARM64" ? "_M_ARM64" : "_M_X64"] = "1";
            }
            else
            {
                macros["_M_IX86"] = "600";
            }
        }
        else
        {
            foreach (var (name, value) in new[]
            {
                ("__clang__", "1"), ("__clang_major__", "18"), ("__clang_minor__", "1"), ("__GNUC__", "4"), ("__GNUC_MINOR__", "2"),
                ("__GNUC_PATCHLEVEL__", "1"), ("__GNUG__", "4"), ("__linux__", "1"), ("__linux", "1"), ("__unix__", "1"), ("__unix", "1"),
                ("__x86_64__", "1"), ("__x86_64", "1"), ("__amd64__", "1"), ("__LP64__", "1"), ("_LP64", "1"), ("__ELF__", "1"),
                ("__CHAR_BIT__", "8"), ("__SIZEOF_POINTER__", "8"), ("__SIZEOF_LONG__", "8"), ("__SIZEOF_INT__", "4"),
                ("__SIZEOF_LONG_LONG__", "8"), ("__SIZEOF_SHORT__", "2"), ("__SIZEOF_SIZE_T__", "8"), ("__SIZEOF_WCHAR_T__", "4"),
                ("__SIZEOF_INT128__", "16"), ("__SIZEOF_FLOAT__", "4"), ("__SIZEOF_DOUBLE__", "8"), ("__SIZEOF_LONG_DOUBLE__", "16"),
                ("__ORDER_LITTLE_ENDIAN__", "1234"), ("__ORDER_BIG_ENDIAN__", "4321"), ("__BYTE_ORDER__", "1234"),
                ("__INT_MAX__", "2147483647"), ("__LONG_MAX__", "9223372036854775807L"), ("__SCHAR_MAX__", "127"),
                ("__EXCEPTIONS", "1"), ("__GXX_RTTI", "1"), ("__STDC_UTF_16__", "1"), ("__STDC_UTF_32__", "1"),
            })
            {
                macros[name] = value;
            }
        }

        // The language's feature-test macros (those of the library come from <version>, which is not read)
        foreach (var (name, value) in new[]
        {
            ("__cpp_exceptions", "199711L"), ("__cpp_rtti", "199711L"), ("__cpp_constexpr", "202211L"), ("__cpp_if_constexpr", "201606L"),
            ("__cpp_inline_variables", "201606L"), ("__cpp_fold_expressions", "201603L"), ("__cpp_concepts", "202002L"),
            ("__cpp_consteval", "202211L"), ("__cpp_char8_t", "202207L"), ("__cpp_nontype_template_args", "201911L"),
            ("__cpp_deduction_guides", "201703L"), ("__cpp_structured_bindings", "201606L"), ("__cpp_variadic_templates", "200704L"),
            ("__cpp_rvalue_references", "200610L"), ("__cpp_lambdas", "200907L"), ("__cpp_generic_lambdas", "201707L"),
            ("__cpp_decltype_auto", "201304L"), ("__cpp_return_type_deduction", "201304L"), ("__cpp_user_defined_literals", "200809L"),
            ("__cpp_unicode_literals", "200710L"), ("__cpp_alias_templates", "200704L"), ("__cpp_variable_templates", "201304L"),
            ("__cpp_aggregate_nsdmi", "201304L"), ("__cpp_binary_literals", "201304L"), ("__cpp_digit_separators", "201309L"),
            ("__cpp_initializer_lists", "200806L"), ("__cpp_static_assert", "201411L"), ("__cpp_noexcept_function_type", "201510L"),
            ("__cpp_three_way_comparison", "201907L"), ("__cpp_designated_initializers", "201707L"), ("__cpp_impl_coroutine", "201902L"),
            ("__cpp_explicit_this_parameter", "202110L"),
        })
        {
            macros[name] = value;
        }

        return macros;
    }

    /// <summary>
    /// The directories of the system's headers, which <c>__has_include</c> looks in (headers are never read): those of
    /// GCC and clang on Linux and of the C library, when they exist.
    /// </summary>
    public static IReadOnlyList<string> SystemIncludeDirectories { get; } = FindSystemIncludeDirectories();

    private static List<string> FindSystemIncludeDirectories()
    {
        var result = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            return result;
        }

        try
        {
            var cxx = "/usr/include/c++";
            if (Directory.Exists(cxx))
            {
                // The newest version of libstdc++
                var newest = Directory.GetDirectories(cxx).OrderByDescending(d => int.TryParse(Path.GetFileName(d).Split('.')[0], out var v) ? v : 0).FirstOrDefault();
                if (newest != null)
                {
                    result.Add(newest);
                    result.AddRange(Directory.GetDirectories("/usr/include").Where(d => Path.GetFileName(d).EndsWith("-linux-gnu", StringComparison.Ordinal))
                        .Select(d => Path.Combine(d, "c++", Path.GetFileName(newest))).Where(Directory.Exists));
                }
            }

            result.AddRange(new[] { "/usr/local/include" }.Concat(Directory.Exists("/usr/include") ? Directory.GetDirectories("/usr/include").Where(d => Path.GetFileName(d).EndsWith("-linux-gnu", StringComparison.Ordinal)) : []).Append("/usr/include").Where(Directory.Exists));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>
    /// The workspace files the <c>#include</c> directives of a file name: next to the file for quoted names, then in
    /// the include directories, then any workspace file whose path ends with the name (the nearest one).
    /// </summary>
    public static IReadOnlyList<string> ResolveIncludes(
        string file, IEnumerable<PreprocessorDirective> directives, IReadOnlyList<string> includeDirectories, IReadOnlySet<string> workspaceFiles, IReadOnlyDictionary<string, List<string>> filesByName)
    {
        var result = new List<string>();
        foreach (var directive in directives)
        {
            if (directive.Kind is not (PreprocessorDirectiveKind.Include or PreprocessorDirectiveKind.IncludeNext or PreprocessorDirectiveKind.Import))
            {
                continue;
            }

            var arguments = directive.Arguments.Trim();
            if (arguments.Length < 3 || arguments[0] is not ('"' or '<'))
            {
                continue;
            }

            var close = arguments.IndexOf(arguments[0] == '"' ? '"' : '>', 1);
            if (close < 0)
            {
                continue;
            }

            var name = arguments[1..close];
            var resolved = ResolveInclude(file, name, arguments[0] == '"', includeDirectories, workspaceFiles, filesByName);
            if (resolved != null && !result.Contains(resolved, SymbolIndexBuilder.PathComparer) && !SymbolIndexBuilder.PathComparer.Equals(resolved, file))
            {
                result.Add(resolved);
            }
        }

        return result;
    }

    /// <summary>The workspace file an include names (<c>"name"</c> or <c>&lt;name&gt;</c>, with its quotes or brackets), or null.</summary>
    public static string? ResolveInclude(string file, string name, IReadOnlyList<string> includeDirectories, IReadOnlySet<string> workspaceFiles, IReadOnlyDictionary<string, List<string>> filesByName) =>
        name.Length > 2 ? ResolveInclude(file, name[1..^1], name[0] == '"', includeDirectories, workspaceFiles, filesByName) : null;

    /// <summary>The workspace files by file name, for <see cref="ResolveInclude(string, string, IReadOnlyList{string}, IReadOnlySet{string}, IReadOnlyDictionary{string, List{string}})"/>.</summary>
    public static Dictionary<string, List<string>> ByName(IEnumerable<string> files)
    {
        var byName = new Dictionary<string, List<string>>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (!byName.TryGetValue(name, out var list))
            {
                byName[name] = list = [];
            }

            list.Add(file);
        }

        return byName;
    }

    private static string? ResolveInclude(string file, string name, bool quoted, IReadOnlyList<string> includeDirectories, IReadOnlySet<string> workspaceFiles, IReadOnlyDictionary<string, List<string>> filesByName)
    {
        var relative = SolutionDiscovery.NormalizeSeparators(name);
        if (quoted)
        {
            var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, relative));
            if (workspaceFiles.Contains(local))
            {
                return local;
            }
        }

        foreach (var directory in includeDirectories)
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, relative));
            if (workspaceFiles.Contains(candidate))
            {
                return candidate;
            }
        }

        if (!filesByName.TryGetValue(Path.GetFileName(relative), out var sameName))
        {
            return null;
        }

        var suffix = Path.DirectorySeparatorChar + relative;
        return sameName
            .Where(f => f.EndsWith(suffix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(f => CommonPrefixLength(f, file))
            .ThenBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var length = 0;
        while (length < a.Length && length < b.Length && a[length] == b[length])
        {
            length++;
        }

        return length;
    }
}
