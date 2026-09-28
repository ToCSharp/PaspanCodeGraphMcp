using System.Text;
using PaspanParsers;
using PaspanParsers.Cpp;

namespace PaspanCodeGraph.Cpp;

/// <summary>A parsed C++ file.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Project">Name of the project the file belongs to.</param>
/// <param name="Utf8">The text the parser read (<see cref="CppMacroPlan.Prepare"/>): the file's, with macros blanked out or expanded.</param>
/// <param name="Lines">The lines of the file.</param>
/// <param name="Includes">The workspace files its <c>#include</c> directives name.</param>
public sealed record CppSource(string Path, string Project, ReadOnlyMemory<byte> Utf8, LineMap Lines, TranslationUnit Unit, IReadOnlyList<string> Includes)
{
    /// <summary>Takes offsets of <see cref="Utf8"/> back to the file when macros were expanded; null when they are the same.</summary>
    public CppOffsetMap? Map { get; init; }

    /// <summary>The offset in the file of an offset of the parsed text.</summary>
    public int Original(int offset, bool end = false) => Map?.Original(offset, end) ?? offset;
}

/// <summary>What a pass over the files of a C++ workspace collects.</summary>
public enum CppPass
{
    /// <summary>Namespaces, classes, enums, aliases, concepts and macros declared with an unqualified name.</summary>
    Types,

    /// <summary>Classes defined with a qualified name (<c>struct Outer::Inner { }</c>), whose qualifier may be in another file.</summary>
    QualifiedTypes,

    /// <summary>Functions, fields, variables and enumerators, with ids whose parameter types are bound; bases.</summary>
    Members,

    /// <summary>References to types and members in declarations and bodies.</summary>
    References,
}

/// <summary>
/// Adds the declarations of parsed C++ files to a <see cref="SymbolIndexBuilder"/>: namespaces, classes, structs,
/// unions, enums and enumerators, aliases, concepts, functions, members, variables and macros. Ids follow the form
/// of documentation-comment ids with C++ names: <c>N:ns</c>, <c>T:ns::Widget</c>,
/// <c>M:ns::Widget::resize(int,int)const</c>, <c>F:ns::Widget::size</c>, <c>D:MACRO</c>; parameter types are
/// spelled canonically (<see cref="TypeKey"/>), so that a declaration in a header and a definition in a source
/// file are one symbol. Template parameters are numbered as in documentation ids (<c>`0</c> of a class template,
/// <c>``0</c> of a function template), entities with internal linkage (<c>static</c> functions and variables of a
/// namespace, unnamed namespaces) get the name of their file in the id.
/// </summary>
public sealed partial class CppSymbolCollector
{
    private readonly CppSource _source;
    private readonly SymbolIndexBuilder _builder;
    private readonly CppBinder _binder;
    private readonly CppPass _pass;
    private readonly string _fileName;

    private CppSymbolCollector(CppSource source, SymbolIndexBuilder builder, CppBinder binder, CppPass pass)
    {
        _source = source;
        _builder = builder;
        _binder = binder;
        _pass = pass;
        _fileName = System.IO.Path.GetFileName(source.Path);
    }

    private ReadOnlySpan<byte> Utf8 => _source.Utf8.Span;

    /// <summary>
    /// Declares the symbols of all the files of a workspace: their types, then qualified class definitions, then
    /// members, and links the hierarchy. The references of each file are found afterwards with <see cref="References"/>.
    /// </summary>
    public static void Declare(IReadOnlyList<CppSource> sources, SymbolIndexBuilder builder, CppBinder binder)
    {
        foreach (var pass in new[] { CppPass.Types, CppPass.QualifiedTypes })
        {
            foreach (var source in sources)
            {
                new CppSymbolCollector(source, builder, binder, pass).Run();
            }
        }

        binder.CompleteForwardDeclarations();
        foreach (var source in sources)
        {
            new CppSymbolCollector(source, builder, binder, CppPass.Members).DeclareFile();
        }

        binder.CompleteFiles();

        foreach (var source in sources)
        {
            new CppSymbolCollector(source, builder, binder, CppPass.Members).Run();
        }

        binder.OrderDeclarations();
        CppHierarchy.Link(builder, binder);
    }

    /// <summary>The references of one file, after <see cref="Declare"/>; files can be walked in parallel.</summary>
    public static List<(string TargetId, SymbolReference Reference)> References(CppSource source, SymbolIndexBuilder builder, CppBinder binder)
    {
        var collector = new CppSymbolCollector(source, builder, binder, CppPass.References);
        collector.Run();
        return collector._references;
    }

    private CppScope GlobalScope()
    {
        var scope = new CppScope(CppScopeKind.Global, null);
        return scope;
    }

    private void Run()
    {
        var scope = GlobalScope();
        if (_pass == CppPass.Types)
        {
            DeclareMacros();
        }

        if (_pass == CppPass.References)
        {
            WalkDirectives();
        }

        VisitDeclarations(_source.Unit.Declarations, scope, []);
    }

    /// <summary>Records the using-directives at the global scope of the file, which files including it see too.</summary>
    private void DeclareFile()
    {
        var scope = GlobalScope();
        var usings = new List<CodeSymbol>();
        foreach (var declaration in Flatten(_source.Unit.Declarations))
        {
            if (declaration is UsingDirective directive && ResolveQualifierAsNamespace(directive.Namespace, scope) is { } ns)
            {
                usings.Add(ns);
            }
        }

        _binder.SetFile(_source.Path, usings, _source.Includes);
    }

