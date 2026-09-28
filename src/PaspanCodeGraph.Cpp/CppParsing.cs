using System.Text;
using PaspanParsers.Cpp;

namespace PaspanCodeGraph.Cpp;

/// <summary>
/// How the files of a C++ workspace are parsed, since the parser reads one file at a time and does not expand
/// macros:
/// <list type="bullet">
/// <item>Macros are prepared by a <see cref="CppMacroPlan"/>: those that only decorate declarations are blanked
/// out, those that stand for syntax (<c>FMT_BEGIN_NAMESPACE</c>) are expanded.</item>
/// <item>A first parse of every file finds the names it declares (types, templates, function templates and
/// concepts); the second parse is given all of them, with those of the standard library, as the names of headers
/// (<see cref="CppParseOptions"/>), so that <c>Widget(x)</c> in a source file is a cast when <c>Widget</c> is a
/// class of a header. Members are left out, so that editing members does not parse every file again.</item>
/// </list>
/// </summary>
public static class CppParsing
{
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
            {
                // The names of an inline namespace are also the names of the namespace around it
                var inline = ns.IsInline || ns.Names.Any(n => n.IsInline);
                CollectNames(ns.Declarations, prefix + string.Concat(ns.Names.Select(n => n.Identifier + "::")), null, names, false);
                if (inline)
                {
                    CollectNames(ns.Declarations, prefix + string.Concat(ns.Names.Where(n => !n.IsInline && !(ns.IsInline && n == ns.Names[^1])).Select(n => n.Identifier + "::")), null, names, false);
                }

                break;
            }
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
