using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PaspanCodeGraph.Metadata;

/// <summary>
/// The public types of a set of assemblies (reference assemblies of a framework and NuGet packages), read with
/// System.Reflection.Metadata. Types are indexed when loaded; their members are read on first use. An assembly
/// name is read once, the highest version winning. Safe to use from several threads.
/// </summary>
public sealed class MetadataCatalog
{
    private readonly Dictionary<string, MetadataType> _types = new(StringComparer.Ordinal);
    private readonly HashSet<string> _namespaces = new(StringComparer.Ordinal);
    private readonly List<MetadataType> _extensionClasses = [];
    private readonly List<string> _assemblies = [];
    private readonly List<PEReader> _peReaders = [];
    private Dictionary<string, List<MetadataMember>>? _extensionMethods;
    private readonly object _lock = new();

    public static MetadataCatalog Empty { get; } = new();

    /// <summary>The paths of the assemblies read.</summary>
    public IReadOnlyList<string> Assemblies => _assemblies;

    public int TypeCount => _types.Count;

    /// <summary>Reads the assemblies at <paramref name="paths"/>; unreadable files go to <paramref name="problems"/>.</summary>
    public static MetadataCatalog Load(IEnumerable<string> paths, List<string>? problems = null)
    {
        var catalog = new MetadataCatalog();

        // One file per assembly name, the highest version
        var byName = new Dictionary<string, (string Path, Version Version, PEReader PE, MetadataReader Reader)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                // Only the metadata is kept in memory
                using var stream = File.OpenRead(path);
                var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
                if (!pe.HasMetadata || pe.GetMetadataReader() is not { IsAssembly: true } reader)
                {
                    pe.Dispose();
                    continue;
                }

                var definition = reader.GetAssemblyDefinition();
                var name = reader.GetString(definition.Name);
                if (!byName.TryGetValue(name, out var existing) || existing.Version < definition.Version)
                {
                    existing.PE?.Dispose();
                    byName[name] = (path, definition.Version, pe, reader);
                }
                else
                {
                    pe.Dispose();
                }
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems?.Add($"Cannot read {path}: {e.Message}");
            }
        }

        foreach (var (name, (path, _, pe, reader)) in byName.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            // The metadata reader reads the memory the PE reader holds, for as long as the catalog lives
            catalog._peReaders.Add(pe);
            catalog._assemblies.Add(path);
            new AssemblyReader(catalog, reader, name).IndexTypes();
        }

        return catalog;
    }

    /// <summary>Whether <paramref name="name"/> (dotted) is a namespace of the assemblies, or a prefix of one.</summary>
    public bool IsNamespace(string name) => _namespaces.Contains(name);

    /// <summary>A top-level type by namespace, name and arity.</summary>
    public MetadataType? FindType(string ns, string name, int arity) =>
        _types.GetValueOrDefault((ns.Length == 0 ? "" : ns + ".") + (arity == 0 ? name : $"{name}`{arity}"));

    /// <summary>A type by its key: <c>System.Collections.Generic.List`1</c>, <c>Outer`1.Inner</c> for nested types.</summary>
    public MetadataType? FindType(string key)
    {
        if (_types.TryGetValue(key, out var type))
        {
            return type;
        }

        // A nested type: find the outermost type, then the nested ones
        var dot = key.Length;
        while ((dot = key.LastIndexOf('.', dot - 1)) > 0)
        {
            if (_types.TryGetValue(key[..dot], out var outer))
            {
                foreach (var part in key[(dot + 1)..].Split('.'))
                {
                    var (name, arity) = SplitArity(part);
                    outer = outer?.NestedTypes.FirstOrDefault(n => n.Name == name && n.Arity == arity);
                }

                return outer;
            }
        }

        return null;
    }

    public MetadataType? FindType(MetaNamed named) => FindType(named.Key);

    /// <summary>A type or member by its documentation id (<c>T:System.String</c>, <c>M:System.String.Split(System.Char[])</c>).</summary>
    public (MetadataType? Type, MetadataMember? Member) FindByDocId(string id)
    {
        if (id.Length < 3 || id[1] != ':')
        {
            return (null, null);
        }

        if (id[0] == 'T')
        {
            return (FindType(id[2..]), null);
        }

        var signature = id[2..];
        var paren = signature.IndexOf('(');
        var name = paren < 0 ? signature : signature[..paren];
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || FindType(name[..dot]) is not { } type)
        {
            return (null, null);
        }

        return (type, type.Members.FirstOrDefault(m => m.DocId == id));
    }

    /// <summary>The extension methods with <paramref name="name"/>, in any namespace.</summary>
    public IReadOnlyList<MetadataMember> ExtensionMethods(string name)
    {
        var index = _extensionMethods;
        if (index == null)
        {
            lock (_lock)
            {
                index = _extensionMethods ??= _extensionClasses
                    .SelectMany(c => c.Members)
                    .Where(m => m.IsExtension)
                    .GroupBy(m => m.Name, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            }
        }

        return index.TryGetValue(name, out var list) ? list : [];
    }

    internal static (string Name, int Arity) SplitArity(string metadataName)
    {
        var tick = metadataName.LastIndexOf('`');
        return tick > 0 && int.TryParse(metadataName.AsSpan(tick + 1), out var arity) ? (metadataName[..tick], arity) : (metadataName, 0);
    }

    /// <summary>Reads the types of one assembly into the catalog, and their members on demand.</summary>
    private sealed class AssemblyReader(MetadataCatalog catalog, MetadataReader reader, string assembly)
    {
        private readonly TypeProvider _provider = new(reader);
        private readonly ConcurrentDictionary<MetadataType, TypeDefinitionHandle> _handles = new();

        public void IndexTypes()
        {
            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                if (definition.GetDeclaringType().IsNil && IsVisible(definition.Attributes))
                {
                    var type = Read(handle, null);
                    if (type == null)
                    {
                        continue;
                    }

                    // A name defined by two assemblies is found as the first one's, but the extension methods
                    // of both count (MAUI's AppHostBuilderExtensions is in two)
                    if (catalog._types.TryAdd(type.Key, type))
                    {
                        AddNamespace(type.Namespace);
                    }

                    if (type.HasExtensions)
                    {
                        catalog._extensionClasses.Add(type);
                    }
                }
            }
        }

        private void AddNamespace(string ns)
        {
            while (ns.Length != 0 && catalog._namespaces.Add(ns))
            {
                var dot = ns.LastIndexOf('.');
                ns = dot < 0 ? "" : ns[..dot];
            }
        }

        private MetadataType? Read(TypeDefinitionHandle handle, MetadataType? declaring)
        {
            var definition = reader.GetTypeDefinition(handle);
            var (name, arity) = SplitArity(reader.GetString(definition.Name));
            if (name.Length == 0 || name[0] == '<')
            {
                return null;
            }

            var ns = declaring?.Namespace ?? reader.GetString(definition.Namespace);
            var attributes = definition.Attributes;
            var baseType = definition.BaseType.IsNil ? null : _provider.Decode(definition.BaseType);
            var kind = (attributes & TypeAttributes.Interface) != 0 ? MetadataTypeKind.Interface
                : baseType is MetaNamed b && b.Is("System", "Enum") ? MetadataTypeKind.Enum
                : baseType is MetaNamed v && v.Is("System", "ValueType") && !(ns == "System" && name == "Enum") ? MetadataTypeKind.Struct
                : baseType is MetaNamed d && d.Is("System", "MulticastDelegate") ? MetadataTypeKind.Delegate
                : MetadataTypeKind.Class;
            var type = new MetadataType(ns, name, arity, declaring, kind, assembly, ReadMembers)
            {
                IsAbstract = (attributes & TypeAttributes.Abstract) != 0,
                IsSealed = (attributes & TypeAttributes.Sealed) != 0,
                IsStatic = (attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed)) == (TypeAttributes.Abstract | TypeAttributes.Sealed) && kind == MetadataTypeKind.Class,
                BaseType = baseType,
                Interfaces = definition.GetInterfaceImplementations().Select(i => _provider.Decode(reader.GetInterfaceImplementation(i).Interface)).ToList(),
                TypeParameters = definition.GetGenericParameters().Select(p => reader.GetString(reader.GetGenericParameter(p).Name)).ToList(),
                Constraints = definition.GetGenericParameters()
                    .Select(p => (IReadOnlyList<MetaType>)reader.GetGenericParameter(p).GetConstraints().Select(c => _provider.Decode(reader.GetGenericParameterConstraint(c).Type)).ToList())
                    .ToList(),
            };
            type.HasExtensions = type.IsStatic && declaring == null && arity == 0 && HasAttribute(definition.GetCustomAttributes(), "System.Runtime.CompilerServices", "ExtensionAttribute");
            _handles[type] = handle;
            foreach (var nested in definition.GetNestedTypes())
            {
                if (IsVisible(reader.GetTypeDefinition(nested).Attributes) && Read(nested, type) is { } nestedType)
                {
                    type.NestedTypes.Add(nestedType);
                }
            }

            return type;
        }

        private static bool IsVisible(TypeAttributes attributes) => (attributes & TypeAttributes.VisibilityMask) switch
        {
            TypeAttributes.Public or TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => true,
            _ => false,
        };

        private List<MetadataMember> ReadMembers(MetadataType type)
        {
            var members = new List<MetadataMember>();
            if (!_handles.TryGetValue(type, out var handle))
            {
                return members;
            }

            var definition = reader.GetTypeDefinition(handle);
            var typeId = type.Key;
            var accessors = new HashSet<MethodDefinitionHandle>();
            foreach (var propertyHandle in definition.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                var methods = property.GetAccessors();
                var getter = methods.Getter.IsNil ? (MethodDefinition?)null : reader.GetMethodDefinition(methods.Getter);
                var setter = methods.Setter.IsNil ? (MethodDefinition?)null : reader.GetMethodDefinition(methods.Setter);
                accessors.Add(methods.Getter);
                accessors.Add(methods.Setter);
                var visible = new[] { getter, setter }.Where(m => m is { } method && IsVisible(method.Attributes)).Select(m => m!.Value).ToList();
                if (visible.Count == 0)
                {
                    continue;
                }

                var name = reader.GetString(property.Name);
                if (name.Contains('.'))
                {
                    continue;
                }

                var signature = property.DecodeSignature(_provider, null);
                var parameters = getter is { } g ? ReadParameters(g, signature.ParameterTypes, false) : ReadParameters(setter!.Value, signature.ParameterTypes, false);
                var first = visible[0];
                var member = new MetadataMember(type, signature.ParameterTypes.Length > 0 ? MetadataMemberKind.Indexer : MetadataMemberKind.Property, name)
                {
                    IsStatic = (first.Attributes & MethodAttributes.Static) != 0,
                    IsVirtual = (first.Attributes & MethodAttributes.Virtual) != 0 && (first.Attributes & MethodAttributes.Final) == 0,
                    IsAbstract = (first.Attributes & MethodAttributes.Abstract) != 0,
                    IsOverride = IsOverride(first.Attributes),
                    IsProtected = visible.All(m => IsProtected(m.Attributes)),
                    Type = Unwrap(signature.ReturnType),
                    Parameters = parameters.Take(signature.ParameterTypes.Length).ToList(),
                };
                member.DocId = $"P:{typeId}.{name}{ParameterList(member.Parameters)}";
                members.Add(member);
            }

            foreach (var eventHandle in definition.GetEvents())
            {
                var definitionEvent = reader.GetEventDefinition(eventHandle);
                var methods = definitionEvent.GetAccessors();
                accessors.Add(methods.Adder);
                accessors.Add(methods.Remover);
                if (methods.Adder.IsNil || reader.GetMethodDefinition(methods.Adder) is var adder && !IsVisible(adder.Attributes))
                {
                    continue;
                }

                var name = reader.GetString(definitionEvent.Name);
                var member = new MetadataMember(type, MetadataMemberKind.Event, name)
                {
                    IsStatic = (adder.Attributes & MethodAttributes.Static) != 0,
                    IsVirtual = (adder.Attributes & MethodAttributes.Virtual) != 0,
                    IsAbstract = (adder.Attributes & MethodAttributes.Abstract) != 0,
                    IsOverride = IsOverride(adder.Attributes),
                    IsProtected = IsProtected(adder.Attributes),
                    Type = _provider.Decode(definitionEvent.Type),
                };
                member.DocId = $"E:{typeId}.{name}";
                members.Add(member);
            }

            foreach (var fieldHandle in definition.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                var attributes = field.Attributes;
                var access = attributes & FieldAttributes.FieldAccessMask;
                if (access is not (FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem) || (attributes & FieldAttributes.SpecialName) != 0)
                {
                    continue;
                }

                var name = reader.GetString(field.Name);
                var member = new MetadataMember(type, MetadataMemberKind.Field, name)
                {
                    IsStatic = (attributes & FieldAttributes.Static) != 0,
                    IsConstant = (attributes & FieldAttributes.Literal) != 0,
                    IsProtected = access != FieldAttributes.Public,
                    Type = type.Kind == MetadataTypeKind.Enum ? type.SelfType : Unwrap(field.DecodeSignature(_provider, null)),
                };
                member.DocId = $"F:{typeId}.{name}";
                members.Add(member);
            }

            foreach (var methodHandle in definition.GetMethods())
            {
                if (accessors.Contains(methodHandle))
                {
                    continue;
                }

                var method = reader.GetMethodDefinition(methodHandle);
                var attributes = method.Attributes;
                if (!IsVisible(attributes))
                {
                    continue;
                }

                var name = reader.GetString(method.Name);
                if (name.Contains('.') && name is not (".ctor" or ".cctor"))
                {
                    continue;
                }

                // A finalizer is not called by name
                if (name == ".cctor" || name == "Finalize" && method.GetParameters().Count == 0 && (attributes & MethodAttributes.Virtual) != 0)
                {
                    continue;
                }

                var signature = method.DecodeSignature(_provider, null);
                var parameters = ReadParameters(method, signature.ParameterTypes, true);
                var kind = name == ".ctor" ? MetadataMemberKind.Constructor
                    : (attributes & MethodAttributes.SpecialName) != 0 && name.StartsWith("op_", StringComparison.Ordinal) ? MetadataMemberKind.Operator
                    : MetadataMemberKind.Method;
                var member = new MetadataMember(type, kind, kind == MetadataMemberKind.Constructor ? type.Name : name)
                {
                    IsStatic = (attributes & MethodAttributes.Static) != 0,
                    IsVirtual = (attributes & MethodAttributes.Virtual) != 0 && (attributes & MethodAttributes.Final) == 0 && type.Kind != MetadataTypeKind.Interface,
                    IsAbstract = (attributes & MethodAttributes.Abstract) != 0,
                    IsOverride = IsOverride(attributes),
                    IsProtected = IsProtected(attributes),
                    TypeParameters = method.GetGenericParameters().Select(p => reader.GetString(reader.GetGenericParameter(p).Name)).ToList(),
                    Parameters = parameters,
                    Type = Unwrap(signature.ReturnType),
                };
                member.IsExtension = type.HasExtensions && member.IsStatic && parameters.Count > 0 && HasAttribute(method.GetCustomAttributes(), "System.Runtime.CompilerServices", "ExtensionAttribute");
                if (member.IsExtension)
                {
                    member.Parameters = [parameters[0] with { IsThis = true }, .. parameters.Skip(1)];
                }

                var idName = kind == MetadataMemberKind.Constructor ? "#ctor" : name;
                var arity = member.TypeParameters.Count;
                var returnSuffix = name is "op_Implicit" or "op_Explicit" or "op_CheckedExplicit" ? "~" + DocId(signature.ReturnType) : "";
                member.DocId = $"M:{typeId}.{idName}{(arity > 0 ? "``" + arity : "")}{ParameterList(member.Parameters)}{returnSuffix}";
                members.Add(member);
            }

            // A struct (or enum) has a parameterless constructor even when metadata declares none
            if (type.Kind is MetadataTypeKind.Struct or MetadataTypeKind.Enum && !members.Any(m => m.Kind == MetadataMemberKind.Constructor && m.Parameters.Count == 0))
            {
                members.Add(new MetadataMember(type, MetadataMemberKind.Constructor, type.Name) { DocId = $"M:{typeId}.#ctor" });
            }

            return members;
        }

        private List<MetadataParameter> ReadParameters(MethodDefinition method, ImmutableArray<MetaType> types, bool isMethod)
        {
            var names = new string[types.Length];
            var refKinds = new MetaRefKind[types.Length];
            var isParams = new bool[types.Length];
            var hasDefault = new bool[types.Length];
            foreach (var handle in method.GetParameters())
            {
                var parameter = reader.GetParameter(handle);
                var index = parameter.SequenceNumber - 1;
                if (index < 0 || index >= types.Length)
                {
                    continue;
                }

                names[index] = reader.GetString(parameter.Name);
                hasDefault[index] = (parameter.Attributes & (ParameterAttributes.HasDefault | ParameterAttributes.Optional)) != 0;
                if (types[index] is MetaByRef)
                {
                    refKinds[index] = (parameter.Attributes & ParameterAttributes.Out) != 0 && (parameter.Attributes & ParameterAttributes.In) == 0 ? MetaRefKind.Out
                        : HasAttribute(parameter.GetCustomAttributes(), "System.Runtime.CompilerServices", "IsReadOnlyAttribute") || HasAttribute(parameter.GetCustomAttributes(), "System.Runtime.CompilerServices", "RequiresLocationAttribute") ? MetaRefKind.In
                        : MetaRefKind.Ref;
                }

                isParams[index] = HasAttribute(parameter.GetCustomAttributes(), "System", "ParamArrayAttribute")
                    || HasAttribute(parameter.GetCustomAttributes(), "System.Runtime.CompilerServices", "ParamCollectionAttribute");
            }

            return types.Select((t, i) => new MetadataParameter(names[i] ?? "arg" + i, t, refKinds[i], isParams[i], hasDefault[i], false)).ToList();
        }

        private static string ParameterList(IReadOnlyList<MetadataParameter> parameters) =>
            parameters.Count == 0 ? "" : "(" + string.Join(",", parameters.Select(p => DocId(p.Type))) + ")";

        private static string DocId(MetaType type) => type.DocId;

        /// <summary>A by-reference return or property type is the type itself for binding.</summary>
        private static MetaType Unwrap(MetaType type) => type is MetaByRef byRef ? byRef.Element : type;

        private static bool IsVisible(MethodAttributes attributes) =>
            (attributes & MethodAttributes.MemberAccessMask) is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;

        private static bool IsProtected(MethodAttributes attributes) =>
            (attributes & MethodAttributes.MemberAccessMask) is MethodAttributes.Family or MethodAttributes.FamORAssem;

        private static bool IsOverride(MethodAttributes attributes) =>
            (attributes & MethodAttributes.Virtual) != 0 && (attributes & MethodAttributes.NewSlot) == 0;

        private bool HasAttribute(CustomAttributeHandleCollection attributes, string ns, string name)
        {
            foreach (var handle in attributes)
            {
                var attribute = reader.GetCustomAttribute(handle);
                var (attributeNs, attributeName) = attribute.Constructor.Kind switch
                {
                    HandleKind.MemberReference => TypeName(reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
                    HandleKind.MethodDefinition => TypeName(reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
                    _ => ("", ""),
                };
                if (attributeName == name && attributeNs == ns)
                {
                    return true;
                }
            }

            return false;
        }

        private (string Namespace, string Name) TypeName(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeReference => (reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Namespace), reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name)),
            HandleKind.TypeDefinition => (reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Namespace), reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name)),
            _ => ("", ""),
        };
    }

    /// <summary>Decodes signatures into <see cref="MetaType"/>.</summary>
    private sealed class TypeProvider(MetadataReader reader) : ISignatureTypeProvider<MetaType, object?>
    {
        public MetaType Decode(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
            _ => MetaUnknown.Instance,
        };

        public MetaType GetPrimitiveType(PrimitiveTypeCode typeCode) => new MetaNamed("System", [(typeCode switch
        {
            PrimitiveTypeCode.Boolean => "Boolean",
            PrimitiveTypeCode.Byte => "Byte",
            PrimitiveTypeCode.SByte => "SByte",
            PrimitiveTypeCode.Char => "Char",
            PrimitiveTypeCode.Int16 => "Int16",
            PrimitiveTypeCode.UInt16 => "UInt16",
            PrimitiveTypeCode.Int32 => "Int32",
            PrimitiveTypeCode.UInt32 => "UInt32",
            PrimitiveTypeCode.Int64 => "Int64",
            PrimitiveTypeCode.UInt64 => "UInt64",
            PrimitiveTypeCode.Single => "Single",
            PrimitiveTypeCode.Double => "Double",
            PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.UIntPtr => "UIntPtr",
            PrimitiveTypeCode.Object => "Object",
            PrimitiveTypeCode.String => "String",
            PrimitiveTypeCode.TypedReference => "TypedReference",
            _ => "Void",
        }, 0)], []);

        public MetaType GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var levels = new List<(string, int)>();
            var definition = metadata.GetTypeDefinition(handle);
            while (true)
            {
                levels.Insert(0, SplitArity(metadata.GetString(definition.Name)));
                var declaring = definition.GetDeclaringType();
                if (declaring.IsNil)
                {
                    break;
                }

                definition = metadata.GetTypeDefinition(declaring);
            }

            return new MetaNamed(metadata.GetString(definition.Namespace), levels, []);
        }

        public MetaType GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var levels = new List<(string, int)>();
            var reference = metadata.GetTypeReference(handle);
            while (true)
            {
                levels.Insert(0, SplitArity(metadata.GetString(reference.Name)));
                if (reference.ResolutionScope.Kind != HandleKind.TypeReference)
                {
                    break;
                }

                reference = metadata.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope);
            }

            return new MetaNamed(metadata.GetString(reference.Namespace), levels, []);
        }

        public MetaType GetTypeFromSpecification(MetadataReader metadata, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            metadata.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public MetaType GetGenericInstantiation(MetaType genericType, ImmutableArray<MetaType> typeArguments) =>
            genericType is MetaNamed named ? named with { Arguments = typeArguments } : MetaUnknown.Instance;

        public MetaType GetGenericTypeParameter(object? genericContext, int index) => new MetaTypeParameter(index, false);

        public MetaType GetGenericMethodParameter(object? genericContext, int index) => new MetaTypeParameter(index, true);

        public MetaType GetArrayType(MetaType elementType, ArrayShape shape) => new MetaArray(elementType, shape.Rank);

        public MetaType GetSZArrayType(MetaType elementType) => new MetaArray(elementType, 1);

        public MetaType GetByReferenceType(MetaType elementType) => new MetaByRef(elementType);

        public MetaType GetPointerType(MetaType elementType) => new MetaPointer(elementType);

        public MetaType GetModifiedType(MetaType modifier, MetaType unmodifiedType, bool isRequired) => unmodifiedType;

        public MetaType GetPinnedType(MetaType elementType) => elementType;

        public MetaType GetFunctionPointerType(MethodSignature<MetaType> signature) => MetaUnknown.Instance;
    }
}
