using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PaspanParsers;

namespace PaspanCodeGraph.Tests.Cpp;

/// <summary>A position in a file: a full path and a byte offset.</summary>
public readonly record struct ClangLocation(string File, int Offset)
{
    private static readonly ConcurrentDictionary<string, LineMap> Lines = new();

    public override string ToString()
    {
        var lines = Lines.GetOrAdd(File, f => new LineMap(System.IO.File.Exists(f) ? Utf8Source.WithoutByteOrderMark(System.IO.File.ReadAllBytes(f)).Span : [], unicodeLineBreaks: false));
        var (line, column) = Offset <= lines.GetLineSpan(lines.LineCount).End ? lines.GetLineAndColumn(Offset) : (0, 0);
        return $"{Path.GetFileName(File)}:{line}:{column}";
    }
}

/// <summary>A declaration of the kinds the code graph declares: where its name is and the kind of symbol it is.</summary>
public sealed record ClangDeclaration(ClangLocation Location, SymbolKind Kind, string Name, bool IsDefinition);

/// <summary>A reference clang binds: where the name is and where the declaration it refers to is.</summary>
public sealed record ClangReference(ClangLocation Location, ClangLocation Target, string Name);

/// <summary>
/// Runs clang (<c>clang++</c>, or <c>CLANG_PATH</c>) over the source files of a C++ workspace and reads its JSON AST:
/// the declarations of the workspace's files outside function bodies, and the references to them from
/// <c>DeclRefExpr</c> and <c>MemberExpr</c> nodes (and so from calls, member accesses and operator calls). A
/// reference counts where the source spells it: implicit code (implicit members and conversions, the calls a
/// range-based for or a coroutine makes) and the bodies of macros are left out.
/// </summary>
public sealed class ClangOracle
{
    private readonly string _root;
    private readonly Dictionary<string, ClangLocation> _declarationLocations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _previous = new(StringComparer.Ordinal);
    private readonly HashSet<ClangDeclaration> _declarations = [];
    private readonly List<(string Id, SymbolKind Kind, string Name, ClangLocation Location, bool IsDefinition, string Parent)> _redeclarable = [];
    private readonly HashSet<(string File, int Begin, int End)> _dependent = [];
    private readonly HashSet<(string File, int Begin, int End, string TargetId, string Name)> _references = [];
    private readonly Dictionary<string, byte[]> _texts = new(StringComparer.Ordinal);

