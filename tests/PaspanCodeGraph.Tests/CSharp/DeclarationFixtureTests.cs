using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PaspanCodeGraph.CSharp;
using PaspanParsers.CSharp;
using CSharpParseOptions = PaspanParsers.CSharp.CSharpParseOptions;

namespace PaspanCodeGraph.Tests.CSharp;

/// <summary>
/// Declarations of every kind, compared with Roslyn. Named types used in signatures are declared in the global
/// namespace, where the name as written is the full name, so every id can be computed from syntax.
/// </summary>
[TestClass]
public sealed class DeclarationFixtureTests
{
    private const string Source = """
        public interface IPair<A, B> { void Swap(); int this[A a] { get; } event System.Action Changed; }
        public interface IShape { double Area { get; } }
        public delegate TResult Transform<T, TResult>(T value, ref int count);
        public enum Color : byte { Red, Green = 2, Blue }

        namespace Outer.Inner
        {
            /// <summary>A generic <see cref="Box{T}"/> with a <c>value</c>.</summary>
            public partial class Box<T> : IPair<int, string>, IShape where T : class
            {
                public const int Size = 4;
                private readonly T? _value, _other;
                public event System.EventHandler Opened;
                public event System.EventHandler Closed { add { } remove { } }

                static Box() { }
                public Box(T value) { _value = value; }
                ~Box() { }

                public double Area => 1;
                double IShape.Area => 2;
                public int this[int index] => index;
                public string this[int row, string column] { get => column; set { } }
                int IPair<int, string>.this[int a] => a;
                event System.Action IPair<int, string>.Changed { add { } remove { } }
                void IPair<int, string>.Swap() { }

                public void Run() { }
                public void Run(int count, params string[] names) { }
                public TOut Map<TOut>(System.Func<T, TOut> map, TOut fallback) => fallback;
                public void Refs(ref int a, out long b, in double c, ref readonly char d) { b = 0; }
                public unsafe void Pointers(int* p, void** q) { }
                public void Arrays(int[] a, int[,] b, int[][] c, T[] d) { }
                public void Nullables(int? a, T? b, string? c) { }
                public (int, string) Tuple((int Id, T Value) item, (int, int, int, int, int, int, int, int) wide) => default;
                public void Dynamic(dynamic d, nint n, nuint u) { }

                public static Box<T> operator +(Box<T> a, Box<T> b) => a;
                public static Box<T> operator -(Box<T> a) => a;
                public static bool operator ==(Box<T> a, int b) => true;
                public static bool operator !=(Box<T> a, int b) => false;
                public static implicit operator int(Box<T> box) => 0;
                public static explicit operator string(Box<T> box) => "";

                public class Nested<U>
                {
                    public void Pair(T t, U u) { }
                    public void Generic<V>(V v, U u, T t) { }
                }

                public override bool Equals(object obj) => false;
                public override int GetHashCode() => 0;
            }

            partial class Box<T>
            {
                internal protected void Second() { }
            }

            public record Point(int X, int Y)
            {
                public int Sum() => X + Y;
            }

            public record struct Size(double Width, double Height);

            public readonly struct Pixel : IShape
            {
                public double Area => 1;
            }

            public static class Extensions
            {
                public static int Count(this string text, char c) => 0;

                extension(string text)
                {
                    public bool IsBlank() => text.Length == 0;
                    public int Width => text.Length;
                }
            }
        }
        """;

    private static Dictionary<string, CodeSymbol> _ours;
    private static List<(ISymbol Symbol, Location Location)> _roslyn;
    private static string _path;

