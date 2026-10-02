using PaspanParsers.CSharp;

namespace PaspanCodeGraph.CSharp;

// The References pass inside bodies: infers the types of expressions, tracks locals, and binds the members
// that names, member accesses, invocations, object creations and indexers refer to
public sealed partial class CSharpSymbolCollector
{
    /// <summary>The type of an expression, and whether it was found by the language's rules alone.</summary>
    private readonly record struct Bound(SemType Type, bool Exact)
    {
        public static readonly Bound Unknown = new(SemType.Unknown, false);
    }

    /// <summary>A local variable, parameter or local function.</summary>
    private sealed record Local(SemType Type, bool Exact, IReadOnlyList<SemType>? FunctionParameters = null);

    /// <summary>The locals of a block, chained to the enclosing blocks.</summary>
    private sealed class Locals(Locals? parent)
    {
        private readonly Dictionary<string, Local> _variables = new(StringComparer.Ordinal);

        public Locals? Parent { get; } = parent;

        public void Declare(string? name, Local local)
        {
            if (!string.IsNullOrEmpty(name) && name != "_")
            {
                _variables[name] = local;
            }
        }

        public Local? Find(string name)
        {
            for (var scope = this; scope != null; scope = scope.Parent)
            {
                if (scope._variables.TryGetValue(name, out var local))
                {
                    return local;
                }
            }

            return null;
        }
    }

    private Locals _locals = new(null);

    /// <summary>The locals of the type being walked: the primary constructor parameters of a class or struct.</summary>
    private Locals _typeLocals = new(null);

    private SemType _thisType = SemType.Unknown;
    private CodeSymbol? _method;
    private SemType _returnType = SemType.Unknown;

    /// <summary>The types of the expressions returned by the lambda being bound, to infer its return type.</summary>
    private List<SemType>? _lambdaReturns;

    private readonly Dictionary<Expression, Bound> _bound = new(ReferenceEqualityComparer.Instance);

    // ========================================
    // Members
    // ========================================

    /// <summary>Sets up the locals and the types in scope of a member's body before walking it.</summary>
    private void EnterMember(Scope scope, CodeSymbol? member, IReadOnlyList<Parameter>? parameters, IReadOnlyDictionary<string, string> typeParameters, SemType returnType, SemType? value = null)
    {
        _thisType = scope.Type is { } type ? _binder.SelfType(type) : SemType.Unknown;
        _method = member;
        _returnType = returnType;
        _locals = new Locals(_typeLocals);
        var context = scope.Context with { TypeParameters = typeParameters };
        foreach (var parameter in parameters ?? [])
        {
            _locals.Declare(parameter.Name, new Local(_binder.ToSemType(parameter.Type, context, member), true));
        }

        if (value != null)
        {
            _locals.Declare("value", new Local(value, true));
        }
    }

    /// <summary>
    /// Sets up the locals of a type's declaration: the primary constructor parameters are in scope in its base
    /// arguments, and, for a class or struct, in all its members.
    /// </summary>
    private void EnterType(SymbolKind kind, CodeSymbol type, IReadOnlyList<Parameter>? primaryConstructorParameters, Scope scope)
    {
        _typeLocals = new Locals(null);
        if (primaryConstructorParameters != null && kind is SymbolKind.Class or SymbolKind.Struct)
        {
            foreach (var parameter in primaryConstructorParameters)
            {
                _typeLocals.Declare(parameter.Name, new Local(_binder.ToSemType(parameter.Type, scope.Context, null), true));
            }
        }

        EnterMember(scope, null, primaryConstructorParameters, scope.TypeParameters, SemType.Void);
    }

    /// <summary><c>: base(...)</c> or <c>: this(...)</c>: the constructor called, recorded at the keyword.</summary>
    private void BindConstructorInitializer(ConstructorInitializer initializer, BindingContext context, string? inMember)
    {
        var target = initializer.IsBase ? BaseClassOf(_thisType) : _thisType;
        var arguments = BindArguments(initializer.Arguments, context, inMember);
        var keyword = FindName(initializer.IsBase ? "base" : "this", initializer.Span.Start, initializer.Span.End);
        BindConstructorCall(target, arguments, keyword, context, inMember, exact: true);
    }

    /// <summary>The arguments of a primary constructor to its base class: <c>record B(int X) : A(X)</c>.</summary>
    private void BindBaseArguments(TypeDeclaration type, BindingContext context, CodeSymbol symbol)
    {
        if (type.BaseArguments == null)
        {
            return;
        }

        var baseTypes = type switch
        {
            ClassDeclaration c => c.BaseTypes,
            RecordDeclaration r => r.BaseTypes,
            _ => null,
        };
        if (baseTypes is not { Count: > 0 })
        {
            return;
        }

        var inMember = symbol.Members.FirstOrDefault(m => m.Kind == SymbolKind.Constructor && m.Parameters.Count == (symbol.Parameters.Count))?.Id ?? symbol.Id;
        var arguments = BindArguments(type.BaseArguments, context, inMember);
        var baseType = baseTypes[0];
        var position = baseType is NamedTypeReference { Name.Parts.Count: > 0 } named
            ? FindName(named.Name.Parts[^1], named.Qualifier?.Span.End ?? named.Span.Start, named.Span.End)
            : baseType.Span.Start;
        BindConstructorCall(_binder.ToSemType(baseType, context with { Type = symbol.ContainingType }, null), arguments, position, context, inMember, exact: true);
    }

    private void BindConstructorCall(SemType type, List<ArgumentInfo> arguments, int position, BindingContext context, string? inMember, bool exact)
    {
        if (_binder.Constructors(type) is { Count: > 0 } constructors && Resolve(constructors, arguments, [], null, context) is { } chosen)
        {
            CompleteArguments(chosen, arguments, context, inMember);
            RecordResolved(chosen, position, inMember, exact);
            return;
        }

        CompleteArguments(null, arguments, context, inMember);
        if (type.Underlying is ExternalType external)
        {
            RecordExternal("M:" + external.FullName + ".#ctor", position, inMember);
        }
    }

    /// <summary>An attribute calls a constructor of its class, and its <c>Name = value</c> arguments set properties or fields.</summary>
    private void BindAttribute(AttributeNode attribute, CodeSymbol? type, BindingContext context, string? inMember)
    {
        var positional = (attribute.Arguments ?? []).Where(a => !a.IsNameEquals).ToList();
        var arguments = BindArguments(positional, context, inMember);
        var nameStart = attribute.Name.Span.Start;
        var position = attribute.Name.Parts.Count > 0 ? FindName(attribute.Name.Parts[^1], nameStart, attribute.Name.Span.End) : nameStart;
        var selfType = type != null ? _binder.SelfType(type) : SemType.Unknown;
        BindConstructorCall(selfType, arguments, position, context, inMember, exact: true);
        foreach (var argument in attribute.Arguments ?? [])
        {
            if (!argument.IsNameEquals || argument.Name == null)
            {
                continue;
            }

            var memberType = SemType.Unknown;
            var found = _binder.LookupMembers(selfType, argument.Name).FirstOrDefault(m => m.Symbol.Kind is SymbolKind.Property or SymbolKind.Field);
            if (found != null)
            {
                RecordReference(TargetId(found.Symbol), FindName(argument.Name, argument.Span.Start, argument.Span.End), inMember, Confidence.Exact);
                memberType = _binder.MemberType(found.Symbol);
            }

            Bind(argument.Expression, context, inMember, memberType.IsUnknown ? null : memberType);
        }
    }

    private SemType TypeOf(TypeReference? type, BindingContext context) => _binder.ToSemType(type, context, _method);

    private static bool IsVar(TypeReference? type) => type is NamedTypeReference { Name.Parts: ["var"], TypeArguments: null or [], Qualifier: null, Alias: null };

    // ========================================
    // Statements
    // ========================================

    private void WalkStatement(Statement? statement, BindingContext context, string? inMember)
    {
        switch (statement)
        {
            case null:
                return;

            case BlockStatement block:
            {
                var saved = _locals;
                _locals = new Locals(saved);
                DeclareLocalFunctions(block.Statements, context);
                foreach (var inner in block.Statements ?? [])
                {
                    WalkStatement(inner, context, inMember);
                }

                _locals = saved;
                return;
            }

            case LocalDeclarationStatement declaration:
                WalkNode(declaration.Type, context, inMember);
                DeclareVariables(declaration.Type, declaration.Variables, context, inMember);
                return;

            case ExpressionStatement expression:
                Bind(expression.Expression, context, inMember);
                return;

            case IfStatement ifStatement:
                Bind(ifStatement.Condition, context, inMember);
                WalkEmbedded(ifStatement.ThenStatement, context, inMember);
                WalkEmbedded(ifStatement.ElseStatement, context, inMember);
                return;

            case SwitchStatement switchStatement:
            {
                var governing = Bind(switchStatement.Expression, context, inMember);
                foreach (var section in switchStatement.Sections ?? [])
                {
                    var saved = _locals;
                    _locals = new Locals(saved);
                    foreach (var label in section.Labels ?? [])
                    {
                        if (label is CaseSwitchLabel caseLabel)
                        {
                            BindPattern(caseLabel.Pattern, governing.Type, context, inMember);
                            Bind(caseLabel.Guard, context, inMember);
                        }
                    }

                    DeclareLocalFunctions(section.Statements, context);
                    foreach (var inner in section.Statements ?? [])
                    {
                        WalkStatement(inner, context, inMember);
                    }

                    _locals = saved;
                }

                return;
            }

            case WhileStatement whileStatement:
                Bind(whileStatement.Condition, context, inMember);
                WalkEmbedded(whileStatement.Body, context, inMember);
                return;

            case DoStatement doStatement:
                WalkEmbedded(doStatement.Body, context, inMember);
                Bind(doStatement.Condition, context, inMember);
                return;

            case ForStatement forStatement:
            {
                var saved = _locals;
                _locals = new Locals(saved);
                foreach (var initializer in forStatement.Initializers ?? [])
                {
                    WalkStatement(initializer, context, inMember);
                }

                Bind(forStatement.Condition, context, inMember);
                foreach (var iterator in forStatement.Iterators ?? [])
                {
                    Bind(iterator, context, inMember);
                }

                WalkEmbedded(forStatement.Body, context, inMember);
                _locals = saved;
                return;
            }

            case ForEachStatement forEach:
            {
                var collection = Bind(forEach.Collection, context, inMember);
                var element = ElementType(collection.Type, forEach.IsAwait);
                var saved = _locals;
                _locals = new Locals(saved);
                WalkNode(forEach.Type, context, inMember);
                if (forEach.Variable != null)
                {
                    Bind(forEach.Variable, context, inMember, element);
                }
                else if (IsVar(forEach.Type) || forEach.Type == null)
                {
                    _locals.Declare(forEach.Identifier, new Local(element ?? SemType.Unknown, collection.Exact && element != null && !SemTypes.IsPredefined(collection.Type)));
                }
                else
                {
                    _locals.Declare(forEach.Identifier, new Local(TypeOf(forEach.Type, context), true));
                }

                WalkEmbedded(forEach.Body, context, inMember);
                _locals = saved;
                return;
            }

            case ReturnStatement returnStatement:
            {
                var returned = Bind(returnStatement.Expression, context, inMember, _returnType.IsUnknown ? null : _returnType);
                if (returnStatement.Expression != null)
                {
                    _lambdaReturns?.Add(returned.Type);
                }

                return;
            }

            case YieldReturnStatement yieldReturn:
                Bind(yieldReturn.Expression, context, inMember, SemTypes.ElementOf(_returnType));
                return;

            case ThrowStatement throwStatement:
                Bind(throwStatement.Expression, context, inMember);
                return;

            case TryStatement tryStatement:
                WalkStatement(tryStatement.Block, context, inMember);
                foreach (var clause in tryStatement.CatchClauses ?? [])
                {
                    var saved = _locals;
                    _locals = new Locals(saved);
                    WalkNode(clause.ExceptionType, context, inMember);
                    _locals.Declare(clause.Identifier, new Local(clause.ExceptionType != null ? TypeOf(clause.ExceptionType, context) : SemType.Unknown, true));
                    Bind(clause.Filter, context, inMember);
                    WalkStatement(clause.Block, context, inMember);
                    _locals = saved;
                }

                WalkStatement(tryStatement.FinallyBlock, context, inMember);
                return;

            case UsingStatement usingStatement:
            {
                var saved = _locals;
                _locals = new Locals(saved);
                WalkStatement(usingStatement.ResourceAcquisition, context, inMember);
                WalkEmbedded(usingStatement.Body, context, inMember);
                _locals = saved;
                return;
            }

            case FixedStatement fixedStatement:
            {
                var saved = _locals;
                _locals = new Locals(saved);
                WalkNode(fixedStatement.Type, context, inMember);
                DeclareVariables(fixedStatement.Type, fixedStatement.Variables, context, inMember);
                WalkEmbedded(fixedStatement.Body, context, inMember);
                _locals = saved;
                return;
            }

            case LockStatement lockStatement:
                Bind(lockStatement.Expression, context, inMember);
                WalkEmbedded(lockStatement.Body, context, inMember);
                return;

            case LabeledStatement labeled:
                WalkStatement(labeled.Statement, context, inMember);
                return;

            case GotoStatement gotoStatement:
                Bind(gotoStatement.CaseExpression, context, inMember);
                return;

            case CheckedStatement checkedStatement:
                WalkStatement(checkedStatement.Block, context, inMember);
                return;

            case UnsafeStatement unsafeStatement:
                WalkStatement(unsafeStatement.Block, context, inMember);
                return;

            case LocalFunctionStatement local:
                WalkLocalFunction(local, context, inMember);
                return;

            default:
                WalkChildren(statement, context, inMember);
                return;
        }
    }

