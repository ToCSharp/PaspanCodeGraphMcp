using System.Collections.Concurrent;

namespace PaspanCodeGraph.Cpp;

/// <summary>
/// The names of a C++ workspace, shared by the passes over its files: what each namespace and class declares
/// (from every file, since headers are not read by the parser), the bases of classes, the types of members,
/// the using-directives of files and their includes, and the macros. It looks names up as C++ does, without
/// templates being instantiated: unqualified names through the enclosing blocks, classes (with their bases)
/// and namespaces (with their inline namespaces and using-directives), qualified names in what their qualifier names.
/// </summary>
public sealed class CppBinder
{
    private readonly Dictionary<CodeSymbol, Dictionary<string, List<CodeSymbol>>> _members = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<CodeSymbol>> _global = new(StringComparer.Ordinal);
    private readonly Dictionary<CodeSymbol, CppSymbolInfo> _info = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<CodeSymbol>> _membersByName = new(StringComparer.Ordinal);
    private readonly Dictionary<CodeSymbol, List<CodeSymbol>> _inlineNamespaces = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, FileNames> _files = new(SymbolIndexBuilder.PathComparer);
    private readonly ConcurrentDictionary<string, IReadOnlyList<CodeSymbol>> _fileUsings = new(SymbolIndexBuilder.PathComparer);

    public CppBinder(SymbolIndexBuilder builder)
    {
        Builder = builder;
    }

    public SymbolIndexBuilder Builder { get; }

    /// <summary>The macros of the workspace by name.</summary>
    public Dictionary<string, CodeSymbol> Macros { get; } = new(StringComparer.Ordinal);

    /// <summary>What a file declares at its global scope that files including it see.</summary>
    private sealed class FileNames
    {
        public List<CodeSymbol> Usings { get; } = [];

        public List<string> Includes { get; set; } = [];
    }

    public CppSymbolInfo Info(CodeSymbol symbol)
    {
        lock (_info)
        {
            if (!_info.TryGetValue(symbol, out var info))
            {
                _info[symbol] = info = new CppSymbolInfo();
            }

            return info;
        }
    }

    public CppSymbolInfo? FindInfo(CodeSymbol symbol)
    {
        lock (_info)
        {
            return _info.GetValueOrDefault(symbol);
        }
    }

    /// <summary>Makes <paramref name="symbol"/> visible under <paramref name="name"/> in <paramref name="container"/> (null: the global namespace).</summary>
    public void Declare(CodeSymbol? container, string name, CodeSymbol symbol)
    {
        var table = Table(container, create: true)!;
        if (!table.TryGetValue(name, out var list))
        {
            table[name] = list = [];
        }

        if (!list.Contains(symbol))
        {
            list.Add(symbol);
        }

        if (container is { } type && type.Kind != SymbolKind.Namespace && symbol.Kind.IsMember())
        {
            if (!_membersByName.TryGetValue(name, out var byName))
            {
                _membersByName[name] = byName = [];
            }

            if (!byName.Contains(symbol))
            {
                byName.Add(symbol);
            }
        }
    }

    public void DeclareInlineNamespace(CodeSymbol? parent, CodeSymbol inline)
    {
        var key = parent ?? GlobalKey;
        if (!_inlineNamespaces.TryGetValue(key, out var list))
        {
            _inlineNamespaces[key] = list = [];
        }

        if (!list.Contains(inline))
        {
            list.Add(inline);
        }
    }

    private static readonly CodeSymbol GlobalKey = new("N:", SymbolKind.Namespace, "");

    private Dictionary<string, List<CodeSymbol>>? Table(CodeSymbol? container, bool create = false)
    {
        if (container == null)
        {
            return _global;
        }

        if (!_members.TryGetValue(container, out var table) && create)
        {
            _members[container] = table = new Dictionary<string, List<CodeSymbol>>(StringComparer.Ordinal);
        }

        return table;
    }

    /// <summary>The class members named <paramref name="name"/> in any class: the candidates of a member access on an unknown type.</summary>
    public IReadOnlyList<CodeSymbol> MembersNamed(string name) => _membersByName.TryGetValue(name, out var list) ? list : [];

