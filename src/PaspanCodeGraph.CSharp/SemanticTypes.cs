namespace PaspanCodeGraph.CSharp;

/// <summary>
/// The type of an expression or a declaration as far as it can be known without referenced assemblies:
/// workspace types with their type arguments, types from references by the name they were written with, type
/// parameters, arrays, tuples and nullables.
/// </summary>
public abstract record SemType
{
    public static readonly SemType Unknown = new UnknownType();
    public static readonly SemType Null = new NullType();
    public static readonly SemType Void = new ExternalType("Void", "System.Void", []);

    /// <summary>The type without a <see cref="NullableType"/> wrapper.</summary>
    public SemType Underlying => this is NullableType nullable ? nullable.Element : this;

    public bool IsUnknown => this is UnknownType;
}

/// <summary>A type that could not be inferred.</summary>
public sealed record UnknownType : SemType
{
    public override string ToString() => "?";
}

/// <summary>The type of the <c>null</c> literal.</summary>
public sealed record NullType : SemType
{
    public override string ToString() => "null";
}

/// <summary>A workspace type; <paramref name="Arguments"/> are those of every generic level, outermost first.</summary>
public sealed record NamedType(CodeSymbol Symbol, IReadOnlyList<SemType> Arguments) : SemType
{
    public bool Equals(NamedType? other) => other is not null && other.Symbol == Symbol && SemTypes.SameList(Arguments, other.Arguments);

    public override int GetHashCode() => Symbol.GetHashCode();

    public override string ToString() => Arguments.Count == 0 ? Symbol.Name : $"{Symbol.Name}<{string.Join(", ", Arguments)}>";
}

/// <summary>A type from a referenced assembly: its simple name (<c>List</c>), the name as written or known in full, and its type arguments.</summary>
public sealed record ExternalType(string Name, string FullName, IReadOnlyList<SemType> Arguments) : SemType
{
    public bool Equals(ExternalType? other) => other is not null && other.Name == Name && SemTypes.SameList(Arguments, other.Arguments);

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Arguments.Count == 0 ? FullName : $"{FullName}<{string.Join(", ", Arguments)}>";
}

/// <summary>
/// A type parameter: <paramref name="Ordinal"/> is its position in its owner's id form (<c>`N</c> across the
/// containing types for a type's, <c>``N</c> for a method's), <paramref name="Owner"/> the type or method declaring it.
/// </summary>
public sealed record TypeParameterType(string Name, int Ordinal, bool IsMethod, CodeSymbol? Owner) : SemType
{
    public override string ToString() => Name;
}

public sealed record ArrayType(SemType Element, int Rank) : SemType
{
    public override string ToString() => $"{Element}[{new string(',', Rank - 1)}]";
}

public sealed record TupleType(IReadOnlyList<SemType> Elements, IReadOnlyList<string?> Names) : SemType
{
    public bool Equals(TupleType? other) => other is not null && SemTypes.SameList(Elements, other.Elements);

    public override int GetHashCode() => Elements.Count;

    public override string ToString() => $"({string.Join(", ", Elements)})";
}

/// <summary><c>T?</c>: a nullable value type, or an annotated reference type.</summary>
public sealed record NullableType(SemType Element) : SemType
{
    public override string ToString() => Element + "?";
}

/// <summary>An expression that names a type, as the target of a static member access.</summary>
public sealed record TypeExpressionType(SemType Type) : SemType;

/// <summary>An expression that names a namespace.</summary>
public sealed record NamespaceExpressionType(string Namespace) : SemType;

/// <summary>A lambda or anonymous method, whose type comes from where it is converted to.</summary>
public sealed record LambdaType(int ParameterCount) : SemType;