    /// <summary>An embedded statement has its own scope for the variables it declares.</summary>
    private void WalkEmbedded(Statement? statement, BindingContext context, string? inMember)
    {
        if (statement is null or BlockStatement)
        {
            WalkStatement(statement, context, inMember);
            return;
        }

        var saved = _locals;
        _locals = new Locals(saved);
        WalkStatement(statement, context, inMember);
        _locals = saved;
    }

    private void DeclareVariables(TypeReference? type, IReadOnlyList<VariableDeclarator>? variables, BindingContext context, string? inMember)
    {
        var isVar = IsVar(type);
        var declared = isVar ? SemType.Unknown : TypeOf(type, context);
        foreach (var variable in variables ?? [])
        {
            if (isVar)
            {
                var initializer = Bind(variable.Initializer, context, inMember);
                _locals.Declare(variable.Name, new Local(initializer.Type is NullType ? SemType.Unknown : initializer.Type, initializer.Exact));
            }
            else
            {
                // A variable is in scope in its own initializer
                _locals.Declare(variable.Name, new Local(declared, true));
                Bind(variable.Initializer, context, inMember, declared);
            }

            foreach (var argument in variable.BracketedArguments ?? [])
            {
                Bind(argument.Expression, context, inMember);
            }
        }
    }

    /// <summary>Local functions can be called before their declaration in the block.</summary>
    private void DeclareLocalFunctions(IReadOnlyList<Statement>? statements, BindingContext context)
    {
        foreach (var statement in statements ?? [])
        {
            if (statement is LocalFunctionStatement local)
            {
                var inner = LocalFunctionContext(local, context);
                _locals.Declare(local.Name, new Local(
                    TypeOf(local.ReturnType, inner),
                    true,
                    (local.Parameters ?? []).Select(p => TypeOf(p.Type, inner)).ToList()));
            }
        }
    }

    private static BindingContext LocalFunctionContext(LocalFunctionStatement local, BindingContext context)
    {
        if (local.TypeParameters is not { Count: > 0 })
        {
            return context;
        }

        // Its type parameters hide types of the same name
        var map = new Dictionary<string, string>(context.TypeParameters, StringComparer.Ordinal);
        foreach (var parameter in local.TypeParameters)
        {
            map[parameter.Name] = parameter.Name;
        }

        return context with { TypeParameters = map };
    }

    private void WalkLocalFunction(LocalFunctionStatement local, BindingContext context, string? inMember)
    {
        var inner = LocalFunctionContext(local, context);
        var savedLocals = _locals;
        var savedReturn = _returnType;
        var savedReturns = _lambdaReturns;
        _locals = new Locals(savedLocals);
        _returnType = TypeOf(local.ReturnType, inner);
        _lambdaReturns = null;
        WalkNode(local.ReturnType, inner, inMember);
        foreach (var section in local.Attributes ?? [])
        {
            WalkNode(section, inner, inMember);
        }

        foreach (var parameter in local.Parameters ?? [])
        {
            WalkNode(parameter, inner, inMember);
            _locals.Declare(parameter.Name, new Local(TypeOf(parameter.Type, inner), true));
        }

        foreach (var constraint in local.Constraints ?? [])
        {
            WalkNode(constraint, inner, inMember);
        }

        WalkStatement(local.Body, inner, inMember);
        Bind(local.ExpressionBody, inner, inMember, _returnType.IsUnknown ? null : _returnType);
        _locals = savedLocals;
        _returnType = savedReturn;
        _lambdaReturns = savedReturns;
    }

    /// <summary>The children of a node no case handles: expressions are bound, statements walked, types recorded.</summary>
    private void WalkChildren(CSharpNode node, BindingContext context, string? inMember)
    {
        foreach (var child in Children(node))
        {
            WalkChild(child, context, inMember);
        }
    }

    /// <summary>What <c>foreach</c> gives for a collection: from the base class library model, or the workspace type's GetEnumerator().Current.</summary>
    private SemType? ElementType(SemType collection, bool isAwait)
    {
        if (SemTypes.ElementOf(collection) is { } element)
        {
            return element;
        }

        if (collection.Underlying is NamedType or TypeParameterType || _binder.SymbolOf(collection.Underlying) != null)
        {
            foreach (var ancestor in _binder.Ancestors(collection))
            {
                if (ancestor is ExternalType external && SemTypes.ElementOf(external) is { } inherited)
                {
                    return inherited;
                }
            }

            var getEnumerator = _binder.LookupMembers(collection, isAwait ? "GetAsyncEnumerator" : "GetEnumerator").FirstOrDefault(m => m.Symbol.Kind == SymbolKind.Method);
            if (getEnumerator != null)
            {
                var enumerator = getEnumerator.Substitute(_binder.MemberType(getEnumerator.Symbol));
                var current = _binder.LookupMembers(enumerator, "Current").FirstOrDefault();
                if (current != null)
                {
                    return current.Substitute(_binder.MemberType(current.Symbol));
                }
            }
        }

        return null;
    }

    // ========================================
    // Patterns
    // ========================================

    private void BindPattern(Pattern? pattern, SemType input, BindingContext context, string? inMember)
    {
        switch (pattern)
        {
            case null:
            case DiscardPattern:
                return;

            case DeclarationPattern declaration:
                WalkNode(declaration.Type, context, inMember);
                _locals.Declare(declaration.Identifier, new Local(TypeOf(declaration.Type, context), true));
                return;

            case TypePattern typePattern:
                WalkNode(typePattern.Type, context, inMember);
                return;

            case VarPattern varPattern:
                DeclareDesignation(varPattern.Designation, input);
                return;

            case ConstantPattern constant:
                Bind(constant.Expression, context, inMember, input.IsUnknown ? null : input);
                return;

            case RelationalPattern relational:
                Bind(relational.Expression, context, inMember, input.IsUnknown ? null : input);
                return;

            case LogicalPattern logical:
                BindPattern(logical.Left, input, context, inMember);
                BindPattern(logical.Right, input, context, inMember);
                return;

            case ParenthesizedPattern parenthesized:
                BindPattern(parenthesized.Pattern, input, context, inMember);
                return;

            case RecursivePattern recursive:
            {
                WalkNode(recursive.Type, context, inMember);
                var type = recursive.Type != null ? TypeOf(recursive.Type, context) : input.Underlying;
                foreach (var positional in recursive.PositionalPatterns ?? [])
                {
                    BindPattern(positional.Pattern, SemType.Unknown, context, inMember);
                }

                foreach (var property in recursive.PropertyPatterns ?? [])
                {
                    var memberType = BindPropertyPath(property, type, inMember);
                    BindPattern(property.Pattern, memberType, context, inMember);
                }

                _locals.Declare(recursive.Designation, new Local(type, true));
                return;
            }

            case ListPattern list:
            {
                var element = SemTypes.IndexedBy(input) ?? ElementType(input, false) ?? SemType.Unknown;
                foreach (var inner in list.Patterns ?? [])
                {
                    BindPattern(inner, inner is SlicePattern ? input : element, context, inMember);
                }

                _locals.Declare(list.Designation, new Local(input, true));
                return;
            }

            case SlicePattern slice:
                BindPattern(slice.Pattern, input, context, inMember);
                return;

            default:
                WalkChildren(pattern, context, inMember);
                return;
        }
    }

    private void DeclareDesignation(VariableDesignation? designation, SemType type)
    {
        switch (designation)
        {
            case SingleVariableDesignation single:
                _locals.Declare(single.Identifier, new Local(type, !type.IsUnknown));
                break;
            case ParenthesizedVariableDesignation parenthesized:
                for (var i = 0; i < parenthesized.Variables.Count; i++)
                {
                    DeclareDesignation(parenthesized.Variables[i], TupleElements(type) is { } elements && i < elements.Count ? elements[i] : SemType.Unknown);
                }

                break;
        }
    }

