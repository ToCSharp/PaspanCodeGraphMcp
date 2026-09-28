using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.Cpp;

/// <summary>Compares the declarations and references of a loaded C++ workspace with clang's.</summary>
public static class CppOracleComparison
{
    public sealed record Result(int Compared, int Matched, int Extra, List<string> Differences)
    {
        public double Recall => Compared == 0 ? 1 : (double)Matched / Compared;

        public double Precision => Matched + Extra == 0 ? 1 : (double)Matched / (Matched + Extra);

        public string Report(string what) =>
            $"{what}: {Compared} in clang, {Matched} matched, {Extra} extra; recall {Recall:P2}, precision {Precision:P2}\n" + string.Join("\n", Differences);
    }

    /// <summary>The offset of a declared name of the graph.</summary>
    private static ClangLocation At(WorkspaceSnapshot snapshot, SourceLocation location) =>
        new(location.File, snapshot.Documents.TryGetValue(location.File, out var document) ? document.Lines.GetOffset(location.Line, location.Column) : -1);

    /// <summary>
    /// Every declaration clang has (outside function bodies) must be a declaration of a symbol of the same kind;
    /// every declaration of a symbol (but macros, which clang's AST does not have) must be one of clang's.
    /// </summary>
    public static Result Declarations(WorkspaceSnapshot snapshot, ClangOracle oracle)
    {
        // The files both read: those clang compiled or included, and that the graph parsed
        var files = Compared(snapshot, oracle);
        var ours = new Dictionary<ClangLocation, List<CodeSymbol>>();
        // Clang's unnamed classes are not compared: the graph names those of variables after them
        foreach (var symbol in snapshot.Index.Symbols.Where(s => s.Language == SourceLanguage.Cpp && s.Kind != SymbolKind.Macro && !s.Name.StartsWith("(unnamed", StringComparison.Ordinal)))
        {
            foreach (var declaration in symbol.Declarations.Where(d => files.Contains(d.File)))
            {
                if (!ours.TryGetValue(At(snapshot, declaration), out var list))
                {
                    ours[At(snapshot, declaration)] = list = [];
                }

                list.Add(symbol);
            }
        }

        var differences = new List<string>();
        var matched = 0;
        var theirs = new HashSet<ClangLocation>();
        var compared = oracle.Declarations.Where(d => files.Contains(d.Location.File)).ToList();
        foreach (var declaration in compared.OrderBy(d => d.Location.File, StringComparer.Ordinal).ThenBy(d => d.Location.Offset))
        {
            theirs.Add(declaration.Location);
            if (ours.TryGetValue(declaration.Location, out var symbols) && symbols.Any(s => SameKind(s.Kind, declaration.Kind)))
            {
                matched++;
            }
            else
            {
                differences.Add($"missing {declaration.Kind} {declaration.Name} at {declaration.Location}" + (symbols != null ? $" (have {string.Join(", ", symbols.Select(s => s.Kind + " " + s.Id))})" : ""));
            }
        }

        var extra = 0;
        foreach (var (location, symbols) in ours.OrderBy(o => o.Key.File, StringComparer.Ordinal).ThenBy(o => o.Key.Offset))
        {
            if (!theirs.Contains(location))
            {
                extra++;
                differences.Add($"extra {string.Join(", ", symbols.Select(s => s.Kind + " " + s.Id))} at {location}");
            }
        }

        return new Result(compared.Count, matched, extra, differences);
    }

    private static HashSet<string> Compared(WorkspaceSnapshot snapshot, ClangOracle oracle) =>
        oracle.Files.Where(f => snapshot.Documents.TryGetValue(f, out var document) && document.CppUnit != null).ToHashSet(StringComparer.Ordinal);

    /// <summary>A typedef of an unnamed class names the class: the graph declares the class there.</summary>
    private static bool SameKind(SymbolKind ours, SymbolKind theirs) =>
        ours == theirs || (theirs == SymbolKind.TypeAlias && ours is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Union or SymbolKind.Enum);