public static class SemTypes
{
    private static readonly Dictionary<string, string> PredefinedNames = new(StringComparer.Ordinal)
    {
        ["Object"] = "object", ["String"] = "string", ["Boolean"] = "bool", ["Byte"] = "byte", ["SByte"] = "sbyte",
        ["Int16"] = "short", ["UInt16"] = "ushort", ["Int32"] = "int", ["UInt32"] = "uint", ["Int64"] = "long",
        ["UInt64"] = "ulong", ["Single"] = "float", ["Double"] = "double", ["Decimal"] = "decimal", ["Char"] = "char",
        ["Void"] = "void", ["IntPtr"] = "nint", ["UIntPtr"] = "nuint",
    };

    public static ExternalType Predefined(string name) => new(name, "System." + name, []);

    public static readonly ExternalType Object = Predefined("Object");
    public static readonly ExternalType String = Predefined("String");
    public static readonly ExternalType Boolean = Predefined("Boolean");
    public static readonly ExternalType Int32 = Predefined("Int32");
    public static readonly ExternalType Int64 = Predefined("Int64");
    public static readonly ExternalType Double = Predefined("Double");
    public static readonly ExternalType Char = Predefined("Char");
    public static readonly ExternalType SystemType = Predefined("Type");

    /// <summary>Whether a type is one of the types with a C# keyword (<c>int</c>, <c>string</c>...).</summary>
    public static bool IsPredefined(SemType type) => type is ExternalType { Arguments.Count: 0 } external && PredefinedNames.ContainsKey(external.Name);

