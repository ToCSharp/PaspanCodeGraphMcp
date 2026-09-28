using System.Text;
using PaspanParsers;
using PaspanParsers.CSharp;

namespace PaspanCodeGraph.CSharp;

/// <summary>A parsed C# file.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Project">Name of the project the file belongs to.</param>
/// <param name="Utf8">The file's source as UTF-8 without the byte order mark; spans are offsets into it.</param>
public sealed record CSharpSource(string Path, string Project, ReadOnlyMemory<byte> Utf8, LineMap Lines, CompilationUnit Unit);

/// <summary>What a pass over the files of a workspace collects.</summary>
public enum CollectPass
{
    /// <summary>Namespaces and types, their base lists and the global usings: what names are bound against.</summary>
    Types,

    /// <summary>Members and enum members, with ids whose parameter types are bound.</summary>
    Members,

    /// <summary>References to types in declarations and bodies.</summary>
    References,
}

/// <summary>
/// Adds the declarations of a parsed C# file to a <see cref="SymbolIndexBuilder"/>: namespaces, types,
/// members and enum members, with documentation-comment ids (<see cref="DocumentationIds"/>), signatures,
/// modifiers and the location of each declared name. A workspace is collected in passes (<see cref="CollectPass"/>)
/// over all its files, so that every type is known before names are bound.
/// </summary>
public sealed partial class CSharpSymbolCollector
{
    private static readonly IReadOnlyDictionary<string, string> NoTypeParameters = BindingContext.NoTypeParameters;

    private readonly CSharpSource _source;
    private readonly SymbolIndexBuilder _builder;
    private readonly CSharpBinder _binder;
    private readonly CollectPass _pass;

    private CSharpSymbolCollector(CSharpSource source, SymbolIndexBuilder builder, CSharpBinder binder, CollectPass pass)
    {
        _source = source;
        _builder = builder;
        _binder = binder;
        _pass = pass;
    }

    private ReadOnlySpan<byte> Utf8 => _source.Utf8.Span;

    /// <summary>Collects a file on its own: its types, then its members (names bind only to types of this file).</summary>
    public static void Collect(CSharpSource source, SymbolIndexBuilder builder)
    {
        var binder = new CSharpBinder(builder);
        Collect(source, builder, binder, CollectPass.Types);
        Collect(source, builder, binder, CollectPass.Members);
        CSharpHierarchy.Link(builder, binder);
    }

    /// <summary>
    /// Runs one pass over a file. <see cref="CollectPass.References"/> returns the references found (to add to
    /// the builder afterwards, so that files can be walked in parallel); the other passes return nothing.
    /// </summary>
    public static List<(string TargetId, SymbolReference Reference)> Collect(CSharpSource source, SymbolIndexBuilder builder, CSharpBinder binder, CollectPass pass)
    {
        var collector = new CSharpSymbolCollector(source, builder, binder, pass);
        var unit = source.Unit;
        var fileScope = new ImportScope(null, "") { Globals = binder.ProjectScope(source.Project) };
        foreach (var directive in unit.Usings ?? [])
        {
            if (!directive.IsGlobal)
            {
                fileScope.AddUsing(directive);
            }
            else if (pass == CollectPass.Types)
            {
                binder.ProjectScope(source.Project).AddUsing(directive);
            }
        }

        var scope = new Scope(null, null, NoTypeParameters, 0, fileScope);
        if (pass == CollectPass.References)
        {
            collector.WalkUsings(unit.Usings, scope);
            collector.WalkAttributes(unit.GlobalAttributes, scope, null);
        }

        collector.VisitMembers(unit.Members, scope);
        return collector._references;
    }

    /// <summary>
    /// Where a declaration is: the namespace symbol (null for the global namespace), the containing type, the type
    /// parameters in scope with their id forms and the import scopes names are looked up in.
    /// </summary>
    private sealed record Scope(CodeSymbol? Namespace, CodeSymbol? Type, IReadOnlyDictionary<string, string> TypeParameters, int TypeParameterCount, ImportScope Imports, bool InExtension = false)
    {
        public CodeSymbol? Container => Type ?? Namespace;

        /// <summary>The id of the container without its prefix: <c>Ns.Outer`1.Inner</c>.</summary>
        public string? ContainerIdBody => Container?.Id[2..];

        public string Qualify(string name) => ContainerIdBody is { } body ? $"{body}.{name}" : name;

        public BindingContext Context => new(Imports, Type, TypeParameters);
    }

    private TypeResolver Resolver(Scope scope) => _binder.Resolver(scope.Context);

    private TypeResolver Resolver(Scope scope, IReadOnlyDictionary<string, string> typeParameters) =>
        _binder.Resolver(scope.Context with { TypeParameters = typeParameters });

