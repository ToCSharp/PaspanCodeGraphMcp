using System.Collections;
using System.Collections.Concurrent;
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
                WalkNode(m, scope.Context with { TypeParameters = map }, id);
                break;
            }

            case FieldDeclaration field when field.Variables.Count > 0:
                WalkNode(field, scope.Context, $"F:{scope.Qualify(field.Variables[0].Name)}");
                break;

            case EventDeclaration e when e.Variables.Count > 0:
                WalkNode(e, scope.Context, $"E:{scope.Qualify(DocumentationIds.MemberName(e.Variables[0].Name, e.ExplicitInterface, scope.TypeParameters, Resolver(scope)))}");
                break;

            case GlobalStatement:
            case IncompleteMemberDeclaration:
                WalkNode(member, scope.Context, null);
                break;

            default:
                // Constructors, properties, indexers, operators, destructors: the id as the Members pass builds it
                WalkNode(member, scope.Context, MemberId(member, scope));
                break;
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
            if (child is IEnumerable<MemberDeclaration> or IEnumerable<EnumMember>)
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

    private void WalkNode(CSharpNode node, BindingContext context, string? inMember)
    {
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
                RecordDottedName(attribute.Name, context, inMember, attribute: true);
                WalkChild(attribute.Name.TypeArguments, context, inMember);
                WalkChild(attribute.Arguments, context, inMember);
                return;

            case NameExpression name:
                BindExpression(name, context, inMember);
                WalkChild(name.TypeArguments, context, inMember);
                return;

            case InvocationExpression { Expression: NameExpression { Parts.Count: 1, Alias: null } invoked } invocation:
                // An invoked simple name is a method or a delegate, never a type
                WalkChild(invoked.TypeArguments, context, inMember);
                WalkChild(invocation.Arguments, context, inMember);
                return;

            case MemberAccessExpression access:
                BindExpression(access, context, inMember);
                if (access.Target != null)
                {
                    WalkNode(access.Target, context, inMember);
                }

                WalkChild(access.TypeArguments, context, inMember);
                return;

            case LocalFunctionStatement { TypeParameters.Count: > 0 } local:
            {
                // Its type parameters hide types of the same name
                var map = new Dictionary<string, string>(context.TypeParameters, StringComparer.Ordinal);
                foreach (var parameter in local.TypeParameters)
                {
                    map[parameter.Name] = parameter.Name;
                }

                var inner = context with { TypeParameters = map };
                foreach (var child in Children(node))
                {
                    WalkChild(child, inner, inMember);
                }

                return;
            }
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
    private void RecordDottedName(NameExpression name, BindingContext context, string? inMember, bool attribute)
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
                return;
            }
        }
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
            _references.Add((type.Symbol.Id, new SymbolReference(_source.Path, offset, line, column, inMember, confidence)));
        }

        return offset + System.Text.Encoding.UTF8.GetByteCount(name);
    }

    private static IEnumerable<object?> Children(CSharpNode node)
    {
        var getters = ChildGetters.GetOrAdd(node.GetType(), static type => type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && IsNodeType(p.PropertyType) && p.Name != nameof(NamespaceDeclaration.NullableDirectives))
            .OrderBy(p => p.MetadataToken)
            .Select(p => (Func<object, object?>)p.GetValue)
            .ToArray());
        foreach (var getter in getters)
        {
            yield return getter(node);
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
