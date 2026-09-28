using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using PaspanParsers.CSharp;

namespace PaspanCodeGraph.CSharp;

// The References pass: walks declarations and bodies and records each name that binds to a workspace type
public sealed partial class CSharpSymbolCollector
{
    private readonly List<(string TargetId, SymbolReference Reference)> _references = [];

    /// <summary>Names bound in expressions, so that a member access chain binds its target once.</summary>
    private readonly Dictionary<Expression, BoundName?> _boundExpressions = new(ReferenceEqualityComparer.Instance);

    /// <summary>The child nodes of each node type, read through its public properties.</summary>
    private static readonly ConcurrentDictionary<Type, Func<object, object?>[]> ChildGetters = new();

    private void WalkMember(MemberDeclaration member, Scope scope)
    {
        switch (member)
        {
            case MethodDeclaration m:
            {
                var map = new Dictionary<string, string>(scope.TypeParameters, StringComparer.Ordinal);
                for (var i = 0; i < (m.TypeParameters?.Count ?? 0); i++)
                {
                    map[m.TypeParameters![i].Name] = "``" + i;
                }

                var name = DocumentationIds.MemberName(m.Name, m.ExplicitInterface, scope.TypeParameters, Resolver(scope));
                var arity = m.TypeParameters?.Count ?? 0;
                var id = $"M:{scope.Qualify(name)}{(arity > 0 ? "``" + arity : "")}{DocumentationIds.ParameterList(m.Parameters, map, Resolver(scope, map))}";
                var context = scope.Context with { TypeParameters = map };
                var symbol = _builder.Get(id);
                var returnType = _binder.ToSemType(m.ReturnType, context, symbol);
                if (m.Modifiers.HasFlag(Modifiers.Async))
                {
                    returnType = SemTypes.Awaited(returnType) ?? SemType.Unknown;
                }

                EnterMember(scope, symbol, m.Parameters, map, returnType);
                PreBind(m.Body, context, id, returnType);
                WalkNode(m, context, id);
                break;
            }

            case FieldDeclaration field when field.Variables.Count > 0:
            {
                var id = $"F:{scope.Qualify(field.Variables[0].Name)}";
                EnterMember(scope, _builder.Get(id), null, scope.TypeParameters, SemType.Unknown);
                var type = _binder.ToSemType(field.Type, scope.Context, null);
                foreach (var variable in field.Variables)
                {
                    Bind(variable.Initializer, scope.Context, id, type);
                }

                WalkNode(field, scope.Context, id);
                break;
            }

            case EventDeclaration e when e.Variables.Count > 0:
            {
                var id = $"E:{scope.Qualify(DocumentationIds.MemberName(e.Variables[0].Name, e.ExplicitInterface, scope.TypeParameters, Resolver(scope)))}";
                var type = _binder.ToSemType(e.Type, scope.Context, null);
                EnterMember(scope, _builder.Get(id), null, scope.TypeParameters, SemType.Void, value: type);
                foreach (var variable in e.Variables)
                {
                    Bind(variable.Initializer, scope.Context, id, type);
                }

                WalkNode(e, scope.Context, id);
                break;
            }

            case GlobalStatement:
            case IncompleteMemberDeclaration:
                EnterMember(scope, null, null, scope.TypeParameters, SemType.Unknown);
                WalkNode(member, scope.Context, null);
                break;

            default:
            {
                // Constructors, properties, indexers, operators, destructors: the id as the Members pass builds it
                var id = MemberId(member, scope);
                var symbol = id != null ? _builder.Get(id) : null;
                switch (member)
                {
                    case PropertyDeclaration p:
                    {
                        var type = _binder.ToSemType(p.Type, scope.Context, null);
                        EnterMember(scope, symbol, null, scope.TypeParameters, type, value: type);
                        Bind(p.ExpressionBody, scope.Context, id, type);
                        Bind(p.Initializer, scope.Context, id, type);
                        PreBindAccessors(p.Accessors, scope.Context, id, type);
                        break;
                    }

                    case IndexerDeclaration indexer:
                    {
                        var type = _binder.ToSemType(indexer.Type, scope.Context, null);
                        EnterMember(scope, symbol, indexer.Parameters, scope.TypeParameters, type, value: type);
                        Bind(indexer.ExpressionBody, scope.Context, id, type);
                        PreBindAccessors(indexer.Accessors, scope.Context, id, type);
                        break;
                    }

                    case ConstructorDeclaration ctor:
                        EnterMember(scope, symbol, ctor.Parameters, scope.TypeParameters, SemType.Void);
                        break;

                    case OperatorDeclaration op:
                    {
                        var type = _binder.ToSemType(op.ReturnType, scope.Context, null);
                        EnterMember(scope, symbol, op.Parameters, scope.TypeParameters, type);
                        PreBind(op.Body, scope.Context, id, type);
                        break;
                    }

                    case ConversionOperatorDeclaration conversion:
                    {
                        var type = _binder.ToSemType(conversion.Type, scope.Context, null);
                        EnterMember(scope, symbol, conversion.Parameters, scope.TypeParameters, type);
                        PreBind(conversion.Body, scope.Context, id, type);
                        break;
                    }

                    default:
                        EnterMember(scope, symbol, null, scope.TypeParameters, SemType.Void);
                        break;
                }

                WalkNode(member, scope.Context, id);
                break;
            }
        }
    }