    private void VisitMembers(IReadOnlyList<MemberDeclaration>? members, Scope scope)
    {
        if (members == null)
        {
            return;
        }

        foreach (var member in members)
        {
            VisitMember(member, scope);
        }
    }

    private void VisitMember(MemberDeclaration member, Scope scope)
    {
        if (_pass == CollectPass.References && member is not (NamespaceDeclaration or TypeDeclaration or DelegateDeclaration or ExtensionBlockDeclaration))
        {
            WalkMember(member, scope);
            return;
        }

        if (_pass == CollectPass.Types && member is not (NamespaceDeclaration or TypeDeclaration or DelegateDeclaration))
        {
            return;
        }

        switch (member)
        {
            case NamespaceDeclaration ns:
                VisitNamespace(ns, scope);
                break;
            case ClassDeclaration c:
                VisitType(c, SymbolKind.Class, c.Name, c.TypeParameters, c.BaseTypes, c.PrimaryConstructorParameters, c.Members, scope);
                break;
            case StructDeclaration s:
                VisitType(s, SymbolKind.Struct, s.Name, s.TypeParameters, s.Interfaces, s.PrimaryConstructorParameters, s.Members, scope);
                break;
            case InterfaceDeclaration i:
                VisitType(i, SymbolKind.Interface, i.Name, i.TypeParameters, i.BaseInterfaces, null, i.Members, scope);
                break;
            case RecordDeclaration r:
                VisitType(r, r.IsRecordStruct ? SymbolKind.RecordStruct : SymbolKind.Record, r.Name, r.TypeParameters, r.BaseTypes, r.PrimaryConstructorParameters, r.Members, scope);
                break;
            case EnumDeclaration e:
                VisitEnum(e, scope);
                break;
            case DelegateDeclaration d:
                VisitDelegate(d, scope);
                break;
            case ExtensionBlockDeclaration extension:
                VisitExtension(extension, scope);
                break;
            case MethodDeclaration m:
                VisitMethod(m, scope);
                break;
            case ConstructorDeclaration ctor:
            {
                var isStatic = ctor.Modifiers.HasFlag(Modifiers.Static);
                var name = isStatic ? "#cctor" : "#ctor";
                var symbol = AddMember(ctor, SymbolKind.Constructor, ctor.Name, $"M:{scope.Qualify(name)}{DocumentationIds.ParameterList(ctor.Parameters, scope.TypeParameters, Resolver(scope))}", scope, ctor.Span.Start);
                if (symbol != null)
                {
                    _binder.RegisterMember(symbol, scope.Context, null, ctor.Parameters);
                    SetParameters(symbol, ctor.Parameters);
                    symbol.Signature = $"{scope.Type?.Name ?? ctor.Name}({ParameterText(ctor.Parameters)})";
                }

                break;
            }

            case DestructorDeclaration dtor:
            {
                var symbol = AddMember(dtor, SymbolKind.Destructor, "~" + dtor.Name, $"M:{scope.Qualify("Finalize")}", scope, dtor.Span.Start);
                if (symbol != null)
                {
                    symbol.Signature = $"~{dtor.Name}()";
                }

                break;
            }

            case PropertyDeclaration p:
            {
                var name = DocumentationIds.MemberName(p.Name, p.ExplicitInterface, scope.TypeParameters, Resolver(scope));
                var symbol = AddMember(p, SymbolKind.Property, p.Name, $"P:{scope.Qualify(name)}", scope, After(p.ExplicitInterface ?? p.Type, p));
                LinkExplicitInterface(symbol, p.ExplicitInterface, scope);
                if (symbol != null)
                {
                    symbol.Type = Text(p.Type);
                    _binder.RegisterMember(symbol, scope.Context, p.Type, null);
                    symbol.Signature = $"{symbol.Type} {Owner(scope)}{InterfacePrefix(p.ExplicitInterface)}{p.Name} {AccessorText(p.Accessors, p.ExpressionBody != null)}";
                }

                break;
            }

            case IndexerDeclaration indexer:
            {
                var name = DocumentationIds.MemberName("Item", indexer.ExplicitInterface, scope.TypeParameters, Resolver(scope));
                var id = $"P:{scope.Qualify(name)}{DocumentationIds.ParameterList(indexer.Parameters, scope.TypeParameters, Resolver(scope))}";
                var symbol = AddMember(indexer, SymbolKind.Indexer, "this[]", id, scope, After(indexer.ExplicitInterface ?? indexer.Type, indexer), nameText: "this");
                LinkExplicitInterface(symbol, indexer.ExplicitInterface, scope);
                if (symbol != null)
                {
                    symbol.Type = Text(indexer.Type);
                    _binder.RegisterMember(symbol, scope.Context, indexer.Type, indexer.Parameters);
                    SetParameters(symbol, indexer.Parameters);
                    symbol.Signature = $"{symbol.Type} {Owner(scope)}{InterfacePrefix(indexer.ExplicitInterface)}this[{ParameterText(indexer.Parameters)}] {AccessorText(indexer.Accessors, indexer.ExpressionBody != null)}";
                }

                break;
            }

            case FieldDeclaration field:
                foreach (var variable in field.Variables)
                {
                    var symbol = AddMember(field, SymbolKind.Field, variable.Name, $"F:{scope.Qualify(variable.Name)}", scope, variable.Span.Start, span: field.Span);
                    if (symbol != null)
                    {
                        symbol.Type = Text(field.Type);
                        _binder.RegisterMember(symbol, scope.Context, field.Type, null);
                        symbol.Signature = $"{symbol.Type} {Owner(scope)}{variable.Name}";
                    }
                }

                break;

            case EventDeclaration e:
                foreach (var variable in e.Variables)
                {
                    var name = DocumentationIds.MemberName(variable.Name, e.ExplicitInterface, scope.TypeParameters, Resolver(scope));
                    var symbol = AddMember(e, SymbolKind.Event, variable.Name, $"E:{scope.Qualify(name)}", scope, variable.Span.Start, span: e.Span);
                    LinkExplicitInterface(symbol, e.ExplicitInterface, scope);
                    if (symbol != null)
                    {
                        symbol.Type = Text(e.Type);
                        _binder.RegisterMember(symbol, scope.Context, e.Type, null);
                        symbol.Signature = $"event {symbol.Type} {Owner(scope)}{InterfacePrefix(e.ExplicitInterface)}{variable.Name}";
                    }
                }

                break;

            case OperatorDeclaration op:
            {
                var parameterCount = op.Parameters?.Count ?? 0;
                var name = DocumentationIds.MemberName(DocumentationIds.OperatorName(op.Operator, parameterCount, op.IsChecked), op.ExplicitInterface, scope.TypeParameters, Resolver(scope));
                var id = $"M:{scope.Qualify(name)}{DocumentationIds.ParameterList(op.Parameters, scope.TypeParameters, Resolver(scope))}";
                var symbol = AddMember(op, SymbolKind.Operator, "operator " + op.Operator, id, scope, After(op.ReturnType, op), nameText: "operator");
                LinkExplicitInterface(symbol, op.ExplicitInterface, scope);
                if (symbol != null)
                {
                    symbol.Type = Text(op.ReturnType);
                    _binder.RegisterMember(symbol, scope.Context, op.ReturnType, op.Parameters);
                    SetParameters(symbol, op.Parameters);
                    symbol.Signature = $"{symbol.Type} {Owner(scope)}operator {op.Operator}({ParameterText(op.Parameters)})";
                }

                break;
            }

            case ConversionOperatorDeclaration conversion:
            {
                var name = conversion.IsImplicit ? "op_Implicit" : conversion.IsChecked ? "op_CheckedExplicit" : "op_Explicit";
                var id = $"M:{scope.Qualify(name)}{DocumentationIds.ParameterList(conversion.Parameters, scope.TypeParameters, Resolver(scope))}~{DocumentationIds.TypeId(conversion.Type, scope.TypeParameters, Resolver(scope))}";
                var keyword = conversion.IsImplicit ? "implicit" : "explicit";
                var symbol = AddMember(conversion, SymbolKind.Operator, $"{keyword} operator {Text(conversion.Type)}", id, scope, conversion.Span.Start, nameText: keyword);
                LinkExplicitInterface(symbol, conversion.ExplicitInterface, scope);
                if (symbol != null)
                {
                    symbol.Type = Text(conversion.Type);
                    _binder.RegisterMember(symbol, scope.Context, conversion.Type, conversion.Parameters);
                    SetParameters(symbol, conversion.Parameters);
                    symbol.Signature = $"{Owner(scope)}{keyword} operator {symbol.Type}({ParameterText(conversion.Parameters)})";
                }

                break;
            }

            // Top-level statements and skipped invalid source declare nothing
            case GlobalStatement:
            case IncompleteMemberDeclaration:
                break;
        }
    }

