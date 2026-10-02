using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PaspanCodeGraph.Workspace;

// What the build generates for a project that its code uses: the global usings packages add, the C# of an Android
// binding project, and the fields and InitializeComponent of XAML files
public sealed partial class ProjectFileReader
{
    /// <summary>The XAML language namespaces (<c>x:</c>) of WPF (2006) and MAUI (2009).</summary>
    private static readonly string[] XamlLanguages = ["http://schemas.microsoft.com/winfx/2006/xaml", "http://schemas.microsoft.com/winfx/2009/xaml"];
    private const string MauiXmlns = "http://schemas.microsoft.com/dotnet/2021/maui";
    private const string FormsXmlns = "http://xamarin.com/schemas/2014/forms";
    private const string WpfXmlns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>The namespaces of the types a XAML vocabulary names without a prefix.</summary>
    private static readonly Dictionary<string, string[]> XamlNamespaces = new(StringComparer.Ordinal)
    {
        [MauiXmlns] = ["Microsoft.Maui.Controls", "Microsoft.Maui.Controls.Shapes"],
        [FormsXmlns] = ["Microsoft.Maui.Controls", "Microsoft.Maui.Controls.Shapes", "Xamarin.Forms", "Xamarin.Forms.Shapes"],
        [WpfXmlns] =
        [
            "System.Windows", "System.Windows.Controls", "System.Windows.Controls.Primitives", "System.Windows.Documents", "System.Windows.Shapes",
            "System.Windows.Media", "System.Windows.Media.Imaging", "System.Windows.Navigation", "System.Windows.Data", "System.Windows.Input",
        ],
    };

    /// <summary>Whether the project has Java libraries to bind (an Android binding project).</summary>
    private bool _bindsJava;

    private readonly List<string> _xamlItems = [];

    /// <summary>Whether XAML items are WPF's (<c>Page</c>, <c>ApplicationDefinition</c>), as in a project without <c>UseWPF</c>.</summary>
    private bool _wpfItems;

