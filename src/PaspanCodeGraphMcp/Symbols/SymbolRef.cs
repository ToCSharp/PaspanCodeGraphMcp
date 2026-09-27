using System.Text.RegularExpressions;

namespace PaspanCodeGraphMcp.Symbols;

/// <summary>
/// How a tool argument names a symbol: a documentation-comment id (<c>M:Ns.Type.Method(System.String)</c>,
/// <c>T:Ns.Type</c>), a 1-based source position (<c>src/File.cs:120:17</c>) or a (dotted) name.
/// </summary>
public abstract partial record SymbolRef
{
    public sealed record DeclarationId(string Id) : SymbolRef;

    public sealed record Position(string FilePath, int Line, int Column) : SymbolRef;

    public sealed record Name(string Query) : SymbolRef;

    public static SymbolRef Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("symbol must not be empty");
        }

        input = input.Trim();
        if (DeclarationIdRegex().IsMatch(input))
        {
            return new DeclarationId(input);
        }

        var position = PositionRegex().Match(input);
        if (position.Success)
        {
            return new Position(position.Groups[1].Value, int.Parse(position.Groups[2].Value), int.Parse(position.Groups[3].Value));
        }

        return new Name(input);
    }

    [GeneratedRegex("^[TMPFEN]:[A-Za-z_@]")]
    private static partial Regex DeclarationIdRegex();

    [GeneratedRegex(@"^(.+?\.cs):(\d+):(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PositionRegex();
}
