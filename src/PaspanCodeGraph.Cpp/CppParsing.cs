using System.Text;
using PaspanParsers.Cpp;

namespace PaspanCodeGraph.Cpp;

/// <summary>
/// How the files of a C++ workspace are parsed, since the parser reads one file at a time and does not expand
/// macros:
/// <list type="bullet">
/// <item>Macros that stand for decorations only (empty, attributes, <c>__declspec(...)</c>, <c>inline</c>,
/// <c>constexpr</c>, ...: <c>FMT_API</c>, <c>LLVM_ABI</c>) in every definition the workspace has are blanked out
/// of the code, with spaces of the same length so that positions do not move.</item>
/// <item>A first parse of every file finds the names it declares (types, templates, function templates and
/// concepts); the second parse is given all of them, with those of the standard library, as the names of headers
/// (<see cref="CppParseOptions"/>), so that <c>Widget(x)</c> in a source file is a cast when <c>Widget</c> is a
/// class of a header. Members are left out, so that editing members does not parse every file again.</item>
/// </list>
/// </summary>
public static class CppParsing
{
    private static readonly HashSet<string> DecorationKeywords = new(StringComparer.Ordinal)
    {
        "inline", "__inline", "__inline__", "__forceinline", "static", "extern", "constexpr", "consteval", "constinit",
        "noexcept", "explicit", "virtual", "thread_local", "__thread", "register", "volatile", "__cdecl", "__stdcall",
        "__fastcall", "__thiscall", "__vectorcall", "__clrcall", "WINAPI", "__restrict", "__restrict__", "__unaligned",
    };

    private static readonly HashSet<string> DecorationCalls = new(StringComparer.Ordinal)
    {
        "__attribute__", "__attribute", "__declspec", "alignas", "_Alignas", "_Pragma", "__pragma", "noexcept",
    };

    // ========================================
    // Macros
    // ========================================

    /// <summary>
    /// The <c>#define</c> directives of a file, in any branch, as <c>NAME\treplacement</c> for object-like macros
    /// and <c>NAME(</c> for function-like ones.
    /// </summary>
    public static List<string> ScanMacros(ReadOnlySpan<byte> text)
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

