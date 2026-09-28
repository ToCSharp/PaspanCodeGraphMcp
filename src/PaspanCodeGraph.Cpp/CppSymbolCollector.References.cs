using PaspanParsers.Cpp;

namespace PaspanCodeGraph.Cpp;

/// <summary>How a name in a declaration is used.</summary>
internal enum NameUse
{
    /// <summary>Anything it finds is referenced.</summary>
    Any,

    /// <summary>A type: the type it finds is referenced.</summary>
    Type,

    /// <summary>A namespace, which is not recorded.</summary>
    Namespace,

    /// <summary>The name a declaration declares: only its qualifier and template arguments are references.</summary>
    Declaration,
}

// The References pass: walks declarations and bodies, binds names and member accesses with the types of
// expressions, chooses overloads by the number and types of arguments, and records what they refer to
public sealed partial class CppSymbolCollector
{
    private readonly List<(string TargetId, SymbolReference Reference)> _references = [];
    private readonly HashSet<DeclSpecifierSequence> _walkedSpecifiers = new(ReferenceEqualityComparer.Instance);
    private int _suppress;

    private const int MaxNameOnlyCandidates = 20;

    private static readonly HashSet<string> SequenceContainers = new(StringComparer.Ordinal)
    {
        "vector", "deque", "list", "forward_list", "array", "span", "set", "multiset", "unordered_set", "unordered_multiset",
        "initializer_list", "valarray", "queue", "stack", "priority_queue", "basic_string_view", "inplace_vector",
        "SmallVector", "ArrayRef", "MutableArrayRef", "SmallPtrSet", "SetVector", "TinyPtrVector",
    };

    private static readonly HashSet<string> MapContainers = new(StringComparer.Ordinal)
    {
        "map", "multimap", "unordered_map", "unordered_multimap", "flat_map", "DenseMap", "StringMap", "MapVector",
    };

    private static readonly HashSet<string> SmartPointers = new(StringComparer.Ordinal)
    {
        "unique_ptr", "shared_ptr", "weak_ptr", "auto_ptr", "optional", "IntrusiveRefCntPtr", "Optional", "expected", "Expected", "ErrorOr",
    };

    /// <summary>The id of the class a declaration at <paramref name="scope"/> belongs to, or null at namespace scope.</summary>
    private static string? InMember(CppScope scope) => scope.Kind == CppScopeKind.Class ? scope.Symbol?.Id : null;

    private void RecordReference(string targetId, int offset, string? inMember, Confidence confidence)
    {
        // A name in the expansion of a macro is written in the macro's definition, not here
        if (_suppress > 0 || offset < 0 || offset > _source.Utf8.Length || _source.Map?.IsExpanded(offset) == true)
        {
            return;
        }

        offset = _source.Original(offset);
        var (line, column) = _source.Lines.GetLineAndColumn(offset);
        _references.Add((targetId, new SymbolReference(_source.Path, offset, line, column, inMember, confidence)));
    }

    private void Record(CodeSymbol symbol, CppNode at, string? inMember, Confidence confidence = Confidence.Exact) =>
        RecordReference(symbol.Id, at.Span.Start, inMember, confidence);

    // ========================================
    // Declarations
    // ========================================

    private void WalkTemplateParameters(TemplateDeclaration template, CppScope scope)
    {
        var templateScope = WithTemplateParameters(scope, template, "`");
        foreach (var parameter in template.Parameters)
        {
            switch (parameter)
            {
                case TypeTemplateParameter type:
                    if (type.Constraint != null)
                    {
                        WalkName(type.Constraint, templateScope, InMember(scope), NameUse.Type);
                    }

                    if (type.Default != null)
                    {
                        WalkTypeId(type.Default, templateScope, InMember(scope));
                    }

                    break;
                case NonTypeTemplateParameter nonType:
                    WalkParameter(nonType.Parameter, templateScope, InMember(scope), null);
                    break;
                case TemplateTemplateParameter templateTemplate when templateTemplate.Default != null:
                    WalkName(templateTemplate.Default, templateScope, InMember(scope), NameUse.Type);
                    break;
            }
        }

        if (template.RequiresClause != null)
        {
            Bind(template.RequiresClause, templateScope, InMember(scope));
        }
    }

    private void WalkDeclarationReferences(Declaration declaration, CppScope scope, string? inMember)
    {
        switch (declaration)
        {
            case SimpleDeclaration simple:
                WalkSpecifierReferences(simple.Specifiers, scope, inMember);
                foreach (var declarator in simple.Declarators)
                {
                    WalkDeclaratorReferences(declarator.Declarator, scope, inMember);
                    if (FindName(declarator.Declarator)?.Name is { } name)
                    {
                        WalkName(name, scope, inMember, NameUse.Any);
                    }
                }

                break;
            case FunctionDefinition function:
                WalkSpecifierReferences(function.Specifiers, scope, inMember);
                WalkDeclaratorReferences(function.Declarator, scope, inMember);
                break;
        }
    }

    /// <summary>The names of a class being defined: its qualifier and its bases.</summary>
    private void RecordDeclaredTypeReferences(ClassSpecifier cls, CodeSymbol symbol, CppScope scope)
    {
        if (cls.Name != null)
        {
            WalkName(cls.Name, scope, symbol.Id, NameUse.Declaration);
        }

        foreach (var baseSpecifier in cls.Bases)
        {
            WalkName(baseSpecifier.Name, scope, symbol.Id, NameUse.Type);
        }
    }

    /// <summary>The types named by declaration specifiers; class and enum bodies are walked where they are declared.</summary>
    private void WalkSpecifierReferences(DeclSpecifierSequence? specifiers, CppScope scope, string? inMember, bool skipClassBodies = true)
    {
        if (specifiers == null || !_walkedSpecifiers.Add(specifiers))
        {
            return;
        }

        foreach (var specifier in specifiers.Specifiers)
        {
            WalkSpecifier(specifier, scope, inMember, skipClassBodies);
        }
    }

    private void WalkSpecifier(DeclSpecifier specifier, CppScope scope, string? inMember, bool skipClassBodies = true)
    {
        switch (specifier)
        {
            case NamedTypeSpecifier named:
                WalkName(named.Name, scope, inMember, NameUse.Type);
                break;
            case ElaboratedTypeSpecifier elaborated:
                WalkName(elaborated.Name, scope, inMember, NameUse.Type);
                break;
            case DecltypeSpecifier { Expression: { } expression }:
                Bind(expression, scope, inMember);
                break;
            case PlaceholderTypeSpecifier placeholder:
                WalkName(placeholder.Concept, scope, inMember, NameUse.Type);
                break;
            case ExplicitSpecifier { Condition: { } condition }:
                Bind(condition, scope, inMember);
                break;
            case BitIntSpecifier bitInt:
                Bind(bitInt.Width, scope, inMember);
                break;
            case ClassSpecifier cls when !skipClassBodies:
                WalkLocalClass(cls, scope, inMember);
                break;
            case EnumSpecifier enumSpecifier when !skipClassBodies:
                foreach (var enumerator in enumSpecifier.Enumerators ?? [])
                {
                    Bind(enumerator.Value, scope, inMember);
                }

                break;
        }
    }

    /// <summary>A class defined in a function body, which has no symbol: the references in it belong to the function.</summary>
    private void WalkLocalClass(ClassSpecifier cls, CppScope scope, string? inMember)
    {
        foreach (var baseSpecifier in cls.Bases)
        {
            WalkName(baseSpecifier.Name, scope, inMember, NameUse.Type);
        }

        var classScope = new CppScope(CppScopeKind.Block, scope);
        foreach (var member in cls.Members)
        {
            switch (member)
            {
                case SimpleDeclaration simple:
                    WalkLocalDeclaration(simple, classScope, inMember);
                    break;
                case FunctionDefinition function:
                {
                    WalkSpecifierReferences(function.Specifiers, classScope, inMember, skipClassBodies: false);
                    var functionScope = new CppScope(CppScopeKind.Block, classScope);
                    if (Analyze(function.Declarator).Function is { } declarator)
                    {
                        foreach (var parameter in declarator.Parameters)
                        {
                            WalkParameter(parameter, functionScope, inMember, functionScope);
                        }
                    }

                    if (function.Body != null)
                    {
                        WalkStatement(function.Body, functionScope, inMember);
                    }

                    break;
                }
            }
        }
    }

    private void WalkTypeId(TypeId? type, CppScope scope, string? inMember)
    {
        if (type == null)
        {
            return;
        }

        foreach (var specifier in type.Specifiers?.Specifiers ?? [])
        {
            WalkSpecifier(specifier, scope, inMember, skipClassBodies: false);
        }

        WalkDeclaratorReferences(type.Declarator, scope, inMember);
    }

