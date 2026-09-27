namespace PaspanCodeGraph.Search;

/// <param name="Node">The node of the <see cref="CodeGraph"/>.</param>
/// <param name="Score">BM25 relevance times the centrality boost.</param>
/// <param name="Terms">The query terms (or their completions) the symbol matched.</param>
public sealed record SearchHit(int Node, double Score, IReadOnlyList<string> Terms);

/// <summary>
/// BM25 over the types and members of a <see cref="CodeGraph"/>. A symbol's document is made of its name (split
/// into words, weighted most), its containing type and namespace, its documentation comment, its signature, the
/// name of its file and, for members, the identifiers, strings and comments of its body. A query term also
/// matches the terms it begins ("watch" finds "watcher"), with less weight, and the ranking is boosted by the
/// symbol's centrality in the graph.
/// </summary>
public sealed class SearchIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>How much centrality can raise a score: the most central symbol gets ×(1 + this).</summary>
    private const double RankWeight = 0.5;

    private readonly CodeGraph _graph;
    private readonly Dictionary<string, List<(int Document, float Frequency)>> _postings = new(StringComparer.Ordinal);
    private readonly string[] _vocabulary;
    private readonly float[] _lengths;
    private readonly double _averageLength;

    private SearchIndex(CodeGraph graph, List<Dictionary<string, float>> documents)
    {
        _graph = graph;
        _lengths = new float[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            foreach (var (term, frequency) in documents[i])
            {
                if (!_postings.TryGetValue(term, out var list))
                {
                    _postings[term] = list = [];
                }

                list.Add((i, frequency));
                _lengths[i] += frequency;
            }
        }

        _averageLength = documents.Count == 0 ? 1 : Math.Max(1, _lengths.Average());
        _vocabulary = _postings.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public int TermCount => _vocabulary.Length;

    /// <param name="source">The UTF-8 text of a file by its path; empty when it is not available.</param>
    public static SearchIndex Build(CodeGraph graph, Func<string, ReadOnlyMemory<byte>> source)
    {
        var documents = new Dictionary<string, float>[graph.Nodes.Count];
        Parallel.For(0, graph.Nodes.Count, i => documents[i] = Document(graph.Nodes[i], source));
        return new SearchIndex(graph, documents.ToList());
    }

    private static Dictionary<string, float> Document(CodeSymbol symbol, Func<string, ReadOnlyMemory<byte>> source)
    {
        var document = new Dictionary<string, float>(StringComparer.Ordinal);
        var terms = new List<string>();

        void Field(float weight)
        {
            foreach (var term in terms)
            {
                document[term] = document.GetValueOrDefault(term) + weight;
            }

            terms.Clear();
        }

        Tokenizer.AddTerms(symbol.Name, terms);
        Field(6);
        if (symbol.ContainingType is { } type)
        {
            Tokenizer.AddTerms(type.Name, terms);
            Field(2);
        }

        Tokenizer.AddTerms(symbol.Namespace, terms);
        Tokenizer.AddTerms(symbol.Signature, terms);
        Field(1);
        Tokenizer.AddTerms(symbol.Documentation, terms);
        Field(2);
        if (symbol.Declarations.Count > 0)
        {
            Tokenizer.AddTerms(Path.GetFileNameWithoutExtension(symbol.Declarations[0].File), terms);
            Field(1);
        }

        if (symbol.Kind.IsType())
        {
            // A type is described by its members' names, not by all their code
            foreach (var member in symbol.Members)
            {
                Tokenizer.AddTerms(member.Name, terms);
            }

            Field(0.5f);
        }
        else
        {
            foreach (var location in symbol.Declarations)
            {
                var text = source(location.File);
                if (location.End <= text.Length && location.Start < location.End)
                {
                    Tokenizer.AddTerms(text.Span[location.Start..location.End], terms);
                }
            }

            Field(1);
        }

        return document;
    }

    /// <summary>
    /// The symbols that best match <paramref name="query"/>, best first; <paramref name="weight"/> can lower or
    /// raise a symbol (0 leaves it out).
    /// </summary>
    public List<SearchHit> Search(string query, int limit, Func<CodeSymbol, double>? weight = null)
    {
        var queryTerms = Tokenizer.Terms(query).Distinct(StringComparer.Ordinal).ToList();
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            // "graph cache" also looks for GraphCache as one name
            queryTerms.Add(string.Concat(words).ToLowerInvariant());
        }

        var scores = new Dictionary<int, (double Score, List<string> Terms)>();
        var n = _lengths.Length;
        foreach (var term in queryTerms)
        {
            var expansions = new List<(string Term, double Factor)>();
            if (_postings.ContainsKey(term))
            {
                expansions.Add((term, 1));
            }

            if (term.Length >= 3)
            {
                var start = Array.BinarySearch(_vocabulary, term, StringComparer.Ordinal);
                start = start < 0 ? ~start : start + 1;
                for (var i = start; i < _vocabulary.Length && i < start + 30 && _vocabulary[i].StartsWith(term, StringComparison.Ordinal); i++)
                {
                    expansions.Add((_vocabulary[i], 0.4));
                }
            }

            // Per document, the best of the term and its completions
            var best = new Dictionary<int, (double Score, string Term)>();
            foreach (var (expansion, factor) in expansions)
            {
                var postings = _postings[expansion];
                var idf = Math.Log(1 + (n - postings.Count + 0.5) / (postings.Count + 0.5));
                foreach (var (document, frequency) in postings)
                {
                    var score = factor * idf * frequency * (K1 + 1) / (frequency + K1 * (1 - B + B * _lengths[document] / _averageLength));
                    if (!best.TryGetValue(document, out var current) || current.Score < score)
                    {
                        best[document] = (score, expansion);
                    }
                }
            }

            foreach (var (document, (score, matched)) in best)
            {
                if (!scores.TryGetValue(document, out var entry))
                {
                    scores[document] = entry = (0, []);
                }

                if (!entry.Terms.Contains(matched))
                {
                    entry.Terms.Add(matched);
                }

                scores[document] = (entry.Score + score, entry.Terms);
            }
        }

        // A query that is the symbol's name, written as one word or in parts, ranks it first
        var joined = string.Concat(words).ToLowerInvariant();
        var hits = new List<SearchHit>();
        foreach (var (document, (score, terms)) in scores)
        {
            var symbol = _graph.Nodes[document];
            var factor = (weight?.Invoke(symbol) ?? 1) * (symbol.Name.Equals(joined, StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            if (factor > 0)
            {
                hits.Add(new SearchHit(document, score * factor * (1 + RankWeight * _graph.RankPercentile(document)), terms));
            }
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => _graph.Nodes[h.Node].Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }
}
