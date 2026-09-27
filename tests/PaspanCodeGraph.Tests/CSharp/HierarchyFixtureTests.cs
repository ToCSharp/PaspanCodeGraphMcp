using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.CSharp;

/// <summary>
/// The hard cases of binding without a compiler, in a small project compared with Roslyn: generic base classes
/// and interfaces, overrides through several levels, interfaces implemented by a base class, explicit
/// implementations, name hiding, aliases and <c>using static</c>, and the forms type names take in code.
/// </summary>
[TestClass]
public sealed class HierarchyFixtureTests
{
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string Shapes = """
        namespace Geometry
        {
            public interface IShape { double Area { get; } string Describe(); }
            public interface INamed { string Name { get; } }
            public interface ISolid : IShape, INamed { double Volume(double depth); }
            public interface IStore<T> { void Put(T item); T Get(int index); event EventHandler<T> Added; }
            public interface IConvert<in TIn, out TOut> { TOut Convert(TIn value); }

            public abstract class Shape : IShape
            {
                public abstract double Area { get; }
                public virtual string Describe() => GetType().Name;
                public override string ToString() => Describe();
            }

            public class Circle(double radius) : Shape, INamed
            {
                public override double Area => Math.PI * radius * radius;
                public string Name => "circle";
                public override string Describe() => "round";
            }

            public sealed class Ring : Circle, ISolid
            {
                public Ring() : base(2) { }
                public override string Describe() => "ring";
                public double Volume(double depth) => Area * depth;
                public new string Name => "ring";
            }

            public class Store<T> : IStore<T>, IConvert<T, string>
            {
                private readonly List<T> _items = [];
                public virtual void Put(T item) => _items.Add(item);
                public T Get(int index) => _items[index];
                public event EventHandler<T>? Added;
                string IConvert<T, string>.Convert(T value) => value?.ToString() ?? "";
            }

            public class ShapeStore : Store<Shape>, IStore<Circle>
            {
                public override void Put(Shape item) { }
                void IStore<Circle>.Put(Circle item) { }
                Circle IStore<Circle>.Get(int index) => new(1);
                event EventHandler<Circle>? IStore<Circle>.Added { add { } remove { } }
            }

            public abstract class Node<TSelf> where TSelf : Node<TSelf>
            {
                public abstract TSelf Clone();
                public virtual void Visit(Func<TSelf, bool> visitor) { }
                public abstract class Child : Node<Child> { }
            }

            public class Leaf : Node<Leaf>
            {
                public override Leaf Clone() => new();
                public override void Visit(Func<Leaf, bool> visitor) { }
            }

            public class Pair<A, B> { public virtual A First(B b) => default!; public virtual void Set(A a, B b) { } }
            public class Swapped<X> : Pair<string, X> { public override string First(X b) => ""; public override void Set(string a, X b) { } }
            public class Closed : Swapped<int> { public override string First(int b) => "1"; }
        }
        """;

    private const string Uses = """
        using Geometry;
        using Round = Geometry.Circle;
        using static Geometry.Palette;

        namespace Geometry
        {
            public enum Color { Red, Green }
            public static class Palette
            {
                public static Color Default => Color.Red;
                public class Swatch { public Color Color { get; set; } = Color.Green; }
            }

            [AttributeUsage(AttributeTargets.All)]
            public sealed class MarkerAttribute(Type type) : Attribute { public Type Type { get; } = type; }
        }

        namespace App.Views
        {
            [Marker(typeof(Shape))]
            public class Canvas
            {
                private readonly Dictionary<string, List<Shape>> _layers = new();
                private Round _round = new Round(1);
                private Geometry.Ring? _ring;
                private global::Geometry.Store<Circle[]> _stores = new();
                private (Shape First, Circle Second) _pair;
                private Swatch _swatch = new() { Color = Palette.Default };
                private Color Color = Color.Green;

                public IEnumerable<Circle> Circles => _layers.Values.SelectMany(l => l).OfType<Circle>();

                public Shape? Pick(object o)
                {
                    if (o is Circle { Area: > 1 } c)
                    {
                        return c;
                    }

                    var ring = o as Ring;
                    var shape = (Shape)o;
                    Shape Local(Shape s) => s;
                    Func<Shape, bool> test = s => s is Ring;
                    Store<int>.Equals(null, null);
                    _ = nameof(Leaf);
                    _ = typeof(Node<>.Child);
                    _ = default(Pair<int, Circle>);
                    Palette.Swatch swatch = new();
                    return ring ?? Local(shape);
                }

                public T Make<T>() where T : Shape, new() => new T();
            }

            public class Layer
            {
                public class Shape { }

                public Shape Inner() => new Shape();
            }

            // Layer.Shape hides Geometry.Shape
            public class Scene : Layer
            {
                public Shape Base() => new Shape();
                public Geometry.Shape Outer() => new Circle(1);
            }
        }
        """;