    /// <summary>Binds an expression body first, with the type it converts to (the walk then finds it bound).</summary>
    private void PreBind(PaspanParsers.CSharp.MethodBody? body, BindingContext context, string? inMember, SemType type)
    {
        if (body is ExpressionMethodBody expression && type != SemType.Void)
        {
            Bind(expression.Expression, context, inMember, type.IsUnknown ? null : type);
        }
    }

    private void PreBindAccessors(IReadOnlyList<Accessor>? accessors, BindingContext context, string? inMember, SemType type)
    {
        foreach (var accessor in accessors ?? [])
        {
            if (accessor.Kind == AccessorKind.Get)
            {
                PreBind(accessor.Body, context, inMember, type);
            }
        }
    }

    /// <summary>The id of a member that has one declaration per node.</summary>
    private string? MemberId(MemberDeclaration member, Scope scope) => member switch
    {
        ConstructorDeclaration ctor => $"M:{scope.Qualify(ctor.Modifiers.HasFlag(Modifiers.Static) ? "#cctor" : "#ctor")}{DocumentationIds.ParameterList(ctor.Parameters, scope.TypeParameters, Resolver(scope))}",
        DestructorDeclaration => $"M:{scope.Qualify("Finalize")}",
        PropertyDeclaration p => $"P:{scope.Qualify(DocumentationIds.MemberName(p.Name, p.ExplicitInterface, scope.TypeParameters, Resolver(scope)))}",
        IndexerDeclaration indexer => $"P:{scope.Qualify(DocumentationIds.MemberName("Item", indexer.ExplicitInterface, scope.TypeParameters, Resolver(scope)))}{DocumentationIds.ParameterList(indexer.Parameters, scope.TypeParameters, Resolver(scope))}",
        OperatorDeclaration op => $"M:{scope.Qualify(DocumentationIds.MemberName(DocumentationIds.OperatorName(op.Operator, op.Parameters?.Count ?? 0, op.IsChecked), op.ExplicitInterface, scope.TypeParameters, Resolver(scope)))}{DocumentationIds.ParameterList(op.Parameters, scope.TypeParameters, Resolver(scope))}",
        ConversionOperatorDeclaration conversion => $"M:{scope.Qualify(conversion.IsImplicit ? "op_Implicit" : conversion.IsChecked ? "op_CheckedExplicit" : "op_Explicit")}{DocumentationIds.ParameterList(conversion.Parameters, scope.TypeParameters, Resolver(scope))}~{DocumentationIds.TypeId(conversion.Type, scope.TypeParameters, Resolver(scope))}",
        _ => scope.Type?.Id,
    };

    /// <summary>The attributes, base list and constraints of a type declaration, without its members.</summary>
    private void WalkType(TypeDeclaration type, Scope inner, CodeSymbol symbol)
    {
        // The base list is bound outside the type's members, with its type parameters in scope
        var outside = inner.Context with { Type = symbol.ContainingType };
        foreach (var child in Children(type))
        {
            if (child is IEnumerable<MemberDeclaration> or IEnumerable<EnumMember> or IEnumerable<Argument>)
            {
                continue;
            }

            if (child is IEnumerable<TypeReference> bases)
            {
                foreach (var baseType in bases)
                {
                    WalkNode(baseType, outside, symbol.Id);
                }

                continue;
            }

            WalkChild(child, inner.Context, symbol.Id);
        }

        BindBaseArguments(type, inner.Context, symbol);
    }

    private void WalkAttributes(IReadOnlyList<AttributeSection>? attributes, Scope scope, string? inMember)
    {
        foreach (var section in attributes ?? [])
        {
            WalkNode(section, scope.Context, inMember);
        }
    }