    private void VisitNamespace(NamespaceDeclaration ns, Scope scope)
    {
        var current = scope.Namespace;
        var imports = scope.Imports;
        var parts = ns.Name.Parts;
        var nameStart = ns.Name.Span.Start;
        for (var i = 0; i < parts.Count; i++)
        {
            var dotted = current is null ? parts[i] : $"{current.Id[2..]}.{parts[i]}";
            CodeSymbol symbol;
            if (_pass == CollectPass.Types)
            {
                symbol = _builder.GetOrAdd("N:" + dotted, SymbolKind.Namespace, parts[i], current, out var created);
                if (created)
                {
                    symbol.Namespace = current?.Id[2..];
                    symbol.Signature = "namespace " + dotted;
                    symbol.Project = _source.Project;
                }

                // Each part of a dotted name declares its namespace in this file, as in Roslyn
                var nameOffset = FindName(parts[i], nameStart, ns.Name.Span.End);
                AddLocation(symbol, ns.Span, nameOffset);
                nameStart = nameOffset + Encoding.UTF8.GetByteCount(parts[i]);
            }
            else
            {
                symbol = _builder.Get("N:" + dotted)!;
            }

            // 'namespace A.B' is 'namespace A { namespace B' with the usings in B
            imports = new ImportScope(imports, dotted);
            current = symbol;
        }

        imports.AddUsings(ns.Usings);
        var inner = new Scope(current, null, NoTypeParameters, 0, imports);
        if (_pass == CollectPass.References)
        {
            WalkUsings(ns.Usings, inner);
        }

        VisitMembers(ns.Members, inner);
    }

