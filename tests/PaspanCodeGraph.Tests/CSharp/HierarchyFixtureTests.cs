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

    private static SymbolIndex _index;
    private static List<CSharpCompilation> _compilations;
    private static TempWorkspace _workspace;

    [ClassInitialize]
    public static void Load(TestContext context)
    {
        _workspace = new TempWorkspace();
        var project = _workspace.Write("Fixture/Fixture.csproj", Project);
        var files = new[] { _workspace.Write("Fixture/Shapes.cs", Shapes), _workspace.Write("Fixture/Uses.cs", Uses) };
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
}