    /// <summary>The types named by <c>using static</c> and alias directives (namespaces are not recorded).</summary>
    private void WalkUsings(IReadOnlyList<UsingDirective>? usings, Scope scope)
    {
        foreach (var directive in usings ?? [])
        {
            var target = directive switch
            {
                UsingAliasDirective alias => (CSharpNode?)alias.Target ?? alias.TargetType,
                UsingStaticDirective type => (CSharpNode?)type.Type ?? type.TargetType,
                UsingNamespaceDirective ns => ns.Namespace,
                _ => null,
            };

            // A using target is bound without the usings of its own declaration
            var context = new BindingContext(new ImportScope(scope.Imports.Parent, scope.Imports.NamespaceName ?? ""), null, NoTypeParameters);
            switch (target)
            {
                case NameExpression name:
                    RecordDottedName(name, context, null, attribute: false);
                    break;
                case TypeReference type:
                    WalkNode(type, context, null);
                    break;
            }
        }
    }

    private void WalkChild(object? child, BindingContext context, string? inMember)
    {
        switch (child)
        {
            case CSharpNode node:
                WalkNode(node, context, inMember);
                break;
            case IEnumerable list and not string:
                foreach (var item in list)
                {
                    if (item is CSharpNode node)
                    {
                        WalkNode(node, context, inMember);
                    }
                }

                break;
        }
    }

    private void WalkNode(CSharpNode? node, BindingContext context, string? inMember)
    {
        if (node == null)
        {
            return;
        }

        switch (node)
        {
            case NamedTypeReference named:
                RecordNamedType(named, context, inMember);
                if (named.Qualifier != null)
                {
                    WalkNode(named.Qualifier, context, inMember);
                }

                WalkChild(named.TypeArguments, context, inMember);
                return;

            case AttributeNode attribute:
            {
                var bound = RecordDottedName(attribute.Name, context, inMember, attribute: true);
                WalkChild(attribute.Name.TypeArguments, context, inMember);
                BindAttribute(attribute, bound?.Type?.Symbol, context, inMember);
                return;
            }

            case Statement statement:
                WalkStatement(statement, context, inMember);
                return;

            case Expression expression:
                Bind(expression, context, inMember);
                return;

            case Pattern pattern:
                BindPattern(pattern, SemType.Unknown, context, inMember);
                return;

            case ConstructorInitializer initializer:
                BindConstructorInitializer(initializer, context, inMember);
                return;

            case Parameter parameter:
                // A default value converts to the parameter's type
                WalkChild(parameter.Attributes, context, inMember);
                WalkChild(parameter.Type, context, inMember);
                Bind(parameter.DefaultValue, context, inMember, parameter.Type is null ? null : _binder.ToSemType(parameter.Type, context, _method));
                return;
        }

        foreach (var child in Children(node))
        {
            WalkChild(child, context, inMember);
        }
    }

    /// <summary>Records the parts of a named type that bind to workspace types (<c>Outer</c> and <c>Inner</c> in <c>Outer.Inner</c>).</summary>
    private void RecordNamedType(NamedTypeReference named, BindingContext context, string? inMember)
    {
        var parts = named.Name.Parts;
        var arity = named.TypeArguments?.Count ?? 0;
        var position = named.Qualifier?.Span.End ?? named.Span.Start;
        BoundName? current;
        int first;
        if (named.Qualifier is NamedTypeReference qualifier)
        {
            current = _binder.BindType(qualifier, context) is { } q ? new BoundName(null, q) : null;
            first = 0;
        }
        else if (named.Alias != null)
        {
            current = named.Alias == "global" ? new BoundName("", null) : null;
            first = 0;
        }
        else
        {
            if (parts.Count == 1 && arity == 0 && context.TypeParameters.ContainsKey(parts[0]))
            {
                return;
            }

            current = _binder.LookupSimple(parts[0], parts.Count == 1 ? arity : 0, context, typeOnly: parts.Count == 1);
            position = Record(current, parts[0], position, named.Span.End, inMember, Confidence.Exact);
            first = 1;
        }

        for (var i = first; i < parts.Count && current != null; i++)
        {
            current = _binder.MemberOf(current, parts[i], i == parts.Count - 1 ? arity : 0, context);
            position = Record(current, parts[i], position, named.Span.End, inMember, Confidence.Exact);
        }
    }

