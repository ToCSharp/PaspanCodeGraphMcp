using System.Reflection;
using System.Text;
using PaspanCodeGraph.CSharp;
using PaspanCodeGraph.Metadata;
using PaspanParsers.CSharp;

namespace PaspanCodeGraph.Workspace;

/// <summary>
/// The graph of a snapshot on disk (<c>.paspan/graph.bin</c> next to the solution), so that a restart reads it
/// instead of parsing and binding every file. It holds the symbols, the references of each file with its content
/// hash and declarations, and what the projects and referenced assemblies looked like; loading compares them
/// with the files on disk and updates what changed. A cache written by another build of the server is ignored.
/// </summary>
public static class GraphCache
{
    private const string Magic = "PASPAN-GRAPH";

    /// <summary>Changes whenever the format changes.</summary>
    private const int SchemaVersion = 1;

    /// <summary>The builds of the assemblies that make the graph: a graph made by other code may differ.</summary>
    private static readonly string BuildStamp = string.Join(
        ',',
        new[] { typeof(CodeSymbol), typeof(CSharpBinder), typeof(WorkspaceLoader), typeof(MetadataCatalog), typeof(CSharpParser), typeof(Paspan.ParseError), typeof(Paspan.Fluent.Parsers) }
            .Select(t => t.Assembly.ManifestModule.ModuleVersionId));

