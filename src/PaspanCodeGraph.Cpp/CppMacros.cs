using System.Text;

namespace PaspanCodeGraph.Cpp;

/// <summary>
/// What is done with the macros of a C++ workspace before parsing, since the parser does not expand macros and
/// fails where the code uses one in a syntactic position. From every <c>#define</c> of the workspace (in any
/// branch) and the macros of the projects:
/// <list type="bullet">
/// <item>Object-like macros that only decorate declarations in every definition (empty, attributes,
/// <c>__declspec(...)</c>, <c>inline</c>, <c>constexpr</c>, ...: <c>FMT_API</c>, <c>LLVM_ABI</c>), or that are
/// defined as nothing in some configuration (<c>FMT_EXPORT</c>, <c>FMT_BUILTIN</c>), are blanked out,
/// with spaces of the same length, and so are the uses of such function-like macros with their arguments
/// (<c>FMT_PRAGMA_GCC(push_options)</c>).</item>
/// <item>Object-like macros that stand for syntax (<c>FMT_BEGIN_NAMESPACE</c> for <c>namespace fmt { inline
/// namespace v12 {</c>, <c>FMT_TRY</c> for <c>try</c>) are expanded, with the first definition found; a
/// <see cref="CppOffsetMap"/> takes the positions of the parsed text back to those of the file.</item>
/// <item>For a file that does not parse so, function-like macros that stand for syntax too
/// (<c>FMT_ENABLE_IF(...)</c> for <c>fmt::enable_if_t&lt;(...), int&gt; = 0</c>, <c>FMT_CATCH(x)</c>) are expanded
/// with their arguments (<see cref="Prepare(ReadOnlyMemory{byte}, bool)"/>).</item>
/// </list>
/// Other macros are left as they are: the parser reads their uses as names, and the graph records references to them.
/// </summary>
public sealed class CppMacroPlan
{
    private static readonly HashSet<string> DecorationKeywords = new(StringComparer.Ordinal)
    {
        "inline", "__inline", "__inline__", "__forceinline", "static", "extern", "constexpr", "consteval", "constinit",
        "noexcept", "explicit", "virtual", "thread_local", "__thread", "register", "volatile", "__cdecl", "__stdcall",
        "__fastcall", "__thiscall", "__vectorcall", "__clrcall", "WINAPI", "__restrict", "__restrict__", "__unaligned",
        "__extension__",
    };

    private static readonly HashSet<string> DecorationCalls = new(StringComparer.Ordinal)
    {
        "__attribute__", "__attribute", "__declspec", "alignas", "_Alignas", "_Pragma", "__pragma", "noexcept",
    };

    /// <summary>The first words of replacements that stand for syntax.</summary>
    private static readonly HashSet<string> SyntaxKeywords = new(StringComparer.Ordinal)
    {
        "namespace", "extern", "export", "try", "catch", "if", "else", "class", "struct", "union", "enum", "template",
        "typedef", "using", "return", "do", "while", "for", "switch", "public", "private", "protected",
    };

    public static CppMacroPlan Empty { get; } = new([], [], new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, FunctionMacro>(StringComparer.Ordinal));

    /// <summary>A function-like macro to expand: its parameters (<c>...</c> for <c>__VA_ARGS__</c>) and replacement.</summary>
    public sealed record FunctionMacro(IReadOnlyList<string> Parameters, string Replacement);