    [ClassInitialize]
    public static void Parse(TestContext context)
    {
        _path = Path.Combine(Path.GetTempPath(), "Fixture.cs");
        var utf8 = Encoding.UTF8.GetBytes(Source);
        Assert.IsTrue(CSharpParser.TryParse(utf8, new CSharpParseOptions(errorRecovery: false), out var unit, out var error), error?.ToString());
        var builder = new SymbolIndexBuilder();
        CSharpSymbolCollector.Collect(new CSharpSource(_path, "Fixture", utf8, new LineMap(utf8), unit), builder);
        _ours = builder.Build().Symbols.ToDictionary(s => s.Id);

        var tree = CSharpSyntaxTree.ParseText(Source, new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(LanguageVersion.Preview), _path);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal))
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Fixture", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors));

        var model = compilation.GetSemanticModel(tree);
        _roslyn = [];
        foreach (var node in tree.GetRoot().DescendantNodes(n => n is not (BlockSyntax or ArrowExpressionClauseSyntax)))
        {
            var symbol = node switch
            {
                BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or EnumMemberDeclarationSyntax
                    or BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax => model.GetDeclaredSymbol(node),
                VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } => model.GetDeclaredSymbol(node),
                _ => null,
            };

            if (symbol is INamespaceSymbol ns)
            {
                for (var n = ns; n is { IsGlobalNamespace: false }; n = n.ContainingNamespace)
                {
                    _roslyn.Add((n, n.Locations.First(l => l.SourceSpan.Start >= node.SpanStart && l.SourceSpan.End <= node.Span.End)));
                }
            }
            else if (symbol is not null)
            {
                // The location of this declaration (a partial type has one per part)
                _roslyn.Add((symbol, symbol.Locations.First(l => node.Span.Contains(l.SourceSpan))));
            }
        }
    }

    [TestMethod]
    public void Ids_MatchRoslyn()
    {
        var roslynIds = _roslyn.Select(r => r.Symbol.GetDocumentationCommentId()).ToHashSet();

        // Members of extension blocks are recorded as members of the static class; Roslyn declares them in the block
        var missing = roslynIds.Where(id => !_ours.ContainsKey(id) && !id.Contains("<G>", StringComparison.Ordinal) && !id.Contains("$", StringComparison.Ordinal)).Order().ToList();
        var extra = _ours.Keys.Where(id => !roslynIds.Contains(id) && !IsExtensionBlockMember(id)).Order().ToList();

        Assert.AreEqual(0, missing.Count + extra.Count, $"missing:\n{string.Join("\n", missing)}\nextra:\n{string.Join("\n", extra)}\nRoslyn:\n{string.Join("\n", roslynIds.Order())}");
    }

    private static bool IsExtensionBlockMember(string id) => id.Contains("Extensions.IsBlank", StringComparison.Ordinal) || id.Contains("Extensions.Width", StringComparison.Ordinal);

    [TestMethod]
    public void NameLocations_MatchRoslyn()
    {
        var wrong = new List<string>();
        foreach (var (symbol, location) in _roslyn)
        {
            if (symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion }
                || !_ours.TryGetValue(symbol.GetDocumentationCommentId(), out var ours))
            {
                continue;
            }

            var position = location.GetLineSpan().StartLinePosition;
            if (!ours.Declarations.Any(d => d.Line == position.Line + 1 && d.Column == position.Character + 1))
            {
                wrong.Add($"{ours.Id}: Roslyn {position.Line + 1}:{position.Character + 1}, index {string.Join(", ", ours.Declarations.Select(d => $"{d.Line}:{d.Column}"))}");
            }
        }

        Assert.AreEqual(0, wrong.Count, string.Join("\n", wrong));
    }

    [TestMethod]
    public void Details_OfDeclarations()
    {
        var box = _ours["T:Outer.Inner.Box`1"];
        Assert.AreEqual(SymbolKind.Class, box.Kind);
        Assert.AreEqual("Public", box.Accessibility);
        Assert.AreEqual(2, box.Declarations.Count);
        CollectionAssert.AreEqual(new[] { "IPair<int, string>", "IShape" }, box.BaseTypes);
        CollectionAssert.AreEqual(new[] { "T" }, box.TypeParameters);
        Assert.AreEqual("Outer.Inner", box.Namespace);
        StringAssert.StartsWith(box.Documentation.Trim(), "<summary>");
        Assert.AreEqual("class Box<T> : IPair<int, string>, IShape", box.Signature);

        var run = _ours["M:Outer.Inner.Box`1.Run(System.Int32,System.String[])"];
        Assert.AreEqual("void Box.Run(int count, params string[] names)", run.Signature);
        Assert.AreEqual("params", run.Parameters[1].Modifier);

        Assert.AreEqual("ProtectedOrInternal", _ours["M:Outer.Inner.Box`1.Second"].Accessibility);
        Assert.AreEqual("Private", _ours["F:Outer.Inner.Box`1._value"].Accessibility);
        Assert.AreEqual("Private", _ours["M:Outer.Inner.Box`1.IPair{System#Int32,System#String}#Swap"].Accessibility);
        Assert.AreEqual("Public", _ours["M:IPair`2.Swap"].Accessibility);
        CollectionAssert.Contains(_ours["F:Outer.Inner.Box`1.Size"].Modifiers, "const");
        CollectionAssert.Contains(_ours["M:Outer.Inner.Extensions.Count(System.String,System.Char)"].Modifiers, "extension");
        Assert.AreEqual(SymbolKind.RecordStruct, _ours["T:Outer.Inner.Size"].Kind);
        Assert.AreEqual(SymbolKind.EnumMember, _ours["F:Color.Green"].Kind);
        Assert.AreEqual("Color.Green = 2", _ours["F:Color.Green"].Signature);
    }
}
