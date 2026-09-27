using System.Text;

namespace PaspanCodeGraph.Search;

/// <summary>
/// Splits code and text into search terms: identifiers are split at case changes, digits and underscores
/// (<c>GetHTTPResponse2</c> gives <c>get</c>, <c>http</c>, <c>response</c>, <c>2</c>), everything is lower-cased and
/// plural and verb endings are removed, so that "parses files" matches <c>ParseFile</c>.
/// </summary>
public static class Tokenizer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // English
        "a", "an", "the", "of", "to", "in", "on", "at", "by", "for", "from", "with", "and", "or", "not", "is", "are", "be",
        "it", "its", "this", "that", "as", "if", "when", "which", "what", "where", "how", "do", "does", "can", "into", "than",
        "then", "so", "no", "all", "any", "each", "one", "there", "they", "we", "you", "i",

        // C# keywords that say nothing about what code does
        "public", "private", "protected", "internal", "static", "readonly", "sealed", "abstract", "virtual", "override",
        "partial", "const", "void", "var", "return", "new", "null", "true", "false", "class", "struct", "interface",
        "record", "enum", "namespace", "using", "else", "int", "bool", "string", "object", "out", "ref", "in", "params",
        "async", "await", "task", "get", "set", "init", "value", "see", "cref", "summary", "param", "name", "returns",
        "langword", "paramref", "typeparam", "remarks", "c", "para", "system", "collections", "generic", "linq",
    };

    /// <summary>The terms of <paramref name="text"/>, in order, with repeats.</summary>
    public static List<string> Terms(string text)
    {
        var terms = new List<string>();
        AddTerms(text, terms);
        return terms;
    }

    public static void AddTerms(ReadOnlySpan<char> text, List<string> terms)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            AddWord(text[start..i], terms);
        }
    }

    /// <summary>The terms of UTF-8 text (source code), without decoding it as a whole.</summary>
    public static void AddTerms(ReadOnlySpan<byte> utf8, List<string> terms)
    {
        Span<char> buffer = stackalloc char[128];
        var i = 0;
        while (i < utf8.Length)
        {
            var b = utf8[i];
            if (!IsWordByte(b))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < utf8.Length && IsWordByte(utf8[i]))
            {
                i++;
            }

            var word = utf8[start..i];
            if (word.Length <= buffer.Length && System.Text.Unicode.Utf8.ToUtf16(word, buffer, out _, out var written) == System.Buffers.OperationStatus.Done)
            {
                AddWord(buffer[..written], terms);
            }
            else
            {
                AddWord(Encoding.UTF8.GetString(word), terms);
            }
        }
    }

    private static bool IsWordByte(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or >= 0x80;

    /// <summary>
    /// Splits one word (<c>parseHTTPRequest2</c>) into its parts, and adds the whole word too when it has several,
    /// so that "pagerank" finds <c>PageRank</c>.
    /// </summary>
    private static void AddWord(ReadOnlySpan<char> word, List<string> terms)
    {
        var count = terms.Count;
        var start = 0;
        for (var i = 1; i <= word.Length; i++)
        {
            var boundary = i == word.Length
                || (char.IsUpper(word[i]) && char.IsLower(word[i - 1]))
                || (char.IsUpper(word[i]) && i + 1 < word.Length && char.IsLower(word[i + 1]) && char.IsUpper(word[i - 1]))
                || (char.IsDigit(word[i]) != char.IsDigit(word[i - 1]));
            if (boundary)
            {
                Add(word[start..i], terms);
                start = i;
            }
        }

        if (terms.Count - count > 1)
        {
            Add(word, terms);
        }
    }

    private static void Add(ReadOnlySpan<char> part, List<string> terms)
    {
        if (part.Length == 0 || part.Length > 40)
        {
            return;
        }

        var lower = part.ToString().ToLowerInvariant();
        if (StopWords.Contains(lower) || (lower.Length == 1 && !char.IsDigit(lower[0])))
        {
            return;
        }

        terms.Add(Stem(lower));
    }

    /// <summary>A light stemmer: "parses", "parsed" and "parsing" give "pars"; "entries" gives "entry".</summary>
    public static string Stem(string word)
    {
        if (word.Length <= 3 || !char.IsLetter(word[^1]))
        {
            return word;
        }

        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4)
        {
            return word[..^3] + "y";
        }

        if (word.EndsWith("sses", StringComparison.Ordinal))
        {
            return word[..^2];
        }

        if (word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 5)
        {
            return Trim(word[..^3]);
        }

        if (word.EndsWith("ed", StringComparison.Ordinal) && word.Length > 4)
        {
            return Trim(word[..^2]);
        }

        if (word.EndsWith("es", StringComparison.Ordinal) && word.Length > 4 && word[^3] is 's' or 'x' or 'z' or 'h')
        {
            return Trim(word[..^2]);
        }

        if (word[^1] == 's' && word[^2] is not ('s' or 'u' or 'i'))
        {
            return Trim(word[..^1]);
        }

        return Trim(word);
    }

    // "parse", "parsed" and "parses" meet at "pars"; "cache" and "cached" at "cach"
    private static string Trim(string stem) => stem.Length > 3 && stem[^1] == 'e' ? stem[..^1] : stem;
}
