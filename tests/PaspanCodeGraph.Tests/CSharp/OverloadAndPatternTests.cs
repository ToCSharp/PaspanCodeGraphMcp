using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.CSharp;

/// <summary>
/// Overload resolution with lambdas and method groups as arguments (the lambda's return type, the method group's
/// parameters), constant patterns the parser takes for types, awaiting a configured task, constants converting to
/// narrower integer types, tuple element names and anonymous types, compared with Roslyn.
/// </summary>
[TestClass]
public sealed class OverloadAndPatternTests
{
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string Code = """
        namespace Fixture;

        public enum Kind { Block, Func, Class, Obj }

        public static class Names
        {
            public const string First = "a";
            public const string Second = "b";
        }

        public class Item
        {
            public string Name { get; set; } = "";
            public int Size { get; set; }
            public decimal Price { get; set; }
            public Kind Kind { get; set; }
            public DateTime Created { get; set; }
        }

        public static class Helper
        {
            public static string NameOf(Item item) => item.Name;
            public static int SizeOf(Item item) => item.Size;
            public static Task<Item> GetAsync() => Task.FromResult(new Item());
            public static Task SaveAsync(Item item) => Task.CompletedTask;
            public static Item Port(Item item, string? note = null, Func<string, string?>? origin = null) => item;
            public static int Count(Item item, Func<Item, bool> keep) => keep(item) ? 1 : 0;
            public static int Count(Item item, Func<Item, int, bool> keep) => keep(item, 0) ? 1 : 0;
            public static string Show(Func<Item, string> show) => show(new Item());
            public static string Show(Func<Item, int> size) => size(new Item()).ToString();
            public static int Flags(string name, Kind kind) => (int)kind;
            public static int Flags(string name, Item item) => item.Size;
            public static int NoFlags() => Flags("a", 0);
        }

        public class Patterns
        {
            private const int Zero = 0;

            public static bool Braced(Item item) => item.Kind is Kind.Block or Kind.Func or Kind.Class;

            public static bool Named(string name) => name is Names.First
                or Names.Second;

            public static bool Small(int value) => value is < 0 or Zero;

            public static int Switched(Item item)
            {
                switch (item.Kind)
                {
                    case Kind.Obj:
                        return 1;
                    default:
                        return item.Kind is not Kind.Block ? 2 : 3;
                }
            }
        }

        public class Groups
        {
            private static string? Origin(string s) => s;

            public static int Run(List<Item> items, string[] paths)
            {
                string? Local(string s) => s;
                var a = Helper.Port(items[0], null, Local);
                var b = Helper.Port(items[0], null, Origin);
                var names = items.Select(Helper.NameOf).ToList();
                var sizes = items.Select(Helper.SizeOf).Sum();
                var files = paths.Select(Path.GetFileName).ToList();
                var existing = paths.Where(File.Exists).ToList();
                var counted = Helper.Count(items[0], i => i.Size > 0) + Helper.Count(items[0], (i, n) => i.Size > n);
                return a.Size + b.Size + names[0].Length + sizes + files.Count + existing.Count + counted;
            }
        }

        public class Lambdas
        {
            public static async Task<int> Run(List<Item> items)
            {
                var total = items.Sum(i => i.Size) + new[] { items[0] }.Sum(i => i.Size);
                var price = items.Max(i => i.Price);
                var task = Task.Run(() => Helper.GetAsync());
                var item = await task;
                await Task.Run(() => Helper.SaveAsync(item));
                var shown = Helper.Show(i => i.Name) + Helper.Show(i => i.Size);
                var ordered = items.OrderBy(i => i.Name).ThenBy(i => i.Size).First();
                return total + (int)price + item.Size + shown.Length + ordered.Size;
            }
        }

        public class Awaits
        {
            public static async Task<int> Run()
            {
                var a = await Helper.GetAsync().ConfigureAwait(false);
                var b = await new ValueTask<Item>(new Item()).ConfigureAwait(false);
                return a.Size + b.Size;
            }
        }

        public class Constants
        {
            public static string Run(ulong big, string[] parts, List<Item> items)
            {
                var min = Math.Min(big, 256);
                var max = Math.Max(256, big);
                var joined = string.Join(", ", parts);
                return min.ToString() + max.ToString() + joined.Trim();
            }
        }

        public class Tuples
        {
            public static int Run(List<Item> items, string tag)
            {
                var first = items.Select(c => (tag, c)).First();
                var named = items.Select(c => (Tag: tag, Item: c)).First();
                var anonymous = new { item = items[0], count = 1 };
                var projected = items.Select(c => new { c, c.Size }).First();
                return first.c.Size + named.Item.Size + anonymous.item.Size + projected.c.Size + projected.Size;
            }
        }

        public class TupleLambdas
        {
            public static int Run(Item one)
            {
                var checks = new List<(string Name, Func<Item, string, (bool Ok, string Why)> Assert)>();
                checks.Add(("size", (item, note) => (item.Size > 0, note)));
                var n = 0;
                foreach (var (name, assert) in checks)
                {
                    n += assert(one, name).Why.Length;
                }

                return n;
            }
        }

        public class Numbers
        {
            public ushort Count { get; set; }

            public static async Task<int> Run(List<int> values, Dictionary<string, int> byName, List<Numbers> all, ulong size, byte[] bytes, List<double> reals)
            {
                var range = values.Min() + values.Max() + byName.Values.Sum() + (int)reals.Average() + (int)reals.Max();
                var counted = all.Sum(n => n.Count) + all.Max(n => n.Count);
                var clipped = (int)Math.Min(size, int.MaxValue);
                var tail = bytes[1..].Select(b => (byte)(b ^ 0x40)).ToArray();
                var done = await Task.Run(() =>
                {
                    var ok = values.Count > 0;
                    return ok;
                });
                await Task.Run(() => { values.Clear(); });
                return range + counted + clipped + tail.Length + (done ? 1 : 0);
            }

            private const int MaxBytes = 4096;

            public static string Text(List<string> texts, string?[] parts, ulong size)
            {
                var nums = texts.Select(v => double.TryParse(v, out var d) ? d : (double?)null).ToList();
                var known = nums.Where(n => n is not null).Select(n => n!.Value).ToList();
                var length = (int)Math.Min(size, MaxBytes);
                return string.Join(", ", parts) + known.Min() + known.Max() + length;
            }
        }

        public class Spans
        {
            public static bool Run(string[] names, char c) => names.Contains("x") || Path.GetInvalidFileNameChars().Contains(c);
        }

        public class Grouping
        {
            public static int Run(List<Item> items)
            {
                var latest = items.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Created).First());
                var sizes = items.GroupBy(e => e.Size).Select(g => g.Sum(e => e.Size)).ToList();
                return latest.Values.First().Size + sizes[0];
            }
        }
        """;

