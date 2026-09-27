using System.Text;
using PaspanParsers.CSharp;

namespace PaspanCodeGraph.CSharp;

/// <summary>
/// Builds documentation-comment ids (the format of <c>DocumentationCommentId</c> in Roslyn and of the
/// <c>cref</c>s in XML docs) from syntax. Without binding, a named type in a parameter list is written as in
/// source (<c>List{System.Int32}</c>), where Roslyn writes its full name
/// (<c>System.Collections.Generic.List{System.Int32}</c>); predefined types and type parameters are exact.
/// </summary>
/// <summary>
/// The id form of the type a named type reference refers to (<c>Ns.Outer{System.Int32}.Inner</c>), when it is
/// known; else null, and the name is written as in source.
/// </summary>
public delegate string? TypeResolver(NamedTypeReference type);

public static class DocumentationIds
{
    private static readonly Dictionary<PredefinedType, string> PredefinedNames = new()
    {
        [PredefinedType.Object] = "System.Object",
        [PredefinedType.String] = "System.String",
        [PredefinedType.Bool] = "System.Boolean",
        [PredefinedType.Byte] = "System.Byte",
        [PredefinedType.SByte] = "System.SByte",
        [PredefinedType.Short] = "System.Int16",
        [PredefinedType.UShort] = "System.UInt16",
        [PredefinedType.Int] = "System.Int32",
        [PredefinedType.UInt] = "System.UInt32",
        [PredefinedType.Long] = "System.Int64",
        [PredefinedType.ULong] = "System.UInt64",
        [PredefinedType.Float] = "System.Single",
        [PredefinedType.Double] = "System.Double",
        [PredefinedType.Decimal] = "System.Decimal",
        [PredefinedType.Char] = "System.Char",
        [PredefinedType.Void] = "System.Void",
        [PredefinedType.Dynamic] = "System.Object",
    };

    /// <summary>Contextual type keywords that name predefined types.</summary>
    private static readonly Dictionary<string, string> ContextualNames = new(StringComparer.Ordinal)
    {
        ["nint"] = "System.IntPtr",
        ["nuint"] = "System.UIntPtr",
        ["dynamic"] = "System.Object",
    };

    /// <summary>The metadata name of an operator: <c>op_Addition</c> for binary <c>+</c>.</summary>
    public static string OperatorName(string op, int parameterCount, bool isChecked)
    {
        var name = (op, parameterCount) switch
        {
            ("+", 1) => "UnaryPlus",
            ("-", 1) => "UnaryNegation",
            ("+", _) => "Addition",
            ("-", _) => "Subtraction",
            ("*", _) => "Multiply",
            ("/", _) => "Division",
            ("%", _) => "Modulus",
            ("&", _) => "BitwiseAnd",
            ("|", _) => "BitwiseOr",
            ("^", _) => "ExclusiveOr",
            ("<<", _) => "LeftShift",
            (">>", _) => "RightShift",
            (">>>", _) => "UnsignedRightShift",
            ("==", _) => "Equality",
            ("!=", _) => "Inequality",
            ("<", _) => "LessThan",
            (">", _) => "GreaterThan",
            ("<=", _) => "LessThanOrEqual",
            (">=", _) => "GreaterThanOrEqual",
            ("!", _) => "LogicalNot",
            ("~", _) => "OnesComplement",
            ("++", _) => "Increment",
            ("--", _) => "Decrement",
            ("true", _) => "True",
            ("false", _) => "False",
            ("+=", _) => "AdditionAssignment",
            ("-=", _) => "SubtractionAssignment",
            ("*=", _) => "MultiplicationAssignment",
            ("/=", _) => "DivisionAssignment",
            ("%=", _) => "ModulusAssignment",
            ("&=", _) => "BitwiseAndAssignment",
            ("|=", _) => "BitwiseOrAssignment",
            ("^=", _) => "ExclusiveOrAssignment",
            ("<<=", _) => "LeftShiftAssignment",
            (">>=", _) => "RightShiftAssignment",
            (">>>=", _) => "UnsignedRightShiftAssignment",
            _ => op,
        };

        return isChecked ? $"op_Checked{name}" : $"op_{name}";
    }

    /// <summary>
    /// The id form of a type in a parameter list. <paramref name="typeParameters"/> maps the names of type
    /// parameters in scope to their id form (<c>`0</c> for a type's, <c>``0</c> for a method's).
    /// </summary>
    public static string TypeId(TypeReference type, IReadOnlyDictionary<string, string> typeParameters, TypeResolver? resolveType = null)
    {
        var builder = new StringBuilder();
        AppendType(builder, type, typeParameters, resolveType);
        return builder.ToString();
    }

    /// <summary>Appends the id form of a type argument list: <c>{System.Int32,`0}</c>.</summary>
    public static string TypeArgumentList(IReadOnlyList<TypeReference> arguments, IReadOnlyDictionary<string, string> typeParameters, TypeResolver? resolveType)
    {
        var builder = new StringBuilder("{");
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i != 0)
            {
                builder.Append(',');
            }