    private const string Calls = """
        using System.Text;
        using Geometry;
        using static Calls.Helpers;

        namespace Calls
        {
            public static class Helpers
            {
                public static int Twice(int x) => x * 2;
                public static string Twice(string s) => s + s;
                public static T Echo<T>(T value) => value;
                public static IEnumerable<TOut> Map<TIn, TOut>(this IEnumerable<TIn> items, Func<TIn, TOut> f) => items.Select(f);
                public static bool IsBig(this Shape shape) => shape.Area > 10;
                public static string Join(string separator, params string[] parts) => string.Join(separator, parts);
            }

            public record Point(int X, int Y)
            {
                public int Sum => X + Y;
                public Point Move(int dx) => this with { X = X + dx };
            }

            public class Matrix
            {
                private readonly Dictionary<(int, int), double> _cells = new();
                public double this[int row, int column] { get => _cells[(row, column)]; set => _cells[(row, column)] = value; }
                public double this[Point p] => this[p.X, p.Y];
                public int Rows { get; init; }
                public event EventHandler? Changed;
                public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
            }

            public class Base
            {
                public Base() { }
                public Base(int seed) { Seed = seed; }
                public int Seed { get; }
                public virtual string Name() => "base";
                protected void Log(string text) { }
                protected void Log(string format, params object[] args) { }
            }

            public class Derived : Base
            {
                private readonly List<Point> _points = [];

                public Derived() : this(1) { }
                public Derived(int seed) : base(seed) { }

                public override string Name() => base.Name() + "!";

                public int Run(Matrix m, IShape shape, Store<Circle> store)
                {
                    Log("start");
                    Log("{0}", 1);
                    var p = new Point(1, 2);
                    var moved = p.Move(3);
                    var (x, y) = moved;
                    var total = Twice(x) + Twice("a").Length + Echo(y);
                    var names = _points.Map(pt => pt.Sum).Where(v => v > 0).Select(v => Twice(v)).ToList();
                    foreach (var point in _points)
                    {
                        total += point.X;
                    }

                    m[1, 2] = m[p] + m.Rows;
                    var copy = new Matrix { Rows = m.Rows };
                    copy.Changed += (_, _) => Log("changed");
                    m.Raise();
                    store.Put(new Circle(2));
                    var first = store.Get(0);
                    if (first.IsBig() && shape.Describe().Length > 0)
                    {
                        total += (int)shape.Area;
                    }

                    if (shape is Circle { Name: "circle" } circle)
                    {
                        total += circle.Describe().Length;
                    }

                    int Local(int v) => v + Seed;
                    Func<int, int> twice = Twice;
                    var text = new StringBuilder().Append(Join(",", "a", "b")).ToString();
                    return Local(total) + twice(1) + text.Length + names.Count + nameof(Run).Length;
                }
            }
        }
        """;

    private static SymbolIndex _index;
    private static List<CSharpCompilation> _compilations;
    private static TempWorkspace _workspace;

    [ClassInitialize]
    public static void Load(TestContext context)
    {
        _workspace = new TempWorkspace();
        var project = _workspace.Write("Fixture/Fixture.csproj", Project);
        var files = new[] { _workspace.Write("Fixture/Shapes.cs", Shapes), _workspace.Write("Fixture/Uses.cs", Uses), _workspace.Write("Fixture/Calls.cs", Calls) };
        var snapshot = WorkspaceLoader.Load(project);
        _index = snapshot.Index;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) == "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var options = new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(LanguageVersion.Preview);
        var global = string.Concat(snapshot.Projects[0].Usings.Select(u => $"global using {u};\n"));
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), options, f))
            .Append(CSharpSyntaxTree.ParseText(global, options, _workspace.PathOf("Fixture/obj/GlobalUsings.g.cs")));
        var compilation = CSharpCompilation.Create("Fixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
        _compilations = [compilation];
    }

    [ClassCleanup]
    public static void Cleanup() => _workspace?.Dispose();

    [TestMethod]
    public void BaseTypes_MatchRoslyn()
    {
        var result = HierarchyComparison.BaseTypes(_index, _compilations);
        Assert.IsTrue(result.Compared > 15, result.Report("base types"));
        Assert.AreEqual(0, result.Differences.Count, result.Report("base types"));
    }

    [TestMethod]
    public void Overrides_MatchRoslyn()
    {
        var result = HierarchyComparison.Overrides(_index, _compilations);
        Assert.IsTrue(result.Compared >= 8, result.Report("overrides"));
        Assert.AreEqual(0, result.Differences.Count, result.Report("overrides"));
    }

    [TestMethod]
    public void InterfaceImplementations_MatchRoslyn()
    {
        var result = HierarchyComparison.InterfaceImplementations(_index, _compilations);
        Assert.IsTrue(result.Compared >= 13, result.Report("implementations"));
        Assert.AreEqual(0, result.Differences.Count, result.Report("implementations"));
    }

    [TestMethod]
    public void TypeReferences_MatchRoslyn()
    {
        var (result, recall, precision) = HierarchyComparison.TypeReferences(_index, _compilations);
        Assert.IsTrue(result.Compared > 50, result.Report("references"));
        Assert.AreEqual(0, result.Differences.Count, $"recall {recall:P2}, precision {precision:P2}; " + result.Report("references"));
    }

    [TestMethod]
    public void MemberReferences_MatchRoslyn()
    {
        var result = HierarchyComparison.MemberReferences(_index, _compilations);
        Assert.IsTrue(result.Result.Compared > 60, result.Report());
        Assert.AreEqual(0, result.Result.Differences.Count, result.Report());
    }
}