    private CppMacroPlan(HashSet<string> blankable, HashSet<string> blankableCalls, Dictionary<string, string> expansions, Dictionary<string, FunctionMacro> functionExpansions)
    {
        Blankable = blankable;
        BlankableCalls = blankableCalls;
        Expansions = expansions;
        FunctionExpansions = functionExpansions;
        Key = string.Join(",", blankable.Order(StringComparer.Ordinal)) + ";" + string.Join(",", blankableCalls.Order(StringComparer.Ordinal)) + ";"
            + string.Join(",", expansions.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + e.Value)) + ";"
            + string.Join(",", functionExpansions.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "(" + string.Join(",", e.Value.Parameters) + ")=" + e.Value.Replacement));
    }

    /// <summary>Function-like macros expanded in files that do not parse otherwise.</summary>
    public IReadOnlyDictionary<string, FunctionMacro> FunctionExpansions { get; }

    /// <summary>Object-like macros blanked out.</summary>
    public IReadOnlySet<string> Blankable { get; }

    /// <summary>Function-like macros whose uses are blanked out with their arguments.</summary>
    public IReadOnlySet<string> BlankableCalls { get; }

    /// <summary>Object-like macros expanded, with their replacements.</summary>
    public IReadOnlyDictionary<string, string> Expansions { get; }

    /// <summary>A text of the plan, for comparing plans between loads.</summary>
    public string Key { get; }

    public bool IsEmpty => Blankable.Count == 0 && BlankableCalls.Count == 0 && Expansions.Count == 0;

    // ========================================
    // Definitions
    // ========================================

    /// <summary>
    /// The <c>#define</c> directives of a file, in any branch, as <c>NAME\treplacement</c> for object-like macros and
    /// <c>NAME(parameters)\treplacement</c> for function-like ones; and its <c>#include</c> directives, as
    /// <c>#include\t&lt;name&gt;</c> or <c>#include\t"name"</c>.
    /// </summary>
    public static List<string> ScanDefinitions(ReadOnlySpan<byte> text)
    {
        var result = new List<string>();
        var i = 0;
        var lineStart = true;
        while (i < text.Length)
        {
            var b = text[i];
            if (b == '\n')
            {
                lineStart = true;
                i++;
                continue;
            }

            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\f' or (byte)'\v')
            {
                i++;
                continue;
            }

            if (b == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = SkipBlockComment(text, i);
                continue;
            }

            if (b == '#' && lineStart)
            {
                var end = DirectiveEnd(text, i);
                if (ParseDefine(text[(i + 1)..end]) is { } define)
                {
                    result.Add(define);
                }
                else if (ParseInclude(text[(i + 1)..end]) is { } include)
                {
                    result.Add(include);
                }

                i = end;
                continue;
            }

            lineStart = false;
            if (b == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (b is (byte)'"' or (byte)'\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }

            i++;
        }

        return result;
    }

    private static string? ParseDefine(ReadOnlySpan<byte> directive)
    {
        var text = Encoding.UTF8.GetString(directive).Replace("\\\r\n", " ").Replace("\\\n", " ");
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (!text.AsSpan(i).StartsWith("define") || i + 6 >= text.Length || !char.IsWhiteSpace(text[i + 6]))
        {
            return null;
        }

        i += 6;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        var start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$'))
        {
            i++;
        }

        if (i == start)
        {
            return null;
        }

        var name = text[start..i];
        if (i < text.Length && text[i] == '(')
        {
            var close = text.IndexOf(')', i);
            if (close < 0)
            {
                return null;
            }

            name = text[start..(close + 1)];
            i = close + 1;
        }

        return name + "\t" + Collapse(StripComments(text[i..]));
    }

    /// <summary>A text without its comments (a line comment ends at its line break).</summary>
    private static string? ParseInclude(ReadOnlySpan<byte> directive)
    {
        var text = Encoding.UTF8.GetString(directive).Trim();
        foreach (var keyword in new[] { "include_next", "include", "import" })
        {
            if (text.StartsWith(keyword, StringComparison.Ordinal))
            {
                var name = text[keyword.Length..].Trim();
                var close = name.Length > 1 ? name.IndexOf(name[0] == '<' ? '>' : '"', 1) : -1;
                return name.Length > 1 && name[0] is '<' or '"' && close > 0 ? "#include\t" + name[..(close + 1)] : null;
            }
        }

        return null;
    }

    /// <summary>The headers a file's definitions (<see cref="ScanDefinitions"/>) include: the names with their quotes or brackets.</summary>
    public static IEnumerable<string> Includes(IEnumerable<string> definitions) =>
        definitions.Where(d => d.StartsWith("#include\t", StringComparison.Ordinal)).Select(d => d["#include\t".Length..]);

    /// <summary>The macros a file defines (<see cref="ScanDefinitions"/>), by name (<c>F(x)</c> for a function-like one), the first definition of each.</summary>
    public static IEnumerable<(string Name, string Replacement)> Macros(IEnumerable<string> definitions)
    {
        foreach (var definition in definitions)
        {
            var tab = definition.IndexOf('\t');
            if (!definition.StartsWith('#') && tab > 0)
            {
                yield return (definition[..tab], definition[(tab + 1)..]);
            }
        }
    }

    private static string StripComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '"' or '\'')
            {
                var quote = text[i];
                builder.Append(text[i++]);
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        builder.Append(text[i++]);
                    }

                    builder.Append(text[i++]);
                }

                if (i < text.Length)
                {
                    builder.Append(text[i]);
                }

                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0)
                {
                    break;
                }

                builder.Append(' ');
                i = end - 1;
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                builder.Append(' ');
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n', '\f', '\v'], StringSplitOptions.RemoveEmptyEntries));

    // ========================================
    // The plan
    // ========================================

    /// <summary>The plan for the definitions of <see cref="ScanDefinitions"/> of all files and the macros of the projects.</summary>
    public static CppMacroPlan From(IEnumerable<string> definitions, IReadOnlyDictionary<string, string>? predefined = null)
    {
        // Every replacement of each macro, and whether it is function-like; null for one defined both ways
        var macros = new Dictionary<string, (bool FunctionLike, List<string> Replacements)?>(StringComparer.Ordinal);
        var functionDefinitions = new Dictionary<string, List<(string Parameters, string Replacement)>>(StringComparer.Ordinal);
        void Add(string name, bool functionLike, string replacement)
        {
            if (!macros.TryGetValue(name, out var entry))
            {
                macros[name] = entry = (functionLike, []);
            }

            if (entry is { } e && e.FunctionLike == functionLike)
            {
                if (!e.Replacements.Contains(replacement))
                {
                    e.Replacements.Add(replacement);
                }
            }
            else
            {
                macros[name] = null;
            }
        }

        foreach (var definition in definitions)
        {
            if (definition.StartsWith('#'))
            {
                continue;
            }

            var tab = definition.IndexOf('\t');
            var head = tab < 0 ? definition : definition[..tab];
            var replacement = tab < 0 ? "" : definition[(tab + 1)..];
            var paren = head.IndexOf('(');
            Add(paren < 0 ? head : head[..paren], paren >= 0, replacement);
            if (paren >= 0)
            {
                if (!functionDefinitions.TryGetValue(head[..paren], out var list))
                {
                    functionDefinitions[head[..paren]] = list = [];
                }

                list.Add((head[(paren + 1)..^1], replacement));
            }
        }

        foreach (var (name, value) in predefined ?? new Dictionary<string, string>())
        {
            var paren = name.IndexOf('(');
            Add(paren < 0 ? name : name[..paren], paren >= 0, value);
        }

        // Decorations, by their own tokens, then only those whose other names are decorations too
        var dependencies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, entry) in macros)
        {
            if (entry is not { } e)
            {
                continue;
            }

            // An object-like macro defined as nothing in some configuration is optional: the code is valid without it
            var names = new List<string>();
            if ((!e.FunctionLike && e.Replacements.Contains("")) || e.Replacements.All(r => IsDecoration(r, names)))
            {
                dependencies[name] = !e.FunctionLike && e.Replacements.Contains("") ? [] : names;
            }
        }

        var decorations = new HashSet<string>(dependencies.Keys, StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var name in decorations.ToList())
            {
                if (dependencies[name].Any(n => !decorations.Contains(n)))
                {
                    decorations.Remove(name);
                    changed = true;
                }
            }
        }
        while (changed);

        var blankable = new HashSet<string>(StringComparer.Ordinal);
        var blankableCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in decorations)
        {
            (macros[name]!.Value.FunctionLike ? blankableCalls : blankable).Add(name);
        }

        // Syntax: object-like macros whose replacement is a statement or declaration head, expanded with the first one
        var expansions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, entry) in macros)
        {
            if (entry is { FunctionLike: false } e && !decorations.Contains(name) && e.Replacements.FirstOrDefault(IsSyntax) is { } replacement
                && !replacement.Contains('#') && !Words(replacement).Contains(name))
            {
                expansions[name] = replacement;
            }
        }

        // An expansion that names a macro not planned (a function-like one) is left alone
        foreach (var name in expansions.Keys.ToList())
        {
            if (Words(expansions[name]).Any(w => macros.TryGetValue(w, out var other) && other is { FunctionLike: true } && !blankableCalls.Contains(w)))
            {
                expansions.Remove(name);
            }
        }

        // Syntax in function-like macros, with the first definition that is syntax and neither stringizes nor pastes
        var functionExpansions = new Dictionary<string, FunctionMacro>(StringComparer.Ordinal);
        foreach (var (name, list) in functionDefinitions)
        {
            if (macros[name] is not { FunctionLike: true } || decorations.Contains(name))
            {
                continue;
            }

            foreach (var (parameters, replacement) in list)
            {
                if (replacement.Length > 0 && !replacement.Contains('#') && (IsSyntax(replacement) || replacement.Contains('=') || replacement.Contains('<')) && !Words(replacement).Contains(name))
                {
                    var names = parameters.Split(',', StringSplitOptions.TrimEntries).Where(p => p.Length > 0).Select(p => p.EndsWith("...", StringComparison.Ordinal) ? "..." : p).ToList();
                    functionExpansions[name] = new FunctionMacro(names, replacement);
                    break;
                }
            }
        }

        return new CppMacroPlan(blankable, blankableCalls, expansions, functionExpansions);
    }

    private static bool IsSyntax(string replacement)
    {
        if (replacement.Contains('{') || replacement.Contains('}') || replacement.Contains(';'))
        {
            return true;
        }

        var first = Words(replacement).FirstOrDefault();
        return first != null && SyntaxKeywords.Contains(first) && replacement.StartsWith(first, StringComparison.Ordinal);
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] is '"' or '\'')
            {
                var quote = text[i++];
                while (i < text.Length && text[i] != quote)
                {
                    i += text[i] == '\\' ? 2 : 1;
                }

                i++;
                continue;
            }

            if (char.IsLetter(text[i]) || text[i] == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                words.Add(text[start..i]);
                continue;
            }

            i++;
        }

        return words;
    }

    /// <summary>
    /// Whether a replacement list is only decorations; the other macros it uses (followed by their arguments or
    /// not) go to <paramref name="names"/>.
    /// </summary>
    private static bool IsDecoration(string replacement, List<string> names)
    {
        var i = 0;
        while (i < replacement.Length)
        {
            var c = replacement[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '[' && i + 1 < replacement.Length && replacement[i + 1] == '[')
            {
                var end = replacement.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                i = end + 2;
                continue;
            }

            if (!(char.IsLetter(c) || c == '_'))
            {
                return false;
            }

            var start = i;
            while (i < replacement.Length && (char.IsLetterOrDigit(replacement[i]) || replacement[i] == '_'))
            {
                i++;
            }

            var word = replacement[start..i];
            var j = i;
            while (j < replacement.Length && char.IsWhiteSpace(replacement[j]))
            {
                j++;
            }

            var hasArguments = j < replacement.Length && replacement[j] == '(';
            if (DecorationKeywords.Contains(word) && !(word == "noexcept" && hasArguments))
            {
                continue;
            }

            if (!DecorationCalls.Contains(word))
            {
                names.Add(word);
            }

            if (hasArguments)
            {
                if (SkipParentheses(replacement, j) is not { } after)
                {
                    return false;
                }

                i = after;
            }
            else if (DecorationCalls.Contains(word) && word != "noexcept")
            {
                return false;
            }
        }

        return true;
    }

    private static int? SkipParentheses(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i + 1;
            }
        }

        return null;
    }

    // ========================================
    // Preparing a file
    // ========================================

    /// <summary>
    /// The text the parser reads for a file: its macros blanked out or expanded (not in directives, comments or
    /// literals), and the map back to the file's offsets (null when the text kept every offset).
    /// </summary>
    /// <param name="expandFunctions">Also expand the <see cref="FunctionExpansions"/>, for a file that does not parse without.</param>
    public (ReadOnlyMemory<byte> Text, CppOffsetMap? Map) Prepare(ReadOnlyMemory<byte> utf8, bool expandFunctions = false)
    {
        if (IsEmpty && (!expandFunctions || FunctionExpansions.Count == 0))
        {
            return (utf8, null);
        }

        byte[]? copy = null;
        List<(int Start, int End, byte[] Replacement)>? expansions = null;
        var text = utf8.Span;
        var i = 0;
        var lineStart = true;
        while (i < text.Length)
        {
            var b = text[i];
            if (b == '\n')
            {
                lineStart = true;
                i++;
                continue;
            }

            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\f' or (byte)'\v')
            {
                i++;
                continue;
            }

            if (b == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = SkipBlockComment(text, i);
                continue;
            }

            if (b == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (b == '#' && lineStart)
            {
                i = DirectiveEnd(text, i);
                continue;
            }

            lineStart = false;
            if (b is (byte)'"' or (byte)'\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }

            if (CppSymbolCollector.IsIdentifierStart(b))
            {
                var start = i;
                while (i < text.Length && CppSymbolCollector.IsIdentifierPart(text[i]))
                {
                    i++;
                }

                // A raw string: R"delimiter( ... )delimiter"
                if (i < text.Length && text[i] == '"' && text[i - 1] == 'R' && i - start <= 3)
                {
                    i = SkipRawString(text, i);
                    continue;
                }

                if (i < text.Length && text[i] is (byte)'"' or (byte)'\'' && i - start <= 2)
                {
                    // An encoding prefix: u8"..."
                    continue;
                }

                if (i - start > 64)
                {
                    continue;
                }

                var word = Encoding.UTF8.GetString(text[start..i]);
                if (Blankable.Contains(word))
                {
                    copy ??= utf8.ToArray();
                    copy.AsSpan(start, i - start).Fill((byte)' ');
                }
                else if (BlankableCalls.Contains(word) && ArgumentsEnd(text, i) is { } end)
                {
                    copy ??= utf8.ToArray();
                    Blank(copy, start, end);
                    i = end;
                }
                else if (Expansions.TryGetValue(word, out var replacement))
                {
                    (expansions ??= []).Add((start, i, Encoding.UTF8.GetBytes(Expand(replacement, 0))));
                }
                else if (expandFunctions && FunctionExpansions.TryGetValue(word, out var function) && ArgumentsEnd(text, i) is { } argumentsEnd)
                {
                    var arguments = Arguments(StripComments(Encoding.UTF8.GetString(text[i..argumentsEnd]).Replace("\r\n", "\n")));
                    (expansions ??= []).Add((start, argumentsEnd, Encoding.UTF8.GetBytes(Expand(Substitute(function, arguments), 0).Replace('\n', ' ').Replace('\r', ' '))));
                    i = argumentsEnd;
                }

                continue;
            }

            if (b is >= (byte)'0' and <= (byte)'9')
            {
                while (i < text.Length && (CppSymbolCollector.IsIdentifierPart(text[i]) || text[i] is (byte)'.' or (byte)'\''))
                {
                    i++;
                }

                continue;
            }

            i++;
        }

        var blanked = copy ?? (ReadOnlyMemory<byte>)utf8;
        if (expansions == null)
        {
            return (blanked, null);
        }

        // Splice the expansions in, and remember where they are
        var result = new List<byte>(blanked.Length + expansions.Sum(e => e.Replacement.Length));
        var segments = new List<CppOffsetMap.Segment>();
        var from = 0;
        foreach (var (start, end, bytes) in expansions)
        {
            result.AddRange(blanked.Span[from..start]);
            segments.Add(new CppOffsetMap.Segment(result.Count, bytes.Length, start, end - start));
            result.AddRange(bytes);
            from = end;
        }

        result.AddRange(blanked.Span[from..]);
        return (result.ToArray(), new CppOffsetMap(segments));
    }

    /// <summary>A replacement with the macros in it blanked out or expanded.</summary>
    private string Expand(string replacement, int depth)
    {
        if (depth > 8)
        {
            return replacement;
        }

        var builder = new StringBuilder();
        var i = 0;
        while (i < replacement.Length)
        {
            var c = replacement[i];
            if (!(char.IsLetter(c) || c == '_'))
            {
                builder.Append(c);
                i++;
                continue;
            }

            var start = i;
            while (i < replacement.Length && (char.IsLetterOrDigit(replacement[i]) || replacement[i] == '_'))
            {
                i++;
            }

            var word = replacement[start..i];
            if (Blankable.Contains(word))
            {
                builder.Append(' ', word.Length);
            }
            else if (BlankableCalls.Contains(word) && SkipParentheses(replacement, SkipSpaces(replacement, i)) is { } after && SkipSpaces(replacement, i) < replacement.Length && replacement[SkipSpaces(replacement, i)] == '(')
            {
                builder.Append(' ', after - start);
                i = after;
            }
            else if (Expansions.TryGetValue(word, out var inner))
            {
                builder.Append(Expand(inner, depth + 1));
            }
            else
            {
                builder.Append(word);
            }
        }

        return builder.ToString();
    }

    /// <summary>The arguments of a macro call, from its parentheses: split at the commas outside brackets.</summary>
    private static List<string> Arguments(string parenthesized)
    {
        var inner = parenthesized.Trim();
        inner = inner[(inner.IndexOf('(') + 1)..inner.LastIndexOf(')')];
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case '"' or '\'':
                {
                    var quote = inner[i++];
                    while (i < inner.Length && inner[i] != quote)
                    {
                        i += inner[i] == '\\' ? 2 : 1;
                    }

                    break;
                }

                case ',' when depth == 0:
                    arguments.Add(inner[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        if (inner.Trim().Length > 0 || arguments.Count > 0)
        {
            arguments.Add(inner[start..].Trim());
        }

        return arguments;
    }

    /// <summary>A function-like macro's replacement with its parameters replaced by the arguments.</summary>
    private static string Substitute(FunctionMacro macro, List<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < macro.Parameters.Count; i++)
        {
            if (macro.Parameters[i] == "...")
            {
                values["__VA_ARGS__"] = string.Join(", ", arguments.Skip(i));
                break;
            }

            values[macro.Parameters[i]] = i < arguments.Count ? arguments[i] : "";
        }

        var builder = new StringBuilder();
        var text = macro.Replacement;
        var j = 0;
        while (j < text.Length)
        {
            if (char.IsLetter(text[j]) || text[j] == '_')
            {
                var start = j;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
                {
                    j++;
                }

                var word = text[start..j];
                builder.Append(values.TryGetValue(word, out var value) ? value : word);
                continue;
            }

            builder.Append(text[j++]);
        }

        return builder.ToString();
    }

    private static int SkipSpaces(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary>Spaces over a range, keeping its line breaks so that lines do not move.</summary>
    private static void Blank(byte[] text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (text[i] is not ((byte)'\n' or (byte)'\r'))
            {
                text[i] = (byte)' ';
            }
        }
    }

    /// <summary>The end of the arguments of a function-like macro used at <paramref name="i"/>, or null when no '(' follows.</summary>
    private static int? ArgumentsEnd(ReadOnlySpan<byte> text, int i)
    {
        while (i < text.Length && text[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        if (i >= text.Length || text[i] != '(')
        {
            return null;
        }

        var depth = 0;
        while (i < text.Length)
        {
            var b = text[i];
            if (b is (byte)'"' or (byte)'\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }

            if (b == '(')
            {
                depth++;
            }
            else if (b == ')' && --depth == 0)
            {
                return i + 1;
            }

            i++;
        }

        return null;
    }

    private static int SkipBlockComment(ReadOnlySpan<byte> text, int i)
    {
        var end = text[(i + 2)..].IndexOf("*/"u8);
        return end < 0 ? text.Length : i + 2 + end + 2;
    }

    /// <summary>The end of a directive: the line break that is not spliced.</summary>
    private static int DirectiveEnd(ReadOnlySpan<byte> text, int i)
    {
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length && (text[i + 1] == '\n' || (text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n')))
            {
                i += text[i + 1] == '\r' ? 3 : 2;
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = SkipBlockComment(text, i);
                continue;
            }

            if (text[i] == '\n')
            {
                return i;
            }

            i++;
        }

        return text.Length;
    }

    private static int SkipLiteral(ReadOnlySpan<byte> text, int i)
    {
        var quote = text[i];
        i++;
        while (i < text.Length && text[i] != quote && text[i] != '\n')
        {
            i += text[i] == '\\' ? 2 : 1;
        }

        return Math.Min(i + 1, text.Length);
    }

    private static int SkipRawString(ReadOnlySpan<byte> text, int i)
    {
        var open = text[i..].IndexOf((byte)'(');
        if (open < 0)
        {
            return text.Length;
        }

        var delimiter = text[(i + 1)..(i + open)];
        var terminator = new byte[delimiter.Length + 2];
        terminator[0] = (byte)')';
        delimiter.CopyTo(terminator.AsSpan(1));
        terminator[^1] = (byte)'"';
        var end = text[(i + open)..].IndexOf(terminator);
        return end < 0 ? text.Length : i + open + end + terminator.Length;
    }
}

/// <summary>
/// Takes offsets of a text with macros expanded back to the file: an offset in an expansion is the macro's name
/// (its start, or its end for the end of a span).
/// </summary>
public sealed class CppOffsetMap
{
    /// <param name="Start">Where the expansion is in the parsed text.</param>
    /// <param name="Length">Its length there.</param>
    /// <param name="OriginalStart">Where the macro's name is in the file.</param>
    /// <param name="OriginalLength">The length of the name.</param>
    public readonly record struct Segment(int Start, int Length, int OriginalStart, int OriginalLength);

    private readonly Segment[] _segments;

    public CppOffsetMap(IEnumerable<Segment> segments)
    {
        _segments = segments.OrderBy(s => s.Start).ToArray();
    }

    /// <summary>
    /// The offset in the file of <paramref name="offset"/> in the parsed text; with <paramref name="end"/>, of the
    /// end of a span (the offset after its last byte).
    /// </summary>
    public int Original(int offset, bool end = false)
    {
        if (end)
        {
            return offset <= 0 ? 0 : Find(offset - 1) is { } inExpansion ? inExpansion.OriginalStart + inExpansion.OriginalLength : Original(offset - 1) + 1;
        }

        return Find(offset) is { } segment ? segment.OriginalStart : Shift(offset);
    }

    /// <summary>Whether an offset of the parsed text is in an expansion, where nothing is written as in the file.</summary>
    public bool IsExpanded(int offset) => Find(offset) != null;

    /// <summary>The expansion an offset of the parsed text is in, or null.</summary>
    private Segment? Find(int offset)
    {
        var index = Last(offset);
        return index >= 0 && offset < _segments[index].Start + _segments[index].Length ? _segments[index] : null;
    }

    /// <summary>An offset outside the expansions, moved by the lengths of the expansions before it.</summary>
    private int Shift(int offset)
    {
        var index = Last(offset);
        if (index < 0)
        {
            return offset;
        }

        var segment = _segments[index];
        return segment.OriginalStart + segment.OriginalLength + (offset - segment.Start - segment.Length);
    }

    /// <summary>The index of the last expansion that starts at or before the offset, or -1.</summary>
    private int Last(int offset)
    {
        int low = 0, high = _segments.Length - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (_segments[middle].Start <= offset)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }
}