    private ClangOracle(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public static string ClangPath { get; } = FindClang();

    public static bool IsAvailable => ClangPath != null;

    /// <summary>The files clang could not compile, with its first error.</summary>
    public List<string> Failures { get; } = [];

    public IReadOnlyCollection<ClangDeclaration> Declarations => _declarations;

    public List<ClangReference> References { get; } = [];

    /// <summary>
    /// The ranges of the names clang leaves unbound in templates (dependent member accesses and calls), where a
    /// reference the graph finds cannot be checked.
    /// </summary>
    public IReadOnlyCollection<(string File, int Begin, int End)> DependentRanges => _dependent;

    public static void RequireClang()
    {
        if (!IsAvailable)
        {
            Assert.Inconclusive("clang++ was not found: install clang or set CLANG_PATH to run the C++ oracle.");
        }
    }

    /// <summary>Reads the ASTs of <paramref name="sources"/> (the files compiled on their own) with the include directories.</summary>
    public static ClangOracle Run(string root, IEnumerable<string> sources, IEnumerable<string> includeDirectories)
    {
        var oracle = new ClangOracle(root);
        var includes = includeDirectories.Select(i => "-I" + Path.GetFullPath(i)).ToList();
        foreach (var source in sources)
        {
            var json = Dump(Path.GetFullPath(source), includes, out var errors);
            if (json == null)
            {
                oracle.Failures.Add($"{source}: {errors.Split('\n').FirstOrDefault(l => l.Contains("error", StringComparison.Ordinal))?.Trim()}");
                continue;
            }

            oracle.Read(json);
        }

        oracle.Complete();
        return oracle;
    }

    private static string Dump(string source, List<string> includes, out string errors)
    {
        var start = new ProcessStartInfo(ClangPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(source),
        };
        foreach (var argument in new[] { "-std=c++23", "-w", "-fno-color-diagnostics", "-fsyntax-only", "-Xclang", "-ast-dump=json" }.Concat(includes).Append(source))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errorsTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        errors = errorsTask.Result;
        return process.ExitCode == 0 ? output : null;
    }

    private byte[] Text(string file)
    {
        if (!_texts.TryGetValue(file, out var text))
        {
            _texts[file] = text = Utf8Source.WithoutByteOrderMark(File.ReadAllBytes(file)).ToArray();
        }

        return text;
    }

    // ========================================
    // Reading the dump
    // ========================================

    /// <summary>The file clang omits when it did not change since the location it printed before.</summary>
    private string _file = "";

    private void Read(string json)
    {
        _file = "";
        var root = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 4096 })!.AsObject();
        Walk(root, inBody: false, inRecord: false, inFriend: false, implicitCode: false, parent: "");
    }

    /// <summary>A location object: updates the file; returns the position, or null for a location in a macro body.</summary>
    private ClangLocation? Location(JsonNode node)
    {
        if (node is not JsonObject location || location.Count == 0)
        {
            return null;
        }

        if (location["spellingLoc"] is JsonObject spelling)
        {
            var spelled = Location(spelling);
            var expansion = location["expansionLoc"] as JsonObject;
            var expanded = expansion != null ? Location(expansion) : null;
            var isArgument = expansion?["isMacroArgExpansion"]?.GetValue<bool>() == true;
            return isArgument ? spelled : expanded is { } e && spelled is { } s && e == s ? s : null;
        }

        if (location["file"] is JsonNode file)
        {
            _file = Path.GetFullPath(file.GetValue<string>(), _root);
        }

        if (location["offset"] is not JsonNode offset || _file.Length == 0)
        {
            return null;
        }

        return new ClangLocation(_file, offset.GetValue<int>());
    }

    private readonly Dictionary<string, bool> _inWorkspace = new(StringComparer.Ordinal);

    /// <summary>A file under the workspace's directory (not a pseudo-file such as clang's "&lt;scratch space&gt;").</summary>
    private bool InWorkspace(ClangLocation location)
    {
        if (!_inWorkspace.TryGetValue(location.File, out var inside))
        {
            _inWorkspace[location.File] = inside = location.File.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(location.File);
        }

        return inside;
    }

    /// <summary>The workspace files clang read: the sources and the headers they include.</summary>
    public IReadOnlySet<string> Files => _inWorkspace.Where(f => f.Value).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

    private void Walk(JsonObject node, bool inBody, bool inRecord, bool inFriend, bool implicitCode, string parent)
    {
        var kind = node["kind"]?.GetValue<string>() ?? "";
        ClangLocation? location = null;
        ClangLocation? rangeBegin = null;
        ClangLocation? rangeEnd = null;
        foreach (var (key, value) in node)
        {
            switch (key)
            {
                case "loc":
                    location = Location(value);
                    break;
                case "range" when value is JsonObject range:
                    if (range["begin"] is { } begin)
                    {
                        rangeBegin = Location(begin);
                    }

                    if (range["end"] is { } end)
                    {
                        rangeEnd = Location(end);
                    }

                    break;
            }
        }

        var isImplicit = node["isImplicit"]?.GetValue<bool>() == true;
        var id = node["id"]?.GetValue<string>();
        if (id != null && node["previousDecl"]?.GetValue<string>() is { } previous)
        {
            _previous[id] = previous;
        }

        if (!inBody && !isImplicit && !implicitCode && location is { } declarationLocation && InWorkspace(declarationLocation) && id != null)
        {
            Declare(node, kind, id, declarationLocation, inRecord, inFriend, rangeBegin, node["parentDeclContextId"]?.GetValue<string>() ?? parent);
        }

        // An expression has no location but its range, in which the name is the last token (or ends it)
        if (!implicitCode && rangeEnd is { } end2 && InWorkspace(end2) && rangeBegin is { } begin2 && begin2.File == end2.File)
        {
            switch (kind)
            {
                case "DeclRefExpr" when node["referencedDecl"] is JsonObject referenced:
                    _references.Add((end2.File, begin2.Offset, end2.Offset, referenced["id"]!.GetValue<string>(), referenced["name"]?.GetValue<string>() ?? ""));
                    break;
                case "MemberExpr" when node["referencedMemberDecl"]?.GetValue<string>() is { } member:
                    _references.Add((end2.File, begin2.Offset, end2.Offset, member, node["name"]?.GetValue<string>() ?? ""));
                    break;
                case "CXXDependentScopeMemberExpr" or "UnresolvedLookupExpr" or "UnresolvedMemberExpr" or "DependentScopeDeclRefExpr" or "CXXUnresolvedConstructExpr":
                    _dependent.Add((end2.File, begin2.Offset, end2.Offset + TokenLength(Text(end2.File), end2.Offset)));
                    break;
            }
        }

        var body = inBody || kind.EndsWith("Stmt", StringComparison.Ordinal) || kind.EndsWith("Expr", StringComparison.Ordinal) || kind == "CXXCtorInitializer";
        var record = kind is "CXXRecordDecl" or "RecordDecl" or "ClassTemplateSpecializationDecl" or "ClassTemplatePartialSpecializationDecl";
        var childImplicit = implicitCode || (isImplicit && kind is not ("CXXRecordDecl" or "ClassTemplateSpecializationDecl"));
        foreach (var (key, value) in node)
        {
            if (key is "inner" or "inits" && value is JsonArray children)
            {
                foreach (var child in children)
                {
                    if (child is JsonObject childObject)
                    {
                        Walk(childObject, body, record || (inRecord && kind.EndsWith("TemplateDecl", StringComparison.Ordinal)), kind == "FriendDecl" || (inFriend && kind.EndsWith("TemplateDecl", StringComparison.Ordinal)), childImplicit,
                            record || kind == "NamespaceDecl" ? id ?? parent : parent);
                    }
                }
            }
        }
    }

    private void Declare(JsonObject node, string kind, string id, ClangLocation location, bool inRecord, bool inFriend, ClangLocation? begin, string parent)
    {
        // References to templates are to the declaration of their pattern, at the same place
        if (kind is "FunctionTemplateDecl" or "VarTemplateDecl" or "ClassTemplateDecl" or "TypeAliasTemplateDecl")
        {
            _declarationLocations.TryAdd(id, location);
            return;
        }

        var name = node["name"]?.GetValue<string>();
        var isOperator = name != null && IsOperatorName(name);
        var isRecord = kind is "CXXRecordDecl" or "RecordDecl" or "ClassTemplateSpecializationDecl" or "ClassTemplatePartialSpecializationDecl";
        SymbolKind? symbolKind = kind switch
        {
            "NamespaceDecl" => SymbolKind.Namespace,
            _ when isRecord && name is { Length: > 0 } && !inFriend =>
                node["tagUsed"]?.GetValue<string>() switch { "struct" => SymbolKind.Struct, "union" => SymbolKind.Union, _ => SymbolKind.Class },
            "EnumDecl" when name is { Length: > 0 } => SymbolKind.Enum,
            "EnumConstantDecl" => SymbolKind.EnumMember,
            "FunctionDecl" => isOperator ? SymbolKind.Operator : SymbolKind.Function,
            "CXXMethodDecl" => isOperator ? SymbolKind.Operator : SymbolKind.Method,
            "CXXConversionDecl" => SymbolKind.Operator,
            "CXXConstructorDecl" => SymbolKind.Constructor,
            "CXXDestructorDecl" => SymbolKind.Destructor,
            "FieldDecl" when name is { Length: > 0 } => SymbolKind.Field,
            "VarDecl" or "VarTemplateSpecializationDecl" or "VarTemplatePartialSpecializationDecl" =>
                inRecord || node["parentDeclContextId"] != null ? SymbolKind.Field : SymbolKind.Variable,
            "TypedefDecl" or "TypeAliasDecl" => SymbolKind.TypeAlias,
            "ConceptDecl" => SymbolKind.Concept,
            _ => null,
        };
        if (symbolKind is not { } symbol)
        {
            return;
        }

        _declarationLocations.TryAdd(id, location);

        // An explicit instantiation (template struct Box<long>;) declares nothing new
        if (kind == "ClassTemplateSpecializationDecl" && begin is { } start && IsExplicitInstantiation(start))
        {
            return;
        }

        var isDefinition = kind switch
        {
            _ when isRecord => node["completeDefinition"]?.GetValue<bool>() == true,
            "EnumDecl" => HasBody(location),
            _ => true,
        };
        if (symbol.IsType() && symbol != SymbolKind.TypeAlias)
        {
            // A class is declared where it is defined; a class only declared, where it is declared
            _redeclarable.Add((id, symbol, name ?? "", location, isDefinition, parent));
            return;
        }

        _declarations.Add(new ClangDeclaration(location, symbol, name ?? "", true));
    }

    private static bool IsOperatorName(string name) =>
        name.StartsWith("operator", StringComparison.Ordinal) && (name.Length == 8 || (!char.IsLetterOrDigit(name[8]) && name[8] != '_') || name[8] == ' ');

    /// <summary>Whether the text after a name has a '{' before a ';': an enum with its enumerators.</summary>
    private bool HasBody(ClangLocation location)
    {
        var text = Text(location.File);
        for (var i = location.Offset; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                return true;
            }

            if (text[i] == ';')
            {
                return false;
            }
        }

        return false;
    }

    private bool IsExplicitInstantiation(ClangLocation begin)
    {
        var text = Text(begin.File);
        var head = Encoding.UTF8.GetString(text, begin.Offset, Math.Min(64, text.Length - begin.Offset));
        return System.Text.RegularExpressions.Regex.IsMatch(head, @"^(extern\s+)?template\s+(struct|class|union)\b");
    }

    private string Canonical(string id)
    {
        for (var i = 0; i < 100 && _previous.TryGetValue(id, out var previous); i++)
        {
            id = previous;
        }

        return id;
    }

    private void Complete()
    {
        // A member class of a class template defined outside it is not linked to its declaration: match them by name
        var defined = _redeclarable.Where(r => r.IsDefinition).Select(r => Canonical(r.Id)).ToHashSet();
        var definedNames = _redeclarable.Where(r => r.IsDefinition).Select(r => (r.Parent, r.Name)).ToHashSet();
        foreach (var (id, kind, name, location, isDefinition, parent) in _redeclarable)
        {
            if (isDefinition || !(defined.Contains(Canonical(id)) || definedNames.Contains((parent, name))))
            {
                _declarations.Add(new ClangDeclaration(location, kind, name, isDefinition));
            }
        }

        var declared = _declarations.Select(d => d.Location).ToHashSet();
        foreach (var (file, begin, end, targetId, name) in _references)
        {
            if (_declarationLocations.TryGetValue(targetId, out var target) && declared.Contains(target) && NamePosition(file, begin, end, name) is { } position)
            {
                References.Add(new ClangReference(new ClangLocation(file, position), target, name));
            }
        }
    }

    /// <summary>
    /// Where the source spells a reference in the range of its expression: the last occurrence of an identifier, of
    /// <c>~</c> for a destructor, of the <c>operator</c> keyword for an operator named so, and the operator's token
    /// (the range's last token) for an operator used as one. Null when the source does not spell it there.
    /// </summary>
    private int? NamePosition(string file, int begin, int end, string name)
    {
        var text = Text(file);
        if (end >= text.Length || begin > end)
        {
            return null;
        }

        var range = text.AsSpan(begin, end - begin + TokenLength(text, end));
        if (name.StartsWith('~'))
        {
            var tilde = range.LastIndexOf((byte)'~');
            return tilde >= 0 ? begin + tilde : null;
        }

        var isOperator = IsOperatorName(name);
        var word = Encoding.UTF8.GetBytes(isOperator ? "operator" : name);
        for (var i = range.Length - word.Length; i >= 0; i--)
        {
            if (range[i..].StartsWith(word) && (i == 0 || !IsIdentifierByte(range[i - 1])) && (i + word.Length == range.Length || !IsIdentifierByte(range[i + word.Length])))
            {
                return begin + i;
            }
        }

        if (!isOperator)
        {
            return null;
        }

        // An operator used as one: the range ends at its token
        var symbol = name[8..].Trim();
        var token = text[end];
        return symbol switch
        {
            "()" => token == ')' ? end : null,
            "[]" => token == ']' ? end : null,
            _ when symbol.Length > 0 && !char.IsLetter(symbol[0]) && (byte)symbol[0] == token => end,
            _ => null,
        };
    }

    private static int TokenLength(byte[] text, int offset)
    {
        var length = 1;
        if (IsIdentifierByte(text[offset]))
        {
            while (offset + length < text.Length && IsIdentifierByte(text[offset + length]))
            {
                length++;
            }
        }

        return length;
    }

    private static bool IsIdentifierByte(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or >= 0x80;

    private static string FindClang()
    {
        var configured = Environment.GetEnvironmentVariable("CLANG_PATH");
        if (!string.IsNullOrEmpty(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "clang++.exe" } : ["clang++"])
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