    /// <summary>What <paramref name="container"/> declares under <paramref name="name"/>, with its inline namespaces.</summary>
    public IReadOnlyList<CodeSymbol> DeclaredIn(CodeSymbol? container, string name)
    {
        var found = Table(container)?.GetValueOrDefault(name);
        if (container is not null && container.Kind != SymbolKind.Namespace)
        {
            return found ?? (IReadOnlyList<CodeSymbol>)[];
        }

        if (!_inlineNamespaces.TryGetValue(container ?? GlobalKey, out var inlines))
        {
            return found ?? (IReadOnlyList<CodeSymbol>)[];
        }

        var all = new List<CodeSymbol>(found ?? []);
        foreach (var inline in inlines)
        {
            foreach (var symbol in DeclaredIn(inline, name))
            {
                if (!all.Contains(symbol))
                {
                    all.Add(symbol);
                }
            }
        }

        return all;
    }

    /// <summary>
    /// The members named <paramref name="name"/> of a class and, when it has none, of its bases, nearest first.
    /// An alias of a class is looked through.
    /// </summary>
    public IReadOnlyList<CodeSymbol> LookupInClass(CodeSymbol type, string name)
    {
        var seen = new HashSet<CodeSymbol>(ReferenceEqualityComparer.Instance);
        var level = new List<CodeSymbol> { ClassOf(type) ?? type };
        while (level.Count > 0)
        {
            var found = new List<CodeSymbol>();
            var next = new List<CodeSymbol>();
            foreach (var current in level)
            {
                if (!seen.Add(current))
                {
                    continue;
                }

                found.AddRange(DeclaredIn(current, name).Where(s => !found.Contains(s)));
                if (FindInfo(current) is { } info)
                {
                    foreach (var (symbol, _) in info.Bases)
                    {
                        if (symbol != null && ClassOf(symbol) is { } baseClass)
                        {
                            next.Add(baseClass);
                        }
                    }
                }
            }

            if (found.Count > 0)
            {
                return found;
            }

            level = next;
        }

        return [];
    }

    /// <summary>The class, struct, union or enum a type symbol or an alias of one names, or null.</summary>
    public CodeSymbol? ClassOf(CodeSymbol symbol)
    {
        for (var i = 0; i < 8 && symbol.Kind == SymbolKind.TypeAlias; i++)
        {
            if (FindInfo(symbol)?.Type.Symbol is not { } target || target == symbol)
            {
                return null;
            }

            symbol = target;
        }

        return symbol.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Union or SymbolKind.Enum ? symbol : null;
    }