    private static SymbolIndex _index;
    private static List<CSharpCompilation> _compilations;
    private static TempWorkspace _workspace;
    private static string _codePath;

    [ClassInitialize]
    public static void Load(TestContext context)
    {
        _workspace = new TempWorkspace();
        var project = _workspace.Write("Overloads/Overloads.csproj", Project);
        _codePath = _workspace.Write("Overloads/Code.cs", Code);
        var snapshot = WorkspaceLoader.Load(project);
        _index = snapshot.Index;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) == "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var global = string.Concat(snapshot.Projects[0].Usings.Select(u => $"global using {u};\n"));
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(File.ReadAllText(_codePath), options, _codePath),
            CSharpSyntaxTree.ParseText(global, options, _workspace.PathOf("Overloads/obj/GlobalUsings.g.cs")),
        };
        var compilation = CSharpCompilation.Create("Overloads", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
        _compilations = [compilation];
    }

    [ClassCleanup]
    public static void Cleanup() => _workspace?.Dispose();

    [TestMethod]
    public void MemberReferences_MatchRoslyn()
    {
        var result = HierarchyComparison.MemberReferences(_index, _compilations);
        Assert.IsTrue(result.Result.Compared > 40, result.Report());
        Assert.AreEqual(0, result.Result.Differences.Count, result.Report());
        Assert.AreEqual(0, result.NameOnly, result.Report());
    }

    [TestMethod]
    public void ExternalMemberReferences_MatchRoslyn()
    {
        var result = HierarchyComparison.MemberReferences(_index, _compilations, external: true);
        Assert.IsTrue(result.Result.Compared > 40, result.Report());
        Assert.AreEqual(0, result.Result.Differences.Count, result.Report());
        Assert.AreEqual(0, result.NameOnly, result.Report());
    }
}
