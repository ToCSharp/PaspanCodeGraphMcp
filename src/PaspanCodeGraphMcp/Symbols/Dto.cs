using System.Text.RegularExpressions;
using System.Xml.Linq;
using PaspanCodeGraph;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraphMcp.Symbols;

/// <param name="Line">1-based line of the declared name.</param>
/// <param name="Column">1-based column of the declared name, in UTF-16 code units.</param>
/// <param name="LineText">The source line, trimmed.</param>
public sealed record LocationDto(string File, int Line, int Column, string? LineText, bool IsTest);

/// <summary>Compact, agent-facing view of a symbol. <see cref="Id"/> can be passed back to any tool.</summary>
public sealed record SymbolDto(
    string Id,
    string Kind,
    string Name,
    string Signature,
    string? ContainingType,
    string? Namespace,
    string? Project,
    LocationDto? Location);

/// <summary>Returned instead of a result when a name matched several declarations.</summary>
public sealed record AmbiguousSymbol(string Query, int TotalCandidates, IReadOnlyList<SymbolDto> Candidates)
{
    public bool Resolved => false;

    public string Hint => TotalCandidates > Candidates.Count
        ? $"{TotalCandidates} declarations match; showing {Candidates.Count}. Repeat the call with one of the candidate ids, or narrow the name with find_symbol (dotted path, exact=true, kind filter)."
        : "Several declarations match. Repeat the call with one of the candidate ids.";
}

public static partial class SymbolFormatter
{
    public static SymbolDto ToDto(CodeSymbol symbol, WorkspaceSnapshot snapshot, bool withLineText = true) => new(
        symbol.Id,
        symbol.Kind.ToString(),
        symbol.Name,
        symbol.Signature,
        symbol.ContainingType?.QualifiedTypeName(),
        symbol.Namespace,
        symbol.Project,
        symbol.Declarations.Count == 0 ? null : ToLocation(symbol.Declarations[0], snapshot, withLineText));

    /// <summary>The names of a type and its containing types: <c>Outer.Inner</c>.</summary>
    public static string QualifiedTypeName(this CodeSymbol type) =>
        type.ContainingType is { } container ? $"{container.QualifiedTypeName()}.{type.Name}" : type.Name;

    public static LocationDto ToLocation(SourceLocation location, WorkspaceSnapshot snapshot, bool withLineText = true)
    {
        string? lineText = null;
        if (withLineText && snapshot.Documents.TryGetValue(location.File, out var document) && location.Line <= document.Lines.LineCount)
        {
            lineText = document.Lines.GetLineSpan(location.Line).GetText(document.Utf8.Span).Trim();
        }

        return new LocationDto(location.File, location.Line, location.Column, lineText, IsTestPath(location.File));
    }

    public static bool IsTestPath(string? path) => path is not null && TestPathRegex().IsMatch(path);

    /// <summary>Plain text of the &lt;summary&gt; of a documentation comment, or null.</summary>
    public static string? Summary(CodeSymbol symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol.Documentation))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse("<doc>" + symbol.Documentation + "</doc>");
            var summary = document.Descendants("summary").FirstOrDefault();
            if (summary is null)
            {
                return null;
            }

            var text = string.Concat(summary.DescendantNodes().Select(n => n switch
            {
                XText t => t.Value,
                XElement { Name.LocalName: "see" or "seealso" or "paramref" or "typeparamref" } e when !e.Nodes().Any() =>
                    (string?)e.Attribute("cref") ?? (string?)e.Attribute("name") ?? (string?)e.Attribute("langword") ?? "",
                _ => "",
            }));
            text = WhitespaceRegex().Replace(text, " ").Trim();
            return text.Length == 0 ? null : text.Length > 400 ? text[..400] + "…" : text;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(^|[\\/])[^\\/]*Tests?([\\/.]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TestPathRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