            AppendType(builder, arguments[i], typeParameters, resolveType);
        }

        return builder.Append('}').ToString();
    }

    private static void AppendType(StringBuilder builder, TypeReference type, IReadOnlyDictionary<string, string> typeParameters, TypeResolver? resolveType)
    {
        switch (type)
        {
            case PredefinedTypeReference predefined:
                if (predefined.IsNullable && predefined.Type is not (PredefinedType.Object or PredefinedType.String or PredefinedType.Dynamic))
                {
                    builder.Append("System.Nullable{").Append(PredefinedNames[predefined.Type]).Append('}');
                }
                else
                {
                    builder.Append(PredefinedNames[predefined.Type]);
                }

                break;

            case NamedTypeReference named:
                AppendNamed(builder, named, typeParameters, resolveType);
                break;

            case ArrayTypeReference array:
                AppendType(builder, array.ElementType, typeParameters, resolveType);
                builder.Append(array.Rank == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", array.Rank)) + "]");
                break;

            case NullableTypeReference nullable:
                // T? is Nullable<T> only for value types; without binding, only predefined value types are known
                if (nullable.ElementType is PredefinedTypeReference { Type: not (PredefinedType.Object or PredefinedType.String or PredefinedType.Dynamic) })
                {
                    builder.Append("System.Nullable{");
                    AppendType(builder, nullable.ElementType, typeParameters, resolveType);
                    builder.Append('}');
                }
                else
                {
                    AppendType(builder, nullable.ElementType, typeParameters, resolveType);
                }

                break;

            case PointerTypeReference pointer:
                AppendType(builder, pointer.ElementType, typeParameters, resolveType);
                builder.Append('*');
                break;

            case TupleTypeReference tuple:
                AppendTuple(builder, tuple.Elements.Select(e => e.Type).ToList(), 0, typeParameters, resolveType);
                break;

            case RefTypeReference reference:
                AppendType(builder, reference.Type, typeParameters, resolveType);
                break;

            case ScopedTypeReference scoped:
                AppendType(builder, scoped.Type, typeParameters, resolveType);
                break;

            case FunctionPointerTypeReference:
                builder.Append("=FUNC");
                break;

            default:
                builder.Append('?');
                break;
        }
    }

    /// <summary>
    /// <c>System.ValueTuple{...}</c>; the elements after the seventh go in a nested tuple, as in
    /// <c>ValueTuple&lt;T1, ..., T7, TRest&gt;</c>.
    /// </summary>
    private static void AppendTuple(StringBuilder builder, List<TypeReference> elements, int start, IReadOnlyDictionary<string, string> typeParameters, TypeResolver? resolveType)
    {
        builder.Append("System.ValueTuple{");
        var count = Math.Min(elements.Count - start, 7);
        for (var i = 0; i < count; i++)
        {
            if (i != 0)
            {
                builder.Append(',');
            }

            AppendType(builder, elements[start + i], typeParameters, resolveType);
        }

        if (elements.Count - start > 7)
        {
            builder.Append(',');
            AppendTuple(builder, elements, start + 7, typeParameters, resolveType);
        }

        builder.Append('}');
    }

    private static void AppendNamed(StringBuilder builder, NamedTypeReference named, IReadOnlyDictionary<string, string> typeParameters, TypeResolver? resolveType)
    {
        if (named.Qualifier == null && named.TypeArguments == null && named.Name.Parts.Count == 1)
        {
            if (typeParameters.TryGetValue(named.Name.Parts[0], out var parameter))
            {
                // T? of an unconstrained or reference type parameter has the id of T
                builder.Append(parameter);
                return;
            }
        }

        if (resolveType?.Invoke(named) is { } resolved)
        {
            builder.Append(resolved);
            return;
        }

        if (named.Qualifier != null)
        {
            AppendType(builder, named.Qualifier, typeParameters, resolveType);
            builder.Append('.');
        }

        var parts = named.Name.Parts;
        if (named.Qualifier == null && named.TypeArguments == null && parts.Count == 1)
        {
            if (ContextualNames.TryGetValue(parts[0], out var contextual))
            {
                builder.Append(contextual);
                return;
            }
        }

        builder.Append(string.Join(".", parts));
        if (named.TypeArguments is { Count: > 0 } arguments)
        {
            builder.Append('{');
            for (var i = 0; i < arguments.Count; i++)
            {
                if (i != 0)
                {
                    builder.Append(',');
                }

                AppendType(builder, arguments[i], typeParameters, resolveType);
            }

            builder.Append('}');
        }
    }

    /// <summary>
    /// The parameter list part of a member id: <c>(System.Int32,System.String@)</c>, or empty without parameters.
    /// </summary>
    public static string ParameterList(IReadOnlyList<Parameter>? parameters, IReadOnlyDictionary<string, string> typeParameters, TypeResolver? resolveType = null)
    {
        if (parameters is not { Count: > 0 })
        {
            return "";
        }

        var builder = new StringBuilder("(");
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i != 0)
            {
                builder.Append(',');
            }

            var parameter = parameters[i];
            if (parameter.Type is null)
            {
                builder.Append('?');
                continue;
            }

            AppendType(builder, parameter.Type, typeParameters, resolveType);
            if (parameter.Modifiers.Any(m => m is ParameterModifier.Ref or ParameterModifier.Out or ParameterModifier.In)
                || parameter.Type is RefTypeReference)
            {
                builder.Append('@');
            }
        }

        return builder.Append(')').ToString();
    }

    /// <summary>A member name as it appears in an id: the '.'s of an explicit interface name become '#'.</summary>
    public static string MemberName(string name, TypeReference? explicitInterface, IReadOnlyDictionary<string, string> typeParameters, TypeResolver? resolveType = null)
    {
        if (explicitInterface is null)
        {
            return name;
        }

        // Type parameters are written by name there, as in source: "Ns#IConvert{T,System#String}#Convert"
        var names = typeParameters.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        var id = TypeId(explicitInterface, typeParameters, resolveType);
        var prefix = System.Text.RegularExpressions.Regex.Replace(id, @"(?<!`)`\d+", m => names.GetValueOrDefault(m.Value, m.Value)).Replace('.', '#');
        return $"{prefix}#{name}";
    }
}