            i++;
        }

        return result;
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
            return name + "(";
        }

        var replacement = StripComments(text[i..]).Trim();
        return name + "\t" + replacement;
    }

    private static string StripComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                break;
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

    /// <summary>
    /// The macros that can be blanked out of the code: object-like in every definition, with replacements made only
    /// of decorations and of other such macros.
    /// </summary>
    public static HashSet<string> BlankableMacros(IEnumerable<string> definitions, IReadOnlyDictionary<string, string>? predefined = null)
    {
        var replacements = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
        void Add(string name, string? replacement)
        {
            if (!replacements.TryGetValue(name, out var list))
            {
                replacements[name] = list = [];
            }

            if (replacement == null)
            {
                replacements[name] = null;
            }
            else
            {
                list?.Add(replacement);
            }
        }

        foreach (var definition in definitions)
        {
            var tab = definition.IndexOf('\t');
            if (tab < 0)
            {
                Add(definition.TrimEnd('('), null);
            }
            else
            {
                Add(definition[..tab], definition[(tab + 1)..]);
            }
        }

        foreach (var (name, value) in predefined ?? new Dictionary<string, string>())
        {
            Add(name, value);
        }

        // Candidates by their own tokens, then those that only name other candidates
        var tokens = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, list) in replacements)
        {
            if (list == null)
            {
                continue;
            }

            var names = new List<string>();
            if (list.All(r => IsDecoration(r, names)))
            {
                tokens[name] = names;
            }
        }

        var blankable = new HashSet<string>(tokens.Keys, StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var name in blankable.ToList())
            {
                if (tokens[name].Any(n => !blankable.Contains(n)))
                {
                    blankable.Remove(name);
                    changed = true;
                }
            }
        }
        while (changed);

        return blankable;
    }

    /// <summary>Whether a replacement list is only decorations; the other identifiers it names go to <paramref name="names"/>.</summary>
    private static bool IsDecoration(string replacement, List<string> names)
    {
        var i = 0;
        var sawAnything = false;
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
                sawAnything = true;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < replacement.Length && (char.IsLetterOrDigit(replacement[i]) || replacement[i] == '_'))
                {
                    i++;
                }

                var word = replacement[start..i];
                sawAnything = true;
                if (DecorationCalls.Contains(word))
                {
                    while (i < replacement.Length && char.IsWhiteSpace(replacement[i]))
                    {
                        i++;
                    }

                    if (i < replacement.Length && replacement[i] == '(')
                    {
                        var depth = 0;
                        for (; i < replacement.Length; i++)
                        {
                            if (replacement[i] == '(')
                            {
                                depth++;
                            }
                            else if (replacement[i] == ')' && --depth == 0)
                            {
                                i++;
                                break;
                            }
                        }

                        if (depth != 0)
                        {
                            return false;
                        }
                    }

                    continue;
                }

                if (DecorationKeywords.Contains(word))
                {
                    continue;
                }

                names.Add(word);
                continue;
            }

            return false;
        }

        _ = sawAnything;
        return true;
    }

    /// <summary>
    /// The text with the uses of <paramref name="macros"/> in code (not in directives, comments or literals)
    /// replaced by spaces; the text itself when it uses none.
    /// </summary>
    public static ReadOnlyMemory<byte> Blank(ReadOnlyMemory<byte> utf8, IReadOnlySet<string> macros)
    {
        if (macros.Count == 0)
        {
            return utf8;
        }

        byte[]? copy = null;
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

                if (i - start <= 64 && macros.Contains(Encoding.UTF8.GetString(text[start..i])))
                {
                    copy ??= utf8.ToArray();
                    copy.AsSpan(start, i - start).Fill((byte)' ');
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

        return copy ?? utf8;
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

    // ========================================
    // Names
    // ========================================

    /// <summary>
    /// The names a parsed file declares for the parse of other files, prefixed with their kind: <c>T:</c> a type,
    /// <c>C:</c> a class or alias template, <c>F:</c> a function or variable template, <c>K:</c> a concept, each
    /// unqualified and qualified by its namespaces.
    /// </summary>
    public static List<string> Names(TranslationUnit unit)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectNames(unit.Declarations, "", null, names, template: false);
        var list = names.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static void CollectNames(IReadOnlyList<Declaration> declarations, string prefix, string? className, HashSet<string> names, bool template)
    {
        foreach (var declaration in declarations)
        {
            CollectName(declaration, prefix, className, names, template);
        }
    }

    private static void AddQualified(HashSet<string> names, string kind, string prefix, string name)
    {
        names.Add(kind + name);
        if (prefix.Length > 0)
        {
            names.Add(kind + prefix + name);
        }
    }

    private static void CollectName(Declaration declaration, string prefix, string? className, HashSet<string> names, bool template)
    {
        switch (declaration)
        {
            case NamespaceDefinition ns:
                CollectNames(ns.Declarations, prefix + string.Concat(ns.Names.Select(n => n.Identifier + "::")), null, names, false);
                break;
            case LinkageSpecification linkage:
                CollectNames(linkage.Declarations, prefix, className, names, false);
                break;
            case ExportDeclaration export:
                CollectNames(export.Declarations, prefix, className, names, false);
                break;
            case TemplateDeclaration t:
                CollectName(t.Declaration, prefix, className, names, template: t.Parameters.Count > 0 || template);
                break;
            case AliasDeclaration alias:
                AddQualified(names, template ? "C:" : "T:", prefix, alias.Identifier);
                break;
            case ConceptDefinition concept:
                AddQualified(names, "K:", prefix, concept.Name);
                break;
            case SimpleDeclaration simple:
            {
                var isTypedef = simple.Specifiers?.Specifiers.Any(s => s is KeywordSpecifier { Keyword: "typedef" }) == true;
                foreach (var specifier in simple.Specifiers?.Specifiers ?? [])
                {
                    switch (specifier)
                    {
                        case ClassSpecifier { Name: { } name } cls:
                        {
                            var identifier = Identifier(name);
                            if (identifier == null)
                            {
                                break;
                            }

                            AddQualified(names, template && name is IdentifierName ? "C:" : "T:", prefix, identifier);

                            CollectNames(cls.Members, prefix + identifier + "::", identifier, names, false);
                            break;
                        }

                        case ClassSpecifier { Name: null } anonymous when className != null:
                            CollectNames(anonymous.Members, prefix, className, names, false);
                            break;
                        case EnumSpecifier { Name: { } enumName }:
                            if (Identifier(enumName) is { } e)
                            {
                                AddQualified(names, "T:", prefix, e);
                            }

                            break;
                        case ElaboratedTypeSpecifier { Name: { } forward } when simple.Declarators.Count == 0:
                            if (Identifier(forward) is { } f)
                            {
                                AddQualified(names, template ? "C:" : "T:", prefix, f);
                            }

                            break;
                    }
                }

                foreach (var declarator in simple.Declarators)
                {
                    if (CppSymbolCollector.FindName(declarator.Declarator)?.Name is not IdentifierName name)
                    {
                        continue;
                    }

                    if (isTypedef)
                    {
                        AddQualified(names, "T:", prefix, name.Identifier);
                    }
                    else if (template && className == null)
                    {
                        AddQualified(names, "F:", prefix, name.Identifier);
                    }
                }

                break;
            }

            case FunctionDefinition function:
                if (CppSymbolCollector.FindName(function.Declarator)?.Name is IdentifierName functionName && template && className == null)
                {
                    AddQualified(names, "F:", prefix, functionName.Identifier);
                }

                break;
        }
    }

    private static string? Identifier(Name name) => name switch
    {
        IdentifierName identifier => identifier.Identifier,
        TemplateIdName templateId => Identifier(templateId.Template),
        QualifiedName qualified => Identifier(qualified.Name),
        _ => null,
    };

    /// <summary>The options of the second parse of a file: its project's options and the names of the workspace and the standard library.</summary>
    public static CppParseOptions Options(
        CppLanguageVersion languageVersion, IReadOnlyDictionary<string, string> macros, IReadOnlyList<string> includeDirectories, string? sourceDirectory, CppNames? names)
    {
        if (names == null)
        {
            return new CppParseOptions(languageVersion, macros, includeDirectories, sourceDirectory);
        }

        return new CppParseOptions(languageVersion, macros, includeDirectories, sourceDirectory,
            names.TypeNames, names.TemplateNames, names.ConceptNames, names.FunctionTemplateNames);
    }
}

/// <summary>The names of types, templates, function templates and concepts the second parse of every file is given.</summary>
public sealed class CppNames
{
    private CppNames(HashSet<string> typeNames, HashSet<string> templateNames, HashSet<string> functionTemplateNames, HashSet<string> conceptNames, string key)
    {
        TypeNames = typeNames;
        TemplateNames = templateNames;
        FunctionTemplateNames = functionTemplateNames;
        ConceptNames = conceptNames;
        Key = key;
    }

    public IReadOnlyCollection<string> TypeNames { get; }

    public IReadOnlyCollection<string> TemplateNames { get; }

    public IReadOnlyCollection<string> FunctionTemplateNames { get; }

    public IReadOnlyCollection<string> ConceptNames { get; }

    /// <summary>A hash of the names: when it does not change, files need not be parsed again.</summary>
    public string Key { get; }

    /// <summary>The names of <see cref="CppParsing.Names"/> of all files, with the standard library's.</summary>
    public static CppNames From(IEnumerable<string> fileNames)
    {
        var types = new HashSet<string>(CppStandardNames.Types, StringComparer.Ordinal);
        var templates = new HashSet<string>(CppStandardNames.Templates, StringComparer.Ordinal);
        var functionTemplates = new HashSet<string>(CppStandardNames.FunctionTemplates, StringComparer.Ordinal);
        var concepts = new HashSet<string>(CppStandardNames.Concepts, StringComparer.Ordinal);
        var all = new SortedSet<string>(fileNames, StringComparer.Ordinal);
        foreach (var name in all)
        {
            var value = name[2..];
            switch (name[0])
            {
                case 'T':
                    types.Add(value);
                    break;
                case 'C':
                    templates.Add(value);
                    break;
                case 'F':
                    functionTemplates.Add(value);
                    break;
                case 'K':
                    concepts.Add(value);
                    break;
            }
        }

        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", all))));
        return new CppNames(types, templates, functionTemplates, concepts, key);
    }
}