    /// <summary>The bases of a class, transitively, nearest first.</summary>
    public IEnumerable<CodeSymbol> AllBases(CodeSymbol type)
    {
        var seen = new HashSet<CodeSymbol>(ReferenceEqualityComparer.Instance) { type };
        var queue = new Queue<CodeSymbol>();
        queue.Enqueue(type);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (FindInfo(current) is not { } info)
            {
                continue;
            }

            foreach (var (symbol, _) in info.Bases)
            {
                if (symbol != null && ClassOf(symbol) is { } baseClass && seen.Add(baseClass))
                {
                    yield return baseClass;
                    queue.Enqueue(baseClass);
                }
            }
        }
    }

    private bool _filesComplete;

    /// <summary>Called when every file was given to <see cref="SetFile"/>: <see cref="FileUsings"/> answers from then on.</summary>
    public void CompleteFiles() => _filesComplete = true;

    /// <summary>Records the using-directives of the global scope of a file and the workspace files it includes.</summary>
    public void SetFile(string path, IReadOnlyList<CodeSymbol> usings, IReadOnlyList<string> includes)
    {
        var names = new FileNames { Includes = [.. includes] };
        names.Usings.AddRange(usings);
        _files[path] = names;
    }

    /// <summary>
    /// The namespaces that using-directives at the global scope of a file make visible: its own and those of the
    /// files it includes, transitively.
    /// </summary>
    public IReadOnlyList<CodeSymbol> FileUsings(string path) => !_filesComplete ? [] : _fileUsings.GetOrAdd(path, p =>
    {
        var result = new List<CodeSymbol>();
        var seen = new HashSet<string>(SymbolIndexBuilder.PathComparer);
        var stack = new Stack<string>();
        stack.Push(p);
        while (stack.Count > 0)
        {
            var file = stack.Pop();
            if (!seen.Add(file) || !_files.TryGetValue(file, out var names))
            {
                continue;
            }

            foreach (var ns in names.Usings)
            {
                if (!result.Contains(ns))
                {
                    result.Add(ns);
                }
            }

            foreach (var include in names.Includes)
            {
                stack.Push(include);
            }
        }

        return result;
    });

    // ========================================
    // Forward declarations and definitions
    // ========================================

    private readonly List<CppForwardDeclaration> _forwardDeclarations = [];
    private readonly Dictionary<CodeSymbol, SourceLocation> _definitions = new(ReferenceEqualityComparer.Instance);

    public void AddForwardDeclaration(CppForwardDeclaration declaration) => _forwardDeclarations.Add(declaration);

    /// <summary>
    /// Declares the classes that are only declared (<c>class X;</c>), not defined, in the workspace; a class that
    /// is defined keeps only its definitions.
    /// </summary>
    public void CompleteForwardDeclarations()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in _forwardDeclarations)
        {
            if (Builder.Get(declaration.Id) is { Declarations.Count: > 0 } existing && !defined.Contains(declaration.Id))
            {
                continue;
            }

            if (declaration.OnlyIfUndeclared && (Builder.Get(declaration.Id) != null || IsDeclaredAround(declaration.Container, declaration.Name)))
            {
                continue;
            }

            var symbol = Builder.GetOrAdd(declaration.Id, declaration.Kind, declaration.Name, declaration.Container, out var created);
            if (created)
            {
                symbol.Language = SourceLanguage.Cpp;
                symbol.Project = declaration.Project;
                for (var c = declaration.Container; c != null; c = c.Container)
                {
                    if (c.Kind == SymbolKind.Namespace)
                    {
                        symbol.Namespace = c.Id[2..];
                        break;
                    }
                }

                symbol.Signature = (declaration.Kind switch { SymbolKind.Struct => "struct ", SymbolKind.Union => "union ", SymbolKind.Enum => "enum ", _ => "class " }) + declaration.Id[2..];
                symbol.Accessibility = "public";
                Declare(declaration.Container, declaration.Name, symbol);
                Info(symbol).TemplateParameters.AddRange(declaration.TemplateParameters);
                symbol.TypeParameters.AddRange(declaration.TemplateParameterTexts);
                defined.Add(declaration.Id);
            }

            Builder.AddDeclaration(symbol, declaration.Location);
            symbol.Documentation ??= declaration.Documentation;
        }

        _forwardDeclarations.Clear();
    }

    /// <summary>Whether a name is declared in a namespace or class or around it.</summary>
    private bool IsDeclaredAround(CodeSymbol? container, string name)
    {
        for (var c = container; ; c = c.Container)
        {
            if (DeclaredIn(c, name).Count > 0)
            {
                return true;
            }

            if (c == null)
            {
                return false;
            }
        }
    }

    /// <summary>Marks the location that defines a function or variable (with its body or initializer).</summary>
    public void SetDefinition(CodeSymbol symbol, SourceLocation location) => _definitions.TryAdd(symbol, location);

    /// <summary>Puts the definition of each function and variable first among its declarations.</summary>
    public void OrderDeclarations()
    {
        foreach (var (symbol, location) in _definitions)
        {
            var index = symbol.Declarations.IndexOf(location);
            if (index > 0)
            {
                symbol.Declarations.RemoveAt(index);
                symbol.Declarations.Insert(0, location);
            }
        }
    }
}

/// <summary>A class, struct, union or enum declared without its definition.</summary>
public sealed record CppForwardDeclaration(
    string Id, SymbolKind Kind, string Name, CodeSymbol? Container, string Project, SourceLocation Location, string? Documentation,
    IReadOnlyList<string> TemplateParameters, IReadOnlyList<string> TemplateParameterTexts)
{
    /// <summary>
    /// An elaborated type specifier in a declaration of something else (<c>struct X *p;</c>), which declares X only
    /// when no X is declared around it.
    /// </summary>
    public bool OnlyIfUndeclared { get; init; }
}