    public static string DefaultPath(string rootPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rootPath))!, ".paspan", "graph.bin");

    /// <summary>Writes <paramref name="snapshot"/> to <paramref name="file"/>, replacing it at once when complete.</summary>
    public static void Save(WorkspaceSnapshot snapshot, string file)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        Directory.CreateDirectory(directory);
        var ignore = Path.Combine(directory, ".gitignore");
        if (!File.Exists(ignore))
        {
            File.WriteAllText(ignore, "*\n");
        }

        var temporary = file + "." + Environment.ProcessId + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Write(snapshot, stream);
            }

            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Reads a cached graph of the workspace at <paramref name="rootPath"/>, or returns null (with the reason) when
    /// there is none or it was made for other options or by another build. The result has no syntax trees and no
    /// referenced assemblies: <see cref="WorkspaceLoader.Update"/> brings it up to date.
    /// </summary>
    public static WorkspaceSnapshot? TryRead(string file, string rootPath, string configuration, string platform, bool readReferences, out string? reason)
    {
        if (!File.Exists(file))
        {
            reason = "no cache";
            return null;
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Read(stream, Path.GetFullPath(rootPath), configuration, platform, readReferences, out reason);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException or FormatException or ArgumentException)
        {
            reason = $"unreadable cache: {e.Message}";
            return null;
        }
    }

    private static void Write(WorkspaceSnapshot snapshot, Stream stream)
    {
        // The payload refers to strings by number; the table goes first
        var strings = new StringTable();
        using var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            void S(string? value) => w.Write7BitEncodedInt(strings.Add(value));
            void I(int value) => w.Write7BitEncodedInt(value);

            S(snapshot.RootPath);
            S(snapshot.Configuration);
            S(snapshot.Platform);
            w.Write(snapshot.ReadReferences);
            S(snapshot.MetadataKey);

            I(snapshot.ProjectPrints.Count);
            foreach (var (path, print) in snapshot.ProjectPrints)
            {
                S(path);
                S(print);
            }

            I(snapshot.MetadataProblems.Count);
            foreach (var problem in snapshot.MetadataProblems)
            {
                I((int)problem.Severity);
                S(problem.Message);
                S(problem.File);
            }

            var documents = snapshot.Documents.Values.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
            I(documents.Count);
            foreach (var document in documents)
            {
                S(document.Path);
                S(document.Project);
                S(document.Failure);
                I(document.Errors.Count);
                foreach (var error in document.Errors)
                {
                    I(error.Span.Start);
                    I(error.Span.End);
                    S(error.Message);
                }

                var state = snapshot.Files.GetValueOrDefault(document.Path);
                S(state?.Hash ?? "");
                var declarations = state?.Declarations ?? [];
                I(declarations.Count);
                foreach (var print in declarations)
                {
                    S(print.Id);
                    S(print.Name);
                    S(print.ContainerName);
                    I((int)print.Kind);
                    S(print.Details);
                    w.Write(print.Global);
                }

                var names = state?.Names ?? new HashSet<string>();
                I(names.Count);
                foreach (var name in names)
                {
                    S(name);
                }

                var references = state?.References ?? [];
                I(references.Count);
                foreach (var (target, reference) in references)
                {
                    S(target);
                    S(reference.File);
                    I(reference.Start);
                    I(reference.Line);
                    I(reference.Column);
                    S(reference.InMember);
                    I((int)reference.Confidence);
                }
            }

            // Symbols in the order they were made, so that containers come before their members
            var symbols = snapshot.Index.Symbols.ToList();
            var numbers = new Dictionary<CodeSymbol, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < symbols.Count; i++)
            {
                numbers[symbols[i]] = i;
            }

            void Symbol(CodeSymbol? symbol) => I(symbol != null && numbers.TryGetValue(symbol, out var number) ? number + 1 : 0);

            void Symbols(List<CodeSymbol> list)
            {
                var known = list.Where(numbers.ContainsKey).ToList();
                I(known.Count);
                foreach (var symbol in known)
                {
                    I(numbers[symbol]);
                }
            }

            void Strings(List<string> list)
            {
                I(list.Count);
                foreach (var value in list)
                {
                    S(value);
                }
            }

            void Link(TypeLink link)
            {
                S(link.Written);
                Symbol(link.Symbol);
                S(link.Id);
                I(link.TypeArguments.Count);
                foreach (var argument in link.TypeArguments)
                {
                    S(argument);
                }
            }

            I(symbols.Count);
            foreach (var symbol in symbols)
            {
                S(symbol.Id);
                I((int)symbol.Kind);
                S(symbol.Name);
                Symbol(symbol.Container);
            }

            foreach (var symbol in symbols)
            {
                S(symbol.Signature);
                S(symbol.Namespace);
                S(symbol.Project);
                S(symbol.Assembly);
                S(symbol.Accessibility);
                Strings(symbol.Modifiers);
                I(symbol.Declarations.Count);
                foreach (var location in symbol.Declarations)
                {
                    S(location.File);
                    I(location.Start);
                    I(location.End);
                    I(location.Line);
                    I(location.Column);
                }

                Strings(symbol.BaseTypes);
                Strings(symbol.TypeParameters);
                I(symbol.Parameters.Count);
                foreach (var parameter in symbol.Parameters)
                {
                    S(parameter.Name);
                    S(parameter.Type);
                    S(parameter.Modifier);
                    S(parameter.DefaultValue);
                }

                S(symbol.Type);
                S(symbol.Documentation);
                I(symbol.Bases.Count);
                foreach (var link in symbol.Bases)
                {
                    Link(link);
                }

                Symbols(symbol.DerivedTypes);
                w.Write(symbol.ExplicitInterface != null);
                if (symbol.ExplicitInterface != null)
                {
                    Link(symbol.ExplicitInterface);
                }

                Symbol(symbol.Overrides);
                Symbols(symbol.OverriddenBy);
                Symbols(symbol.Implements);
                Symbols(symbol.ImplementedBy);
            }
        }

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(SchemaVersion);
        writer.Write(BuildStamp);
        writer.Write7BitEncodedInt(strings.Values.Count);
        foreach (var value in strings.Values)
        {
            writer.Write(value);
        }

        writer.Flush();
        payload.Position = 0;
        payload.CopyTo(stream);
    }

    private static WorkspaceSnapshot? Read(Stream stream, string rootPath, string configuration, string platform, bool readReferences, out string? reason)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadString() != Magic || r.ReadInt32() != SchemaVersion)
        {
            reason = "cache of another format";
            return null;
        }

        if (r.ReadString() != BuildStamp)
        {
            reason = "cache written by another build";
            return null;
        }

        var table = new string[r.Read7BitEncodedInt()];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = r.ReadString();
        }

        string? S()
        {
            var number = r.Read7BitEncodedInt();
            return number == 0 ? null : table[number - 1];
        }

        string N() => S() ?? "";
        int I() => r.Read7BitEncodedInt();

        var cachedRoot = N();
        var cachedConfiguration = N();
        var cachedPlatform = N();
        var cachedReferences = r.ReadBoolean();
        if (!SymbolIndexBuilder.PathComparer.Equals(cachedRoot, rootPath) || cachedConfiguration != configuration || cachedPlatform != platform || cachedReferences != readReferences)
        {
            reason = "cache made for another workspace or other options";
            return null;
        }

        var metadataKey = N();
        var prints = new Dictionary<string, string>(SymbolIndexBuilder.PathComparer);
        for (var count = I(); count > 0; count--)
        {
            var path = N();
            prints[path] = N();
        }

        var metadataProblems = new List<LoadProblem>();
        for (var count = I(); count > 0; count--)
        {
            var severity = (ProblemSeverity)I();
            var message = N();
            metadataProblems.Add(new LoadProblem(severity, message, S()));
        }

        var documents = new Dictionary<string, SourceDocument>(SymbolIndexBuilder.PathComparer);
        var files = new Dictionary<string, FileState>(SymbolIndexBuilder.PathComparer);
        var fileOrder = new List<string>();
        for (var count = I(); count > 0; count--)
        {
            var path = N();
            var project = N();
            var failure = S();
            var errors = new List<SyntaxError>();
            for (var e = I(); e > 0; e--)
            {
                var start = I();
                var end = I();
                errors.Add(new SyntaxError(new TextSpan(start, end), N()));
            }

            documents[path] = new SourceDocument(path, project, ReadOnlyMemory<byte>.Empty, new LineMap([]), null, failure) { CachedErrors = errors };

            var hash = N();
            var declarations = new List<DeclarationPrint>();
            for (var d = I(); d > 0; d--)
            {
                var id = N();
                var name = N();
                var container = S();
                var kind = (SymbolKind)I();
                var details = N();
                declarations.Add(new DeclarationPrint(id, name, container, kind, details, r.ReadBoolean()));
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var n = I(); n > 0; n--)
            {
                names.Add(N());
            }

            var references = new List<(string, SymbolReference)>();
            for (var n = I(); n > 0; n--)
            {
                var target = N();
                var file = N();
                var start = I();
                var line = I();
                var column = I();
                var inMember = S();
                references.Add((target, new SymbolReference(file, start, line, column, inMember, (Confidence)I())));
            }

            files[path] = new FileState { Hash = hash, Declarations = declarations, Names = names, References = references };
            fileOrder.Add(path);
        }

        var builder = new SymbolIndexBuilder();
        var symbols = new CodeSymbol[I()];
        CodeSymbol? Symbol()
        {
            var number = I();
            return number == 0 ? null : symbols[number - 1];
        }

        void Symbols(List<CodeSymbol> list)
        {
            for (var count = I(); count > 0; count--)
            {
                list.Add(symbols[I()]);
            }
        }

        void Strings(List<string> list)
        {
            for (var count = I(); count > 0; count--)
            {
                list.Add(N());
            }
        }

        TypeLink Link()
        {
            var written = N();
            var symbol = Symbol();
            var id = N();
            var arguments = new string[I()];
            for (var i = 0; i < arguments.Length; i++)
            {
                arguments[i] = N();
            }

            return new TypeLink(written, symbol, id, arguments);
        }

        for (var i = 0; i < symbols.Length; i++)
        {
            var id = N();
            var kind = (SymbolKind)I();
            var name = N();
            symbols[i] = builder.GetOrAdd(id, kind, name, Symbol(), out _);
        }

        foreach (var symbol in symbols)
        {
            symbol.Signature = N();
            symbol.Namespace = S();
            symbol.Project = S();
            symbol.Assembly = S();
            symbol.Accessibility = N();
            Strings(symbol.Modifiers);
            for (var count = I(); count > 0; count--)
            {
                var file = N();
                builder.AddDeclaration(symbol, new SourceLocation(file, I(), I(), I(), I()));
            }

            Strings(symbol.BaseTypes);
            Strings(symbol.TypeParameters);
            for (var count = I(); count > 0; count--)
            {
                symbol.Parameters.Add(new ParameterInfo(N(), S(), S(), S()));
            }

            symbol.Type = S();
            symbol.Documentation = S();
            for (var count = I(); count > 0; count--)
            {
                symbol.Bases.Add(Link());
            }

            Symbols(symbol.DerivedTypes);
            if (r.ReadBoolean())
            {
                symbol.ExplicitInterface = Link();
            }

            symbol.Overrides = Symbol();
            Symbols(symbol.OverriddenBy);
            Symbols(symbol.Implements);
            Symbols(symbol.ImplementedBy);
        }

        // In the order a load adds them, so that the index sorts them the same way
        foreach (var path in fileOrder)
        {
            foreach (var (target, reference) in files[path].References)
            {
                builder.AddReference(target, reference);
            }
        }

        reason = null;
        return new WorkspaceSnapshot
        {
            RootPath = rootPath,
            Configuration = configuration,
            Platform = platform,
            ReadReferences = readReferences,
            Projects = [],
            Documents = documents,
            Index = builder.Build(),
            Problems = [],
            Files = files,
            ProjectPrints = prints,
            MetadataKey = metadataKey,
            MetadataLoaded = false,
            MetadataProblems = metadataProblems,
            Kind = SnapshotKind.Cache,
            LoadedAt = DateTimeOffset.UtcNow,
            Elapsed = TimeSpan.Zero,
        };
    }

    private sealed class StringTable
    {
        private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);

        public List<string> Values { get; } = [];

        /// <summary>The string's number, from 1; 0 stands for null.</summary>
        public int Add(string? value)
        {
            if (value == null)
            {
                return 0;
            }

            if (!_numbers.TryGetValue(value, out var number))
            {
                Values.Add(value);
                _numbers[value] = number = Values.Count;
            }

            return number;
        }
    }
}