    /// <summary>What deconstructing a value gives: a tuple's elements, a <c>Deconstruct</c> method's out parameters, a positional record's properties.</summary>
    private IReadOnlyList<SemType>? TupleElements(SemType type)
    {
        switch (type.Underlying)
        {
            case TupleType tuple:
                return tuple.Elements;
            case ExternalType { Name: "ValueTuple" or "Tuple" or "KeyValuePair" } tuple:
                return tuple.Arguments;
            case NamedType named:
            {
                var deconstruct = _binder.LookupMembers(named, "Deconstruct").FirstOrDefault(m => m.Symbol.Kind == SymbolKind.Method);
                if (deconstruct != null)
                {
                    return _binder.Parameters(deconstruct.Symbol).Where(p => p.IsOut).Select(p => deconstruct.Substitute(p.Type)).ToList();
                }

                if (named.Symbol.Kind is SymbolKind.Record or SymbolKind.RecordStruct && named.Symbol.Parameters.Count > 0)
                {
                    return named.Symbol.Parameters
                        .Select(p => _binder.LookupMembers(named, p.Name).FirstOrDefault() is { } property ? property.Substitute(_binder.MemberType(property.Symbol)) : SemType.Unknown)
                        .ToList();
                }

                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>The members of a property pattern (<c>{ Name: ... }</c>, or <c>{ A.B: ... }</c>); returns the last one's type.</summary>
    private SemType BindPropertyPath(PropertySubPattern property, SemType type, string? inMember)
    {
        var position = property.Span.Start;
        foreach (var part in property.PropertyName.Split('.'))
        {
            var name = part.Trim();
            var found = _binder.LookupMembers(type, name).FirstOrDefault(m => m.Symbol.Kind is SymbolKind.Property or SymbolKind.Field);
            var offset = FindName(name, position, property.Span.End);
            position = offset + System.Text.Encoding.UTF8.GetByteCount(name);
            if (found == null)
            {
                return SemTypes.KnownProperty(type, name) ?? SemType.Unknown;
            }

            RecordReference(TargetId(found.Symbol), offset, inMember, Confidence.Exact);
            type = found.Substitute(_binder.MemberType(found.Symbol));
        }

        return type;
    }

    // ========================================
    // Expressions
    // ========================================

    /// <summary>
    /// Binds an expression once: records the types and members it refers to, and returns its type.
    /// <paramref name="expected"/> is the type it converts to, for lambdas, <c>new()</c> and collection expressions.
    /// </summary>
    private Bound Bind(Expression? expression, BindingContext context, string? inMember, SemType? expected = null)
    {
        if (expression == null)
        {
            return Bound.Unknown;
        }

        if (_bound.TryGetValue(expression, out var known))
        {
            return known;
        }

        // Placeholder: an expression is bound once even when a case walks its children again
        _bound[expression] = Bound.Unknown;
        var result = BindCore(expression, context, inMember, expected);
        _bound[expression] = result;
        return result;
    }

    private Bound BindCore(Expression expression, BindingContext context, string? inMember, SemType? expected)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                return new Bound(LiteralType(literal), true);

            case InterpolatedStringExpression:
                WalkChildren(expression, context, inMember);
                return new Bound(SemTypes.String, true);

            case NameExpression { Parts.Count: 1, Alias: null } name:
                WalkChild(name.TypeArguments, context, inMember);
                return BindSimpleName(name, context, inMember, out _);

            case NameExpression or AliasQualifiedNameExpression:
                WalkChildren(expression, context, inMember);
                return AsTypeOrNamespace(BindExpression(expression, context, inMember), expression);

            case ThisExpression:
                return new Bound(_thisType, true);

            case BaseExpression:
                return new Bound(BaseClassOf(_thisType), true);

            case PredefinedTypeExpression predefined:
                return new Bound(new TypeExpressionType(CSharpBinder.Predefined(predefined.Type)), true);

            case BinaryExpression binary:
                return BindBinary(binary, context, inMember);

            case UnaryExpression unary:
            {
                var operand = Bind(unary.Operand, context, inMember);
                return unary.Operator switch
                {
                    UnaryOperator.Not => new Bound(SemTypes.Boolean, true),
                    UnaryOperator.NullForgiving or UnaryOperator.Increment or UnaryOperator.Decrement or UnaryOperator.Plus or UnaryOperator.Minus or UnaryOperator.BitwiseNot => operand,
                    UnaryOperator.Index => new Bound(new ExternalType("Index", "System.Index", []), true),
                    _ => Bound.Unknown,
                };
            }

            case ConditionalExpression conditional:
            {
                Bind(conditional.Condition, context, inMember);
                var whenTrue = Bind(conditional.TrueExpression, context, inMember, expected);
                var whenFalse = Bind(conditional.FalseExpression, context, inMember, expected);
                return whenTrue.Type is NullType or UnknownType ? whenFalse : whenTrue;
            }

            case InvocationExpression invocation:
                return BindInvocation(invocation, context, inMember);

            case MemberAccessExpression access:
                return BindMemberAccess(access, context, inMember, invoked: false, out _);

            case ElementAccessExpression element:
                return BindElementAccess(element, context, inMember);

            case ObjectCreationExpression creation:
                return BindCreation(creation, context, inMember, expected);

            case InitializerExpression initializer:
                BindInitializer(initializer, expected ?? SemType.Unknown, context, inMember);
                return new Bound(expected ?? SemType.Unknown, false);

            case ArrayCreationExpression array:
            {
                WalkNode(array.ElementType, context, inMember);
                foreach (var size in array.Sizes ?? [])
                {
                    Bind(size, context, inMember);
                }

                var element = TypeOf(array.ElementType, context);
                var rank = array.Sizes is { Count: > 0 } sizes ? sizes.Count : 1;
                var arrayType = new ArrayType(element, rank);
                foreach (var extra in array.AdditionalRanks ?? [])
                {
                    arrayType = new ArrayType(arrayType, extra);
                }

                BindInitializer(array.Initializer, arrayType, context, inMember);
                return new Bound(arrayType, true);
            }

            case ImplicitArrayCreationExpression implicitArray:
            {
                var elementType = SemType.Unknown;
                foreach (var element in implicitArray.Initializer?.Expressions ?? [])
                {
                    var bound = Bind(element, context, inMember);
                    if (elementType.IsUnknown && bound.Type is not (NullType or UnknownType))
                    {
                        elementType = bound.Type;
                    }
                }

                return new Bound(new ArrayType(elementType, implicitArray.Rank), false);
            }

            case CollectionExpression collection:
            {
                var element = expected != null ? SemTypes.ElementOf(expected) : null;
                foreach (var item in collection.Elements ?? [])
                {
                    Bind(item, context, inMember, item is SpreadElement ? expected : element);
                }

                return new Bound(expected ?? SemType.Unknown, false);
            }

            case SpreadElement spread:
                return Bind(spread.Expression, context, inMember, expected);

            case CastExpression cast:
            {
                WalkNode(cast.Type, context, inMember);
                var type = TypeOf(cast.Type, context);
                Bind(cast.Expression, context, inMember, type);
                return new Bound(type, true);
            }

            case AsExpression asExpression:
                Bind(asExpression.Expression, context, inMember);
                WalkNode(asExpression.Type, context, inMember);
                return new Bound(TypeOf(asExpression.Type, context), true);

            case IsExpression isExpression:
            {
                var tested = Bind(isExpression.Expression, context, inMember);
                BindPattern(isExpression.Pattern, tested.Type, context, inMember);
                return new Bound(SemTypes.Boolean, true);
            }

            case LambdaExpression or AnonymousMethodExpression:
                return BindLambda(expression, context, inMember, expected != null ? _binder.DelegateSignature(expected) : null, out _);

            case SwitchExpression switchExpression:
            {
                var governing = Bind(switchExpression.GoverningExpression, context, inMember);
                var result = Bound.Unknown;
                foreach (var arm in switchExpression.Arms ?? [])
                {
                    var saved = _locals;
                    _locals = new Locals(saved);
                    BindPattern(arm.Pattern, governing.Type, context, inMember);
                    Bind(arm.Guard, context, inMember);
                    var value = Bind(arm.Expression, context, inMember, expected);
                    if (result.Type is UnknownType or NullType && value.Type is not UnknownType)
                    {
                        result = value;
                    }

                    _locals = saved;
                }

                return result;
            }

            case ThrowExpression throwExpression:
                Bind(throwExpression.Expression, context, inMember);
                return Bound.Unknown;

            case DefaultExpression defaultExpression:
                WalkNode(defaultExpression.Type, context, inMember);
                return defaultExpression.Type != null ? new Bound(TypeOf(defaultExpression.Type, context), true) : new Bound(expected ?? SemType.Unknown, expected != null);

            case TypeOfExpression typeOf:
                WalkNode(typeOf.Type, context, inMember);
                return new Bound(SemTypes.SystemType, true);

            case SizeOfExpression sizeOf:
                WalkNode(sizeOf.Type, context, inMember);
                return new Bound(SemTypes.Int32, true);

            case NameOfExpression nameOf:
                BindNameOf(nameOf.Expression, context, inMember);
                return new Bound(SemTypes.String, true);

            case AwaitExpression awaitExpression:
            {
                var awaited = Bind(awaitExpression.Expression, context, inMember);
                return SemTypes.Awaited(awaited.Type) is { } result ? new Bound(result, awaited.Exact) : Bound.Unknown;
            }

            case ParenthesizedExpression parenthesized:
                return Bind(parenthesized.Expression, context, inMember, expected);

            case TupleExpression tuple:
            {
                var expectedElements = expected?.Underlying as TupleType;
                var elements = new List<SemType>();
                var exact = true;
                for (var i = 0; i < tuple.Elements.Count; i++)
                {
                    var bound = Bind(tuple.Elements[i].Expression, context, inMember, expectedElements != null && i < expectedElements.Elements.Count ? expectedElements.Elements[i] : null);
                    elements.Add(bound.Type);
                    exact &= bound.Exact;
                }

                return new Bound(new TupleType(elements, tuple.Elements.Select(e => e.Name).ToList()), exact);
            }

            case RangeExpression range:
                Bind(range.Start, context, inMember);
                Bind(range.End, context, inMember);
                return new Bound(new ExternalType("Range", "System.Range", []), true);

            case WithExpression with:
            {
                var target = Bind(with.Expression, context, inMember);
                BindInitializer(with.Initializer, target.Type, context, inMember);
                return target;
            }

            case CheckedExpression checkedExpression:
                return Bind(checkedExpression.Expression, context, inMember, expected);

            case RefExpression refExpression:
                return Bind(refExpression.Expression, context, inMember, expected);

            case DeclarationExpression declaration:
            {
                WalkNode(declaration.Type, context, inMember);
                var type = IsVar(declaration.Type) ? expected ?? SemType.Unknown : TypeOf(declaration.Type, context);
                DeclareDesignation(declaration.Designation, type);
                return new Bound(type, !IsVar(declaration.Type));
            }

            case QueryExpression query:
                return BindQuery(query, context, inMember);

            default:
                WalkChildren(expression, context, inMember);
                return Bound.Unknown;
        }
    }

    private static SemType LiteralType(LiteralExpression literal)
    {
        var text = literal.Text ?? "";
        return literal.Kind switch
        {
            LiteralKind.Null => SemType.Null,
            LiteralKind.Boolean => SemTypes.Boolean,
            LiteralKind.Character => SemTypes.Char,
            LiteralKind.String => SemTypes.String,
            LiteralKind.Utf8String => new ExternalType("ReadOnlySpan", "System.ReadOnlySpan", [SemTypes.Predefined("Byte")]),
            LiteralKind.Real when text.EndsWith('f') || text.EndsWith('F') => SemTypes.Predefined("Single"),
            LiteralKind.Real when text.EndsWith('m') || text.EndsWith('M') => SemTypes.Predefined("Decimal"),
            LiteralKind.Real => SemTypes.Double,
            LiteralKind.Integer when text.EndsWith("ul", StringComparison.OrdinalIgnoreCase) || text.EndsWith("lu", StringComparison.OrdinalIgnoreCase) => SemTypes.Predefined("UInt64"),
            LiteralKind.Integer when text.EndsWith('l') || text.EndsWith('L') => SemTypes.Int64,
            LiteralKind.Integer when text.EndsWith('u') || text.EndsWith('U') => SemTypes.Predefined("UInt32"),
            LiteralKind.Integer => SemTypes.Int32,
            _ => SemType.Unknown,
        };
    }

    private SemType BaseClassOf(SemType type)
    {
        if (type is NamedType named)
        {
            var bases = _binder.BaseTypes(named.Symbol);
            if (bases.Count > 0 && named.Symbol.Kind is SymbolKind.Class or SymbolKind.Record)
            {
                Func<TypeParameterType, SemType?> map = p => !p.IsMethod && p.Ordinal >= 0 && p.Ordinal < named.Arguments.Count ? named.Arguments[p.Ordinal] : null;
                var first = SemTypes.Substitute(bases[0], map);
                if (first is not NamedType { Symbol.Kind: SymbolKind.Interface })
                {
                    return first;
                }
            }
        }

        return SemTypes.Object;
    }

    /// <summary>A name that bound to a type or namespace (recorded by <see cref="BindExpression"/>); else a type from a reference.</summary>
    private Bound AsTypeOrNamespace(BoundName? bound, Expression expression)
    {
        if (bound?.Type is { } type)
        {
            return new Bound(new TypeExpressionType(_binder.TypeOf(type.Symbol, [])), true);
        }

        if (bound?.Namespace is { } ns)
        {
            return new Bound(new NamespaceExpressionType(ns), true);
        }

        // Locals and members are tracked, so an unknown name is a type or namespace from a reference
        var written = expression switch
        {
            NameExpression name => string.Join(".", name.Parts),
            AliasQualifiedNameExpression alias => string.Join(".", alias.Name.Parts),
            _ => "?",
        };
        return new Bound(new TypeExpressionType(new ExternalType(written.Split('.')[^1], written, [])), false);
    }

    /// <summary>
    /// A simple name in an expression: a local or parameter, a member of the containing types (or of a
    /// <c>using static</c> type), else a type or namespace. For a method name, <paramref name="methods"/> gets the
    /// candidates and nothing is recorded (the invocation does it).
    /// </summary>
    private Bound BindSimpleName(NameExpression name, BindingContext context, string? inMember, out List<FoundMember>? methods, bool invoked = false)
    {
        methods = null;
        var text = name.Parts[0];
        var arity = name.TypeArguments?.Count ?? 0;
        if (arity == 0 && _locals.Find(text) is { } local)
        {
            return new Bound(local.Type, local.Exact);
        }

        if (text == "_" && arity == 0)
        {
            // A discard
            return Bound.Unknown;
        }

        if (arity == 0 && context.TypeParameters.ContainsKey(text))
        {
            return new Bound(new TypeExpressionType(TypeOf(new NamedTypeReference(name), context)), true);
        }

        for (var type = context.Type; type != null; type = type.ContainingType)
        {
            if (_binder.FindNestedType(type, text, arity) != null)
            {
                break;
            }

            var receiver = type == context.Type && _thisType is NamedType ? _thisType : _binder.SelfType(type);
            var found = _binder.LookupMembers(receiver, text, arity);
            if (found.Count > 0)
            {
                return BindFoundMembers(found, text, name.Span.Start, name.Span.End, inMember, true, invoked, out methods);
            }
        }

        foreach (var staticType in _binder.StaticImports(context))
        {
            var found = _binder.LookupMembers(_binder.SelfType(staticType), text, arity);
            if (found.Count > 0)
            {
                return BindFoundMembers(found, text, name.Span.Start, name.Span.End, inMember, true, invoked, out methods);
            }
        }

        if (invoked)
        {
            return Bound.Unknown;
        }

        return AsTypeOrNamespace(BindExpression(name, context, inMember), name);
    }

    /// <summary>Records a property, field or event found by member lookup and returns its type; methods are left to the caller.</summary>
    private Bound BindFoundMembers(List<FoundMember> found, string name, int start, int end, string? inMember, bool receiverExact, bool invoked, out List<FoundMember>? methods)
    {
        methods = null;

        // Invoked, only the invocable members count: methods, and properties, fields and events of a delegate type (or
        // of a type not known). A property that is not a delegate, List<T>.Count in list.Count(x => ...), leaves the
        // methods, and with none of them the extension methods.
        var value = found.FirstOrDefault(f => f.Symbol.Kind is not SymbolKind.Method && (!invoked || Invocable(f)));
        if (value != null)
        {
            RecordReference(TargetId(value.Symbol), FindName(name, start, end), inMember, receiverExact ? Confidence.Exact : Confidence.Inferred);
            return new Bound(value.Substitute(_binder.MemberType(value.Symbol)), receiverExact);
        }

        methods = invoked ? found.Where(f => f.Symbol.Kind == SymbolKind.Method).ToList() : found;
        if (!invoked)
        {
            // A method group converted to a delegate
            var confidence = found.Count == 1 && receiverExact ? Confidence.Exact : Confidence.Inferred;
            RecordReference(found[0].Symbol.Id, FindName(name, start, end), inMember, confidence);
        }

        return Bound.Unknown;
    }

    /// <summary>Whether a property, field or event can be invoked: its type is a delegate, or not known.</summary>
    private bool Invocable(FoundMember member)
    {
        var type = member.Substitute(_binder.MemberType(member.Symbol));
        return type.IsUnknown || _binder.DelegateSignature(type) != null;
    }

    private Bound BindMemberAccess(MemberAccessExpression access, BindingContext context, string? inMember, bool invoked, out (Bound Receiver, List<FoundMember>? Methods, bool IsStatic) callee)
    {
        callee = (Bound.Unknown, null, false);
        WalkChild(access.TypeArguments, context, inMember);
        if (access.Target == null)
        {
            return Bound.Unknown;
        }

        var name = access.MemberName;
        var arity = access.TypeArguments?.Count ?? 0;

        // Color Color: a property of type Color named Color, and a static member of the type accessed
        Bound target;
        if (access.Target is NameExpression { Parts.Count: 1, Alias: null, TypeArguments: null or [] } colorName && _locals.Find(colorName.Parts[0]) == null
            && _binder.ColorColor(colorName.Parts[0], name, context) is { Type: { } colorType } colorBound)
        {
            Record(colorBound, colorName.Parts[0], colorName.Span.Start, colorName.Span.End, inMember, Confidence.Inferred);
            _bound[access.Target] = target = new Bound(new TypeExpressionType(_binder.TypeOf(colorType.Symbol, [])), true);
        }
        else
        {
            target = Bind(access.Target, context, inMember);
        }

        var nameStart = access.Target.Span.End;
        var nameEnd = access.Span.End;
        switch (target.Type)
        {
            case NamespaceExpressionType:
            case TypeExpressionType { Type: var typeExpression } when _binder.SymbolOf(typeExpression) != null:
            {
                if (target.Type is TypeExpressionType { Type: var staticType } && _binder.SymbolOf(staticType) is { } staticSymbol)
                {
                    if (_binder.FindNestedType(staticSymbol.Symbol, name, arity) == null)
                    {
                        var found = _binder.LookupMembers(staticType, name, arity);
                        if (found.Count > 0)
                        {
                            callee = (target, null, true);
                            var bound = BindFoundMembers(found, name, nameStart, nameEnd, inMember, true, invoked, out var methods);
                            callee = (target, methods, true);
                            return bound;
                        }

                        if (invoked)
                        {
                            callee = (target, [], true);
                            return Bound.Unknown;
                        }
                    }
                }

                // A nested type or a namespace member, recorded as a type reference
                var nested = BindExpression(access, context, inMember);
                if (nested != null)
                {
                    return AsTypeOrNamespace(nested, access);
                }

                return new Bound(new TypeExpressionType(new ExternalType(name, Describe(target.Type) + "." + name, [])), false);
            }

            case TypeExpressionType { Type: var externalType }:
                // A static member or nested type of a type from a reference
                callee = (target, [], true);
                if (invoked)
                {
                    return Bound.Unknown;
                }

                RecordExternal("P:" + Describe(externalType) + "." + name, FindName(name, nameStart, nameEnd), inMember);
                return new Bound(new TypeExpressionType(new ExternalType(name, Describe(externalType) + "." + name, [])), false);

            case UnknownType:
                callee = (target, null, false);
                if (!invoked)
                {
                    RecordNameOnly(name, null, FindName(name, nameStart, nameEnd), inMember, invoked: false);
                }

                return Bound.Unknown;

            default:
            {
                var receiver = target.Type;
                var found = _binder.LookupMembers(receiver, name, arity);
                if (found.Count > 0)
                {
                    var bound = BindFoundMembers(found, name, nameStart, nameEnd, inMember, target.Exact, invoked, out var methods);
                    callee = (target, methods, false);
                    return bound;
                }

                callee = (target, [], false);
                if (invoked)
                {
                    return Bound.Unknown;
                }

                var offset = FindName(name, nameStart, nameEnd);
                if (SemTypes.KnownProperty(receiver, name) is { } property)
                {
                    // A tuple element is a field of the tuple, not a member worth a reference
                    if (receiver.Underlying is not TupleType)
                    {
                        RecordExternal("P:" + Describe(receiver) + "." + name, offset, inMember);
                    }

                    return new Bound(property, target.Exact);
                }

                if (receiver.Underlying is not TupleType)
                {
                    RecordExternal("P:" + Describe(receiver) + "." + name, offset, inMember);
                }

                return Bound.Unknown;
            }
        }
    }

    /// <summary>The name of a type as it goes into the id of a member from a reference: <c>System.String</c>, <c>List</c>.</summary>
    private static string Describe(SemType type) => type.Underlying switch
    {
        ExternalType external => external.FullName,
        NamedType named => named.Symbol.Id[2..],
        ArrayType => "System.Array",
        TupleType => "System.ValueTuple",
        TypeExpressionType expression => Describe(expression.Type),
        NamespaceExpressionType ns => ns.Namespace,
        _ => "?",
    };

    private Bound BindElementAccess(ElementAccessExpression element, BindingContext context, string? inMember)
    {
        var target = Bind(element.Target, context, inMember);
        var arguments = BindArguments(element.Arguments, context, inMember);
        var bracket = FindPunctuation((byte)'[', element.Target?.Span.End ?? element.Span.Start, element.Span.End);
        var indexers = target.Type.Underlying is TypeParameterType || _binder.SymbolOf(target.Type.Underlying) != null ? _binder.LookupIndexers(target.Type) : [];
        if (indexers.Count > 0)
        {
            var resolution = Resolve(indexers, arguments, [], null, context);
            if (resolution is { } chosen)
            {
                CompleteArguments(chosen, arguments, context, inMember);
                RecordResolved(chosen, bracket, inMember, target.Exact);
                return new Bound(chosen.Substitute(_binder.MemberType(chosen.Member.Symbol)), target.Exact && chosen.Unique);
            }

            // A range or a from-end index on a type without such an indexer: Slice (Substring for a string), or the int indexer
            if (arguments is [{ Bound.Type: ExternalType { Name: "Range" or "Index", Arguments.Count: 0 } implicitArgument }])
            {
                var isRange = implicitArgument.Name == "Range";
                var candidates = isRange
                    ? _binder.LookupMembers(target.Type, target.Type.Underlying is ExternalType { Name: "String" } ? "Substring" : "Slice")
                    : indexers;
                var implicitMember = candidates.FirstOrDefault(c => _binder.Parameters(c.Symbol) is var p
                    && (isRange ? p.Count == 2 : p.Count == 1) && p.All(x => Equals(x.Type, SemTypes.Int32)));
                if (implicitMember != null)
                {
                    CompleteArguments(null, arguments, context, inMember);
                    RecordReference(TargetId(implicitMember.Symbol), bracket, inMember, target.Exact ? Confidence.Exact : Confidence.Inferred);
                    return new Bound(implicitMember.Substitute(_binder.MemberType(implicitMember.Symbol)), target.Exact);
                }
            }
        }

        CompleteArguments(null, arguments, context, inMember);
        return SemTypes.IndexedBy(target.Type) is { } item ? new Bound(item, target.Exact) : Bound.Unknown;
    }

    private int FindPunctuation(byte punctuation, int start, int end)
    {
        var source = Utf8;
        end = Math.Min(end, source.Length);
        for (var i = start; i < end; i++)
        {
            if (source[i] == punctuation)
            {
                return i;
            }
        }

        return start;
    }

    private Bound BindBinary(BinaryExpression binary, BindingContext context, string? inMember)
    {
        switch (binary.Operator)
        {
            case BinaryOperator.Assign when binary.Left is DeclarationExpression or TupleExpression:
            {
                // A deconstruction: the variables get the types of the elements
                var right = Bind(binary.Right, context, inMember);
                Bind(binary.Left, context, inMember, right.Type.IsUnknown ? null : right.Type);
                return right;
            }

            case BinaryOperator.Assign:
            case BinaryOperator.NullCoalescingAssign:
            {
                var left = Bind(binary.Left, context, inMember);
                Bind(binary.Right, context, inMember, left.Type.IsUnknown ? null : left.Type);
                return left;
            }

            case BinaryOperator.AddAssign or BinaryOperator.SubtractAssign:
            {
                // An event subscription converts a lambda or method group to the event's delegate type
                var left = Bind(binary.Left, context, inMember);
                Bind(binary.Right, context, inMember, left.Type.IsUnknown ? null : left.Type);
                return left;
            }

            case BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan
                or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual:
                Bind(binary.Left, context, inMember);
                Bind(binary.Right, context, inMember);
                return new Bound(SemTypes.Boolean, true);

            case BinaryOperator.NullCoalescing:
            {
                var left = Bind(binary.Left, context, inMember);
                var right = Bind(binary.Right, context, inMember, left.Type.IsUnknown ? null : left.Type.Underlying);
                return left.Type is UnknownType or NullType ? right : new Bound(right.Type is NullableType ? left.Type : left.Type.Underlying, left.Exact);
            }

            default:
            {
                var left = Bind(binary.Left, context, inMember);
                var right = Bind(binary.Right, context, inMember);
                if (binary.Operator == BinaryOperator.Add && (left.Type is ExternalType { Name: "String" } || right.Type is ExternalType { Name: "String" }))
                {
                    return new Bound(SemTypes.String, true);
                }

                if (SemTypes.IsPredefined(left.Type) && SemTypes.IsPredefined(right.Type))
                {
                    // The wider of two numeric types
                    return _binder.Conversion(left.Type, right.Type) > 0 ? right : left;
                }

                return left.Type is NamedType or ExternalType ? new Bound(left.Type, false) : Bound.Unknown;
            }
        }
    }

    private void BindNameOf(Expression? expression, BindingContext context, string? inMember)
    {
        switch (expression)
        {
            case NameExpression { Parts.Count: 1, Alias: null } name:
                BindSimpleName(name, context, inMember, out _);
                break;
            case MemberAccessExpression access:
                BindMemberAccess(access, context, inMember, invoked: false, out _);
                break;
            default:
                Bind(expression, context, inMember);
                break;
        }
    }

    // ========================================
    // Object creation and initializers
    // ========================================

    private Bound BindCreation(ObjectCreationExpression creation, BindingContext context, string? inMember, SemType? expected)
    {
        WalkNode(creation.Type, context, inMember);
        var type = creation.Type != null ? TypeOf(creation.Type, context) : expected?.Underlying ?? SemType.Unknown;
        var exact = creation.Type != null || expected != null;
        var arguments = BindArguments(creation.Arguments, context, inMember);

        // Where the constructor is named: the type's last name, or 'new' in a target-typed creation
        var position = creation.Type switch
        {
            NamedTypeReference named when named.Name.Parts.Count > 0 => FindName(named.Name.Parts[^1], named.Qualifier?.Span.End ?? named.Span.Start, named.Span.End),
            null => creation.Span.Start,
            _ => creation.Type.Span.Start,
        };
        if (_binder.SymbolOf(type.Underlying) is { Symbol.Kind: not SymbolKind.Delegate })
        {
            var constructors = _binder.Constructors(type);
            if (constructors.Count > 0 && Resolve(constructors, arguments, [], null, context) is { } chosen)
            {
                CompleteArguments(chosen, arguments, context, inMember);
                RecordResolved(chosen, position, inMember, exact);
            }
            else
            {
                CompleteArguments(null, arguments, context, inMember);
            }
        }
        else if (_binder.SymbolOf(type.Underlying) is { Symbol.Kind: SymbolKind.Delegate })
        {
            CompleteArguments(null, arguments, context, inMember, [type]);
        }
        else
        {
            CompleteArguments(null, arguments, context, inMember);
            if (type is ExternalType external)
            {
                RecordExternal("M:" + external.FullName + ".#ctor", position, inMember);
            }
        }

        BindInitializer(creation.Initializer, type, context, inMember);
        return new Bound(type, exact);
    }

    /// <summary>An object initializer sets members of <paramref name="type"/>; a collection initializer adds elements.</summary>
    private void BindInitializer(InitializerExpression? initializer, SemType type, BindingContext context, string? inMember)
    {
        if (initializer == null)
        {
            return;
        }

        _bound[initializer] = new Bound(type, false);
        var element = SemTypes.ElementOf(type) ?? (type is ArrayType array ? array.Element : null);
        foreach (var item in initializer.Expressions ?? [])
        {
            if (initializer.Kind == InitializerKind.Object && item is BinaryExpression { Operator: BinaryOperator.Assign, Left: var left } assignment)
            {
                _bound[assignment] = Bound.Unknown;
                var memberType = SemType.Unknown;
                switch (left)
                {
                    case NameExpression { Parts.Count: 1, Alias: null } memberName:
                    {
                        _bound[left] = Bound.Unknown;
                        var found = _binder.LookupMembers(type, memberName.Parts[0]).FirstOrDefault(m => m.Symbol.Kind is not SymbolKind.Method);
                        if (found != null)
                        {
                            RecordReference(TargetId(found.Symbol), FindName(memberName.Parts[0], left.Span.Start, left.Span.End), inMember, Confidence.Exact);
                            memberType = found.Substitute(_binder.MemberType(found.Symbol));
                        }

                        break;
                    }

                    case ImplicitElementAccessExpression indexer:
                    {
                        _bound[left] = Bound.Unknown;
                        var arguments = BindArguments(indexer.Arguments, context, inMember);
                        var indexers = _binder.LookupIndexers(type);
                        if (indexers.Count > 0 && Resolve(indexers, arguments, [], null, context) is { } chosen)
                        {
                            CompleteArguments(chosen, arguments, context, inMember);
                            RecordResolved(chosen, left.Span.Start, inMember, true);
                            memberType = chosen.Substitute(_binder.MemberType(chosen.Member.Symbol));
                        }
                        else
                        {
                            CompleteArguments(null, arguments, context, inMember);
                            memberType = SemTypes.IndexedBy(type) ?? SemType.Unknown;
                        }

                        break;
                    }

                    default:
                        Bind(left, context, inMember);
                        break;
                }

                if (assignment.Right is InitializerExpression nested)
                {
                    BindInitializer(nested, memberType, context, inMember);
                }
                else
                {
                    Bind(assignment.Right, context, inMember, memberType.IsUnknown ? null : memberType);
                }
            }
            else if (item is InitializerExpression complex)
            {
                // { key, value } of a dictionary, or a nested array
                BindInitializer(complex, type is ArrayType nestedArray ? nestedArray.Element : SemType.Unknown, context, inMember);
            }
            else
            {
                Bind(item, context, inMember, element);
            }
        }
    }

    // ========================================
    // Invocations
    // ========================================

    /// <summary>An argument bound before overload resolution; lambdas and <c>out var</c> wait for the parameter's type.</summary>
    private sealed record ArgumentInfo(Argument Argument, Bound Bound, bool Deferred);

    private List<ArgumentInfo> BindArguments(IReadOnlyList<Argument>? arguments, BindingContext context, string? inMember)
    {
        var result = new List<ArgumentInfo>();
        foreach (var argument in arguments ?? [])
        {
            switch (argument.Expression)
            {
                case LambdaExpression lambda:
                    result.Add(new ArgumentInfo(argument, new Bound(new LambdaType(lambda.Parameters?.Count ?? 0), true), true));
                    break;
                case AnonymousMethodExpression anonymous:
                    result.Add(new ArgumentInfo(argument, new Bound(new LambdaType(anonymous.Parameters?.Count ?? -1), true), true));
                    break;
                case DeclarationExpression declaration when IsVar(declaration.Type):
                    result.Add(new ArgumentInfo(argument, Bound.Unknown, true));
                    break;
                default:
                    result.Add(new ArgumentInfo(argument, Bind(argument.Expression, context, inMember), false));
                    break;
            }
        }

        return result;
    }

    /// <summary>Binds the lambdas and <c>out var</c> declarations of the arguments with the chosen member's parameter types.</summary>
    private void CompleteArguments(Resolution? chosen, List<ArgumentInfo> arguments, BindingContext context, string? inMember, IReadOnlyList<SemType>? delegateTypes = null)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (!argument.Deferred)
            {
                continue;
            }

            var parameterType = chosen?.ParameterType(i) ?? (delegateTypes != null && i < delegateTypes.Count ? delegateTypes[i] : null);
            switch (argument.Argument.Expression)
            {
                case LambdaExpression or AnonymousMethodExpression:
                {
                    var signature = parameterType != null ? _binder.DelegateSignature(parameterType) : null;
                    BindLambda(argument.Argument.Expression, context, inMember, signature, out var returned);
                    if (chosen != null && signature is { } known && returned != null)
                    {
                        // A lambda's result infers the method's type parameters in its delegate's return type
                        chosen.Infer(known.Return, returned);
                    }

                    break;
                }

                case var expression:
                    Bind(expression, context, inMember, parameterType);
                    break;
            }
        }
    }

    /// <summary>Binds a lambda's body with its parameters typed from the delegate it converts to; returns the lambda type and the body's result type.</summary>
    private Bound BindLambda(Expression lambda, BindingContext context, string? inMember, (IReadOnlyList<SemType> Parameters, SemType Return)? signature, out SemType? returned)
    {
        returned = null;
        if (_bound.TryGetValue(lambda, out var known) && known.Type is not UnknownType)
        {
            return known;
        }

        _bound[lambda] = new Bound(new LambdaType(0), true);
        var savedLocals = _locals;
        var savedReturn = _returnType;
        var savedReturns = _lambdaReturns;
        _locals = new Locals(savedLocals);
        _returnType = signature?.Return ?? SemType.Unknown;
        if (lambda is LambdaExpression { IsAsync: true } && SemTypes.Awaited(_returnType) is { } awaitedReturn)
        {
            _returnType = awaitedReturn;
        }

        _lambdaReturns = [];
        var (parameters, attributes, body) = lambda switch
        {
            LambdaExpression l => (l.Parameters, l.Attributes, (CSharpNode?)l.Body),
            AnonymousMethodExpression a => (a.Parameters, null, a.Block),
            _ => (null, null, null),
        };
        foreach (var section in attributes ?? [])
        {
            WalkNode(section, context, inMember);
        }

        if (lambda is LambdaExpression { ReturnType: { } explicitReturn })
        {
            WalkNode(explicitReturn, context, inMember);
        }

        for (var i = 0; i < (parameters?.Count ?? 0); i++)
        {
            var parameter = parameters![i];
            WalkNode(parameter, context, inMember);
            var type = parameter.Type is null or OmittedTypeReference
                ? signature is { } s && i < s.Parameters.Count ? s.Parameters[i] : SemType.Unknown
                : TypeOf(parameter.Type, context);
            _locals.Declare(parameter.Name, new Local(type, parameter.Type is not (null or OmittedTypeReference) || signature != null));
        }

        switch (body)
        {
            case ExpressionLambdaBody expressionBody:
            {
                var result = Bind(expressionBody.Expression, context, inMember, _returnType.IsUnknown || _returnType == SemType.Void ? null : _returnType);
                returned = result.Type;
                break;
            }

            case BlockLambdaBody block:
                WalkStatement(block.Block, context, inMember);
                returned = _lambdaReturns.FirstOrDefault(r => r is not (NullType or UnknownType));
                break;
            case BlockStatement anonymousBody:
                WalkStatement(anonymousBody, context, inMember);
                returned = _lambdaReturns.FirstOrDefault(r => r is not (NullType or UnknownType));
                break;
        }

        if (lambda is LambdaExpression { IsAsync: true } && returned != null)
        {
            returned = new ExternalType("Task", "System.Threading.Tasks.Task", [returned]);
        }

        _locals = savedLocals;
        _returnType = savedReturn;
        _lambdaReturns = savedReturns;
        var bound = new Bound(new LambdaType(parameters?.Count ?? 0), true);
        _bound[lambda] = bound;
        return bound;
    }

    private Bound BindInvocation(InvocationExpression invocation, BindingContext context, string? inMember)
    {
        var target = invocation.Expression;
        List<FoundMember>? methods = null;
        Bound receiver = Bound.Unknown;
        var isStatic = false;
        var receiverKnown = false;
        string name;
        int nameStart, nameEnd;
        IReadOnlyList<TypeReference> typeArguments;
        switch (target)
        {
            case NameExpression { Parts.Count: 1, Alias: null } simple:
            {
                name = simple.Parts[0];
                nameStart = simple.Span.Start;
                nameEnd = simple.Span.End;
                typeArguments = simple.TypeArguments ?? [];
                WalkChild(simple.TypeArguments, context, inMember);
                _bound[simple] = Bound.Unknown;

                // A local function or a local of a delegate type
                if (typeArguments.Count == 0 && _locals.Find(name) is { } local)
                {
                    var localArguments = BindArguments(invocation.Arguments, context, inMember);
                    if (local.FunctionParameters is { } parameters)
                    {
                        CompleteArguments(null, localArguments, context, inMember, parameters);
                        return new Bound(local.Type, local.Exact);
                    }

                    var signature = _binder.DelegateSignature(local.Type);
                    CompleteArguments(null, localArguments, context, inMember, signature?.Parameters);
                    return signature is { } known ? new Bound(known.Return, local.Exact) : Bound.Unknown;
                }

                var value = BindSimpleName(simple, context, inMember, out methods, invoked: true);
                if (methods is not { Count: > 0 })
                {
                    // A property or field of a delegate type, or nothing known
                    var arguments = BindArguments(invocation.Arguments, context, inMember);
                    var signature = _binder.DelegateSignature(value.Type);
                    CompleteArguments(null, arguments, context, inMember, signature?.Parameters);
                    if (value.Type.IsUnknown && signature == null)
                    {
                        RecordNameOnly(name, arguments.Count, FindName(name, nameStart, nameEnd), inMember, invoked: true);
                    }

                    return signature is { } known ? new Bound(known.Return, value.Exact) : Bound.Unknown;
                }

                receiver = new Bound(_thisType, true);
                receiverKnown = true;
                break;
            }

            case MemberAccessExpression { IsPointerAccess: false } access:
            {
                name = access.MemberName;
                nameStart = access.Target?.Span.End ?? access.Span.Start;
                nameEnd = access.Span.End;
                typeArguments = access.TypeArguments ?? [];
                _bound[access] = Bound.Unknown;
                var value = BindMemberAccess(access, context, inMember, invoked: true, out var callee);
                receiver = callee.Receiver;
                methods = callee.Methods;
                isStatic = callee.IsStatic;
                receiverKnown = receiver.Type is not UnknownType;
                if (methods == null && receiverKnown)
                {
                    // A property or field of a delegate type
                    var arguments = BindArguments(invocation.Arguments, context, inMember);
                    var signature = _binder.DelegateSignature(value.Type);
                    CompleteArguments(null, arguments, context, inMember, signature?.Parameters);
                    return signature is { } known ? new Bound(known.Return, value.Exact) : Bound.Unknown;
                }

                break;
            }

            default:
            {
                // Invoking the result of an expression: a delegate
                var value = Bind(target, context, inMember);
                var arguments = BindArguments(invocation.Arguments, context, inMember);
                var signature = _binder.DelegateSignature(value.Type);
                CompleteArguments(null, arguments, context, inMember, signature?.Parameters);
                return signature is { } known ? new Bound(known.Return, value.Exact) : Bound.Unknown;
            }
        }

        var args = BindArguments(invocation.Arguments, context, inMember);
        var offset = FindName(name, nameStart, nameEnd);
        var explicitTypeArguments = typeArguments.Select(t => TypeOf(t, context)).ToList();

        // Instance or static methods of the receiver's type
        if (methods is { Count: > 0 } && Resolve(methods, args, explicitTypeArguments, null, context) is { } chosen)
        {
            CompleteArguments(chosen, args, context, inMember);
            var exact = receiver.Exact && chosen.Unique;
            RecordResolved(chosen, offset, inMember, receiver.Exact);
            return new Bound(chosen.Substitute(_binder.MemberType(chosen.Member.Symbol)), exact);
        }

        // Extension methods, for an instance receiver
        if (!isStatic && receiverKnown && receiver.Type is not (TypeExpressionType or NamespaceExpressionType))
        {
            var extensions = _binder.ExtensionMethods(name, context).Select(m => new FoundMember(m, _ => null)).ToList();
            if (extensions.Count > 0 && Resolve(extensions, args, explicitTypeArguments, receiver.Type, context) is { } extension)
            {
                CompleteArguments(extension, args, context, inMember);
                var exact = receiver.Exact && extension.Unique;
                RecordResolved(extension, offset, inMember, receiver.Exact);
                var returned = extension.Substitute(_binder.MemberType(extension.Member.Symbol));
                if ((returned.IsUnknown || SemTypes.HasTypeParameters(returned)) && SemTypes.KnownMethod(receiver.Type, name) is { } known)
                {
                    // The type arguments were not all inferred: the base class library model may know the result
                    returned = known.Result(null) is { IsUnknown: false } fromModel && !SemTypes.HasTypeParameters(fromModel) ? fromModel : returned;
                }

                return new Bound(returned, exact);
            }
        }

        if (!receiverKnown)
        {
            // The receiver's type is unknown: the workspace methods with this name are candidates
            CompleteArguments(null, args, context, inMember);
            RecordNameOnly(name, args.Count, offset, inMember, invoked: true);
            return Bound.Unknown;
        }

        // A method of a type from a reference: the base class library model knows a few
        if (SemTypes.KnownMethod(receiver.Type, name) is { } modeled && !isStatic)
        {
            var lambdaSignature = modeled.LambdaParameter is { } parameter ? ((IReadOnlyList<SemType>)[parameter], SemType.Unknown) : ((IReadOnlyList<SemType>, SemType)?)null;
            SemType? lambdaResult = null;
            foreach (var argument in args)
            {
                if (argument.Deferred && argument.Argument.Expression is LambdaExpression or AnonymousMethodExpression)
                {
                    BindLambda(argument.Argument.Expression, context, inMember, lambdaSignature, out var returned);
                    lambdaResult ??= returned;
                }
                else if (argument.Deferred)
                {
                    Bind(argument.Argument.Expression, context, inMember);
                }
            }

            RecordExternal("M:" + Describe(receiver.Type) + "." + name, offset, inMember);
            var result = name is "Cast" or "OfType" && explicitTypeArguments.Count == 1
                ? new ExternalType("IEnumerable", "System.Collections.Generic.IEnumerable", explicitTypeArguments)
                : modeled.Result(lambdaResult);
            return new Bound(result, false);
        }

        CompleteArguments(null, args, context, inMember);
        if (receiver.Type.Underlying is not TupleType)
        {
            RecordExternal("M:" + Describe(receiver.Type) + "." + name, offset, inMember);
        }

        return Bound.Unknown;
    }

    // ========================================
    // Overload resolution
    // ========================================

    /// <summary>The member overload resolution chose, the argument-to-parameter mapping and the inferred method type arguments.</summary>
    private sealed class Resolution(FoundMember member, IReadOnlyList<ParameterSem> parameters, int[] parameterOf, bool[] expanded, Dictionary<int, SemType> methodArguments, bool unique, CSharpBinder binder, bool isExtension)
    {
        public FoundMember Member { get; } = member;

        public bool Unique { get; set; } = unique;

        /// <summary>The candidates overload resolution could not tell apart (the argument types are not known), this one first.</summary>
        public IReadOnlyList<FoundMember> Tied { get; set; } = [member];

        /// <summary>The first parameter of an extension method, which the receiver goes to.</summary>
        public SemType? ReceiverParameterType => isExtension && parameters.Count > 0 ? parameters[0].Type : null;

        /// <summary>The type the argument at <paramref name="argument"/> converts to, with the type arguments known so far.</summary>
        public SemType? ParameterType(int argument)
        {
            var index = parameterOf[argument];
            if (index < 0 || index >= parameters.Count)
            {
                return null;
            }

            var type = Substitute(parameters[index].Type);
            return expanded[argument] ? SemTypes.ElementOf(type) ?? type : type;
        }

        /// <summary>The declared type of the parameter the argument at <paramref name="argument"/> goes to, before substitution.</summary>
        /// <summary>Whether the argument at <paramref name="argument"/> goes to a params parameter in its expanded form.</summary>
        public bool IsExpanded(int argument) => expanded[argument];

        public SemType? DeclaredParameterType(int argument)
        {
            var index = parameterOf[argument];
            return index >= 0 && index < parameters.Count ? parameters[index].Type : null;
        }

        public SemType Substitute(SemType type) => SemTypes.Substitute(type, p =>
            p.IsMethod && p.Owner == Member.Symbol ? methodArguments.GetValueOrDefault(p.Ordinal) : Member.Map(p));

        /// <summary>Infers the method's type parameters in <paramref name="parameterType"/> from an argument's type.</summary>
        public void Infer(SemType parameterType, SemType argumentType) => Unify(parameterType, argumentType, Member.Symbol, methodArguments, binder, 0);

        public static void Unify(SemType parameter, SemType argument, CodeSymbol method, Dictionary<int, SemType> inferred, CSharpBinder binder, int depth)
        {
            if (depth > 8 || argument is UnknownType or NullType or LambdaType)
            {
                return;
            }

            switch (parameter)
            {
                case TypeParameterType { IsMethod: true } p when p.Owner == method:
                    if (!inferred.TryAdd(p.Ordinal, argument) && inferred[p.Ordinal] is var known && !Equals(known, argument)
                        && binder.Conversion(known, argument) > 0 && binder.Conversion(argument, known) < 0)
                    {
                        // Several bounds: the type the others convert to (int and object give object)
                        inferred[p.Ordinal] = argument;
                    }

                    return;
                case NullableType nullable:
                    Unify(nullable.Element, argument.Underlying, method, inferred, binder, depth + 1);
                    return;
                case ArrayType array when argument.Underlying is ArrayType argumentArray:
                    Unify(array.Element, argumentArray.Element, method, inferred, binder, depth + 1);
                    return;
                case TupleType tuple when argument.Underlying is TupleType argumentTuple && tuple.Elements.Count == argumentTuple.Elements.Count:
                    for (var i = 0; i < tuple.Elements.Count; i++)
                    {
                        Unify(tuple.Elements[i], argumentTuple.Elements[i], method, inferred, binder, depth + 1);
                    }

                    return;
                case NamedType named:
                    foreach (var ancestor in binder.Ancestors(argument))
                    {
                        if (ancestor is NamedType match && match.Symbol == named.Symbol)
                        {
                            for (var i = 0; i < Math.Min(named.Arguments.Count, match.Arguments.Count); i++)
                            {
                                Unify(named.Arguments[i], match.Arguments[i], method, inferred, binder, depth + 1);
                            }

                            return;
                        }
                    }

                    return;
                case ExternalType external when external.Arguments.Count > 0:
                {
                    if (argument.Underlying is ExternalType same && same.Name == external.Name && same.Arguments.Count == external.Arguments.Count)
                    {
                        for (var i = 0; i < external.Arguments.Count; i++)
                        {
                            Unify(external.Arguments[i], same.Arguments[i], method, inferred, binder, depth + 1);
                        }

                        return;
                    }

                    // A base type or interface of the argument's type from a reference
                    if (argument.Underlying is ExternalType or NamedType)
                    {
                        foreach (var ancestor in binder.Ancestors(argument))
                        {
                            if (ancestor is ExternalType match && match.Name == external.Name && match.Arguments.Count == external.Arguments.Count
                                && binder.DefinitionOf(match) is { } definition && definition == binder.DefinitionOf(external))
                            {
                                for (var i = 0; i < external.Arguments.Count; i++)
                                {
                                    Unify(external.Arguments[i], match.Arguments[i], method, inferred, binder, depth + 1);
                                }

                                return;
                            }
                        }
                    }

                    // IEnumerable<T> from any sequence
                    if (external.Arguments.Count == 1 && SemTypes.ElementOf(external) != null)
                    {
                        var element = SemTypes.ElementOf(argument);
                        if (element == null && argument.Underlying is NamedType)
                        {
                            foreach (var ancestor in binder.Ancestors(argument))
                            {
                                if (ancestor is ExternalType sequence && SemTypes.ElementOf(sequence) is { } inherited)
                                {
                                    element = inherited;
                                    break;
                                }
                            }
                        }

                        if (element != null)
                        {
                            Unify(external.Arguments[0], element, method, inferred, binder, depth + 1);
                        }
                    }

                    return;
                }
            }
        }
    }

    /// <summary>
    /// Chooses among candidate methods (or constructors, indexers) as overload resolution does, as far as the
    /// argument types are known. <paramref name="extensionReceiver"/> is the receiver of an extension method call,
    /// which goes into the first parameter.
    /// </summary>
    private Resolution? Resolve(List<FoundMember> candidates, List<ArgumentInfo> arguments, IReadOnlyList<SemType> typeArguments, SemType? extensionReceiver, BindingContext context)
    {
        var applicable = new List<(Resolution Resolution, int Score, int Unknown, bool Generic, bool Expanded, int Defaults)>();
        foreach (var candidate in candidates)
        {
            var symbol = candidate.Symbol;
            if (symbol.Kind is not (SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Indexer or SymbolKind.Operator))
            {
                continue;
            }

            if (typeArguments.Count > 0 && symbol.TypeParameters.Count != typeArguments.Count)
            {
                continue;
            }

            var parameters = _binder.Parameters(symbol);
            var offset = extensionReceiver != null ? 1 : 0;
            if (offset > parameters.Count)
            {
                continue;
            }

            var methodArguments = new Dictionary<int, SemType>();
            for (var i = 0; i < typeArguments.Count; i++)
            {
                methodArguments[i] = typeArguments[i];
            }

            // Arguments to parameters: positional first, then by name
            var parameterOf = new int[arguments.Count];
            var expanded = new bool[arguments.Count];
            var used = new bool[parameters.Count];
            var fits = true;
            var position = offset;
            for (var i = 0; i < arguments.Count && fits; i++)
            {
                var argumentName = arguments[i].Argument.Name;
                int index;
                if (argumentName != null && !arguments[i].Argument.IsNameEquals)
                {
                    index = -1;
                    for (var p = offset; p < parameters.Count; p++)
                    {
                        if (parameters[p].Name == argumentName)
                        {
                            index = p;
                        }
                    }
                }
                else
                {
                    index = position < parameters.Count ? position : parameters.Count > offset && parameters[^1].IsParams ? parameters.Count - 1 : -1;
                    position++;
                }

                if (index < 0)
                {
                    fits = false;
                    break;
                }

                parameterOf[i] = index;
                if (used[index] && !parameters[index].IsParams)
                {
                    fits = false;
                    break;
                }

                used[index] = true;
            }

            if (!fits)
            {
                continue;
            }

            var defaults = 0;
            for (var p = offset; p < parameters.Count; p++)
            {
                if (!used[p] && !parameters[p].HasDefault && !parameters[p].IsParams)
                {
                    fits = false;
                    break;
                }

                if (!used[p] && parameters[p].HasDefault)
                {
                    defaults++;
                }
            }

            if (!fits)
            {
                continue;
            }

            var resolution = new Resolution(candidate, parameters, parameterOf, expanded, methodArguments, false, _binder, extensionReceiver != null);

            // The receiver of an extension method converts to the first parameter
            var score = 0;
            var unknown = 0;
            if (extensionReceiver != null)
            {
                var receiverParameter = parameters[0].Type;
                resolution.Infer(receiverParameter, extensionReceiver);
                var conversion = _binder.Conversion(extensionReceiver, resolution.Substitute(receiverParameter));
                if (conversion < 0)
                {
                    continue;
                }

                score += conversion;
            }

            // Infer the method's type arguments from the arguments, then check each conversion
            if (symbol.TypeParameters.Count > 0 && typeArguments.Count == 0)
            {
                for (var i = 0; i < arguments.Count; i++)
                {
                    var parameter = parameters[parameterOf[i]];
                    var type = parameter.IsParams && !(arguments.Count == parameterOf[i] + 1 - offset + offset && ArrayLike(arguments[i].Bound.Type))
                        ? SemTypes.ElementOf(parameter.Type) ?? parameter.Type
                        : parameter.Type;
                    resolution.Infer(type, arguments[i].Bound.Type);
                }
            }

            // A params parameter without arguments is the expanded form too
            var usesExpanded = parameters.Count > offset && parameters[^1].IsParams && !used[parameters.Count - 1];
            for (var i = 0; i < arguments.Count && fits; i++)
            {
                var parameter = parameters[parameterOf[i]];
                var parameterType = resolution.Substitute(parameter.Type);
                var argumentType = arguments[i].Bound.Type;
                var conversion = _binder.Conversion(argumentType, parameterType);
                if (parameter.IsParams && conversion < 2)
                {
                    // Expanded form: each argument converts to the element type
                    var element = SemTypes.ElementOf(parameterType) ?? SemType.Unknown;
                    var expandedConversion = _binder.Conversion(argumentType, element);
                    if (expandedConversion > conversion || parameterOf.Count(p => p == parameterOf[i]) > 1)
                    {
                        conversion = expandedConversion;
                        expanded[i] = true;
                        usesExpanded = true;
                    }
                }

                if (argumentType is LambdaType lambdaType && _binder.DelegateSignature(expanded[i] ? SemTypes.ElementOf(parameterType) ?? parameterType : parameterType) is { } signature
                    && lambdaType.ParameterCount >= 0 && signature.Parameters.Count != lambdaType.ParameterCount)
                {
                    conversion = -1;
                }

                // A collection expression converts to collection types only
                if (arguments[i].Argument.Expression is CollectionExpression && (_binder.DelegateSignature(parameterType) != null || SemTypes.IsPredefined(parameterType) && !Equals(parameterType, SemTypes.String) && !Equals(parameterType, SemTypes.Object)))
                {
                    conversion = -1;
                }

                // An interpolated string goes to an interpolated string handler before a string
                if (arguments[i].Argument.Expression is InterpolatedStringExpression && parameterType is ExternalType { Name: var handler } && handler.EndsWith("InterpolatedStringHandler", StringComparison.Ordinal))
                {
                    conversion = 3;
                }

                if (conversion < 0)
                {
                    fits = false;
                    break;
                }

                if (conversion == 0)
                {
                    unknown++;
                }

                score += conversion;
            }

            if (fits)
            {
                applicable.Add((resolution, score, unknown, symbol.TypeParameters.Count > 0, usesExpanded, defaults));
            }
        }

        if (applicable.Count == 0)
        {
            return null;
        }

        // The best candidate: better conversions, then non-generic, non-expanded, fewer defaults, more specific parameters
        var best = applicable
            .OrderByDescending(a => a.Score)
            .ThenBy(a => a.Generic)
            .ThenBy(a => a.Expanded)
            .ThenBy(a => a.Defaults)
            .ToList();
        var first = best[0];

        // A better conversion target decides before genericity: IEnumerable<T> is better than IEnumerable
        var top = best.Where(b => b.Score == first.Score).ToList();
        if (top.Count > 1 && top.FirstOrDefault(t => top.All(o => o.Resolution == t.Resolution || MoreSpecific(t.Resolution, o.Resolution, arguments.Count))) is { Resolution: not null } better)
        {
            first = better;
        }

        var tied = best.Where(b => b.Score == first.Score && b.Generic == first.Generic && b.Expanded == first.Expanded && b.Defaults == first.Defaults).ToList();
        if (top.Count > 1 && first.Resolution != best[0].Resolution)
        {
            tied = [first];
        }

        // A collection expression or expanded params argument goes to a span before an array
        if (tied.Count > 1)
        {
            bool Spans((Resolution Resolution, int, int, bool, bool, int) candidate) => Enumerable.Range(0, arguments.Count).Any(i =>
                (arguments[i].Argument.Expression is CollectionExpression || candidate.Resolution.IsExpanded(i))
                && candidate.Resolution.DeclaredParameterType(i)?.Underlying is ExternalType { Name: "ReadOnlySpan" or "Span" });
            var withSpans = tied.Where(Spans).ToList();
            if (withSpans.Count > 0 && withSpans.Count < tied.Count)
            {
                tied = withSpans;
                first = tied[0];
            }
        }

        if (tied.Count > 1)
        {
            var specific = tied.FirstOrDefault(t => tied.All(o => o.Resolution == t.Resolution
                || MoreSpecific(t.Resolution, o.Resolution, arguments.Count)
                || MoreSpecificDeclaration(t.Resolution, o.Resolution, arguments.Count)));
            if (specific.Resolution != null)
            {
                first = specific;
                tied = [specific];
            }
        }

        first.Resolution.Unique = tied.Count == 1 && (first.Unknown == 0 || applicable.Count == 1);
        first.Resolution.Tied = tied.Select(t => t.Resolution.Member).ToList();
        return first.Resolution;
    }

    private static bool ArrayLike(SemType type) => type.Underlying is ArrayType or NullType;

    /// <summary>Whether every parameter of <paramref name="a"/> converts to the corresponding one of <paramref name="b"/> (a derived type is better than its base).</summary>
    private bool MoreSpecific(Resolution a, Resolution b, int argumentCount)
    {
        var strictly = false;
        for (var i = 0; i < argumentCount; i++)
        {
            var pa = a.ParameterType(i);
            var pb = b.ParameterType(i);
            if (pa == null || pb == null)
            {
                return false;
            }

            var ab = _binder.Conversion(pa, pb);
            var ba = _binder.Conversion(pb, pa);
            if (ab < 0 && ba > 0)
            {
                return false;
            }

            if (ab > 0 && ba < 0)
            {
                strictly = true;
            }
        }

        return strictly;
    }

    /// <summary>
    /// The tie-break of generic methods whose parameter types are the same once substituted: the one whose declared
    /// parameter types are more specific (<c>Parser&lt;(T1, T2)&gt;</c> is more specific than <c>Parser&lt;T&gt;</c>).
    /// </summary>
    private static bool MoreSpecificDeclaration(Resolution a, Resolution b, int argumentCount)
    {
        var strictly = false;
        if (a.ReceiverParameterType is { } ra && b.ReceiverParameterType is { } rb)
        {
            var receiver = Specificity(ra, rb);
            if (receiver < 0)
            {
                return false;
            }

            strictly = receiver > 0;
        }

        for (var i = 0; i < argumentCount; i++)
        {
            var pa = a.DeclaredParameterType(i);
            var pb = b.DeclaredParameterType(i);
            if (pa == null || pb == null)
            {
                return false;
            }

            var compared = Specificity(pa, pb);
            if (compared < 0)
            {
                return false;
            }

            strictly |= compared > 0;
        }

        return strictly;
    }

    /// <summary>1 when <paramref name="a"/> is more specific than <paramref name="b"/>, -1 when less, 0 when neither.</summary>
    private static int Specificity(SemType a, SemType b)
    {
        switch (a, b)
        {
            case (TypeParameterType, TypeParameterType):
                return 0;
            case (_, TypeParameterType):
                return 1;
            case (TypeParameterType, _):
                return -1;
            case (NamedType x, NamedType y) when x.Symbol == y.Symbol:
                return Combine(x.Arguments, y.Arguments);
            case (ExternalType x, ExternalType y) when x.Name == y.Name:
                return Combine(x.Arguments, y.Arguments);
            case (TupleType x, TupleType y):
                return Combine(x.Elements, y.Elements);
            case (ArrayType x, ArrayType y):
                return Specificity(x.Element, y.Element);
            case (NullableType x, NullableType y):
                return Specificity(x.Element, y.Element);
            default:
                return 0;
        }

        static int Combine(IReadOnlyList<SemType> x, IReadOnlyList<SemType> y)
        {
            if (x.Count != y.Count)
            {
                return 0;
            }

            var more = false;
            var less = false;
            for (var i = 0; i < x.Count; i++)
            {
                var compared = Specificity(x[i], y[i]);
                more |= compared > 0;
                less |= compared < 0;
            }

            return more && !less ? 1 : less && !more ? -1 : 0;
        }
    }

    // ========================================
    // Queries
    // ========================================

    private Bound BindQuery(QueryExpression query, BindingContext context, string? inMember)
    {
        var saved = _locals;
        _locals = new Locals(saved);
        var source = Bind(query.FromClause.Expression, context, inMember);
        WalkNode(query.FromClause.Type, context, inMember);
        var element = query.FromClause.Type != null ? TypeOf(query.FromClause.Type, context) : ElementType(source.Type, false) ?? SemType.Unknown;
        _locals.Declare(query.FromClause.Identifier, new Local(element, false));
        var result = BindQueryBody(query.BodyClauses, query.SelectOrGroupClause, query.Continuation, context, inMember);
        _locals = saved;
        return result;
    }

    private Bound BindQueryBody(IReadOnlyList<QueryClause>? clauses, SelectOrGroupClause? selectOrGroup, QueryContinuation? continuation, BindingContext context, string? inMember)
    {
        foreach (var clause in clauses ?? [])
        {
            switch (clause)
            {
                case FromClause from:
                {
                    var source = Bind(from.Expression, context, inMember);
                    WalkNode(from.Type, context, inMember);
                    _locals.Declare(from.Identifier, new Local(from.Type != null ? TypeOf(from.Type, context) : ElementType(source.Type, false) ?? SemType.Unknown, false));
                    break;
                }

                case LetClause let:
                    _locals.Declare(let.Identifier, new Local(Bind(let.Expression, context, inMember).Type, false));
                    break;
                case JoinClause join:
                {
                    var source = Bind(join.InExpression, context, inMember);
                    WalkNode(join.Type, context, inMember);
                    Bind(join.LeftExpression, context, inMember);
                    _locals.Declare(join.Identifier, new Local(join.Type != null ? TypeOf(join.Type, context) : ElementType(source.Type, false) ?? SemType.Unknown, false));
                    Bind(join.RightExpression, context, inMember);
                    _locals.Declare(join.IntoIdentifier, new Local(SemType.Unknown, false));
                    break;
                }

                default:
                    WalkChildren(clause, context, inMember);
                    break;
            }
        }

        var selected = SemType.Unknown;
        switch (selectOrGroup)
        {
            case SelectClause select:
                selected = Bind(select.Expression, context, inMember).Type;
                break;
            case GroupClause group:
                Bind(group.GroupExpression, context, inMember);
                Bind(group.ByExpression, context, inMember);
                break;
        }

        if (continuation != null)
        {
            _locals.Declare(continuation.Identifier, new Local(SemType.Unknown, false));
            return BindQueryBody(continuation.BodyClauses, continuation.SelectOrGroupClause, continuation.Continuation, context, inMember);
        }

        return new Bound(new ExternalType("IEnumerable", "System.Collections.Generic.IEnumerable", [selected]), false);
    }

    // ========================================
    // Recording
    // ========================================

    /// <summary>
    /// Records the member overload resolution chose; when the argument types could not tell the candidates apart,
    /// each of them as a name-only candidate.
    /// </summary>
    private void RecordResolved(Resolution chosen, int offset, string? inMember, bool receiverExact)
    {
        if (chosen.Tied.Count > 1)
        {
            foreach (var candidate in chosen.Tied)
            {
                RecordReference(TargetId(candidate.Symbol), offset, inMember, Confidence.NameOnly);
            }

            return;
        }

        RecordReference(TargetId(chosen.Member.Symbol), offset, inMember, receiverExact && chosen.Unique ? Confidence.Exact : Confidence.Inferred);
    }

    /// <summary>The id a reference to <paramref name="symbol"/> is recorded with: <c>external:</c> and its id for a member of a reference.</summary>
    private static string TargetId(CodeSymbol symbol) => symbol.IsExternal ? ReferenceTargets.External + symbol.Id : symbol.Id;

    private void RecordReference(string targetId, int offset, string? inMember, Confidence confidence)
    {
        var (line, column) = _source.Lines.GetLineAndColumn(offset);
        _references.Add((targetId, new SymbolReference(_source.Path, offset, line, column, inMember, confidence)));
    }

    /// <summary>A call to or access of a member of a type from a reference, by the id it would have.</summary>
    private void RecordExternal(string id, int offset, string? inMember)
    {
        if (inMember != null)
        {
            RecordReference(ReferenceTargets.External + id, offset, inMember, Confidence.Inferred);
        }
    }

    /// <summary>A member access whose receiver's type is unknown: the workspace members with that name are candidates.</summary>
    private void RecordNameOnly(string name, int? argumentCount, int offset, string? inMember, bool invoked)
    {
        var candidates = _binder.MembersNamed(name)
            .Where(m => invoked ? m.Kind == SymbolKind.Method && Accepts(m, argumentCount ?? 0) : m.Kind is not SymbolKind.Method)
            .Take(MaxNameOnlyCandidates)
            .ToList();
        foreach (var candidate in candidates)
        {
            RecordReference(candidate.Id, offset, inMember, Confidence.NameOnly);
        }

        if (inMember != null && candidates.Count == 0)
        {
            RecordReference(ReferenceTargets.Unresolved + name, offset, inMember, Confidence.NameOnly);
        }
    }

    private bool Accepts(CodeSymbol method, int argumentCount)
    {
        var parameters = _binder.Parameters(method);
        var offset = _binder.IsExtensionMethod(method) ? 1 : 0;
        var required = parameters.Skip(offset).Count(p => !p.HasDefault && !p.IsParams);
        var total = parameters.Count - offset;
        return argumentCount >= required && (argumentCount <= total || parameters.Count > 0 && parameters[^1].IsParams);
    }

    private const int MaxNameOnlyCandidates = 20;

}