    private void VisitType(
        TypeDeclaration type,
        SymbolKind kind,
        string name,
        IReadOnlyList<TypeParameter>? typeParameters,
        IReadOnlyList<TypeReference>? baseTypes,
        IReadOnlyList<Parameter>? primaryConstructorParameters,
        IReadOnlyList<MemberDeclaration>? members,
        Scope scope)
    {
        var arity = typeParameters?.Count ?? 0;
        var id = "T:" + scope.Qualify(arity == 0 ? name : $"{name}`{arity}");
        if (_pass != CollectPass.Types)
        {
            var existing = _builder.Get(id)!;
            var nestedScope = NestedScope(scope, existing, typeParameters);
            if (_pass == CollectPass.Members)
            {
                _binder.RegisterTypeConstraints(existing, nestedScope.Context, TypeConstraints(type));
                AddPrimaryConstructor(type, kind, name, existing, primaryConstructorParameters, members, nestedScope);
                VisitMembers(members, nestedScope);
                return;
            }

            var savedTypeLocals = _typeLocals;
            EnterType(kind, existing, primaryConstructorParameters, nestedScope);
            WalkType(type, nestedScope, existing);
            VisitMembers(members, nestedScope);
            _typeLocals = savedTypeLocals;
            return;
        }

        var symbol = AddMember(type, kind, name, id, scope, FindName(name, AttributesEnd(type), type.Span.End));
        if (symbol == null)
        {
            return;
        }

        if (baseTypes != null)
        {
            foreach (var baseType in baseTypes)
            {
                var text = Text(baseType);
                if (!symbol.BaseTypes.Contains(text))
                {
                    symbol.BaseTypes.Add(text);
                }
            }
        }

        if (symbol.TypeParameters.Count == 0 && typeParameters != null)
        {
            symbol.TypeParameters.AddRange(typeParameters.Select(t => t.Name));
        }

        if (symbol.Parameters.Count == 0 && primaryConstructorParameters != null)
        {
            SetParameters(symbol, primaryConstructorParameters);
        }

        var keyword = kind switch
        {
            SymbolKind.Record => "record",
            SymbolKind.RecordStruct => "record struct",
            _ => kind.ToString().ToLowerInvariant(),
        };
        var parameterList = primaryConstructorParameters != null ? $"({ParameterText(primaryConstructorParameters)})" : "";
        var bases = symbol.BaseTypes.Count != 0 ? " : " + string.Join(", ", symbol.BaseTypes) : "";
        symbol.Signature = $"{keyword} {symbol.QualifiedDisplayName()}{TypeParameterText(typeParameters)}{parameterList}{bases}";

        var inner = NestedScope(scope, symbol, typeParameters);
        if (baseTypes != null)
        {
            _binder.AddBaseReferences(symbol, inner.Context, baseTypes.Select(b => (b, Text(b))));
        }

        VisitMembers(members, inner);
    }

    private static IReadOnlyList<TypeParameterConstraint>? TypeConstraints(TypeDeclaration type) => type switch
    {
        ClassDeclaration c => c.Constraints,
        StructDeclaration s => s.Constraints,
        InterfaceDeclaration i => i.Constraints,
        RecordDeclaration r => r.Constraints,
        _ => null,
    };