    /// <summary>
    /// References to members, functions, variables and enumerators: clang's against the graph's exact and inferred
    /// ones (name-only candidates are not counted either way), matched by the position of the name and a
    /// declaration of the target.
    /// </summary>
    public static Result References(WorkspaceSnapshot snapshot, ClangOracle oracle)
    {
        var index = snapshot.Index;
        var files = Compared(snapshot, oracle);
        var initializers = MemberInitializerNames(snapshot);
        var hidden = HiddenSpans(snapshot);
        foreach (var (file, begin, end) in oracle.DependentRanges)
        {
            if (!hidden.TryGetValue(file, out var list))
            {
                hidden[file] = list = [];
            }

            list.Add(new PaspanParsers.TextSpan(begin, end));
        }

        bool Hidden(string file, int offset) => hidden.TryGetValue(file, out var spans) && spans.Any(span => span.Start <= offset && offset < span.End);
        var ours = new Dictionary<ClangLocation, List<CodeSymbol>>();
        foreach (var id in index.ReferenceTargetIds)
        {
            // Clang's JSON AST has no references to constructors and none for the names of ctor-initializers
            if (index.Get(id) is not { Language: SourceLanguage.Cpp } target || target.Kind.IsType()
                || target.Kind is SymbolKind.Namespace or SymbolKind.Macro or SymbolKind.Concept or SymbolKind.Constructor)
            {
                continue;
            }

            foreach (var reference in index.ReferencesTo(id))
            {
                if (reference.Confidence == Confidence.NameOnly || !files.Contains(reference.File) || initializers.Contains((reference.File, reference.Start)) || Hidden(reference.File, reference.Start))
                {
                    continue;
                }

                var location = new ClangLocation(reference.File, reference.Start);
                if (!ours.TryGetValue(location, out var list))
                {
                    ours[location] = list = [];
                }

                if (!list.Contains(target))
                {
                    list.Add(target);
                }
            }
        }

        var theirs = oracle.References.Where(r => files.Contains(r.Location.File) && !Hidden(r.Location.File, r.Location.Offset)).Select(r => (r.Location, r.Target, r.Name)).Distinct().OrderBy(r => r.Location.File, StringComparer.Ordinal).ThenBy(r => r.Location.Offset).ToList();
        var differences = new List<string>();
        var matched = 0;
        var matchedOurs = new HashSet<(ClangLocation, CodeSymbol)>();
        foreach (var (location, target, name) in theirs)
        {
            var found = ours.GetValueOrDefault(location)?.FirstOrDefault(s => s.Declarations.Any(d => At(snapshot, d) == target));
            if (found != null)
            {
                matched++;
                matchedOurs.Add((location, found));
            }
            else
            {
                differences.Add($"missing {name} at {location} -> {target}" + (ours.TryGetValue(location, out var other) ? $" (have {string.Join(", ", other.Select(o => o.Id))})" : ""));
            }
        }

        var extra = 0;
        var theirLocations = theirs.Select(t => t.Location).ToHashSet();
        foreach (var (location, targets) in ours.OrderBy(o => o.Key.File, StringComparer.Ordinal).ThenBy(o => o.Key.Offset))
        {
            foreach (var target in targets)
            {
                if (!matchedOurs.Contains((location, target)))
                {
                    extra++;
                    differences.Add($"extra {target.Id} at {location}" + (theirLocations.Contains(location) ? " (clang binds another)" : ""));
                }
            }
        }

        return new Result(theirs.Count, matched, extra, differences);
    }

    /// <summary>The names of the ctor-initializers (<c>value(1)</c>) of the C++ files, by file and offset.</summary>
    private static HashSet<(string, int)> MemberInitializerNames(WorkspaceSnapshot snapshot)
    {
        var names = new HashSet<(string, int)>();
        foreach (var document in snapshot.Documents.Values)
        {
            if (document.CppUnit != null)
            {
                var inFile = new HashSet<(string, int)>();
                Collect(document.Path, document.CppUnit.Declarations, inFile);
                foreach (var (file, offset) in inFile)
                {
                    names.Add((file, document.CppMap?.Original(offset) ?? offset));
                }
            }
        }

        return names;
    }

    private static void Collect(string file, IEnumerable<PaspanParsers.Cpp.Declaration> declarations, HashSet<(string, int)> names)
    {
        foreach (var declaration in declarations)
        {
            switch (declaration)
            {
                case PaspanParsers.Cpp.NamespaceDefinition ns:
                    Collect(file, ns.Declarations, names);
                    break;
                case PaspanParsers.Cpp.LinkageSpecification linkage:
                    Collect(file, linkage.Declarations, names);
                    break;
                case PaspanParsers.Cpp.TemplateDeclaration template:
                    Collect(file, [template.Declaration], names);
                    break;
                case PaspanParsers.Cpp.FunctionDefinition function:
                    foreach (var initializer in function.Initializers ?? [])
                    {
                        names.Add((file, initializer.Member is PaspanParsers.Cpp.QualifiedName q ? q.Name.Span.Start : initializer.Member.Span.Start));
                    }

                    break;
                case PaspanParsers.Cpp.SimpleDeclaration simple:
                    foreach (var cls in simple.Specifiers?.Specifiers.OfType<PaspanParsers.Cpp.ClassSpecifier>() ?? [])
                    {
                        Collect(file, cls.Members, names);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// What clang's JSON AST holds no references for, by file: the operands of <c>decltype</c> in types,
    /// using-declarations and explicit instantiations; and the names of structured bindings, which it binds to
    /// the members they hold only in its implicit code.
    /// </summary>
    private static Dictionary<string, List<PaspanParsers.TextSpan>> HiddenSpans(WorkspaceSnapshot snapshot)
    {
        var result = new Dictionary<string, List<PaspanParsers.TextSpan>>();
        foreach (var document in snapshot.Documents.Values)
        {
            if (document.CppUnit == null)
            {
                continue;
            }

            var spans = new List<PaspanParsers.TextSpan>();
            Hidden(document.CppUnit, spans);
            result[document.Path] = document.CppMap is { } map ? spans.Select(s => new PaspanParsers.TextSpan(map.Original(s.Start), map.Original(s.End, end: true))).ToList() : spans;
        }

        return result;
    }

    private static void Hidden(object node, List<PaspanParsers.TextSpan> spans)
    {
        switch (node)
        {
            case PaspanParsers.Cpp.DecltypeSpecifier or PaspanParsers.Cpp.UsingDeclaration or PaspanParsers.Cpp.ExplicitInstantiation or PaspanParsers.Cpp.DecltypeName
                or PaspanParsers.Cpp.StructuredBindingDeclarator:
                spans.Add(((PaspanParsers.Cpp.CppNode)node).Span);
                return;
            case null or string:
                return;
        }

        if (node is not PaspanParsers.Cpp.CppNode)
        {
            return;
        }

        foreach (var property in node.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length > 0 || property.Name is "Span" or "LeadingDirectives")
            {
                continue;
            }

            var value = property.GetValue(node);
            if (value is PaspanParsers.Cpp.CppNode child)
            {
                Hidden(child, spans);
            }
            else if (value is System.Collections.IEnumerable list and not string)
            {
                foreach (var item in list)
                {
                    if (item is PaspanParsers.Cpp.CppNode element)
                    {
                        Hidden(element, spans);
                    }
                }
            }
        }
    }
}