    /// <summary>
    /// The intermediate directory of the configuration and framework (<c>obj/Debug/net10.0-android</c>), where the
    /// build writes what it generates; null when the project was not built.
    /// </summary>
    private string? IntermediateDirectory(string baseIntermediate, string targetFramework)
    {
        var directory = _properties["MSBuildProjectDirectory"];
        var configuration = _properties["Configuration"];
        var candidates = new List<string>();
        if (_properties.TryGetValue("IntermediateOutputPath", out var configured) && Expand(configured) is { Length: > 0 } expanded)
        {
            candidates.Add(Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(expanded))));
        }

        candidates.Add(Path.Combine(baseIntermediate, configuration, targetFramework));
        candidates.Add(Path.Combine(baseIntermediate, _properties["Platform"], configuration, targetFramework));
        return candidates.FirstOrDefault(System.IO.Directory.Exists);
    }

    /// <summary>
    /// The namespaces the build imports in every file, as written to <c>&lt;Assembly&gt;.GlobalUsings.g.cs</c>: the
    /// SDK's implicit usings and the <c>Using</c> items of the project and of its packages (MAUI's). Null when it
    /// is not there.
    /// </summary>
    private static List<string>? BuiltGlobalUsings(string? intermediate, string assemblyName)
    {
        var file = intermediate == null ? null : Path.Combine(intermediate, assemblyName + ".GlobalUsings.g.cs");
        if (file == null || !File.Exists(file))
        {
            return null;
        }

        try
        {
            return File.ReadLines(file)
                .Select(line => GlobalUsingLine().Match(line))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s*global\s+using\s+(?:global::)?([\w.]+)\s*;")]
    private static partial Regex GlobalUsingLine();

    /// <summary>The C# an Android binding project generates from its Java libraries (<c>obj/.../generated/src</c>).</summary>
    private IEnumerable<string> BindingSources(string? intermediate)
    {
        var generated = intermediate == null ? null : Path.Combine(intermediate, "generated", "src");
        if (!_bindsJava || generated == null || !System.IO.Directory.Exists(generated))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(generated, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// The XAML files of a MAUI or WPF project: those of <c>MauiXaml</c>, <c>Page</c> and
    /// <c>ApplicationDefinition</c> items, and by default every .xaml file outside bin and obj.
    /// </summary>
    private List<string> XamlFiles(bool isSdk)
    {
        var files = new List<string>(_xamlItems);
        var useXaml = IsTrueProperty("UseMaui") || IsTrueProperty("UseWPF");
        if (isSdk && useXaml && Expand("$(EnableDefaultItems)") is not "false")
        {
            var directory = _properties["MSBuildProjectDirectory"];
            var excluded = new List<string>
            {
                Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(Expand("$(BaseOutputPath)")))),
                Path.GetFullPath(Path.Combine(directory, SolutionDiscovery.NormalizeSeparators(Expand("$(BaseIntermediateOutputPath)")))),
            };
            files.AddRange(EnumerateSources(directory, excluded, "*.xaml"));
        }

        return files.Distinct(SymbolIndexBuilder.PathComparer).ToList();
    }

    private bool IsTrueProperty(string name) => string.Equals(Expand($"$({name})"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The C# the XAML compiler makes of a XAML file with an <c>x:Class</c>: a part of the class with a field for each
    /// named element and <c>InitializeComponent</c>. Each member is written on the line of its name in the XAML, at
    /// its column when it fits, so that the file read as C# puts the declarations where they are in the XAML.
    /// Null for XAML without a class (a resource dictionary) or that does not parse.
    /// </summary>
    internal static string? XamlCode(string path, bool wpf)
    {
        XDocument document;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
            document = XDocument.Parse(string.Join("\n", lines), LoadOptions.SetLineInfo);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }

        var root = document.Root;
        if (root == null || XamlAttribute(root, "Class") is not { } classAttribute || classAttribute.Value.Trim() is not { Length: > 0 } fullName)
        {
            return null;
        }

        var dot = fullName.LastIndexOf('.');
        var ns = dot > 0 ? fullName[..dot] : null;
        var className = fullName[(dot + 1)..];
        var usings = new SortedSet<string>(StringComparer.Ordinal);
        var members = new SortedDictionary<int, StringBuilder>();
        void Add(int line, int column, string prefix, string name, string suffix)
        {
            if (!members.TryGetValue(line, out var text))
            {
                members[line] = text = new StringBuilder();
            }

            // The name at its column in the XAML when the declaration's start fits before it
            var padding = column - 1 - text.Length - prefix.Length;
            if (padding > 0 && line > 1)
            {
                text.Append(' ', padding);
            }
            else if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(prefix).Append(name).Append(suffix);
        }

        var (classLine, classColumn) = ValuePosition(classAttribute, lines);
        Add(classLine, classColumn + (dot + 1), (wpf ? "public" : "private") + " void ", "InitializeComponent", "() { }");
        foreach (var element in NamedElements(root))
        {
            var nameAttribute = XamlAttribute(element, "Name") ?? (wpf ? element.Attribute("Name") : null);
            if (nameAttribute == null || !IsIdentifier(nameAttribute.Value) || TypeName(element, usings) is not { } type)
            {
                continue;
            }

            var modifier = XamlAttribute(element, "FieldModifier")?.Value.Trim().ToLowerInvariant() switch
            {
                "public" => "public",
                "private" => "private",
                "protected" => "protected",
                "internal" or "notpublic" or "friend" => "internal",
                _ => wpf ? "internal" : "private",
            };
            var (line, column) = ValuePosition(nameAttribute, lines);
            Add(line, column, $"{modifier} {type} ", nameAttribute.Value, ";");
        }

        var code = new StringBuilder();
        var header = string.Concat(usings.Select(u => $"using {u}; ")) + (ns != null ? $"namespace {ns} {{ " : "") + $"partial class {className} {{";
        var last = Math.Max(1, members.Count == 0 ? 1 : members.Keys.Max());
        for (var line = 1; line <= last; line++)
        {
            if (line == 1)
            {
                code.Append(header);
            }

            if (members.TryGetValue(line, out var text))
            {
                code.Append(line == 1 ? " " : "").Append(text);
            }

            code.Append('\n');
        }

        code.Append(ns != null ? "} }\n" : "}\n");
        return code.ToString();
    }

    /// <summary>An attribute of the XAML language namespace: <c>x:Name</c>, <c>x:Class</c>.</summary>
    private static XAttribute? XamlAttribute(XElement element, string name) =>
        XamlLanguages.Select(ns => element.Attribute(XName.Get(name, ns))).FirstOrDefault(a => a != null);

    /// <summary>The elements whose names make fields: not those inside templates, which are instantiated apart.</summary>
    private static IEnumerable<XElement> NamedElements(XElement root)
    {
        var pending = new Stack<XElement>([root]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            yield return element;
            foreach (var child in element.Elements().Reverse())
            {
                if (!child.Name.LocalName.EndsWith("Template", StringComparison.Ordinal))
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>The C# name of an element's type: qualified for a <c>clr-namespace:</c> prefix, else with the vocabulary's namespaces imported.</summary>
    private static string? TypeName(XElement element, SortedSet<string> usings)
    {
        var local = element.Name.LocalName;
        if (local.Contains('.') || !IsIdentifier(local))
        {
            return null;
        }

        var xmlns = element.Name.NamespaceName;
        foreach (var scheme in new[] { "clr-namespace:", "using:" })
        {
            if (xmlns.StartsWith(scheme, StringComparison.Ordinal))
            {
                var clr = xmlns[scheme.Length..].Split(';')[0].Trim();
                return clr.Length == 0 ? local : $"global::{clr}.{local}";
            }
        }

        if (XamlNamespaces.TryGetValue(xmlns, out var namespaces))
        {
            usings.UnionWith(namespaces);
        }

        return local;
    }

    /// <summary>The 1-based line and column of an attribute's value.</summary>
    private static (int Line, int Column) ValuePosition(XAttribute attribute, string[] lines)
    {
        var info = (IXmlLineInfo)attribute;
        if (!info.HasLineInfo() || info.LineNumber < 1 || info.LineNumber > lines.Length)
        {
            return (1, 1);
        }

        var text = lines[info.LineNumber - 1];
        var start = Math.Max(0, info.LinePosition - 1);
        var quote = text.IndexOfAny(['"', '\''], Math.Min(start, text.Length));
        return (info.LineNumber, quote >= 0 ? quote + 2 : info.LinePosition);
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}