    /// <summary>
    /// The members a primary constructor declares: the constructor, and for a record a property for each parameter
    /// the record does not declare itself.
    /// </summary>
    private void AddPrimaryConstructor(TypeDeclaration type, SymbolKind kind, string name, CodeSymbol symbol, IReadOnlyList<Parameter>? parameters, IReadOnlyList<MemberDeclaration>? members, Scope scope)
    {
        if (parameters == null)
        {
            return;
        }

        var nameOffset = FindName(name, AttributesEnd(type), type.Span.End);
        var ctor = _builder.GetOrAdd($"M:{scope.Qualify("#ctor")}{DocumentationIds.ParameterList(parameters, scope.TypeParameters, Resolver(scope))}", SymbolKind.Constructor, name, symbol, out var created);
        if (created)
        {
            ctor.Namespace = scope.Namespace?.Id[2..];
            ctor.Project = _source.Project;
            ctor.Accessibility = kind is SymbolKind.Record or SymbolKind.RecordStruct || !type.Modifiers.HasFlag(Modifiers.Abstract) ? "public" : "protected";
            SetParameters(ctor, parameters);
            ctor.Signature = $"{name}({ParameterText(parameters)})";
            _binder.RegisterMember(ctor, scope.Context, null, parameters);
            AddLocation(ctor, type.Span, nameOffset);
        }

        if (kind is not (SymbolKind.Record or SymbolKind.RecordStruct))
        {
            return;
        }

        foreach (var parameter in parameters)
        {
            var declared = members?.Any(m => m is PropertyDeclaration { ExplicitInterface: null } p && p.Name == parameter.Name
                || m is FieldDeclaration f && f.Variables.Any(v => v.Name == parameter.Name)) ?? false;
            if (declared || string.IsNullOrEmpty(parameter.Name))
            {
                continue;
            }

            var property = _builder.GetOrAdd($"P:{scope.Qualify(parameter.Name)}", SymbolKind.Property, parameter.Name, symbol, out var added);
            if (added)
            {
                property.Namespace = scope.Namespace?.Id[2..];
                property.Project = _source.Project;
                property.Accessibility = "public";
                property.Type = Text(parameter.Type);
                var accessors = kind == SymbolKind.Record || type.Modifiers.HasFlag(Modifiers.Readonly) ? "{ get; init; }" : "{ get; set; }";
                property.Signature = $"{property.Type} {symbol.QualifiedDisplayName()}.{parameter.Name} {accessors}";
                _binder.RegisterMember(property, scope.Context, parameter.Type, null);
                AddLocation(property, parameter.Span, FindName(parameter.Name, parameter.Type?.Span.End ?? parameter.Span.Start, parameter.Span.End));
            }
        }
    }

    private static Scope NestedScope(Scope scope, CodeSymbol type, IReadOnlyList<TypeParameter>? typeParameters)
    {
        var map = new Dictionary<string, string>(scope.TypeParameters.Where(p => !p.Value.StartsWith("``", StringComparison.Ordinal)), StringComparer.Ordinal);
        var count = scope.TypeParameterCount;
        foreach (var parameter in typeParameters ?? [])
        {
            map[parameter.Name] = "`" + count++;
        }

        return new Scope(scope.Namespace, type, map, count, scope.Imports);
    }

    private void VisitEnum(EnumDeclaration e, Scope scope)
    {
        var enumId = "T:" + scope.Qualify(e.Name);
        if (_pass == CollectPass.Types)
        {
            var created = AddMember(e, SymbolKind.Enum, e.Name, enumId, scope, FindName(e.Name, AttributesEnd(e), e.Span.End));
            created.Type = e.BaseType != null ? Text(e.BaseType) : null;
            created.Signature = $"enum {created.QualifiedDisplayName()}{(created.Type != null ? " : " + created.Type : "")}";
            return;
        }

        var symbol = _builder.Get(enumId)!;
        if (_pass == CollectPass.References)
        {
            var inner = scope with { Type = symbol };
            var savedTypeLocals = _typeLocals;
            EnterType(SymbolKind.Enum, symbol, null, inner);
            WalkType(e, inner, symbol);
            foreach (var member in e.Members ?? [])
            {
                WalkNode(member, inner.Context, $"F:{symbol.Id[2..]}.{member.Name}");
            }

            _typeLocals = savedTypeLocals;
            return;
        }

        foreach (var member in e.Members ?? [])
        {
            var id = $"F:{symbol.Id[2..]}.{member.Name}";
            var field = _builder.GetOrAdd(id, SymbolKind.EnumMember, member.Name, symbol, out var created);
            if (created)
            {
                field.Accessibility = "Public";
                field.Namespace = symbol.Namespace;
                field.Project = _source.Project;
                field.Type = symbol.Name;
                field.Signature = $"{symbol.Name}.{member.Name}{(member.Value != null ? " = " + Text(member.Value) : "")}";
                field.Documentation = DocumentationComment.GetXml(Utf8, member);
            }

            var attributesEnd = member.Attributes is { Count: > 0 } attributes ? attributes[^1].Span.End : member.Span.Start;
            AddLocation(field, member.Span, FindName(member.Name, attributesEnd, member.Span.End));
        }
    }

    private void VisitDelegate(DelegateDeclaration d, Scope scope)
    {
        var arity = d.TypeParameters?.Count ?? 0;
        var id = "T:" + scope.Qualify(arity == 0 ? d.Name : $"{d.Name}`{arity}");
        if (_pass != CollectPass.Types)
        {
            if (_pass == CollectPass.References && _builder.Get(id) is { } existing)
            {
                WalkNode(d, NestedScope(scope, existing, d.TypeParameters).Context with { Type = scope.Type }, id);
            }

            return;
        }

        var symbol = AddMember(d, SymbolKind.Delegate, d.Name, id, scope, After(d.ReturnType, d));

        symbol.Type = Text(d.ReturnType);
        _binder.RegisterMember(symbol, NestedScope(scope, symbol, d.TypeParameters).Context with { Type = scope.Type }, d.ReturnType, d.Parameters);
        SetParameters(symbol, d.Parameters);
        if (symbol.TypeParameters.Count == 0 && d.TypeParameters != null)
        {
            symbol.TypeParameters.AddRange(d.TypeParameters.Select(t => t.Name));
        }

        symbol.Signature = $"delegate {symbol.Type} {symbol.QualifiedDisplayName()}{TypeParameterText(d.TypeParameters)}({ParameterText(d.Parameters)})";
    }

    private void VisitExtension(ExtensionBlockDeclaration extension, Scope scope)
    {
        // Members of a C# 14 extension block are recorded as members of the enclosing static class; the block's
        // type parameters are numbered like the enclosing type's
        var map = new Dictionary<string, string>(scope.TypeParameters, StringComparer.Ordinal);
        var count = scope.TypeParameterCount;
        foreach (var parameter in extension.TypeParameters ?? [])
        {
            map[parameter.Name] = "`" + count++;
        }

        var inner = scope with { TypeParameters = map, TypeParameterCount = count, InExtension = true };
        if (_pass == CollectPass.References)
        {
            WalkNode(extension.Receiver, inner.Context, scope.Type?.Id);
        }

        VisitMembers(extension.Members, inner);
    }

    private void VisitMethod(MethodDeclaration m, Scope scope)
    {
        var map = new Dictionary<string, string>(scope.TypeParameters, StringComparer.Ordinal);
        var arity = m.TypeParameters?.Count ?? 0;
        for (var i = 0; i < arity; i++)
        {
            map[m.TypeParameters![i].Name] = "``" + i;
        }

        var name = DocumentationIds.MemberName(m.Name, m.ExplicitInterface, scope.TypeParameters, Resolver(scope));
        var id = $"M:{scope.Qualify(name)}{(arity > 0 ? "``" + arity : "")}{DocumentationIds.ParameterList(m.Parameters, map, Resolver(scope, map))}";
        var symbol = AddMember(m, SymbolKind.Method, m.Name, id, scope, After(m.ExplicitInterface ?? m.ReturnType, m));
        LinkExplicitInterface(symbol, m.ExplicitInterface, scope);
        if (symbol == null)
        {
            return;
        }

        symbol.Type = Text(m.ReturnType);
        _binder.RegisterMember(symbol, scope.Context with { TypeParameters = map }, m.ReturnType, m.Parameters, m.Constraints);
        SetParameters(symbol, m.Parameters);
        if (symbol.TypeParameters.Count == 0 && m.TypeParameters != null)
        {
            symbol.TypeParameters.AddRange(m.TypeParameters.Select(t => t.Name));
        }

        if (m.Parameters is { Count: > 0 } parameters && parameters[0].Modifiers.Contains(ParameterModifier.This) && !symbol.Modifiers.Contains("extension"))
        {
            symbol.Modifiers.Add("extension");
        }

        symbol.Signature = $"{symbol.Type} {Owner(scope)}{InterfacePrefix(m.ExplicitInterface)}{m.Name}{TypeParameterText(m.TypeParameters)}({ParameterText(m.Parameters)})";
    }

    // ========================================
    // Symbols and locations
    // ========================================

    /// <summary>
    /// Adds a declaration of a type or member (created on its first declaration) at its name
    /// (<paramref name="nameText"/>, else <paramref name="name"/>) found from <paramref name="nameSearchStart"/>.
    /// Returns the symbol, whose details the caller fills in.
    /// </summary>
    private CodeSymbol AddMember(MemberDeclaration declaration, SymbolKind kind, string name, string id, Scope scope, int nameSearchStart, TextSpan? span = null, string? nameText = null)
    {
        var symbol = _builder.GetOrAdd(id, kind, name, scope.Container, out var created);
        if (created)
        {
            symbol.Namespace = scope.Namespace?.Id[2..];
            symbol.Project = _source.Project;
            symbol.Accessibility = Accessibility(declaration.Modifiers, kind, scope, IsExplicitImplementation(declaration));
            symbol.Modifiers.AddRange(ModifierNames(declaration.Modifiers));
            if (scope.InExtension)
            {
                symbol.Modifiers.Add("extension");
            }
        }

        symbol.Documentation ??= DocumentationComment.GetXml(Utf8, declaration);

        var declarationSpan = span ?? declaration.Span;
        var nameOffset = FindName(nameText ?? TrimTilde(name), Math.Max(nameSearchStart, AttributesEnd(declaration)), declarationSpan.End);
        AddLocation(symbol, declarationSpan, nameOffset);
        return symbol;
    }

    /// <summary>Binds the interface of an explicit interface implementation, for matching it with the interface member.</summary>
    private void LinkExplicitInterface(CodeSymbol symbol, TypeReference? explicitInterface, Scope scope)
    {
        if (explicitInterface is NamedTypeReference named && symbol.ExplicitInterface == null)
        {
            var bound = _binder.BindType(named, scope.Context);
            symbol.ExplicitInterface = new TypeLink(Text(named), bound?.Symbol, bound?.Id ?? Text(named), bound?.OwnArguments ?? []);
        }
    }

    private static string TrimTilde(string name) => name.StartsWith('~') ? name[1..] : name;

    private void AddLocation(CodeSymbol symbol, TextSpan span, int nameOffset)
    {
        var (line, column) = _source.Lines.GetLineAndColumn(nameOffset);
        _builder.AddDeclaration(symbol, new SourceLocation(_source.Path, span.Start, span.End, line, column));
    }

    /// <summary>The end of the attribute sections of a declaration, or its start.</summary>
    private static int AttributesEnd(MemberDeclaration declaration) =>
        declaration.Attributes is { Count: > 0 } attributes ? attributes[^1].Span.End : declaration.Span.Start;

    /// <summary>Where to look for the name of a member: after its type (or explicit interface), else after its attributes.</summary>
    private static int After(TypeReference? type, MemberDeclaration declaration) =>
        type != null && type.Span.End > 0 ? type.Span.End : AttributesEnd(declaration);

    /// <summary>
    /// The offset of the identifier <paramref name="name"/> (possibly written with '@') between
    /// <paramref name="start"/> and <paramref name="end"/>, or <paramref name="start"/> when it is not there.
    /// </summary>
    private int FindName(string name, int start, int end)
    {
        var source = Utf8;
        end = Math.Min(end, source.Length);
        if (start >= end || name.Length == 0)
        {
            return Math.Min(start, source.Length);
        }

        var bytes = Encoding.UTF8.GetBytes(name);
        var region = source[start..end];
        var from = 0;
        while (true)
        {
            var index = region[from..].IndexOf(bytes);
            if (index < 0)
            {
                return start;
            }

            index += from;
            var before = index > 0 ? region[index - 1] : (byte)' ';
            var after = index + bytes.Length < region.Length ? region[index + bytes.Length] : (byte)' ';
            if (!IsIdentifierByte(before) && !IsIdentifierByte(after))
            {
                return start + (before == '@' ? index - 1 : index);
            }

            from = index + 1;
        }
    }

    private static bool IsIdentifierByte(byte b) => b == '_' || b >= 0x80 || char.IsAsciiLetterOrDigit((char)b);

    // ========================================
    // Text
    // ========================================

    /// <summary>The source text of a node with runs of whitespace collapsed to one space.</summary>
    private string Text(CSharpNode? node)
    {
        if (node == null || node.Span.End <= node.Span.Start || node.Span.End > Utf8.Length)
        {
            return "";
        }

        var text = node.Span.GetText(Utf8);
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    private void SetParameters(CodeSymbol symbol, IReadOnlyList<Parameter>? parameters)
    {
        if (symbol.Parameters.Count != 0 || parameters == null)
        {
            return;
        }

        foreach (var p in parameters)
        {
            var modifier = p.Modifiers.Count == 0 ? null : string.Join(" ", p.Modifiers.Select(m => m.ToString().ToLowerInvariant()));
            symbol.Parameters.Add(new ParameterInfo(p.Name, p.Type != null ? Text(p.Type) : null, modifier, p.DefaultValue != null ? Text(p.DefaultValue) : null));
        }
    }

    private string ParameterText(IReadOnlyList<Parameter>? parameters)
    {
        if (parameters == null)
        {
            return "";
        }

        return string.Join(", ", parameters.Select(p =>
        {
            var builder = new StringBuilder();
            foreach (var modifier in p.Modifiers)
            {
                builder.Append(modifier.ToString().ToLowerInvariant()).Append(' ');
            }

            if (p.Type != null)
            {
                builder.Append(Text(p.Type)).Append(' ');
            }

            builder.Append(p.Name);
            if (p.DefaultValue != null)
            {
                builder.Append(" = ").Append(Text(p.DefaultValue));
            }

            return builder.ToString();
        }));
    }

    private static string TypeParameterText(IReadOnlyList<TypeParameter>? typeParameters) =>
        typeParameters is { Count: > 0 } ? "<" + string.Join(", ", typeParameters.Select(t => t.Name)) + ">" : "";

    private static string Owner(Scope scope) => scope.Type is { } type ? type.QualifiedDisplayName() + "." : "";

    private string InterfacePrefix(TypeReference? explicitInterface) => explicitInterface != null ? Text(explicitInterface) + "." : "";

    private static string AccessorText(IReadOnlyList<Accessor>? accessors, bool hasExpressionBody)
    {
        if (accessors is not { Count: > 0 })
        {
            return hasExpressionBody ? "{ get; }" : "";
        }

        return "{ " + string.Concat(accessors.Select(a => a.Kind.ToString().ToLowerInvariant() + "; ")) + "}";
    }

    // ========================================
    // Modifiers
    // ========================================

    private static bool IsExplicitImplementation(MemberDeclaration declaration) => declaration switch
    {
        MethodDeclaration { ExplicitInterface: not null } => true,
        PropertyDeclaration { ExplicitInterface: not null } => true,
        IndexerDeclaration { ExplicitInterface: not null } => true,
        EventDeclaration { ExplicitInterface: not null } => true,
        OperatorDeclaration { ExplicitInterface: not null } => true,
        ConversionOperatorDeclaration { ExplicitInterface: not null } => true,
        _ => false,
    };

    /// <summary>Accessibility names as in Roslyn's <c>Accessibility</c> enum.</summary>
    private static string Accessibility(Modifiers modifiers, SymbolKind kind, Scope scope, bool isExplicitImplementation)
    {
        var isPublic = modifiers.HasFlag(Modifiers.Public);
        var isPrivate = modifiers.HasFlag(Modifiers.Private);
        var isProtected = modifiers.HasFlag(Modifiers.Protected);
        var isInternal = modifiers.HasFlag(Modifiers.Internal);

        if (isPublic)
        {
            return "Public";
        }

        if (isProtected && isInternal)
        {
            return "ProtectedOrInternal";
        }

        if (isPrivate && isProtected)
        {
            return "ProtectedAndInternal";
        }

        if (isProtected)
        {
            return "Protected";
        }

        if (isInternal)
        {
            return "Internal";
        }

        if (isPrivate || isExplicitImplementation)
        {
            return "Private";
        }

        // Defaults: top-level types are internal, interface members public, other members private
        if (scope.Type is null)
        {
            return "Internal";
        }

        if (scope.Type.Kind == SymbolKind.Interface || kind == SymbolKind.EnumMember)
        {
            return "Public";
        }

        // Operators must be public; static constructors and destructors have no accessibility modifiers
        return kind is SymbolKind.Operator ? "Public" : "Private";
    }

    private static readonly (Modifiers Flag, string Name)[] ModifierOrder =
    [
        (Modifiers.New, "new"),
        (Modifiers.Static, "static"),
        (Modifiers.Abstract, "abstract"),
        (Modifiers.Virtual, "virtual"),
        (Modifiers.Override, "override"),
        (Modifiers.Sealed, "sealed"),
        (Modifiers.Extern, "extern"),
        (Modifiers.Async, "async"),
        (Modifiers.Readonly, "readonly"),
        (Modifiers.Const, "const"),
        (Modifiers.Volatile, "volatile"),
        (Modifiers.Unsafe, "unsafe"),
        (Modifiers.Partial, "partial"),
        (Modifiers.Required, "required"),
        (Modifiers.Ref, "ref"),
        (Modifiers.Fixed, "fixed"),
        (Modifiers.File, "file"),
    ];

    private static IEnumerable<string> ModifierNames(Modifiers modifiers) =>
        ModifierOrder.Where(m => modifiers.HasFlag(m.Flag)).Select(m => m.Name);
}

internal static class CodeSymbolDisplay
{
    /// <summary>The type names from the outermost containing type: <c>Outer.Inner</c>.</summary>
    public static string QualifiedDisplayName(this CodeSymbol symbol) =>
        symbol.ContainingType is { } container ? $"{container.QualifiedDisplayName()}.{symbol.Name}" : symbol.Name;
}