    /// <summary>A dotted name of a using or an attribute; an attribute name may leave out its <c>Attribute</c> suffix.</summary>
    private BoundName? RecordDottedName(NameExpression name, BindingContext context, string? inMember, bool attribute)
    {
        var parts = name.Parts;
        var position = name.Span.Start;
        BoundName? current = name.Alias == "global" ? new BoundName("", null) : null;
        for (var i = 0; i < parts.Count; i++)
        {
            var last = i == parts.Count - 1;
            var arity = last ? name.TypeArguments?.Count ?? 0 : 0;
            BoundName? Find(string part) => current == null && i == 0 && name.Alias == null
                ? _binder.LookupSimple(part, arity, context, typeOnly: false)
                : current == null ? null : _binder.MemberOf(current, part, arity, context);

            var next = attribute && last ? Find(parts[i] + "Attribute") ?? Find(parts[i]) : Find(parts[i]);
            current = next;
            position = Record(current, parts[i], position, name.Span.End, inMember, Confidence.Exact);
            if (current == null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    /// Binds an expression as a namespace or type (a simple name, or a member access on one), recording the types
    /// found. A simple name could also be a local, parameter or member, so these references are
    /// <see cref="Confidence.Inferred"/>.
    /// </summary>
    private BoundName? BindExpression(Expression expression, BindingContext context, string? inMember)
    {
        if (_boundExpressions.TryGetValue(expression, out var known))
        {
            return known;
        }

        BoundName? bound = null;
        switch (expression)
        {
            case NameExpression { Parts.Count: 1, Alias: null } name:
            {
                var arity = name.TypeArguments?.Count ?? 0;
                if (arity != 0 || !context.TypeParameters.ContainsKey(name.Parts[0]))
                {
                    bound = _binder.LookupSimple(name.Parts[0], arity, context, typeOnly: false, expression: true);
                    Record(bound, name.Parts[0], name.Span.Start, name.Span.End, inMember, Confidence.Inferred);
                }

                break;
            }

            case AliasQualifiedNameExpression { Alias: "global" } global:
                bound = _binder.BindNamespaceOrTypeName(new NameExpression(global.Name.Parts, global.Name.TypeArguments, "global"), context);
                break;

            case MemberAccessExpression { Target: { } target, IsPointerAccess: false } access:
                var container = BindExpression(target, context, inMember);
                if (container == null && target is NameExpression { Parts.Count: 1, Alias: null, TypeArguments: null or [] } colorName)
                {
                    container = _binder.ColorColor(colorName.Parts[0], access.MemberName, context);
                    Record(container, colorName.Parts[0], target.Span.Start, target.Span.End, inMember, Confidence.Inferred);
                }

                if (container != null)
                {
                    bound = _binder.MemberOf(container, access.MemberName, access.TypeArguments?.Count ?? 0, context);
                    Record(bound, access.MemberName, target.Span.End, access.Span.End, inMember, Confidence.Inferred);
                }

                break;
        }

        _boundExpressions[expression] = bound;
        return bound;
    }

    /// <summary>Records a reference when <paramref name="bound"/> is a type; returns where to look for the next part.</summary>
    private int Record(BoundName? bound, string name, int start, int end, string? inMember, Confidence confidence)
    {
        var offset = FindName(name, start, end);
        if (bound?.Type is { } type)
        {
            var (line, column) = _source.Lines.GetLineAndColumn(offset);
            _references.Add((TargetId(type.Symbol), new SymbolReference(_source.Path, offset, line, column, inMember, confidence)));
        }

        return offset + System.Text.Encoding.UTF8.GetByteCount(name);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "The server keeps every member of PaspanParsers (TrimmerRootAssembly), whose node types these are.")]
    private static IEnumerable<object?> Children(CSharpNode node)
    {
        var getters = ChildGetters.GetOrAdd(node.GetType(), static type => DeclarationOrder(type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && IsNodeType(p.PropertyType) && p.Name != nameof(NamespaceDeclaration.NullableDirectives)))
            .Select(p => (Func<object, object?>)p.GetValue)
            .ToArray());
        foreach (var getter in getters)
        {
            yield return getter(node);
        }
    }

    /// <summary>
    /// Properties in the order they are declared, which is source order for the nodes' parts. NativeAOT has no
    /// metadata tokens; there reflection lists them in metadata order already.
    /// </summary>
    private static IEnumerable<PropertyInfo> DeclarationOrder(IEnumerable<PropertyInfo> properties)
    {
        var list = properties.ToList();
        try
        {
            return list.OrderBy(p => p.MetadataToken).ToList();
        }
        catch (InvalidOperationException)
        {
            return list;
        }
    }

    private static bool IsNodeType(Type type)
    {
        if (typeof(CSharpNode).IsAssignableFrom(type))
        {
            return true;
        }

        return type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            && typeof(CSharpNode).IsAssignableFrom(type.GetGenericArguments()[0]);
    }
}