    /// <summary>The types and expressions in a declarator: parameters, array sizes, trailing return types, noexcept.</summary>
    private void WalkDeclaratorReferences(Declarator? declarator, CppScope scope, string? inMember)
    {
        while (declarator != null)
        {
            switch (declarator)
            {
                case PointerDeclarator pointer:
                    declarator = pointer.Inner;
                    break;
                case ReferenceDeclarator reference:
                    declarator = reference.Inner;
                    break;
                case MemberPointerDeclarator memberPointer:
                    WalkName(memberPointer.Class, scope, inMember, NameUse.Type);
                    declarator = memberPointer.Inner;
                    break;
                case ArrayDeclarator array:
                    Bind(array.Size, scope, inMember);
                    declarator = array.Inner;
                    break;
                case FunctionDeclarator function:
                    foreach (var parameter in function.Parameters)
                    {
                        WalkParameter(parameter, scope, inMember, null);
                    }

                    WalkTypeId(function.TrailingReturnType, scope, inMember);
                    Bind(function.Noexcept?.Condition, scope, inMember);
                    declarator = function.Inner;
                    break;
                case ParenthesizedDeclarator parenthesized:
                    declarator = parenthesized.Inner;
                    break;
                case PackDeclarator pack:
                    declarator = pack.Inner;
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>A parameter's type and default value; with <paramref name="locals"/>, declares it there.</summary>
    private void WalkParameter(ParameterDeclaration parameter, CppScope scope, string? inMember, CppScope? locals)
    {
        foreach (var specifier in parameter.Specifiers?.Specifiers ?? [])
        {
            WalkSpecifier(specifier, scope, inMember, skipClassBodies: false);
        }

        WalkDeclaratorReferences(parameter.Declarator, scope, inMember);
        Bind(parameter.DefaultValue, scope, inMember);
        if (locals != null && FindName(parameter.Declarator)?.Name is IdentifierName name)
        {
            locals.AddLocal(name.Identifier, BindType(parameter.Specifiers, parameter.Declarator, scope));
        }
    }

    private void WalkFunction(
        DeclSpecifierSequence? specifiers, Declarator declarator, FunctionDeclarator function, NameDeclarator nameDeclarator, CppScope memberScope,
        CodeSymbol? container, FunctionDefinition? definition, InitDeclarator? initDeclarator, string id, CppScope lexicalScope)
    {
        var symbol = _builder.Get(id);
        WalkSpecifierReferences(specifiers, memberScope, id);
        WalkName(nameDeclarator.Name, lexicalScope, id, NameUse.Declaration);
        if (nameDeclarator.Name is ConversionFunctionName conversion)
        {
            WalkTypeId(conversion.Type, memberScope, id);
        }

        // The declarator outside the function's own (a returned pointer to array or function)
        for (var d = declarator; d != null && d != function;)
        {
            switch (d)
            {
                case FunctionDeclarator outerFunction:
                    foreach (var parameter in outerFunction.Parameters)
                    {
                        WalkParameter(parameter, memberScope, id, null);
                    }

                    d = outerFunction.Inner;
                    break;
                case PointerDeclarator p:
                    d = p.Inner;
                    break;
                case ReferenceDeclarator r:
                    d = r.Inner;
                    break;
                case ParenthesizedDeclarator paren:
                    d = paren.Inner;
                    break;
                case ArrayDeclarator a:
                    Bind(a.Size, memberScope, id);
                    d = a.Inner;
                    break;
                default:
                    d = null;
                    break;
            }
        }

        var isMember = container is { Kind: not SymbolKind.Namespace };
        var isStatic = symbol?.Modifiers.Contains("static") == true || specifiers?.Specifiers.Any(s => s is KeywordSpecifier { Keyword: "static" }) == true;
        var functionScope = new CppScope(CppScopeKind.Block, memberScope)
        {
            This = isMember && !isStatic && container != null ? ThisOf(container) : null,
        };
        foreach (var parameter in function.Parameters)
        {
            WalkParameter(parameter, memberScope, id, functionScope);
        }

        WalkTypeId(function.TrailingReturnType, functionScope, id);
        Bind(function.Noexcept?.Condition, functionScope, id);
        Bind(initDeclarator?.RequiresClause ?? definition?.RequiresClause, functionScope, id);
        if (definition == null)
        {
            return;
        }

        foreach (var initializer in definition.Initializers ?? [])
        {
            WalkMemberInitializer(initializer, container, functionScope, id);
        }

        if (definition.Body != null)
        {
            WalkStatement(definition.Body, functionScope, id);
        }

        foreach (var handler in definition.Handlers ?? [])
        {
            WalkCatch(handler, functionScope, id);
        }
    }

    /// <summary>The type of <c>this</c> in a member of <paramref name="type"/>: its own template parameters are its arguments.</summary>
    private CppType ThisOf(CodeSymbol type)
    {
        var parameters = _binder.FindInfo(type)?.TemplateParameters ?? [];
        var arguments = parameters.Select((_, i) => new CppType(null, "`" + i, [], TemplateParameter: i)).ToList();
        return CppType.Of(type, arguments).Pointer();
    }

    /// <summary><c>value(1)</c> or <c>Base{ 2 }</c> in a ctor-initializer: a field, or a base and its constructor.</summary>
    private void WalkMemberInitializer(MemberInitializer initializer, CodeSymbol? container, CppScope scope, string id)
    {
        var arguments = InitializerArguments(initializer.Initializer, scope, id);
        var name = initializer.Member;
        if (container != null && name is IdentifierName identifier)
        {
            var fields = _binder.DeclaredIn(container, identifier.Identifier).Where(s => s.Kind is SymbolKind.Field).ToList();
            if (fields.Count > 0)
            {
                Record(fields[0], identifier, id);
                return;
            }
        }

        var found = ResolveName(name, scope, typesOnly: true);
        if (found.Symbols.FirstOrDefault(s => s.Kind.IsType()) is { } type)
        {
            if (name is QualifiedName qualified)
            {
                WalkQualifier(qualified.Qualifier, scope, id);
            }

            Record(type, NameNode(name), id);
            if (_binder.ClassOf(type) is { } cls)
            {
                RecordConstructor(cls, arguments ?? [], NameNode(name), id, exact: true);
            }

            WalkTemplateArguments(name, scope, id);
        }
    }

    private List<CppType>? InitializerArguments(Initializer? initializer, CppScope scope, string? inMember) => initializer switch
    {
        ParenthesizedInitializer parenthesized => parenthesized.Arguments.Select(a => Bind(a, scope, inMember)).ToList(),
        BracedInitializer braced => braced.List.Elements.Select(a => Bind(a, scope, inMember)).ToList(),
        EqualsInitializer equals => [Bind(equals.Value, scope, inMember)],
        _ => null,
    };

    private void WalkVariable(SimpleDeclaration declaration, InitDeclarator initDeclarator, NameDeclarator nameDeclarator, CppScope scope, string id)
    {
        WalkSpecifierReferences(declaration.Specifiers, scope, id);
        WalkName(nameDeclarator.Name, scope, id, NameUse.Declaration);
        WalkDeclaratorReferences(initDeclarator.Declarator, scope, id);
        Bind(initDeclarator.BitFieldWidth, scope, id);
        var type = BindType(declaration.Specifiers, initDeclarator.Declarator, scope);
        BindInitializer(initDeclarator.Initializer, type, declaration.Specifiers, scope, id);
    }

    /// <summary>The initializer of a variable of <paramref name="type"/>: its expressions and the constructor it calls.</summary>
    private CppType BindInitializer(Initializer? initializer, CppType type, DeclSpecifierSequence? specifiers, CppScope scope, string? inMember)
    {
        if (initializer == null)
        {
            return type;
        }

        if (initializer is EqualsInitializer { Value: var value } && value is not InitializerListExpression)
        {
            return Bind(value, scope, inMember);
        }

        var arguments = InitializerArguments(initializer, scope, inMember) ?? [];
        if (type.Pointers == 0 && Resolve(type).Symbol is { } symbol && _binder.ClassOf(symbol) is { } cls
            && specifiers?.Specifiers.OfType<NamedTypeSpecifier>().FirstOrDefault() is { } named)
        {
            RecordConstructor(cls, arguments, NameNode(named.Name), inMember, exact: true);
        }

        return type;
    }

    // ========================================
    // Names
    // ========================================

    /// <summary>Records what a name in a declaration refers to, as <paramref name="use"/> says.</summary>
    private void WalkName(Name name, CppScope scope, string? inMember, NameUse use)
    {
        if (use == NameUse.Namespace)
        {
            return;
        }

        if (name is QualifiedName qualified)
        {
            WalkQualifier(qualified.Qualifier, scope, inMember);
        }

        WalkTemplateArguments(name, scope, inMember);
        if (use == NameUse.Declaration)
        {
            return;
        }

        var found = ResolveName(name, scope, typesOnly: use == NameUse.Type);
        var target = use == NameUse.Type
            ? found.Symbols.FirstOrDefault(s => s.Kind.IsType() || s.Kind == SymbolKind.Concept)
            : found.Symbols.FirstOrDefault(s => s.Kind != SymbolKind.Namespace);
        if (target != null)
        {
            Record(target, NameNode(name), inMember);
        }
    }

    private void WalkTemplateArguments(Name name, CppScope scope, string? inMember)
    {
        var templateId = name switch
        {
            TemplateIdName t => t,
            QualifiedName { Name: TemplateIdName t } => t,
            _ => null,
        };
        foreach (var argument in templateId?.Arguments ?? [])
        {
            switch (argument)
            {
                case TypeId typeId:
                    WalkTypeId(typeId, scope, inMember);
                    break;
                case Expression expression:
                    Bind(expression, scope, inMember);
                    break;
            }
        }
    }

    /// <summary>The classes a nested name specifier names: <c>Outer</c> and <c>Inner</c> in <c>Outer::Inner::f</c>.</summary>
    private void WalkQualifier(Name? qualifier, CppScope scope, string? inMember)
    {
        switch (qualifier)
        {
            case null or DecltypeName:
                if (qualifier is DecltypeName decltype)
                {
                    Bind(decltype.Expression, scope, inMember);
                }

                return;
            case QualifiedName qualified:
                WalkQualifier(qualified.Qualifier, scope, inMember);
                break;
        }

        WalkTemplateArguments(qualifier, scope, inMember);
        if (ResolveQualifier(qualifier, scope).Symbol is { Kind: not SymbolKind.Namespace } type)
        {
            // An alias names the class, but the reference is to the alias written there
            var written = ResolveName(qualifier, scope, typesOnly: true).Symbols.FirstOrDefault(s => s.Kind.IsType()) ?? type;
            Record(written, NameNode(qualifier), inMember);
        }
    }

    // ========================================
    // Statements
    // ========================================

    private void WalkStatement(Statement? statement, CppScope scope, string? inMember)
    {
        switch (statement)
        {
            case null:
                return;
            case CompoundStatement compound:
            {
                var block = new CppScope(CppScopeKind.Block, scope);
                foreach (var inner in compound.Statements)
                {
                    WalkStatement(inner, block, inMember);
                }

                break;
            }

            case DeclarationStatement declaration:
                WalkBlockDeclaration(declaration.Declaration, scope, inMember);
                break;
            case ExpressionStatement expression:
                Bind(expression.Expression, scope, inMember);
                break;
            case IfStatement ifStatement:
            {
                var block = new CppScope(CppScopeKind.Block, scope);
                WalkStatement(ifStatement.InitStatement, block, inMember);
                WalkCondition(ifStatement.Condition, block, inMember);
                WalkStatement(ifStatement.Then, block, inMember);
                WalkStatement(ifStatement.Else, block, inMember);
                break;
            }

            case SwitchStatement switchStatement:
            {
                var block = new CppScope(CppScopeKind.Block, scope);
                WalkStatement(switchStatement.InitStatement, block, inMember);
                WalkCondition(switchStatement.Condition, block, inMember);
                WalkStatement(switchStatement.Body, block, inMember);
                break;
            }

            case WhileStatement whileStatement:
            {
                var block = new CppScope(CppScopeKind.Block, scope);
                WalkCondition(whileStatement.Condition, block, inMember);
                WalkStatement(whileStatement.Body, block, inMember);
                break;
            }

            case DoStatement doStatement:
                WalkStatement(doStatement.Body, scope, inMember);
                Bind(doStatement.Condition, scope, inMember);
                break;
            case ForStatement forStatement:
            {
                var block = new CppScope(CppScopeKind.Block, scope);
                WalkStatement(forStatement.InitStatement, block, inMember);
                WalkCondition(forStatement.Condition, block, inMember);
                Bind(forStatement.Increment, block, inMember);
                WalkStatement(forStatement.Body, block, inMember);
                break;
            }

            case RangeForStatement rangeFor:
            {
                var block = new CppScope(CppScopeKind.Block, scope);
                WalkStatement(rangeFor.InitStatement, block, inMember);
                var range = Bind(rangeFor.Range, block, inMember);
                var loop = rangeFor.Declaration;
                WalkSpecifierReferences(loop.Specifiers, block, inMember, skipClassBodies: false);
                WalkDeclaratorReferences(loop.Declarator, block, inMember);
                var declared = BindType(loop.Specifiers, loop.Declarator, block);
                var element = IsAuto(loop.Specifiers) ? ElementType(range) : declared;
                DeclareLocal(loop.Declarator, element, block, inMember);
                WalkStatement(rangeFor.Body, block, inMember);
                break;
            }

            case CaseStatement caseStatement:
                Bind(caseStatement.Value, scope, inMember);
                Bind(caseStatement.RangeEnd, scope, inMember);
                WalkStatement(caseStatement.Statement, scope, inMember);
                break;
            case DefaultStatement defaultStatement:
                WalkStatement(defaultStatement.Statement, scope, inMember);
                break;
            case LabeledStatement labeled:
                WalkStatement(labeled.Statement, scope, inMember);
                break;
            case AttributedStatement attributed:
                WalkStatement(attributed.Statement, scope, inMember);
                break;
            case ReturnStatement returnStatement:
                Bind(returnStatement.Expression, scope, inMember);
                break;
            case CoReturnStatement coReturn:
                Bind(coReturn.Expression, scope, inMember);
                break;
            case TryStatement tryStatement:
                WalkStatement(tryStatement.Block, scope, inMember);
                foreach (var handler in tryStatement.Handlers)
                {
                    WalkCatch(handler, scope, inMember);
                }

                break;
        }
    }

    private void WalkCatch(CatchClause handler, CppScope scope, string? inMember)
    {
        var block = new CppScope(CppScopeKind.Block, scope);
        if (handler.Declaration != null)
        {
            WalkParameter(handler.Declaration, block, inMember, block);
        }

        WalkStatement(handler.Body, block, inMember);
    }

    private void WalkCondition(CppNode? condition, CppScope scope, string? inMember)
    {
        switch (condition)
        {
            case Expression expression:
                Bind(expression, scope, inMember);
                break;
            case ConditionDeclaration declaration:
            {
                WalkSpecifierReferences(declaration.Specifiers, scope, inMember, skipClassBodies: false);
                WalkDeclaratorReferences(declaration.Declarator, scope, inMember);
                var type = BindType(declaration.Specifiers, declaration.Declarator, scope);
                var value = BindInitializer(declaration.Initializer, type, declaration.Specifiers, scope, inMember);
                DeclareLocal(declaration.Declarator, IsAuto(declaration.Specifiers) ? Inferred(value) : type, scope, inMember);
                break;
            }
        }
    }

    private void WalkBlockDeclaration(Declaration declaration, CppScope scope, string? inMember)
    {
        switch (declaration)
        {
            case SimpleDeclaration simple:
                WalkLocalDeclaration(simple, scope, inMember);
                break;
            case AliasDeclaration alias:
                WalkTypeId(alias.Type, scope, inMember);
                break;
            case UsingDirective directive:
                if (ResolveQualifierAsNamespace(directive.Namespace, scope) is { } ns)
                {
                    (scope.Usings ??= []).Add(ns);
                }

                break;
            case UsingEnumDeclaration usingEnum:
                WalkName(usingEnum.Enum, scope, inMember, NameUse.Type);
                if (ResolveName(usingEnum.Enum, scope, typesOnly: true).Symbols.FirstOrDefault(s => s.Kind == SymbolKind.Enum) is { } usedEnum)
                {
                    foreach (var enumerator in usedEnum.Members)
                    {
                        scope.AddAlias(enumerator.Name, enumerator);
                    }
                }

                break;
            case UsingDeclaration usingDeclaration:
                foreach (var declarator in usingDeclaration.Declarators)
                {
                    foreach (var symbol in ResolveName(declarator.Name, scope).Symbols)
                    {
                        scope.AddAlias(LastName(declarator.Name), symbol);
                    }

                    WalkName(declarator.Name, scope, inMember, NameUse.Any);
                }

                break;
            case NamespaceAliasDefinition alias:
                if (ResolveQualifierAsNamespace(alias.Target, scope) is { } target)
                {
                    scope.AddAlias(alias.Alias, target);
                }

                break;
            case StaticAssertDeclaration staticAssert:
                Bind(staticAssert.Condition, scope, inMember);
                break;
        }
    }

    private void WalkLocalDeclaration(SimpleDeclaration declaration, CppScope scope, string? inMember)
    {
        WalkSpecifierReferences(declaration.Specifiers, scope, inMember, skipClassBodies: false);
        var isAuto = IsAuto(declaration.Specifiers);
        var isTypedef = declaration.Specifiers?.Specifiers.Any(s => s is KeywordSpecifier { Keyword: "typedef" }) == true;
        foreach (var initDeclarator in declaration.Declarators)
        {
            WalkDeclaratorReferences(initDeclarator.Declarator, scope, inMember);
            if (isTypedef || initDeclarator.Declarator == null)
            {
                continue;
            }

            var (function, _) = Analyze(initDeclarator.Declarator);
            if (function != null)
            {
                continue;
            }

            var declared = BindType(declaration.Specifiers, initDeclarator.Declarator, scope);
            var value = BindInitializer(initDeclarator.Initializer, declared, declaration.Specifiers, scope, inMember);
            var type = isAuto ? Inferred(declared.Pointers > 0 && value.Pointers == 0 ? value.Pointer() : value) : declared;
            DeclareLocal(initDeclarator.Declarator, type, scope, inMember);
        }
    }

    private static CppType Inferred(CppType type) => type.IsUnknown ? type : type with { IsInferred = true };

    private static bool IsAuto(DeclSpecifierSequence? specifiers) =>
        specifiers?.Specifiers.Any(s => s is KeywordSpecifier { Keyword: "auto" } or PlaceholderTypeSpecifier or DecltypeSpecifier { Expression: null }) == true;

    /// <summary>Declares the names of a declarator, the names of a structured binding with the types of the parts.</summary>
    private void DeclareLocal(Declarator? declarator, CppType type, CppScope scope, string? inMember)
    {
        if (Unwrap(declarator) is StructuredBindingDeclarator binding)
        {
            var parts = PartTypes(type, binding.Names.Count);
            for (var i = 0; i < binding.Names.Count; i++)
            {
                scope.AddLocal(binding.Names[i].Identifier, parts[i].Type);
            }

            return;
        }

        if (FindName(declarator)?.Name is IdentifierName name)
        {
            scope.AddLocal(name.Identifier, type);
        }
    }

    private static Declarator? Unwrap(Declarator? declarator)
    {
        while (true)
        {
            switch (declarator)
            {
                case ReferenceDeclarator r:
                    declarator = r.Inner;
                    break;
                case ParenthesizedDeclarator p:
                    declarator = p.Inner;
                    break;
                default:
                    return declarator;
            }
        }
    }

    /// <summary>The types of the parts of a structured binding: the arguments of a pair or tuple, the fields of a struct.</summary>
    private List<(CppType Type, CodeSymbol? Field)> PartTypes(CppType type, int count)
    {
        var parts = Enumerable.Repeat((CppType.Unknown, (CodeSymbol?)null), count).ToList();
        type = Resolve(type);
        if (type.Symbol == null && type.StandardName is "pair" or "tuple")
        {
            for (var i = 0; i < count && i < type.Arguments.Count; i++)
            {
                parts[i] = (Inferred(type.Arguments[i]), null);
            }
        }
        else if (type.Symbol != null && type.Pointers == 0 && _binder.ClassOf(type.Symbol) is { } cls && !_binder.LookupInClass(cls, "get").Any())
        {
            var fields = cls.Members.Where(m => m.Kind == SymbolKind.Field && !m.Modifiers.Contains("static")).ToList();
            for (var i = 0; i < count && i < fields.Count; i++)
            {
                parts[i] = (Inferred((_binder.FindInfo(fields[i])?.Type ?? CppType.Unknown).Substitute(type.Arguments)), fields[i]);
            }
        }

        return parts;
    }

    // ========================================
    // Expressions
    // ========================================

    private void WalkExpression(Expression? expression, CppScope scope, string? inMember) => Bind(expression, scope, inMember);

    private partial CppType TypeOfDecltype(Expression expression, CppScope scope)
    {
        if (_pass != CppPass.References)
        {
            return CppType.Unknown;
        }

        _suppress++;
        try
        {
            return Bind(expression, scope, null);
        }
        finally
        {
            _suppress--;
        }
    }

    /// <summary>Records the references of an expression and returns its type, as far as it is known.</summary>
    private CppType Bind(Expression? expression, CppScope scope, string? inMember)
    {
        switch (expression)
        {
            case null:
                return CppType.Unknown;
            case LiteralExpression literal:
                return (literal.Kind switch
                {
                    LiteralKind.String => CppType.Named("char").Pointer(),
                    LiteralKind.Character => CppType.Named("char"),
                    LiteralKind.Boolean => CppType.Named("bool"),
                    LiteralKind.Floating => CppType.Named(literal.Suffix is "f" or "F" ? "float" : "double"),
                    LiteralKind.Integer => CppType.Named(literal.Suffix is { } s && s.Contains('u', StringComparison.OrdinalIgnoreCase) ? "unsigned int" : "int"),
                    _ => CppType.Named("std::nullptr_t"),
                }) with { IsRvalue = true };
            case ConcatenatedStringExpression:
                return CppType.Named("char").Pointer();
            case NameExpression name:
                return BindNameExpression(name.Name, scope, inMember);
            case ParenthesizedExpression parenthesized:
                return Bind(parenthesized.Expression, scope, inMember);
            case ThisExpression:
                return scope.ThisType ?? CppType.Unknown;
            case UnaryExpression unary:
                return BindUnary(unary, scope, inMember);
            case BinaryExpression binary:
                return BindBinary(binary, scope, inMember);
            case ConditionalExpression conditional:
            {
                Bind(conditional.Condition, scope, inMember);
                var whenTrue = Bind(conditional.WhenTrue, scope, inMember);
                var whenFalse = Bind(conditional.WhenFalse, scope, inMember);
                return whenTrue.IsUnknown ? whenFalse : whenTrue;
            }

            case CallExpression call:
                return BindCall(call, scope, inMember);
            case MemberAccessExpression access:
                return BindMemberAccess(access, null, scope, inMember);
            case SubscriptExpression subscript:
                return BindSubscript(subscript, scope, inMember);
            case CastExpression cast:
                WalkTypeId(cast.Type, scope, inMember);
                Bind(cast.Operand, scope, inMember);
                return BindType(cast.Type.Specifiers, cast.Type.Declarator, scope) with { IsRvalue = Unparenthesize(cast.Type.Declarator) is not ReferenceDeclarator { IsRvalue: false } };
            case NamedCastExpression namedCast:
                WalkTypeId(namedCast.Type, scope, inMember);
                Bind(namedCast.Operand, scope, inMember);
                return BindType(namedCast.Type.Specifiers, namedCast.Type.Declarator, scope) with { IsRvalue = Unparenthesize(namedCast.Type.Declarator) is not ReferenceDeclarator { IsRvalue: false } };
            case FunctionalCastExpression functionalCast:
                return BindFunctionalCast(functionalCast, scope, inMember);
            case NewExpression newExpression:
            {
                foreach (var argument in newExpression.Placement ?? [])
                {
                    Bind(argument, scope, inMember);
                }

                WalkTypeId(newExpression.Type, scope, inMember);
                var type = BindType(newExpression.Type.Specifiers, newExpression.Type.Declarator, scope);
                var arguments = InitializerArguments(newExpression.Initializer, scope, inMember) ?? [];
                if (type.Pointers == 0 && Resolve(type).Symbol is { } symbol && _binder.ClassOf(symbol) is { } cls
                    && newExpression.Type.Specifiers?.Specifiers.OfType<NamedTypeSpecifier>().FirstOrDefault() is { } named)
                {
                    RecordConstructor(cls, arguments, NameNode(named.Name), inMember, exact: true);
                }

                return newExpression.Type.Declarator is ArrayDeclarator ? type : type.Pointer();
            }

            case DeleteExpression delete:
                Bind(delete.Operand, scope, inMember);
                return CppType.Named("void");
            case ThrowExpression throwExpression:
                Bind(throwExpression.Operand, scope, inMember);
                return CppType.Named("void");
            case YieldExpression yieldExpression:
                Bind(yieldExpression.Operand, scope, inMember);
                return CppType.Unknown;
            case SizeOfExpression sizeOf:
                BindOperand(sizeOf.Operand, scope, inMember);
                return CppType.Named("std::size_t");
            case TypeidExpression typeid:
                BindOperand(typeid.Operand, scope, inMember);
                return CppType.Named("std::type_info");
            case NoexceptExpression noexcept:
                Bind(noexcept.Operand, scope, inMember);
                return CppType.Named("bool");
            case SizeOfPackExpression:
                return CppType.Named("std::size_t");
            case BuiltinCallExpression builtin:
                foreach (var argument in builtin.Arguments ?? [])
                {
                    BindOperand(argument, scope, inMember);
                }

                return CppType.Unknown;
            case InitializerListExpression list:
                foreach (var element in list.Elements)
                {
                    Bind(element, scope, inMember);
                }

                return CppType.Unknown;
            case DesignatedInitializerExpression designated:
                foreach (var designator in designated.Designators)
                {
                    Bind(designator.Index, scope, inMember);
                }

                Bind(designated.Value, scope, inMember);
                return CppType.Unknown;
            case PackExpansionExpression pack:
                return Bind(pack.Pattern, scope, inMember) with { IsPack = true };
            case FoldExpression fold:
                Bind(fold.Left, scope, inMember);
                Bind(fold.Right, scope, inMember);
                return CppType.Unknown;
            case LambdaExpression lambda:
                BindLambda(lambda, scope, inMember);
                return CppType.Unknown;
            case RequiresExpression requires:
            {
                var block = new CppScope(CppScopeKind.Block, scope);
                foreach (var parameter in requires.Parameters ?? [])
                {
                    WalkParameter(parameter, block, inMember, block);
                }

                foreach (var requirement in requires.Requirements)
                {
                    switch (requirement)
                    {
                        case SimpleRequirement simple:
                            Bind(simple.Expression, block, inMember);
                            break;
                        case CompoundRequirement compound:
                            Bind(compound.Expression, block, inMember);
                            if (compound.TypeConstraint != null)
                            {
                                WalkName(compound.TypeConstraint, block, inMember, NameUse.Type);
                            }

                            break;
                        case NestedRequirement nested:
                            Bind(nested.Constraint, block, inMember);
                            break;
                        case TypeRequirement type:
                            WalkName(type.Type, block, inMember, NameUse.Type);
                            break;
                    }
                }

                return CppType.Named("bool");
            }

            default:
                return CppType.Unknown;
        }
    }

    private void BindOperand(CppNode? operand, CppScope scope, string? inMember)
    {
        switch (operand)
        {
            case TypeId type:
                WalkTypeId(type, scope, inMember);
                break;
            case Expression expression:
                Bind(expression, scope, inMember);
                break;
        }
    }

    private void BindLambda(LambdaExpression lambda, CppScope scope, string? inMember)
    {
        var block = new CppScope(CppScopeKind.Block, scope);
        foreach (var capture in lambda.Captures)
        {
            if (capture.Initializer != null && capture.Identifier != null)
            {
                var type = capture.Initializer switch
                {
                    EqualsInitializer equals => Bind(equals.Value, scope, inMember),
                    _ => (InitializerArguments(capture.Initializer, scope, inMember) ?? []).FirstOrDefault() ?? CppType.Unknown,
                };
                block.AddLocal(capture.Identifier, Inferred(type));
            }
        }

        if (lambda.TemplateParameters is { Count: > 0 } parameters)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                if (TemplateParameterName(parameter) is { } name)
                {
                    map[name] = "``";
                }
            }

            block.TemplateParameters = map;
        }

        foreach (var parameter in lambda.Parameters ?? [])
        {
            WalkParameter(parameter, block, inMember, block);
        }

        WalkTypeId(lambda.TrailingReturnType, block, inMember);
        Bind(lambda.RequiresClause, block, inMember);
        WalkStatement(lambda.Body, block, inMember);
    }

    /// <summary>A name used as a value: a local, a field (through <c>this</c>), a variable, an enumerator or a macro.</summary>
    private CppType BindNameExpression(Name name, CppScope scope, string? inMember)
    {
        if (name is QualifiedName qualified)
        {
            WalkQualifier(qualified.Qualifier, scope, inMember);
        }

        WalkTemplateArguments(name, scope, inMember);
        var found = ResolveName(name, scope);
        if (found.Local != null)
        {
            return found.Local;
        }

        if (found.TemplateParameter != null)
        {
            return CppType.Unknown;
        }

        if (found.Symbols.Count == 0)
        {
            if (name is IdentifierName identifier && _binder.Macros.TryGetValue(identifier.Identifier, out var macro))
            {
                Record(macro, identifier, inMember);
            }

            return CppType.Unknown;
        }

        var value = found.Symbols.FirstOrDefault(s => s.Kind is SymbolKind.Field or SymbolKind.Variable or SymbolKind.EnumMember);
        if (value != null)
        {
            Record(value, NameNode(name), inMember);
            var type = _binder.FindInfo(value)?.Type ?? CppType.Unknown;
            return value.Kind == SymbolKind.Field && scope.ThisType is { } self ? type.Substitute(self.Arguments) : type;
        }

        var function = found.Symbols.Where(s => s.Kind.IsCallable()).ToList();
        if (function.Count > 0)
        {
            // A function named without a call: taken as a pointer, or passed on
            Record(function[0], NameNode(name), inMember, function.Count == 1 ? Confidence.Exact : Confidence.Inferred);
            return CppType.Unknown;
        }

        if (found.Symbols.FirstOrDefault(s => s.Kind.IsType() || s.Kind == SymbolKind.Concept) is { } type2)
        {
            Record(type2, NameNode(name), inMember);
            return CppType.Of(type2);
        }

        return CppType.Unknown;
    }

    private CppType BindUnary(UnaryExpression unary, CppScope scope, string? inMember)
    {
        var operand = Bind(unary.Operand, scope, inMember);
        if (OperatorCall(unary.Operator, operand, null, UnaryOperatorPosition(unary), scope, inMember, unary.IsPostfix) is { } result)
        {
            return result;
        }

        return unary.Operator switch
        {
            "*" => Dereference(operand),
            "&" => operand.IsUnknown ? operand : operand.Pointer(),
            "!" or "not" => CppType.Named("bool"),
            "co_await" => CppType.Unknown,
            _ => operand,
        };
    }

    private int UnaryOperatorPosition(UnaryExpression unary) => unary.IsPostfix
        ? FindText(unary.Operand.Span.End, unary.Span.End, unary.Operator)
        : unary.Span.Start;

    private CppType BindBinary(BinaryExpression binary, CppScope scope, string? inMember)
    {
        var left = Bind(binary.Left, scope, inMember);
        var right = Bind(binary.Right, scope, inMember);
        var position = FindText(binary.Left.Span.End, binary.Right.Span.Start, binary.Operator);
        if (binary.Operator is not ("." or "->" or ".*" or "->*" or ",") && OperatorCall(binary.Operator, left, right, position, scope, inMember, false) is { } result)
        {
            return result;
        }

        return binary.Operator switch
        {
            "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" or "and" or "or" or "not_eq" => CppType.Named("bool"),
            "," => right,
            ".*" or "->*" => CppType.Unknown,
            _ => left.IsUnknown ? right : left,
        };
    }

    /// <summary>The first offset of <paramref name="text"/> between two offsets, or the first of them.</summary>
    private int FindText(int start, int end, string text)
    {
        var span = _source.Utf8.Span;
        end = Math.Min(end, span.Length);
        start = Math.Clamp(start, 0, end);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var index = span[start..end].IndexOf(bytes);
        return index >= 0 ? start + index : start;
    }

    /// <summary>
    /// An operator applied to a workspace class: its member <c>operator@</c> or one of its namespace (or the scope's).
    /// Records it at the operator and returns its result type; null when no workspace operator applies.
    /// </summary>
    private CppType? OperatorCall(string op, CppType left, CppType? right, int position, CppScope scope, string? inMember, bool postfix)
    {
        var leftClass = ClassOfType(left);
        var rightClass = right != null ? ClassOfType(right) : null;
        if (leftClass == null && rightClass == null)
        {
            return null;
        }

        var name = "operator" + op;
        var candidates = new List<(CodeSymbol Symbol, bool Member)>();
        if (leftClass != null)
        {
            candidates.AddRange(_binder.LookupInClass(leftClass, name).Where(s => s.Kind == SymbolKind.Operator).Select(s => (s, true)));
        }

        var free = new List<CodeSymbol>(Lookup(name, scope).Symbols.Where(s => s.Kind == SymbolKind.Operator && s.ContainingType == null));
        foreach (var cls in new[] { leftClass, rightClass })
        {
            if (cls != null)
            {
                AddNew(free, _binder.DeclaredIn(NamespaceOf(cls), name).Where(s => s.Kind == SymbolKind.Operator));

                // Hidden friends are declared in their namespace but found through their class
                AddNew(free, _binder.DeclaredIn(NamespaceOf(cls), name).Where(s => s.Kind == SymbolKind.Operator));
            }
        }

        candidates.AddRange(free.Where(s => !candidates.Any(c => c.Symbol == s)).Select(s => (s, false)));
        var postfixArguments = postfix ? 1 : 0;
        var viable = candidates.Where(c =>
        {
            var info = _binder.FindInfo(c.Symbol);
            var count = c.Member ? (right != null ? 1 : postfixArguments) : (right != null ? 2 : 1 + postfixArguments);
            return info == null || info.Accepts(count) && (info.Parameters.Count == count || info.IsVariadic || count > 0);
        }).ToList();
        if (viable.Count == 0)
        {
            return null;
        }

        var arguments = new List<CppType>();
        var chosen = Choose(viable.Select(c => c.Symbol).ToList(), c =>
        {
            var member = viable.First(v => v.Symbol == c).Member;
            return member ? (right != null ? [right] : postfix ? [CppType.Named("int")] : []) : right != null ? [left, right] : postfix ? [left, CppType.Named("int")] : [left];
        });
        if (chosen.Symbol == null)
        {
            return null;
        }

        RecordReference(chosen.Symbol.Id, position, inMember, chosen.Confidence);
        var result = _binder.FindInfo(chosen.Symbol)?.Type ?? CppType.Unknown;
        return chosen.Symbol.ContainingType != null ? result.Substitute(Resolve(left).Arguments) : result;
    }

    private static CodeSymbol? NamespaceOf(CodeSymbol symbol)
    {
        for (var c = symbol.Container; c != null; c = c.Container)
        {
            if (c.Kind == SymbolKind.Namespace)
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>The class a value of <paramref name="type"/> is (not a pointer to it), or null.</summary>
    private CodeSymbol? ClassOfType(CppType type)
    {
        type = Resolve(type);
        return type.Pointers == 0 && type.Symbol != null ? _binder.ClassOf(type.Symbol) is { Kind: not SymbolKind.Enum } cls ? cls : null : null;
    }

    /// <summary>The type <c>*value</c> has: the pointee, or what a smart pointer or optional holds.</summary>
    private CppType Dereference(CppType type)
    {
        type = Resolve(type);
        if (type.Pointers > 0)
        {
            return type.Deref();
        }

        if (type.Symbol == null && SmartPointers.Contains(type.StandardName) && type.Arguments.Count > 0)
        {
            return Inferred(type.Arguments[0]);
        }

        return CppType.Unknown;
    }

    /// <summary>What <c>value-&gt;</c> accesses: the pointee, what a smart pointer holds, or what a class's <c>operator-&gt;</c> returns.</summary>
    private CppType ArrowTarget(CppType type)
    {
        type = Resolve(type);
        if (type.Pointers > 0)
        {
            return type.Deref();
        }

        if (type.Symbol == null && SmartPointers.Contains(type.StandardName) && type.Arguments.Count > 0)
        {
            return Inferred(type.Arguments[0]);
        }

        if (ClassOfType(type) is { } cls && _binder.LookupInClass(cls, "operator->").FirstOrDefault() is { } arrow)
        {
            return Inferred(Resolve((_binder.FindInfo(arrow)?.Type ?? CppType.Unknown).Substitute(type.Arguments)).Deref());
        }

        return CppType.Unknown;
    }

    /// <summary>The element type of a range: of an array, a container of the standard library, or what <c>begin()</c> returns.</summary>
    private CppType ElementType(CppType type)
    {
        type = Resolve(type);
        if (type.Pointers > 0)
        {
            return Inferred(type.Deref());
        }

        if (type.Symbol == null)
        {
            var name = type.StandardName;
            if (SequenceContainers.Contains(name) && type.Arguments.Count > 0)
            {
                return Inferred(type.Arguments[0]);
            }

            if (MapContainers.Contains(name) && type.Arguments.Count > 1)
            {
                return Inferred(new CppType(null, "std::pair", [type.Arguments[0], type.Arguments[1]]));
            }

            if (name is "string" or "basic_string" or "string_view")
            {
                return CppType.Named("char");
            }

            return CppType.Unknown;
        }

        if (ClassOfType(type) is { } cls && _binder.LookupInClass(cls, "begin").FirstOrDefault() is { } begin)
        {
            var iterator = Resolve((_binder.FindInfo(begin)?.Type ?? CppType.Unknown).Substitute(type.Arguments));
            return Inferred(Dereference(iterator));
        }

        return CppType.Unknown;
    }

    private CppType BindSubscript(SubscriptExpression subscript, CppScope scope, string? inMember)
    {
        var target = Bind(subscript.Object, scope, inMember);
        var arguments = subscript.Arguments.Select(a => Bind(a, scope, inMember)).ToList();
        if (ClassOfType(target) is { } cls)
        {
            var operators = _binder.LookupInClass(cls, "operator[]").Where(s => s.Kind == SymbolKind.Operator).ToList();
            if (operators.Count > 0)
            {
                var chosen = Choose(operators, _ => arguments);
                if (chosen.Symbol != null)
                {
                    // At the closing bracket, as clang records it
                    RecordReference(chosen.Symbol.Id, subscript.Span.End - 1, inMember, chosen.Confidence);
                    return (_binder.FindInfo(chosen.Symbol)?.Type ?? CppType.Unknown).Substitute(Resolve(target).Arguments);
                }
            }
        }

        var resolved = Resolve(target);
        if (resolved.Pointers > 0)
        {
            return resolved.Deref();
        }

        if (resolved.Symbol == null && MapContainers.Contains(resolved.StandardName) && resolved.Arguments.Count > 1)
        {
            return Inferred(resolved.Arguments[1]);
        }

        return ElementType(resolved);
    }

    private CppType BindFunctionalCast(FunctionalCastExpression cast, CppScope scope, string? inMember)
    {
        var arguments = InitializerArguments(cast.Initializer, scope, inMember) ?? [];
        WalkSpecifier(cast.Type, scope, inMember);
        var type = BindBaseType(new DeclSpecifierSequence([cast.Type]), scope);
        if (cast.Type is KeywordSpecifier { Keyword: "auto" } && arguments.Count == 1)
        {
            // auto(x): a copy of x
            return Inferred(arguments[0]) with { IsRvalue = true };
        }

        if (cast.Type is NamedTypeSpecifier named && Resolve(type).Symbol is { } symbol && _binder.ClassOf(symbol) is { } cls)
        {
            RecordConstructor(cls, arguments, NameNode(named.Name), inMember, exact: true);
        }

        return type with { IsRvalue = true };
    }

    // ========================================
    // Calls
    // ========================================

    private CppType BindCall(CallExpression call, CppScope scope, string? inMember)
    {
        switch (call.Callee)
        {
            case NameExpression name:
            {
                var arguments = call.Arguments.Select(a => Bind(a, scope, inMember)).ToList();
                return BindNamedCall(name.Name, arguments, call, scope, inMember);
            }

            case MemberAccessExpression access:
            {
                var arguments = new List<CppType>();
                return BindMemberAccess(access, () =>
                {
                    arguments.AddRange(call.Arguments.Select(a => Bind(a, scope, inMember)));
                    return arguments;
                }, scope, inMember);
            }

            case ParenthesizedExpression { Expression: NameExpression inner }:
            {
                var arguments = call.Arguments.Select(a => Bind(a, scope, inMember)).ToList();
                return BindNamedCall(inner.Name, arguments, call, scope, inMember);
            }

            default:
            {
                var callee = Bind(call.Callee, scope, inMember);
                var arguments = call.Arguments.Select(a => Bind(a, scope, inMember)).ToList();
                return CallOperator(callee, arguments, call, inMember) ?? CppType.Unknown;
            }
        }
    }

    /// <summary><c>value(arguments)</c> on a value of a workspace class: its <c>operator()</c>.</summary>
    private CppType? CallOperator(CppType callee, List<CppType> arguments, CallExpression call, string? inMember)
    {
        if (ClassOfType(callee) is not { } cls)
        {
            return null;
        }

        var operators = _binder.LookupInClass(cls, "operator()").Where(s => s.Kind == SymbolKind.Operator).ToList();
        var chosen = Choose(operators, _ => arguments);
        if (chosen.Symbol == null)
        {
            return null;
        }

        // At the closing parenthesis, as clang records it
        RecordReference(chosen.Symbol.Id, call.Span.End - 1, inMember, chosen.Confidence);
        return (_binder.FindInfo(chosen.Symbol)?.Type ?? CppType.Unknown).Substitute(Resolve(callee).Arguments);
    }

    private CppType BindNamedCall(Name name, List<CppType> arguments, CallExpression call, CppScope scope, string? inMember)
    {
        if (name is QualifiedName qualified)
        {
            WalkQualifier(qualified.Qualifier, scope, inMember);
        }

        WalkTemplateArguments(name, scope, inMember);
        var nameNode = NameNode(name);
        var found = ResolveName(name, scope);
        if (found.Local is { } local)
        {
            return CallOperator(local, arguments, call, inMember) ?? CppType.Unknown;
        }

        if (found.TemplateParameter != null)
        {
            return CppType.Unknown;
        }

        // A type: a functional cast or a constructor call the parser could not tell from a call
        if (found.Symbols.FirstOrDefault(s => s.Kind.IsType()) is { } type && !found.Symbols.Any(s => s.Kind.IsCallable() && s.Kind != SymbolKind.Constructor))
        {
            Record(type, nameNode, inMember);
            if (_binder.ClassOf(type) is { } cls)
            {
                RecordConstructor(cls, arguments, nameNode, inMember, exact: true);
            }

            return CppType.Of(type, TemplateArguments(name, scope));
        }

        var callables = found.Symbols.Where(s => s.Kind.IsCallable()).ToList();

        // Argument-dependent lookup adds the functions of the arguments' namespaces
        if (name is IdentifierName or TemplateIdName && (callables.Count == 0 || callables.All(c => c.ContainingType == null)))
        {
            foreach (var argument in arguments)
            {
                if (ClassOfType(argument) is { } argumentClass && NamespaceOf(argumentClass) is { } ns)
                {
                    AddNew(callables, _binder.DeclaredIn(ns, LastName(name)).Where(s => s.Kind.IsCallable()));
                }
            }
        }

        // Explicit template arguments name the template, or its explicit specialization for them
        if (callables.Count > 1 && (name as TemplateIdName ?? (name as QualifiedName)?.Name as TemplateIdName) is { } explicitArguments)
        {
            var key = TemplateArgumentsKey(explicitArguments, scope, canonical: true);
            var specialized = callables.Where(c => _binder.FindInfo(c)?.SpecializationKey == key).ToList();
            callables = specialized.Count > 0 ? specialized : callables.Where(c => _binder.FindInfo(c) is { FunctionTemplateArity: > 0, SpecializationKey: null }).ToList() is { Count: > 0 } templates ? templates : callables;
        }

        if (callables.Count > 0)
        {
            var chosen = Choose(callables, _ => arguments);
            if (chosen.Symbol != null)
            {
                RecordReference(chosen.Symbol.Id, nameNode.Span.Start, inMember, chosen.Confidence);
                return ReturnType(chosen.Symbol, scope.ThisType, name, scope);
            }

            foreach (var candidate in chosen.Tied.Take(MaxNameOnlyCandidates))
            {
                RecordReference(candidate.Id, nameNode.Span.Start, inMember, Confidence.NameOnly);
            }

            return CppType.Unknown;
        }

        if (found.Symbols.FirstOrDefault(s => s.Kind is SymbolKind.Field or SymbolKind.Variable) is { } functionObject)
        {
            Record(functionObject, nameNode, inMember);
            var objectType = _binder.FindInfo(functionObject)?.Type ?? CppType.Unknown;
            return CallOperator(objectType, arguments, call, inMember) ?? CppType.Unknown;
        }

        if (found.Symbols.Count == 0 && name is IdentifierName identifier && _binder.Macros.TryGetValue(identifier.Identifier, out var macro))
        {
            Record(macro, identifier, inMember);
            return CppType.Unknown;
        }

        if (found.Symbols.Count == 0 && inMember != null)
        {
            RecordReference(ReferenceTargets.Unresolved + LastName(name), nameNode.Span.Start, inMember, Confidence.NameOnly);
        }

        return StandardCall(name, arguments, scope);
    }

    /// <summary>The types of the template arguments of a template-id, bound.</summary>
    private List<CppType> TemplateArguments(Name name, CppScope scope)
    {
        var templateId = name switch { TemplateIdName t => t, QualifiedName { Name: TemplateIdName t } => t, _ => null };
        return templateId?.Arguments.Select(a => a is TypeId typeId ? BindType(typeId.Specifiers, typeId.Declarator, scope) : CppType.Unknown).ToList() ?? [];
    }

    /// <summary>The results of a few functions of the standard library: <c>std::make_unique&lt;T&gt;</c>, <c>std::move(x)</c>.</summary>
    private CppType StandardCall(Name name, List<CppType> arguments, CppScope scope)
    {
        var last = LastName(name);
        var templateArguments = TemplateArguments(name, scope);
        return last switch
        {
            "make_unique" when templateArguments.Count > 0 => Inferred(new CppType(null, "std::unique_ptr", [templateArguments[0]])),
            "make_shared" or "allocate_shared" when templateArguments.Count > 0 => Inferred(new CppType(null, "std::shared_ptr", [templateArguments[0]])),
            "make_optional" when templateArguments.Count > 0 => Inferred(new CppType(null, "std::optional", [templateArguments[0]])),
            "move" or "forward" when arguments.Count == 1 => arguments[0] with { IsRvalue = true },
            "as_const" or "ref" or "cref" when arguments.Count == 1 => arguments[0],
            "static_pointer_cast" or "dynamic_pointer_cast" when templateArguments.Count > 0 => Inferred(new CppType(null, "std::shared_ptr", [templateArguments[0]])),
            "make_pair" when arguments.Count == 2 => Inferred(new CppType(null, "std::pair", [arguments[0], arguments[1]])),
            "cast" or "dyn_cast" or "dyn_cast_or_null" or "cast_or_null" or "isa" when templateArguments.Count > 0 => last == "isa" ? CppType.Named("bool") : Inferred(templateArguments[0].Pointer()),
            _ => CppType.Unknown,
        };
    }

    /// <summary>The return type of a chosen function, with the template arguments of the class it is called on.</summary>
    private CppType ReturnType(CodeSymbol function, CppType? receiver, Name? name, CppScope scope)
    {
        var info = _binder.FindInfo(function);
        var type = info?.Type ?? CppType.Unknown;
        if (receiver != null)
        {
            type = type.Substitute(Resolve(receiver).Arguments);
        }

        return type.IsUnknown ? type : type with { IsRvalue = info?.ReturnsReference != 1 };
    }

    /// <summary>
    /// <c>object.member</c> or <c>pointer-&gt;member</c>, called with the arguments <paramref name="arguments"/> gives
    /// (bound after the object, in source order) or not called.
    /// </summary>
    private CppType BindMemberAccess(MemberAccessExpression access, Func<List<CppType>>? arguments, CppScope scope, string? inMember)
    {
        var target = Bind(access.Object, scope, inMember);
        var receiver = access.Operator == "->" ? ArrowTarget(target) : target;
        var args = arguments?.Invoke();
        var memberName = access.Member;
        if (memberName is QualifiedName qualifiedMember)
        {
            WalkQualifier(qualifiedMember.Qualifier, scope, inMember);
        }

        WalkTemplateArguments(memberName, scope, inMember);
        var name = LastName(memberName);
        var nameNode = NameNode(memberName);
        var resolved = Resolve(receiver);
        var cls = ClassOfType(resolved);
        if (cls != null)
        {
            var members = memberName is QualifiedName q && ResolveQualifier(q.Qualifier, scope).Symbol is { Kind: not SymbolKind.Namespace } qualifier
                ? _binder.LookupInClass(qualifier, name)
                : _binder.LookupInClass(cls, name);
            var confidence = resolved.IsInferred ? Confidence.Inferred : Confidence.Exact;
            if (args == null)
            {
                if (members.FirstOrDefault(m => m.Kind is SymbolKind.Field or SymbolKind.Variable or SymbolKind.EnumMember) is { } field)
                {
                    RecordReference(field.Id, nameNode.Span.Start, inMember, confidence);
                    return (_binder.FindInfo(field)?.Type ?? CppType.Unknown).Substitute(resolved.Arguments) with { IsInferred = resolved.IsInferred };
                }

                if (members.FirstOrDefault(m => m.Kind.IsCallable()) is { } method)
                {
                    RecordReference(method.Id, nameNode.Span.Start, inMember, members.Count(m => m.Kind.IsCallable()) == 1 ? confidence : Confidence.Inferred);
                }

                return CppType.Unknown;
            }

            var callables = members.Where(m => m.Kind.IsCallable()).ToList();
            if (callables.Count > 0)
            {
                var chosen = Choose(callables, _ => args);
                if (chosen.Symbol != null)
                {
                    RecordReference(chosen.Symbol.Id, nameNode.Span.Start, inMember, chosen.Confidence == Confidence.Exact ? confidence : chosen.Confidence);
                    var result = ReturnType(chosen.Symbol, resolved, memberName, scope);
                    return resolved.IsInferred ? Inferred(result) : result;
                }

                foreach (var candidate in chosen.Tied.Take(MaxNameOnlyCandidates))
                {
                    RecordReference(candidate.Id, nameNode.Span.Start, inMember, Confidence.NameOnly);
                }

                return CppType.Unknown;
            }

            if (members.FirstOrDefault(m => m.Kind is SymbolKind.Field or SymbolKind.Variable) is { } functionObject)
            {
                RecordReference(functionObject.Id, nameNode.Span.Start, inMember, confidence);
                return CppType.Unknown;
            }

            if (inMember != null && members.Count == 0)
            {
                RecordReference(ReferenceTargets.Unresolved + name, nameNode.Span.Start, inMember, Confidence.NameOnly);
            }

            return CppType.Unknown;
        }

        if (StandardMember(resolved, name, args != null) is { } standard)
        {
            return standard;
        }

        // The receiver's type is unknown: every workspace member with the name is a candidate
        if (resolved.Symbol == null && resolved.Pointers == 0 || resolved.TemplateParameter >= 0)
        {
            var candidates = _binder.MembersNamed(name)
                .Where(m => args != null ? m.Kind.IsCallable() && m.Kind != SymbolKind.Constructor && (_binder.FindInfo(m)?.Accepts(args.Count) ?? true) : m.Kind is SymbolKind.Field or SymbolKind.Variable)
                .Take(MaxNameOnlyCandidates)
                .ToList();
            foreach (var candidate in candidates)
            {
                RecordReference(candidate.Id, nameNode.Span.Start, inMember, Confidence.NameOnly);
            }

            if (candidates.Count == 0 && args != null && inMember != null)
            {
                RecordReference(ReferenceTargets.Unresolved + name, nameNode.Span.Start, inMember, Confidence.NameOnly);
            }
        }

        return CppType.Unknown;
    }

    /// <summary>Members of standard library types whose results binding follows.</summary>
    private static CppType? StandardMember(CppType receiver, string member, bool called)
    {
        if (receiver.Symbol != null || receiver.Pointers > 0 || receiver.Name.Length == 0)
        {
            return null;
        }

        var name = receiver.StandardName;
        var arguments = receiver.Arguments;
        if (arguments.Count == 0)
        {
            return null;
        }

        if (SmartPointers.Contains(name))
        {
            return member switch
            {
                "get" when called => Inferred(arguments[0].Pointer()),
                "value" or "operator*" when called => Inferred(arguments[0]),
                "value_or" when called => Inferred(arguments[0]),
                "lock" when called => Inferred(new CppType(null, "std::shared_ptr", [arguments[0]])),
                _ => null,
            };
        }

        if (SequenceContainers.Contains(name))
        {
            return member switch
            {
                "front" or "back" or "at" or "top" when called => Inferred(arguments[0]),
                "data" when called => Inferred(arguments[0].Pointer()),
                _ => null,
            };
        }

        if (MapContainers.Contains(name) && arguments.Count > 1)
        {
            return member switch
            {
                "at" or "lookup" when called => Inferred(arguments[1]),
                _ => null,
            };
        }

        if (name is "pair" && arguments.Count > 1)
        {
            return member switch
            {
                "first" when !called => Inferred(arguments[0]),
                "second" when !called => Inferred(arguments[1]),
                _ => null,
            };
        }

        if (name is "reference_wrapper" && member == "get" && called)
        {
            return Inferred(arguments[0]);
        }

        return null;
    }

    /// <summary>A constructor of <paramref name="type"/> called with the arguments, recorded at the type's name.</summary>
    private void RecordConstructor(CodeSymbol type, List<CppType> arguments, CppNode at, string? inMember, bool exact)
    {
        var constructors = _binder.DeclaredIn(type, ConstructorKey);
        if (constructors.Count == 0)
        {
            return;
        }

        var chosen = Choose(constructors.ToList(), _ => arguments);
        if (chosen.Symbol != null)
        {
            RecordReference(chosen.Symbol.Id, at.Span.Start, inMember, exact ? chosen.Confidence : Confidence.Inferred);
        }
    }

    /// <summary>The overload chosen, how sure the choice is, and the candidates that stayed tied when it could not choose.</summary>
    private readonly record struct Choice(CodeSymbol? Symbol, Confidence Confidence, IReadOnlyList<CodeSymbol> Tied);

    /// <summary>
    /// Overload resolution without conversions sequences: the candidates that accept the number of arguments, then
    /// those whose parameter types match the known argument types best.
    /// </summary>
    private Choice Choose(List<CodeSymbol> candidates, Func<CodeSymbol, List<CppType>> argumentsFor)
    {
        if (candidates.Count == 0)
        {
            return new Choice(null, Confidence.NameOnly, []);
        }

        // A pack expansion among the arguments stands for any number of them
        var packs = argumentsFor(candidates[0]).Any(a => a.IsPack);
        var viable = packs ? candidates : candidates.Where(c => _binder.FindInfo(c)?.Accepts(argumentsFor(c).Count) ?? true).ToList();
        if (viable.Count == 0)
        {
            return new Choice(null, Confidence.NameOnly, candidates);
        }

        if (viable.Count == 1)
        {
            return new Choice(viable[0], packs ? Confidence.Inferred : Confidence.Exact, []);
        }

        var scored = viable.Select(c => (Symbol: c, Score: Score(c, argumentsFor(c)))).Where(s => s.Score > int.MinValue).ToList();
        if (scored.Count == 0)
        {
            return new Choice(null, Confidence.NameOnly, viable);
        }

        var best = scored.Max(s => s.Score);
        var top = scored.Where(s => s.Score == best).Select(s => s.Symbol).ToList();
        if (top.Count == 1)
        {
            var arguments = argumentsFor(top[0]);
            return new Choice(top[0], arguments.All(a => !a.IsUnknown && !a.IsInferred) ? Confidence.Exact : Confidence.Inferred, []);
        }

        // Same parameters, different qualifiers (const and non-const overloads): either is the one called
        var keys = top.Select(t => ParameterPart(_binder.FindInfo(t)?.SignatureKey ?? "")).Distinct().Count();
        if (keys == 1)
        {
            return new Choice(top[0], Confidence.Inferred, []);
        }

        // Between a function and function templates that match as well, the function is chosen
        var functions = top.Where(t => _binder.FindInfo(t) is { FunctionTemplateArity: 0 }).ToList();
        if (functions.Count == 1)
        {
            return new Choice(functions[0], Confidence.Inferred, []);
        }

        return new Choice(null, Confidence.NameOnly, top);
    }

    private static string ParameterPart(string key) => key.LastIndexOf(')') is var close and >= 0 ? key[..(close + 1)] : key;

    /// <summary>How well the arguments match a candidate's parameters; <see cref="int.MinValue"/> when one cannot match.</summary>
    private int Score(CodeSymbol candidate, List<CppType> arguments)
    {
        var info = _binder.FindInfo(candidate);
        if (info == null)
        {
            return 0;
        }

        var score = 0;
        for (var i = 0; i < arguments.Count && i < info.Parameters.Count; i++)
        {
            var argument = Resolve(arguments[i]);
            var parameter = Resolve(info.Parameters[i].Type);

            // T&& takes rvalues, T& lvalues, const T& both
            var reference = info.Parameters[i].Reference;
            if (!argument.IsUnknown && reference == 2)
            {
                score += argument.IsRvalue ? 1 : -5;
            }
            else if (!argument.IsUnknown && reference == 1 && !info.Parameters[i].IsConst && argument.IsRvalue)
            {
                score -= 5;
            }

            if (argument.IsUnknown || parameter.IsUnknown || parameter.TemplateParameter >= 0 || parameter.Name.StartsWith("``", StringComparison.Ordinal))
            {
                continue;
            }

            if (argument.Symbol != null && parameter.Symbol != null)
            {
                if (argument.Symbol == parameter.Symbol || _binder.AllBases(argument.Symbol).Contains(parameter.Symbol))
                {
                    score += argument.Symbol == parameter.Symbol && argument.Pointers == parameter.Pointers ? 4 : argument.Pointers == parameter.Pointers ? 3 : 0;
                    continue;
                }

                // A class can convert to another only through a constructor or conversion function
                score -= 2;
                continue;
            }

            if (argument.Symbol == null && parameter.Symbol == null)
            {
                if (argument.Name == parameter.Name && argument.Pointers == parameter.Pointers)
                {
                    score += 4;
                }
                else if (IsArithmetic(argument) && IsArithmetic(parameter))
                {
                    score += 1;
                }
                else if (argument.Pointers != parameter.Pointers && !(argument.Name == "std::nullptr_t" && parameter.Pointers > 0))
                {
                    score -= 3;
                }

                continue;
            }

            // A class against a fundamental type, or the other way
            var classSide = argument.Symbol != null ? argument : parameter;
            var other = argument.Symbol != null ? parameter : argument;
            if (classSide.Symbol!.Kind == SymbolKind.Enum && IsArithmetic(other))
            {
                score += argument.Symbol != null ? 1 : -3;
                continue;
            }

            score -= 3;
        }

        if (arguments.Count < info.Parameters.Count)
        {
            // Default arguments used
            score -= 1;
        }

        return score;
    }

    private static bool IsArithmetic(CppType type) => type.Pointers == 0 && type.Symbol == null && type.Name is "int" or "unsigned int" or "long" or "unsigned long"
        or "long long" or "unsigned long long" or "short" or "unsigned short" or "char" or "signed char" or "unsigned char" or "bool" or "float" or "double"
        or "long double" or "wchar_t" or "char8_t" or "char16_t" or "char32_t" or "std::size_t" or "size_t";

    // ========================================
    // Directives
    // ========================================

    /// <summary>Macros named in conditional directives and <c>#undef</c>.</summary>
    private void WalkDirectives()
    {
        if (_binder.Macros.Count == 0)
        {
            return;
        }

        foreach (var directive in _source.Unit.Directives)
        {
            if (directive.Kind is not (PreprocessorDirectiveKind.If or PreprocessorDirectiveKind.Ifdef or PreprocessorDirectiveKind.Ifndef
                or PreprocessorDirectiveKind.Elif or PreprocessorDirectiveKind.Elifdef or PreprocessorDirectiveKind.Elifndef or PreprocessorDirectiveKind.Undef))
            {
                continue;
            }

            var first = true;
            foreach (var (word, offset) in Words(directive.Span.Start, directive.Span.End))
            {
                if (first)
                {
                    // The directive's name
                    first = false;
                    continue;
                }

                if (word != "defined" && _binder.Macros.TryGetValue(word, out var macro))
                {
                    RecordReference(macro.Id, offset, null, Confidence.Exact);
                }
            }
        }
    }
}