    /// <summary>Declarations of the global scope, through linkage specifications and export blocks.</summary>
    private static IEnumerable<Declaration> Flatten(IReadOnlyList<Declaration> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is LinkageSpecification linkage)
            {
                foreach (var inner in Flatten(linkage.Declarations))
                {
                    yield return inner;
                }
            }
            else if (declaration is ExportDeclaration export)
            {
                foreach (var inner in Flatten(export.Declarations))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return declaration;
            }
        }
    }

    private CodeSymbol? ResolveQualifierAsNamespace(Name name, CppScope scope)
    {
        var found = ResolveName(name, scope, typesOnly: true);
        return found.Symbols.FirstOrDefault(s => s.Kind == SymbolKind.Namespace);
    }

    // ========================================
    // Declarations
    // ========================================

    private void VisitDeclarations(IReadOnlyList<Declaration> declarations, CppScope scope, IReadOnlyList<TemplateDeclaration> templates)
    {
        foreach (var declaration in declarations)
        {
            VisitDeclaration(declaration, scope, templates);
        }
    }

    private void VisitDeclaration(Declaration declaration, CppScope scope, IReadOnlyList<TemplateDeclaration> templates)
    {
        switch (declaration)
        {
            case NamespaceDefinition ns:
                VisitNamespace(ns, scope);
                break;
            case LinkageSpecification linkage:
                VisitDeclarations(linkage.Declarations, scope, []);
                break;
            case ExportDeclaration export:
                VisitDeclarations(export.Declarations, scope, []);
                break;
            case TemplateDeclaration template:
                if (_pass == CppPass.References)
                {
                    WalkTemplateParameters(template, scope);
                }

                VisitDeclaration(template.Declaration, scope, [.. templates, template]);
                break;
            case ExplicitInstantiation instantiation:
                if (_pass == CppPass.References)
                {
                    WalkDeclarationReferences(instantiation.Declaration, scope, null);
                }

                break;
            case SimpleDeclaration simple:
                VisitSimple(simple, scope, templates);
                break;
            case FunctionDefinition function:
                VisitFunctionDefinition(function, scope, templates);
                break;
            case AliasDeclaration alias:
                VisitAlias(alias, scope, templates);
                break;
            case ConceptDefinition concept:
                VisitConcept(concept, scope, templates);
                break;
            case AccessSpecifier access:
                scope.Access = access.Access;
                break;
            case UsingDirective directive when _pass >= CppPass.Members:
                if (ResolveQualifierAsNamespace(directive.Namespace, scope) is { } used)
                {
                    (scope.Usings ??= []).Add(used);
                }

                if (_pass == CppPass.References)
                {
                    WalkName(directive.Namespace, scope, null, NameUse.Namespace);
                }

                break;
            case NamespaceAliasDefinition alias when _pass >= CppPass.QualifiedTypes:
                if (ResolveQualifierAsNamespace(alias.Target, scope) is { } target)
                {
                    scope.AddAlias(alias.Alias, target);
                }

                break;
            case UsingDeclaration usingDeclaration when _pass >= CppPass.Members:
                foreach (var declarator in usingDeclaration.Declarators)
                {
                    var found = ResolveName(declarator.Name, scope);
                    foreach (var symbol in found.Symbols)
                    {
                        scope.AddAlias(LastName(declarator.Name), symbol);
                    }

                    if (_pass == CppPass.References)
                    {
                        WalkName(declarator.Name, scope, InMember(scope), NameUse.Any);
                    }
                }

                break;
            case UsingEnumDeclaration usingEnum when _pass >= CppPass.Members:
                if (ResolveName(usingEnum.Enum, scope, typesOnly: true).Symbols.FirstOrDefault(s => s.Kind == SymbolKind.Enum) is { } usedEnum)
                {
                    foreach (var enumerator in usedEnum.Members)
                    {
                        scope.AddAlias(enumerator.Name, enumerator);
                    }
                }

                break;
            case StaticAssertDeclaration staticAssert when _pass == CppPass.References:
                WalkExpression(staticAssert.Condition, scope, InMember(scope));
                break;
        }
    }

    // ========================================
    // Namespaces
    // ========================================

    private void VisitNamespace(NamespaceDefinition ns, CppScope scope)
    {
        var positions = NamespaceNamePositions(ns);
        var inner = scope;
        if (ns.Names.Count == 0)
        {
            var symbol = Namespace(scope.Container, "(anonymous)", $"(anonymous@{_fileName})", inline: true, ns, positions.Count > 0 ? positions[0] : ns.Span.Start);
            inner = new CppScope(CppScopeKind.Namespace, inner, symbol);
        }
        else
        {
            for (var i = 0; i < ns.Names.Count; i++)
            {
                var part = ns.Names[i];
                var inline = part.IsInline || (i == ns.Names.Count - 1 && ns.IsInline);
                var symbol = Namespace(inner.Container, part.Identifier, part.Identifier, inline, ns, i < positions.Count ? positions[i] : ns.Span.Start);
                inner = new CppScope(CppScopeKind.Namespace, inner, symbol);
            }
        }

        VisitDeclarations(ns.Declarations, inner, []);
    }

    private CodeSymbol? Namespace(CodeSymbol? container, string name, string idName, bool inline, NamespaceDefinition definition, int namePosition)
    {
        var id = "N:" + Qualify(container, idName);
        if (_pass != CppPass.Types)
        {
            return _builder.Get(id);
        }

        var symbol = _builder.GetOrAdd(id, SymbolKind.Namespace, name, container, out var created);
        if (created)
        {
            Setup(symbol, container);
            symbol.Signature = "namespace " + id[2..];
            if (idName == name)
            {
                _binder.Declare(container, name, symbol);
            }
        }

        if (inline)
        {
            _binder.DeclareInlineNamespace(container, symbol);
        }

        _builder.AddDeclaration(symbol, Location(definition, namePosition));
        return symbol;
    }

    /// <summary>The offsets of the names of a namespace definition, or of <c>namespace</c> for an unnamed one.</summary>
    private List<int> NamespaceNamePositions(NamespaceDefinition ns)
    {
        var positions = new List<int>();
        var namespaceKeyword = -1;
        foreach (var (word, offset) in Words(ns.Span.Start, ns.Declarations.Count > 0 ? ns.Declarations[0].Span.Start : ns.Span.End, stopAt: (byte)'{'))
        {
            if (word == "namespace")
            {
                namespaceKeyword = offset;
                continue;
            }

            if (word == "inline")
            {
                continue;
            }

            if (namespaceKeyword >= 0)
            {
                positions.Add(offset);
            }
        }

        if (ns.Names.Count == 0)
        {
            // An unnamed namespace is where its brace is, as in clang
            var brace = _source.Utf8.Span[ns.Span.Start..ns.Span.End].IndexOf((byte)'{');
            return brace >= 0 ? [ns.Span.Start + brace] : [];
        }

        return positions;
    }

    // ========================================
    // Simple declarations
    // ========================================

    private void VisitSimple(SimpleDeclaration declaration, CppScope scope, IReadOnlyList<TemplateDeclaration> templates)
    {
        var specifiers = declaration.Specifiers?.Specifiers ?? [];
        var isTypedef = specifiers.Any(s => s is KeywordSpecifier { Keyword: "typedef" });
        var isFriend = specifiers.Any(s => s is KeywordSpecifier { Keyword: "friend" });
        var outer = templates.Count > 0 ? (Declaration)templates[0] : declaration;
        var typedefName = isTypedef && declaration.Declarators.Count > 0 && FindName(declaration.Declarators[0].Declarator) is { Name: IdentifierName first }
            ? first
            : null;

        CodeSymbol? declaredType = null;
        var namedByTypedef = false;
        foreach (var specifier in specifiers)
        {
            switch (specifier)
            {
                case ClassSpecifier cls:
                    namedByTypedef = cls.Name == null && typedefName != null;
                    declaredType = VisitClass(cls, declaration, outer, scope, templates, namedByTypedef ? typedefName : null);
                    break;
                case EnumSpecifier enumSpecifier:
                    namedByTypedef = enumSpecifier.Name == null && typedefName != null;
                    declaredType = VisitEnum(enumSpecifier, declaration, outer, scope, templates, namedByTypedef ? typedefName : null);
                    break;
                case ElaboratedTypeSpecifier elaborated when !isFriend && (declaration.Declarators.Count == 0 || elaborated.Name is IdentifierName):
                    // struct X *p; declares X when nothing named X is declared around it
                    ForwardDeclare(elaborated, outer, scope, templates, onlyIfUndeclared: declaration.Declarators.Count > 0);
                    break;
            }
        }

        if (_pass == CppPass.References)
        {
            WalkSpecifierReferences(declaration.Specifiers, scope, InMember(scope), skipClassBodies: true);
        }

        for (var i = 0; i < declaration.Declarators.Count; i++)
        {
            var initDeclarator = declaration.Declarators[i];
            if (initDeclarator.Declarator == null)
            {
                continue;
            }

            if (isTypedef)
            {
                if (i == 0 && namedByTypedef)
                {
                    continue;
                }

                VisitTypedef(declaration, initDeclarator, outer, scope, templates, declaredType);
                continue;
            }

            var (function, name) = Analyze(initDeclarator.Declarator);
            if (name == null)
            {
                continue;
            }

            if (function != null)
            {
                VisitFunction(declaration.Specifiers, initDeclarator.Declarator, function, name, outer, scope, templates, null, initDeclarator, isFriend);
            }
            else if (!isFriend)
            {
                VisitVariable(declaration, initDeclarator, name, outer, scope, templates);
            }
        }
    }

    /// <summary>
    /// The function declarator that declares the name of <paramref name="declarator"/> (null for a variable) and
    /// the name; <c>int *f(int)</c> is a pointer declarator around the function declarator of <c>f</c>.
    /// </summary>
    internal static (FunctionDeclarator? Function, NameDeclarator? Name) Analyze(Declarator? declarator)
    {
        FunctionDeclarator? function = null;
        while (declarator != null)
        {
            switch (declarator)
            {
                case NameDeclarator name:
                    return (function, name);
                case FunctionDeclarator f:
                    if (Unparenthesize(f.Inner) is NameDeclarator)
                    {
                        function = f;
                    }

                    declarator = f.Inner;
                    break;
                case PointerDeclarator p:
                    declarator = p.Inner;
                    break;
                case ReferenceDeclarator r:
                    declarator = r.Inner;
                    break;
                case ArrayDeclarator a:
                    declarator = a.Inner;
                    break;
                case ParenthesizedDeclarator paren:
                    declarator = paren.Inner;
                    break;
                case PackDeclarator pack:
                    declarator = pack.Inner;
                    break;
                case MemberPointerDeclarator m:
                    declarator = m.Inner;
                    break;
                default:
                    return (null, null);
            }
        }

        return (null, null);
    }

    private static Declarator? Unparenthesize(Declarator? declarator)
    {
        while (declarator is ParenthesizedDeclarator paren)
        {
            declarator = paren.Inner;
        }

        return declarator;
    }

    internal static NameDeclarator? FindName(Declarator? declarator) => Analyze(declarator).Name;

    // ========================================
    // Classes and enums
    // ========================================

    /// <summary>The template parameters of a declaration's innermost template, by name, as <c>`0</c>, <c>`1</c>.</summary>
    private static CppScope WithTemplateParameters(CppScope scope, TemplateDeclaration? template, string prefix)
    {
        if (template == null || template.Parameters.Count == 0)
        {
            return scope;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < template.Parameters.Count; i++)
        {
            if (TemplateParameterName(template.Parameters[i]) is { } name)
            {
                map[name] = prefix + i;
            }
        }

        return new CppScope(CppScopeKind.Template, scope) { TemplateParameters = map };
    }

    internal static string? TemplateParameterName(TemplateParameter parameter) => parameter switch
    {
        TypeTemplateParameter type => type.Identifier,
        NonTypeTemplateParameter nonType => FindName(nonType.Parameter.Declarator)?.Name is IdentifierName id ? id.Identifier : null,
        TemplateTemplateParameter template => template.Identifier,
        _ => null,
    };

    private CodeSymbol? VisitClass(ClassSpecifier cls, SimpleDeclaration declaration, Declaration outer, CppScope scope, IReadOnlyList<TemplateDeclaration> templates, IdentifierName? typedefName)
    {
        var template = templates.Count > 0 ? templates[^1] : null;
        var templateScope = WithTemplateParameters(scope, template, "`");
        if (cls.Name == null && typedefName == null && (declaration.Declarators.Count == 0 || FindName(declaration.Declarators[0].Declarator)?.Name is not IdentifierName))
        {
            // An anonymous union or struct: its members are members of the enclosing class or namespace
            VisitDeclarations(cls.Members, scope, []);
            return null;
        }

        // An unnamed class of a variable is named after it: struct { int x; } point;
        var nameNode = cls.Name ?? typedefName ?? new IdentifierName($"(unnamed {cls.Key} of {((IdentifierName)FindName(declaration.Declarators[0].Declarator)!.Name).Identifier})")
        {
            Span = new PaspanParsers.TextSpan(cls.Span.Start, cls.Span.Start),
        };
        if (!TryContainer(nameNode, scope, out var container, out var qualified))
        {
            return null;
        }

        var name = LastName(nameNode);
        var idName = nameNode is TemplateIdName or QualifiedName { Name: TemplateIdName }
            ? name + TemplateArgumentsKey(nameNode as TemplateIdName ?? (TemplateIdName)((QualifiedName)nameNode).Name, templateScope, canonical: true)
            : name;
        var kind = cls.Key switch { "struct" => SymbolKind.Struct, "union" => SymbolKind.Union, _ => SymbolKind.Class };
        var id = "T:" + Qualify(container, idName);
        CodeSymbol? symbol;
        if ((_pass == CppPass.Types && !qualified) || (_pass == CppPass.QualifiedTypes && qualified))
        {
            symbol = _builder.GetOrAdd(id, kind, name, container, out var created);
            if (created)
            {
                Setup(symbol, container);
                if (idName == name)
                {
                    _binder.Declare(container, name, symbol);
                }

                if (template != null)
                {
                    _binder.Info(symbol).TemplateParameters.AddRange(template.Parameters.Select(p => TemplateParameterName(p) ?? ""));
                    symbol.TypeParameters.AddRange(template.Parameters.Select(Text));
                }
            }

            _builder.AddDeclaration(symbol, Location(outer, NameNode(nameNode).Span.Start));
            symbol.Documentation ??= Documentation(outer);
            if (cls.IsFinal && !symbol.Modifiers.Contains("final"))
            {
                symbol.Modifiers.Add("final");
            }
        }
        else
        {
            symbol = _builder.Get(id);
        }

        if (symbol == null)
        {
            return null;
        }

        if (_pass == CppPass.Members)
        {
            symbol.Accessibility = scope.Kind == CppScopeKind.Class ? scope.Access : "public";
            symbol.Signature = TemplatePrefix(templates) + cls.Key + " " + id[2..] + (cls.IsFinal ? " final" : "")
                + (cls.Bases.Count > 0 ? " : " + string.Join(", ", cls.Bases.Select(Text)) : "");
            var info = _binder.Info(symbol);
            if (info.Bases.Count == 0)
            {
                foreach (var baseSpecifier in cls.Bases)
                {
                    var bound = BindTypeName(baseSpecifier.Name, templateScope);
                    var baseSymbol = bound.Symbol is { } b ? _binder.ClassOf(b) ?? b : null;
                    info.Bases.Add((baseSymbol, bound));
                    var written = Text(baseSpecifier.Name);
                    symbol.BaseTypes.Add(written);
                    symbol.Bases.Add(new TypeLink(written, baseSymbol, baseSymbol?.Id[2..] ?? NameKey(baseSpecifier.Name, templateScope), bound.Arguments.Select(a => a.ToString()).ToList()));
                }
            }
        }

        if (_pass == CppPass.References)
        {
            RecordDeclaredTypeReferences(cls, symbol, templateScope);
        }

        var classScope = new CppScope(CppScopeKind.Class, templateScope, symbol)
        {
            Access = cls.Key == "class" ? "private" : "public",
        };
        VisitDeclarations(cls.Members, classScope, []);
        return symbol;
    }

    /// <summary>
    /// The namespace or class a name declares something in: its qualifier, or the scope's container. False when
    /// the qualifier is not a workspace namespace or class.
    /// </summary>
    private bool TryContainer(Name name, CppScope scope, out CodeSymbol? container, out bool qualified)
    {
        qualified = name is QualifiedName;
        if (name is QualifiedName q)
        {
            var qualifier = ResolveQualifier(q.Qualifier, scope);
            container = qualifier.Symbol;
            return qualifier.Known;
        }

        container = scope.Container;
        return true;
    }

    private CodeSymbol? VisitEnum(EnumSpecifier specifier, SimpleDeclaration declaration, Declaration outer, CppScope scope, IReadOnlyList<TemplateDeclaration> templates, IdentifierName? typedefName)
    {
        if (specifier.Enumerators == null && specifier.Name != null)
        {
            // An opaque declaration: enum class E : int;
            ForwardDeclare(specifier.Name, SymbolKind.Enum, outer, scope, templates);
            return null;
        }

        CodeSymbol? symbol = null;
        var container = scope.Container;
        var nameNode = specifier.Name ?? typedefName;
        if (nameNode != null)
        {
            if (!TryContainer(nameNode, scope, out container, out var qualified))
            {
                return null;
            }

            var name = LastName(nameNode);
            var id = "T:" + Qualify(container, name);
            if ((_pass == CppPass.Types && !qualified) || (_pass == CppPass.QualifiedTypes && qualified))
            {
                symbol = _builder.GetOrAdd(id, SymbolKind.Enum, name, container, out var created);
                if (created)
                {
                    Setup(symbol, container);
                    _binder.Declare(container, name, symbol);
                }

                _builder.AddDeclaration(symbol, Location(outer, NameNode(nameNode).Span.Start));
                symbol.Documentation ??= Documentation(outer);
                if (specifier.ScopedKey != null && !symbol.Modifiers.Contains("scoped"))
                {
                    symbol.Modifiers.Add("scoped");
                }
            }
            else
            {
                symbol = _builder.Get(id);
            }

            if (symbol == null)
            {
                return null;
            }

            if (_pass == CppPass.Members)
            {
                symbol.Accessibility = scope.Kind == CppScopeKind.Class ? scope.Access : "public";
                symbol.Signature = "enum " + (specifier.ScopedKey != null ? specifier.ScopedKey + " " : "") + id[2..]
                    + (specifier.UnderlyingType is { } underlying ? " : " + Text(underlying) : "");
                symbol.Type = specifier.UnderlyingType is { } u ? Text(u) : null;
            }

            if (_pass == CppPass.References)
            {
                WalkName(nameNode, scope, symbol.Id, NameUse.Declaration);
                if (specifier.UnderlyingType is { } underlying)
                {
                    WalkTypeId(underlying, scope, symbol.Id);
                }
            }
        }

        if (_pass is CppPass.Members or CppPass.References)
        {
            foreach (var enumerator in specifier.Enumerators ?? [])
            {
                var owner = symbol ?? container;
                var id = "F:" + Qualify(owner, enumerator.Identifier);
                if (_pass == CppPass.References)
                {
                    if (enumerator.Value != null)
                    {
                        WalkExpression(enumerator.Value, scope, id);
                    }

                    continue;
                }

                var member = _builder.GetOrAdd(id, SymbolKind.EnumMember, enumerator.Identifier, owner, out var created);
                if (created)
                {
                    Setup(member, owner);
                    _binder.Declare(owner, enumerator.Identifier, member);
                    if (symbol != null && specifier.ScopedKey == null)
                    {
                        _binder.Declare(container, enumerator.Identifier, member);
                    }

                    member.Accessibility = symbol?.Accessibility ?? "public";
                    member.Signature = id[2..] + (enumerator.Value is { } value ? " = " + Text(value) : "");
                    member.Type = symbol?.Name;
                    member.Documentation = DocumentationComment.GetText(Utf8, enumerator);
                    if (symbol != null)
                    {
                        _binder.Info(member).Type = CppType.Of(symbol);
                    }
                }

                _builder.AddDeclaration(member, Location(enumerator, enumerator.Span.Start));
            }
        }

        return symbol;
    }

    /// <summary><c>class X;</c>: declares X when the workspace does not define it.</summary>
    private void ForwardDeclare(ElaboratedTypeSpecifier elaborated, Declaration outer, CppScope scope, IReadOnlyList<TemplateDeclaration> templates, bool onlyIfUndeclared)
    {
        var kind = elaborated.Key switch { "struct" => SymbolKind.Struct, "union" => SymbolKind.Union, "enum" => SymbolKind.Enum, _ => SymbolKind.Class };
        if (onlyIfUndeclared && _pass == CppPass.References)
        {
            // The name is a reference, walked with the specifiers
            return;
        }

        ForwardDeclare(elaborated.Name, kind, outer, scope, templates, onlyIfUndeclared);
    }

    private void ForwardDeclare(Name nameNode, SymbolKind kind, Declaration outer, CppScope scope, IReadOnlyList<TemplateDeclaration> templates, bool onlyIfUndeclared = false)
    {
        if (nameNode is TemplateIdName or QualifiedName { Name: TemplateIdName })
        {
            return;
        }

        if (_pass == CppPass.References)
        {
            WalkName(nameNode, scope, InMember(scope), NameUse.Declaration);
            return;
        }

        var qualified = nameNode is QualifiedName;
        if (!((_pass == CppPass.Types && !qualified) || (_pass == CppPass.QualifiedTypes && qualified)) || !TryContainer(nameNode, scope, out var container, out _))
        {
            return;
        }

        var name = LastName(nameNode);
        var template = templates.Count > 0 ? templates[^1] : null;
        _binder.AddForwardDeclaration(new CppForwardDeclaration(
            "T:" + Qualify(container, name), kind, name, container, _source.Project, Location(outer, NameNode(nameNode).Span.Start), Documentation(outer),
            template?.Parameters.Select(p => TemplateParameterName(p) ?? "").ToList() ?? [],
            template?.Parameters.Select(Text).ToList() ?? [])
        {
            OnlyIfUndeclared = onlyIfUndeclared,
        });
    }

    // ========================================
    // Aliases and concepts
    // ========================================

    private void VisitTypedef(SimpleDeclaration declaration, InitDeclarator initDeclarator, Declaration outer, CppScope scope, IReadOnlyList<TemplateDeclaration> templates, CodeSymbol? declaredType)
    {
        if (FindName(initDeclarator.Declarator) is not { Name: IdentifierName nameNode })
        {
            return;
        }

        var name = nameNode.Identifier;
        var container = scope.Container;

        // typedef struct Node { } Node;
        if (declaredType != null && declaredType.Name == name && declaredType.Container == container)
        {
            return;
        }

        var id = "T:" + Qualify(container, name);
        if (_pass == CppPass.Types)
        {
            var symbol = _builder.GetOrAdd(id, SymbolKind.TypeAlias, name, container, out var created);
            if (created)
            {
                Setup(symbol, container);
                _binder.Declare(container, name, symbol);
            }

            _builder.AddDeclaration(symbol, Location(outer, nameNode.Span.Start));
            symbol.Documentation ??= outer == declaration ? DocumentationText(DocumentationComment.Find(Utf8, declaration, initDeclarator)) : Documentation(outer);
        }
        else if (_pass == CppPass.Members && _builder.Get(id) is { } symbol)
        {
            symbol.Accessibility = scope.Kind == CppScopeKind.Class ? scope.Access : "public";
            var type = TypeKey(declaration.Specifiers, initDeclarator.Declarator, scope, canonical: false);
            symbol.Type = type;
            symbol.Signature = "typedef " + type + " " + id[2..];
            _binder.Info(symbol).Type = declaredType != null ? CppType.Of(declaredType) : BindType(declaration.Specifiers, initDeclarator.Declarator, scope);
        }
        else if (_pass == CppPass.References)
        {
            WalkDeclaratorReferences(initDeclarator.Declarator, scope, id);
        }
    }

    private void VisitAlias(AliasDeclaration alias, CppScope scope, IReadOnlyList<TemplateDeclaration> templates)
    {
        var outer = templates.Count > 0 ? (Declaration)templates[0] : alias;
        var template = templates.Count > 0 ? templates[^1] : null;
        var container = scope.Container;
        var id = "T:" + Qualify(container, alias.Identifier);
        var templateScope = WithTemplateParameters(scope, template, "`");
        if (_pass == CppPass.Types)
        {
            var position = Words(alias.Span.Start, alias.Type.Span.Start).Where(w => w.Word == alias.Identifier).Select(w => w.Offset).DefaultIfEmpty(alias.Span.Start).First();
            var symbol = _builder.GetOrAdd(id, SymbolKind.TypeAlias, alias.Identifier, container, out var created);
            if (created)
            {
                Setup(symbol, container);
                _binder.Declare(container, alias.Identifier, symbol);
                if (template != null)
                {
                    _binder.Info(symbol).TemplateParameters.AddRange(template.Parameters.Select(p => TemplateParameterName(p) ?? ""));
                    symbol.TypeParameters.AddRange(template.Parameters.Select(Text));
                }
            }

            _builder.AddDeclaration(symbol, Location(outer, position));
            symbol.Documentation ??= Documentation(outer);
        }
        else if (_pass == CppPass.Members && _builder.Get(id) is { } symbol)
        {
            symbol.Accessibility = scope.Kind == CppScopeKind.Class ? scope.Access : "public";
            symbol.Type = Text(alias.Type);
            symbol.Signature = TemplatePrefix(templates) + "using " + id[2..] + " = " + symbol.Type;
            _binder.Info(symbol).Type = BindType(alias.Type.Specifiers, alias.Type.Declarator, templateScope);
        }
        else if (_pass == CppPass.References)
        {
            WalkTypeId(alias.Type, templateScope, id);
        }
    }

    private void VisitConcept(ConceptDefinition concept, CppScope scope, IReadOnlyList<TemplateDeclaration> templates)
    {
        var outer = templates.Count > 0 ? (Declaration)templates[0] : concept;
        var container = scope.Container;
        var id = "T:" + Qualify(container, concept.Name);
        if (_pass == CppPass.Types)
        {
            var position = Words(concept.Span.Start, concept.Constraint.Span.Start).Where(w => w.Word == concept.Name).Select(w => w.Offset).DefaultIfEmpty(concept.Span.Start).First();
            var symbol = _builder.GetOrAdd(id, SymbolKind.Concept, concept.Name, container, out var created);
            if (created)
            {
                Setup(symbol, container);
                _binder.Declare(container, concept.Name, symbol);
                symbol.TypeParameters.AddRange(templates.Count > 0 ? templates[^1].Parameters.Select(Text) : []);
                symbol.Signature = TemplatePrefix(templates) + "concept " + id[2..];
                symbol.Accessibility = "public";
            }

            _builder.AddDeclaration(symbol, Location(outer, position));
            symbol.Documentation ??= Documentation(outer);
        }
        else if (_pass == CppPass.References)
        {
            WalkExpression(concept.Constraint, WithTemplateParameters(scope, templates.Count > 0 ? templates[^1] : null, "``"), id);
        }
    }

    // ========================================
    // Functions and variables
    // ========================================

    private void VisitFunctionDefinition(FunctionDefinition definition, CppScope scope, IReadOnlyList<TemplateDeclaration> templates)
    {
        var (function, name) = Analyze(definition.Declarator);
        if (function == null || name == null)
        {
            return;
        }

        var isFriend = definition.Specifiers?.Specifiers.Any(s => s is KeywordSpecifier { Keyword: "friend" }) == true;
        var outer = templates.Count > 0 ? (Declaration)templates[0] : definition;
        VisitFunction(definition.Specifiers, definition.Declarator, function, name, outer, scope, templates, definition, null, isFriend);
    }

    /// <summary>A function declaration or definition: its id, and in the Members pass its symbol; in the References pass its body.</summary>
    private void VisitFunction(
        DeclSpecifierSequence? specifiers, Declarator declarator, FunctionDeclarator function, NameDeclarator nameDeclarator, Declaration outer,
        CppScope scope, IReadOnlyList<TemplateDeclaration> templates, FunctionDefinition? definition, InitDeclarator? initDeclarator, bool isFriend)
    {
        if (_pass is CppPass.Types or CppPass.QualifiedTypes)
        {
            return;
        }

        var target = FunctionTarget(specifiers, function, nameDeclarator.Name, scope, templates, isFriend);
        if (target == null)
        {
            return;
        }

        var (id, kind, name, container, memberScope, arity) = target.Value;
        var specializationArguments = nameDeclarator.Name is QualifiedName { Name: TemplateIdName qualifiedArguments } ? qualifiedArguments : nameDeclarator.Name as TemplateIdName;
        if (_pass == CppPass.References)
        {
            WalkFunction(specifiers, declarator, function, nameDeclarator, memberScope, container, definition, initDeclarator, id, scope);
            return;
        }

        var isDefinition = definition != null;
        var symbol = _builder.GetOrAdd(id, kind, name, container, out var created);
        if (created)
        {
            Setup(symbol, container);
            _binder.Declare(container, kind == SymbolKind.Constructor ? ConstructorKey : name, symbol);
        }

        var location = Location(outer, NameNode(nameDeclarator.Name).Span.Start);
        _builder.AddDeclaration(symbol, location);
        var info = _binder.Info(symbol);
        if (isDefinition)
        {
            _binder.SetDefinition(symbol, location);
        }

        if (created || (info.SignatureFromDefinition && !isDefinition))
        {
            info.SignatureFromDefinition = isDefinition;
            info.SignatureKey = ParameterListKey(function, memberScope);
            info.FunctionTemplateArity = arity;
            info.IsVariadic = function.IsVariadic;
            info.SpecializationKey = specializationArguments != null ? TemplateArgumentsKey(specializationArguments, memberScope, canonical: true) : null;
            info.ReturnsReference = (function.TrailingReturnType is { } returned ? Unparenthesize(returned.Declarator) : Unparenthesize(declarator)) is ReferenceDeclarator returnedReference
                ? (returnedReference.IsRvalue ? 2 : 1)
                : 0;
            info.Type = kind is SymbolKind.Constructor ? (container != null ? CppType.Of(container) : CppType.Unknown)
                : nameDeclarator.Name is ConversionFunctionName conversion ? BindType(conversion.Type.Specifiers, conversion.Type.Declarator, memberScope)
                : function.TrailingReturnType is { } trailing ? BindType(trailing.Specifiers, trailing.Declarator, memberScope)
                : BindType(specifiers, declarator, memberScope, stopAt: function);
            info.Parameters.Clear();
            symbol.Parameters.Clear();
            foreach (var parameter in function.Parameters)
            {
                // An explicit object parameter (this Self& self) is the object of a call, not an argument
                if (IsVoidParameter(parameter) || parameter.IsExplicitObject)
                {
                    continue;
                }

                var parameterName = FindName(parameter.Declarator)?.Name is IdentifierName p ? p.Identifier : "";
                var isPack = parameter.Declarator is PackDeclarator || ContainsPack(parameter.Declarator);
                var reference = Unparenthesize(parameter.Declarator) is ReferenceDeclarator byReference ? (byReference.IsRvalue ? 2 : 1) : 0;
                var isConst = parameter.Specifiers?.Specifiers.Any(s => s is KeywordSpecifier { Keyword: "const" }) == true;
                info.Parameters.Add(new CppParameter(parameterName, BindType(parameter.Specifiers, parameter.Declarator, memberScope), parameter.DefaultValue != null, isPack, reference, isConst));
                symbol.Parameters.Add(new ParameterInfo(parameterName, TypeKey(parameter.Specifiers, parameter.Declarator, memberScope, canonical: false), null, parameter.DefaultValue is { } d ? Text(d) : null));
            }

            var returnType = kind is SymbolKind.Constructor or SymbolKind.Destructor || nameDeclarator.Name is ConversionFunctionName
                ? ""
                : function.TrailingReturnType is { } t ? Text(t) : TypeKey(specifiers, declarator, memberScope, canonical: false, stopAt: function);
            symbol.Type = returnType.Length > 0 ? returnType : null;
            symbol.TypeParameters.Clear();
            if (arity > 0)
            {
                symbol.TypeParameters.AddRange(templates[^1].Parameters.Select(Text));
            }

            var parameters = string.Join(", ", function.Parameters.Select(Text)) + (function.IsVariadic && !function.EllipsisWithoutComma ? (function.Parameters.Count > 0 ? ", ..." : "...") : function.IsVariadic ? "..." : "");
            var qualifiers = string.Join(" ", function.Qualifiers.Concat(function.RefQualifier is { } r ? [r] : []).Concat(function.Noexcept != null ? [function.Noexcept.IsThrow ? "throw()" : "noexcept"] : []));
            symbol.Signature = TemplatePrefix(templates) + (returnType.Length > 0 ? returnType + " " : "") + Qualify(container, name) + "(" + parameters + ")" + (qualifiers.Length > 0 ? " " + qualifiers : "")
                + (function.TrailingReturnType != null && returnType.Length > 0 ? "" : "");
        }

        AddModifiers(symbol, specifiers);
        var virtSpecifiers = definition?.VirtSpecifiers ?? initDeclarator?.VirtSpecifiers ?? [];
        foreach (var virt in virtSpecifiers)
        {
            AddModifier(symbol, virt);
        }

        if (function.Qualifiers.Contains("const"))
        {
            AddModifier(symbol, "const");
        }

        if (initDeclarator?.IsPure == true)
        {
            AddModifier(symbol, "virtual");
            AddModifier(symbol, "pure");
            if (container != null)
            {
                AddModifier(container, "abstract");
            }
        }

        if (definition?.IsDeleted == true)
        {
            AddModifier(symbol, "deleted");
        }

        if (definition?.IsDefaulted == true)
        {
            AddModifier(symbol, "defaulted");
        }

        if (isFriend)
        {
            AddModifier(symbol, "friend");
        }

        info.IsVirtual |= symbol.Modifiers.Contains("virtual") || virtSpecifiers.Count > 0;
        info.IsStatic |= symbol.Modifiers.Contains("static");
        if (scope.Kind == CppScopeKind.Class && !isFriend)
        {
            symbol.Accessibility = scope.Access;
        }
        else if (symbol.Accessibility.Length == 0 && (container == null || container.Kind == SymbolKind.Namespace))
        {
            symbol.Accessibility = id.Contains('@') ? "internal" : "public";
        }

        symbol.Documentation ??= initDeclarator != null && outer is SimpleDeclaration simple
            ? DocumentationText(DocumentationComment.Find(Utf8, simple, initDeclarator))
            : Documentation(outer);
    }

    private static bool ContainsPack(Declarator? declarator)
    {
        for (var d = declarator; d != null;)
        {
            switch (d)
            {
                case PackDeclarator:
                    return true;
                case PointerDeclarator p:
                    d = p.Inner;
                    break;
                case ReferenceDeclarator r:
                    d = r.Inner;
                    break;
                case ParenthesizedDeclarator paren:
                    d = paren.Inner;
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Where a function declarator puts its function: the id, the kind, the name, the class or namespace, the scope
    /// its parameters and body see (the class of an out-of-class definition with its namespaces) and the number of its
    /// own template parameters. Null for a deduction guide or a friend declaration of a function of another class.
    /// </summary>
    private (string Id, SymbolKind Kind, string Name, CodeSymbol? Container, CppScope Scope, int Arity)? FunctionTarget(
        DeclSpecifierSequence? specifiers, FunctionDeclarator function, Name nameNode, CppScope scope, IReadOnlyList<TemplateDeclaration> templates, bool isFriend)
    {
        CodeSymbol? container;
        var written = "";
        var classTemplates = 0;
        TemplateIdName? classArguments = null;
        if (nameNode is QualifiedName qualified)
        {
            var qualifier = ResolveQualifier(qualified.Qualifier, scope);
            if (qualifier.Known)
            {
                container = qualifier.Symbol;
            }
            else
            {
                if (isFriend)
                {
                    return null;
                }

                container = scope.Container;
                written = WrittenKey(qualified.Qualifier!, scope, canonical: false) + "::";
            }

            for (var q = qualified.Qualifier; q != null;)
            {
                switch (q)
                {
                    case TemplateIdName t:
                        classTemplates++;
                        classArguments ??= t;
                        q = null;
                        break;
                    case QualifiedName inner:
                        if (inner.Name is TemplateIdName t2)
                        {
                            classTemplates++;
                            classArguments ??= t2;
                        }

                        q = inner.Qualifier;
                        break;
                    default:
                        q = null;
                        break;
                }
            }
        }
        else
        {
            container = isFriend ? scope.Namespace : scope.Container;
        }

        var name = nameNode is ConversionFunctionName conversionName
            ? "operator " + TypeKey(conversionName.Type.Specifiers, conversionName.Type.Declarator, scope, canonical: false)
            : LastName(nameNode);
        var isClass = container is { Kind: not SymbolKind.Namespace };

        // A deduction guide: Box(int) -> Box<int>;
        if (!isClass && specifiers == null && function.TrailingReturnType != null && nameNode is IdentifierName guide
            && Lookup(guide.Identifier, scope, typesOnly: true).Symbols.Any(s => s.Kind.IsType()))
        {
            return null;
        }

        var kind = nameNode is DestructorName || (nameNode is QualifiedName { Name: DestructorName }) ? SymbolKind.Destructor
            : isClass && name == container!.Name && nameNode is not (OperatorFunctionName or ConversionFunctionName) ? SymbolKind.Constructor
            : NameNode(nameNode) is OperatorFunctionName or ConversionFunctionName or LiteralOperatorName || nameNode is ConversionFunctionName ? SymbolKind.Operator
            : isClass ? SymbolKind.Method
            : SymbolKind.Function;
        if (kind == SymbolKind.Destructor)
        {
            name = "~" + (container?.Name ?? name.TrimStart('~'));
        }

        // The scope of the parameters and the body: the class of an out-of-class definition, its template
        // parameters (by the position of the qualifier's arguments) and the function's own template parameters
        var memberScope = container != null && container != scope.Container && !isFriend ? ScopeFor(container, scope) : scope;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (classArguments != null)
        {
            for (var i = 0; i < classArguments.Arguments.Count; i++)
            {
                if (ArgumentIdentifier(classArguments.Arguments[i]) is { } parameter)
                {
                    map[parameter] = "`" + i;
                }
            }
        }

        var arity = 0;
        var own = templates.Count - classTemplates;
        if (own > 0 && templates[^1].Parameters.Count > 0)
        {
            var template = templates[^1];
            arity = template.Parameters.Count;
            for (var i = 0; i < template.Parameters.Count; i++)
            {
                if (TemplateParameterName(template.Parameters[i]) is { } parameter)
                {
                    map[parameter] = "``" + i;
                }
            }
        }

        if (map.Count > 0)
        {
            memberScope = new CppScope(CppScopeKind.Template, memberScope) { TemplateParameters = map };
        }

        var isStatic = specifiers?.Specifiers.Any(s => s is KeywordSpecifier { Keyword: "static" }) == true;
        var suffix = !isClass && isStatic ? "@" + _fileName : "";

        // An explicit specialization of a function template (template <> int max<int>(int, int)) is its own
        // declaration, which calls do not name: they name the template
        var specialization = (nameNode is QualifiedName { Name: TemplateIdName s } ? s : nameNode as TemplateIdName) is { } arguments
            ? TemplateArgumentsKey(arguments, memberScope, canonical: true)
            : "";
        var idName = written + name + specialization + (arity > 0 ? "``" + arity : "");
        var id = "M:" + Qualify(container, idName) + ParameterListKey(function, memberScope) + suffix;
        return (id, kind, name, container, memberScope, arity);
    }

    /// <summary>The identifier a template argument is: <c>T</c> in <c>Box&lt;T&gt;</c>.</summary>
    private static string? ArgumentIdentifier(CppNode argument) => argument switch
    {
        TypeId { Declarator: null, Specifiers.Specifiers: [NamedTypeSpecifier { Name: IdentifierName id }] } => id.Identifier,
        TypeId { Declarator: PackDeclarator { Inner: null }, Specifiers.Specifiers: [NamedTypeSpecifier { Name: IdentifierName id }] } => id.Identifier,
        NameExpression { Name: IdentifierName id } => id.Identifier,
        PackExpansionExpression { Pattern: NameExpression { Name: IdentifierName id } } => id.Identifier,
        _ => null,
    };

    /// <summary>The scopes of a class or namespace and those around it, for a declaration outside it.</summary>
    private static CppScope ScopeFor(CodeSymbol container, CppScope lexical)
    {
        var chain = new List<CodeSymbol>();
        for (var c = container; c != null; c = c.Container)
        {
            chain.Add(c);
        }

        var scope = lexical;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            scope = new CppScope(chain[i].Kind == SymbolKind.Namespace ? CppScopeKind.Namespace : CppScopeKind.Class, scope, chain[i]);
        }

        return scope;
    }

    private void VisitVariable(SimpleDeclaration declaration, InitDeclarator initDeclarator, NameDeclarator nameDeclarator, Declaration outer, CppScope scope, IReadOnlyList<TemplateDeclaration> templates)
    {
        if (_pass is CppPass.Types or CppPass.QualifiedTypes)
        {
            return;
        }

        var nameNode = nameDeclarator.Name;
        if (!TryContainer(nameNode, scope, out var container, out var qualified) || nameNode is not (IdentifierName or QualifiedName { Name: IdentifierName } or TemplateIdName))
        {
            return;
        }

        var name = LastName(nameNode);
        var isClass = container is { Kind: not SymbolKind.Namespace };
        var isStatic = declaration.Specifiers?.Specifiers.Any(s => s is KeywordSpecifier { Keyword: "static" }) == true;
        var id = "F:" + Qualify(container, name) + (!isClass && isStatic ? "@" + _fileName : "");
        var memberScope = qualified && container != null ? ScopeFor(container, scope) : scope;
        if (qualified && nameNode is QualifiedName { Qualifier: TemplateIdName classArguments })
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < classArguments.Arguments.Count; i++)
            {
                if (ArgumentIdentifier(classArguments.Arguments[i]) is { } parameter)
                {
                    map[parameter] = "`" + i;
                }
            }

            memberScope = new CppScope(CppScopeKind.Template, memberScope) { TemplateParameters = map };
        }

        if (_pass == CppPass.References)
        {
            WalkVariable(declaration, initDeclarator, nameDeclarator, memberScope, id);
            return;
        }

        var symbol = _builder.GetOrAdd(id, isClass ? SymbolKind.Field : SymbolKind.Variable, name, container, out var created);
        if (created)
        {
            Setup(symbol, container);
            _binder.Declare(container, name, symbol);
        }

        var location = Location(outer, NameNode(nameNode).Span.Start);
        _builder.AddDeclaration(symbol, location);
        var isExtern = declaration.Specifiers?.Specifiers.Any(s => s is KeywordSpecifier { Keyword: "extern" }) == true;
        if (!isExtern && !(isClass && isStatic && !qualified && initDeclarator.Initializer == null))
        {
            _binder.SetDefinition(symbol, location);
        }

        var info = _binder.Info(symbol);
        if (created || info.Type.IsUnknown)
        {
            info.Type = BindType(declaration.Specifiers, initDeclarator.Declarator, memberScope);
            var type = TypeKey(declaration.Specifiers, initDeclarator.Declarator, memberScope, canonical: false);
            symbol.Type = type;
            symbol.Signature = TemplatePrefix(templates) + type + " " + Qualify(container, name);
        }

        AddModifiers(symbol, declaration.Specifiers);
        info.IsStatic |= isStatic;
        if (scope.Kind == CppScopeKind.Class)
        {
            symbol.Accessibility = scope.Access;
        }
        else if (symbol.Accessibility.Length == 0 && !isClass)
        {
            symbol.Accessibility = id.Contains('@') ? "internal" : "public";
        }

        symbol.Documentation ??= outer == declaration ? DocumentationText(DocumentationComment.Find(Utf8, declaration, initDeclarator)) : Documentation(outer);
    }

    // ========================================
    // Macros
    // ========================================

    private void DeclareMacros()
    {
        var directives = _source.Unit.Directives;
        var guard = IncludeGuard(directives);
        foreach (var directive in directives)
        {
            if (directive.Kind != PreprocessorDirectiveKind.Define || directive.Macro is not { } macro || macro.Name == guard)
            {
                continue;
            }

            var id = "D:" + macro.Name;
            var symbol = _builder.GetOrAdd(id, SymbolKind.Macro, macro.Name, null, out var created);
            if (created)
            {
                symbol.Language = SourceLanguage.Cpp;
                symbol.Project = _source.Project;
                symbol.Accessibility = "public";
                var replacement = macro.Replacement.Length > 200 ? macro.Replacement[..200] + "…" : macro.Replacement;
                symbol.Signature = "#define " + macro.Name + (macro.IsFunctionLike ? "(" + string.Join(", ", macro.Parameters.Concat(macro.IsVariadic ? ["..."] : [])) + ")" : "") + (replacement.Length > 0 ? " " + replacement : "");
                if (macro.IsFunctionLike)
                {
                    foreach (var parameter in macro.Parameters)
                    {
                        symbol.Parameters.Add(new ParameterInfo(parameter, null, null, null));
                    }
                }

                lock (_binder.Macros)
                {
                    _binder.Macros.TryAdd(macro.Name, symbol);
                }
            }

            var position = Words(directive.Span.Start, directive.Span.End).Where(w => w.Word == macro.Name).Select(w => w.Offset).DefaultIfEmpty(directive.Span.Start).First();
            var (line, column) = _source.Lines.GetLineAndColumn(_source.Original(position));
            _builder.AddDeclaration(symbol, new SourceLocation(_source.Path, _source.Original(directive.Span.Start), _source.Original(directive.Span.End, end: true), line, column));
        }
    }

    /// <summary>The macro of an include guard: <c>#ifndef X</c> as the first conditional, then <c>#define X</c>.</summary>
    private static string? IncludeGuard(IReadOnlyList<PreprocessorDirective> directives)
    {
        for (var i = 0; i + 1 < directives.Count; i++)
        {
            if (directives[i].Kind == PreprocessorDirectiveKind.Ifndef)
            {
                var next = directives[i + 1];
                return next.Kind == PreprocessorDirectiveKind.Define && next.Macro is { IsFunctionLike: false, Replacement.Length: 0 } macro
                    && macro.Name == directives[i].Arguments.Trim() ? macro.Name : null;
            }

            if (directives[i].IsConditional)
            {
                return null;
            }
        }

        return null;
    }

    // ========================================
    // Helpers
    // ========================================

    private void Setup(CodeSymbol symbol, CodeSymbol? container)
    {
        symbol.Language = SourceLanguage.Cpp;
        symbol.Project = _source.Project;
        for (var c = container; c != null; c = c.Container)
        {
            if (c.Kind == SymbolKind.Namespace)
            {
                symbol.Namespace = c.Id[2..];
                break;
            }
        }
    }

    /// <summary>A name qualified by its container's name: <c>ns::Widget::resize</c>.</summary>
    internal static string Qualify(CodeSymbol? container, string name) => container == null ? name : container.Id[2..] + "::" + name;

    private SourceLocation Location(CppNode declaration, int nameOffset)
    {
        var (line, column) = _source.Lines.GetLineAndColumn(_source.Original(nameOffset));
        return new SourceLocation(_source.Path, _source.Original(declaration.Span.Start), _source.Original(declaration.Span.End, end: true), line, column);
    }

    private string? Documentation(Declaration declaration) => DocumentationComment.GetText(Utf8, declaration) is { Length: > 0 } text ? text : null;

    private string? DocumentationText(TextSpan? comment) => comment is { } span && DocumentationComment.GetText(Utf8, span) is { Length: > 0 } text ? text : null;

    private string TemplatePrefix(IReadOnlyList<TemplateDeclaration> templates)
    {
        var builder = new StringBuilder();
        foreach (var template in templates)
        {
            builder.Append("template <").AppendJoin(", ", template.Parameters.Select(Text)).Append("> ");
        }

        return builder.ToString();
    }

    private static void AddModifiers(CodeSymbol symbol, DeclSpecifierSequence? specifiers)
    {
        foreach (var specifier in specifiers?.Specifiers ?? [])
        {
            if (specifier is KeywordSpecifier keyword && ModifierKeywords.Contains(keyword.Keyword) && keyword.Keyword is not ("typedef" or "friend" or "__extension__"))
            {
                AddModifier(symbol, keyword.Keyword switch { "__inline" or "__inline__" or "__forceinline" => "inline", _ => keyword.Keyword });
            }
        }
    }

    private static void AddModifier(CodeSymbol symbol, string modifier)
    {
        if (!symbol.Modifiers.Contains(modifier))
        {
            symbol.Modifiers.Add(modifier);
        }
    }

    /// <summary>
    /// The identifiers between two offsets, skipping comments, literals and attributes (<c>[[...]]</c>,
    /// <c>__attribute__((...))</c>, <c>alignas(...)</c>, <c>__declspec(...)</c>); stops at <paramref name="stopAt"/>.
    /// </summary>
    internal IEnumerable<(string Word, int Offset)> Words(int start, int end, byte stopAt = 0)
    {
        var text = _source.Utf8;
        end = Math.Min(end, text.Length);
        var i = start;
        while (i < end)
        {
            var b = text.Span[i];
            if (stopAt != 0 && b == stopAt)
            {
                yield break;
            }

            if (b == '/' && i + 1 < end && text.Span[i + 1] == '/')
            {
                while (i < end && text.Span[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (b == '/' && i + 1 < end && text.Span[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < end && !(text.Span[i] == '*' && text.Span[i + 1] == '/'))
                {
                    i++;
                }

                i += 2;
                continue;
            }

            if (b is (byte)'"' or (byte)'\'')
            {
                i++;
                while (i < end && text.Span[i] != b)
                {
                    i += text.Span[i] == '\\' ? 2 : 1;
                }

                i++;
                continue;
            }

            if (b == '[' && i + 1 < end && text.Span[i + 1] == '[')
            {
                i = SkipBalanced(text.Span, i, end, (byte)'[', (byte)']');
                continue;
            }

            if (IsIdentifierStart(b))
            {
                var wordStart = i;
                while (i < end && IsIdentifierPart(text.Span[i]))
                {
                    i++;
                }

                var word = Encoding.UTF8.GetString(text.Span[wordStart..i]);
                if (word is "__attribute__" or "__attribute" or "alignas" or "__declspec" or "_Alignas")
                {
                    while (i < end && text.Span[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                    {
                        i++;
                    }

                    if (i < end && text.Span[i] == '(')
                    {
                        i = SkipBalanced(text.Span, i, end, (byte)'(', (byte)')');
                    }

                    continue;
                }

                yield return (word, wordStart);
                continue;
            }

            i++;
        }
    }

    private static int SkipBalanced(ReadOnlySpan<byte> text, int i, int end, byte open, byte close)
    {
        var depth = 0;
        for (; i < end; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close && --depth == 0)
            {
                return i + 1;
            }
        }

        return end;
    }

    internal static bool IsIdentifierStart(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_' or (byte)'$' or >= 0x80;

    internal static bool IsIdentifierPart(byte b) => IsIdentifierStart(b) || b is >= (byte)'0' and <= (byte)'9';
}