    public static bool SameList(IReadOnlyList<SemType> a, IReadOnlyList<SemType> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!Equals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a type mentions a type or method type parameter (so that substituting it can change it).</summary>
    public static bool HasTypeParameters(SemType type) => type switch
    {
        TypeParameterType => true,
        NamedType named => named.Arguments.Any(HasTypeParameters),
        ExternalType external => external.Arguments.Any(HasTypeParameters),
        ArrayType array => HasTypeParameters(array.Element),
        TupleType tuple => tuple.Elements.Any(HasTypeParameters),
        NullableType nullable => HasTypeParameters(nullable.Element),
        _ => false,
    };

    /// <summary>Replaces the type parameters <paramref name="map"/> knows.</summary>
    public static SemType Substitute(SemType type, Func<TypeParameterType, SemType?> map)
    {
        switch (type)
        {
            case TypeParameterType parameter:
                return map(parameter) ?? parameter;
            case NamedType named when named.Arguments.Count > 0:
                return new NamedType(named.Symbol, named.Arguments.Select(a => Substitute(a, map)).ToList());
            case ExternalType external when external.Arguments.Count > 0:
                return external with { Arguments = external.Arguments.Select(a => Substitute(a, map)).ToList() };
            case ArrayType array:
                return array with { Element = Substitute(array.Element, map) };
            case TupleType tuple:
                return tuple with { Elements = tuple.Elements.Select(e => Substitute(e, map)).ToList() };
            case NullableType nullable:
                return new NullableType(Substitute(nullable.Element, map));
            default:
                return type;
        }
    }

    // A small model of the base class library, until referenced assemblies are read: what a foreach, an
    // indexer, an await or a LINQ operator gives for the collection types everyone uses

    private static readonly HashSet<string> Sequences = new(StringComparer.Ordinal)
    {
        "IEnumerable", "ICollection", "IList", "IReadOnlyCollection", "IReadOnlyList", "List", "HashSet", "SortedSet", "ISet",
        "IReadOnlySet", "Queue", "Stack", "LinkedList", "ImmutableArray", "ImmutableList", "IImmutableList", "ImmutableHashSet",
        "Span", "ReadOnlySpan", "Memory", "ReadOnlyMemory", "IAsyncEnumerable", "ConcurrentBag", "ConcurrentQueue",
        "ConcurrentStack", "BlockingCollection", "IOrderedEnumerable", "IQueryable", "IOrderedQueryable", "Collection",
        "ReadOnlyCollection", "ObservableCollection", "ArraySegment", "IGrouping", "ILookup", "FrozenSet", "ImmutableQueue",
        "ImmutableStack", "ImmutableSortedSet", "IProducerConsumerCollection", "PriorityQueue",
    };

    private static readonly HashSet<string> Dictionaries = new(StringComparer.Ordinal)
    {
        "Dictionary", "IDictionary", "IReadOnlyDictionary", "SortedDictionary", "SortedList", "ConcurrentDictionary",
        "ImmutableDictionary", "IImmutableDictionary", "ImmutableSortedDictionary", "FrozenDictionary",
    };

    /// <summary>The element type of a sequence (what <c>foreach</c> gives), from the base class library model; null when not known.</summary>
    public static SemType? ElementOf(SemType type)
    {
        switch (type.Underlying)
        {
            case ArrayType array:
                return array.Rank == 1 ? array.Element : array.Element;
            case ExternalType { Name: "String", Arguments.Count: 0 }:
                return Char;
            case ExternalType external when external.Arguments.Count == 1 && Sequences.Contains(external.Name):
                return external.Name == "IGrouping" ? external.Arguments[0] : external.Arguments[0];
            case ExternalType { Name: "IGrouping" or "ILookup", Arguments.Count: 2 } grouping:
                return grouping.Name == "IGrouping" ? grouping.Arguments[1] : new ExternalType("IGrouping", "System.Linq.IGrouping", grouping.Arguments);
            case ExternalType external when external.Arguments.Count == 2 && Dictionaries.Contains(external.Name):
                return new ExternalType("KeyValuePair", "System.Collections.Generic.KeyValuePair", external.Arguments);
            default:
                return null;
        }
    }

    /// <summary>What an indexer of a type from the base class library model gives.</summary>
    public static SemType? IndexedBy(SemType type)
    {
        switch (type.Underlying)
        {
            case ArrayType array:
                return array.Element;
            case ExternalType { Name: "String", Arguments.Count: 0 }:
                return Char;
            case ExternalType external when external.Arguments.Count == 2 && Dictionaries.Contains(external.Name):
                return external.Arguments[1];
            case ExternalType external when external.Arguments.Count == 1 && Sequences.Contains(external.Name) && external.Name is not ("IEnumerable" or "ICollection" or "IReadOnlyCollection" or "HashSet" or "ISet" or "Queue" or "Stack"):
                return external.Arguments[0];
            default:
                return null;
        }
    }

    /// <summary>What <c>await</c> gives for a task type; null when not a known task type.</summary>
    public static SemType? Awaited(SemType type) => type.Underlying switch
    {
        ExternalType { Name: "Task" or "ValueTask", Arguments.Count: 1 } task => task.Arguments[0],
        ExternalType { Name: "Task" or "ValueTask", Arguments.Count: 0 } => SemType.Void,
        _ => null,
    };

    /// <summary>The type of a property of a type from the base class library model; null when not known.</summary>
    public static SemType? KnownProperty(SemType type, string name)
    {
        var underlying = type.Underlying;
        switch (name)
        {
            case "Count" or "Length" when underlying is ArrayType || ElementOf(underlying) != null || underlying is ExternalType { Name: "String" }:
                return Int32;
            case "Key" when underlying is ExternalType { Name: "KeyValuePair" or "IGrouping", Arguments.Count: 2 } pair:
                return pair.Arguments[0];
            case "Value" when underlying is ExternalType { Name: "KeyValuePair", Arguments.Count: 2 } pair:
                return pair.Arguments[1];
            case "Value" when type is NullableType nullable:
                return nullable.Element;
            case "Value" when underlying is ExternalType { Name: "Lazy" or "Nullable" or "StrongBox" or "AsyncLocal" or "ThreadLocal", Arguments.Count: 1 } box:
                return box.Arguments[0];
            case "Result" when underlying is ExternalType { Name: "Task" or "ValueTask", Arguments.Count: 1 } task:
                return task.Arguments[0];
            case "Keys" when underlying is ExternalType { Arguments.Count: 2 } map && Dictionaries.Contains(map.Name):
                return new ExternalType("IEnumerable", "System.Collections.Generic.IEnumerable", [map.Arguments[0]]);
            case "Values" when underlying is ExternalType { Arguments.Count: 2 } map && Dictionaries.Contains(map.Name):
                return new ExternalType("IEnumerable", "System.Collections.Generic.IEnumerable", [map.Arguments[1]]);
            case "Item1" or "Item2" or "Item3" or "Item4" or "Item5" or "Item6" or "Item7" when underlying is TupleType tuple:
            {
                var index = name[^1] - '1';
                return index < tuple.Elements.Count ? tuple.Elements[index] : null;
            }

            default:
                if (underlying is TupleType named && named.Names.ToList().IndexOf(name) is var position and >= 0)
                {
                    return named.Elements[position];
                }

                return null;
        }
    }

    /// <summary>
    /// A LINQ or collection method of the base class library model: the type its lambdas get as their first
    /// parameter and what it returns given the lambda's result; null when the method is not modeled.
    /// </summary>
    public static (SemType? LambdaParameter, Func<SemType?, SemType> Result)? KnownMethod(SemType receiver, string name)
    {
        var element = ElementOf(receiver);
        if (element == null)
        {
            if (receiver.Underlying is ExternalType { Name: "Task" or "ValueTask" } task && name == "ConfigureAwait")
            {
                return (null, _ => new ExternalType("ConfiguredTaskAwaitable", "ConfiguredTaskAwaitable", task.Arguments));
            }

            return null;
        }

        SemType Sequence(SemType of) => new ExternalType("IEnumerable", "System.Collections.Generic.IEnumerable", [of]);
        return name switch
        {
            "Where" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Distinct" or "DistinctBy" or "Skip"
                or "Take" or "SkipWhile" or "TakeWhile" or "Reverse" or "Concat" or "Union" or "Intersect" or "Except" or "AsEnumerable"
                or "SkipLast" or "TakeLast" or "Append" or "Prepend" or "UnionBy" or "IntersectBy" or "ExceptBy" or "Order" or "OrderDescending"
                or "DefaultIfEmpty" => (element, _ => Sequence(element)),
            "Select" => (element, result => Sequence(result ?? SemType.Unknown)),
            "SelectMany" => (element, result => Sequence(result != null ? ElementOf(result) ?? SemType.Unknown : SemType.Unknown)),
            "First" or "FirstOrDefault" or "Last" or "LastOrDefault" or "Single" or "SingleOrDefault" or "ElementAt" or "ElementAtOrDefault"
                or "MinBy" or "MaxBy" or "Find" or "FindLast" => (element, _ => element),
            "Min" or "Max" or "Sum" or "Average" => (element, result => result ?? element),
            "Any" or "All" or "Contains" or "Exists" or "TrueForAll" or "SequenceEqual" or "Remove" => (element, _ => Boolean),
            "Count" or "LongCount" or "FindIndex" or "FindLastIndex" or "IndexOf" or "RemoveAll" => (element, _ => Int32),
            "ToList" => (element, _ => new ExternalType("List", "System.Collections.Generic.List", [element])),
            "ToArray" => (element, _ => new ArrayType(element, 1)),
            "ToHashSet" => (element, _ => new ExternalType("HashSet", "System.Collections.Generic.HashSet", [element])),
            "ToDictionary" => (element, result => new ExternalType("Dictionary", "System.Collections.Generic.Dictionary", [result ?? SemType.Unknown, element])),
            "ToLookup" or "GroupBy" => (element, result => Sequence(new ExternalType("IGrouping", "System.Linq.IGrouping", [result ?? SemType.Unknown, element]))),
            "ForEach" or "Add" or "Insert" or "Clear" or "Sort" or "AddRange" or "RemoveAt" => (element, _ => SemType.Void),
            "Aggregate" or "Zip" or "Join" or "GroupJoin" or "Cast" or "OfType" => (element, _ => SemType.Unknown),
            _ => null,
        };
    }
}
