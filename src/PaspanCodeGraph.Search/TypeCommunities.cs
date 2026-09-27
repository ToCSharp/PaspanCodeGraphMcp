namespace PaspanCodeGraph.Search;

/// <param name="Types">The graph nodes of the top-level types in the community, most central first.</param>
/// <param name="Rank">The summed centrality of the types and their members.</param>
public sealed record Community(int Number, IReadOnlyList<int> Types, double Rank);

/// <summary>
/// Clusters of top-level types that use each other more than they use the rest: the graph's edges between
/// members and types, folded onto their top-level types, grouped with <see cref="Communities.Louvain"/>.
/// </summary>
public sealed class TypeCommunities
{
    private readonly int[] _communityOfNode;
    private readonly Dictionary<(int, int), double> _links;

    private TypeCommunities(int[] communityOfNode, List<Community> communities, Dictionary<(int, int), double> links, double modularity)
    {
        _communityOfNode = communityOfNode;
        All = communities;
        _links = links;
        Modularity = modularity;
    }

    /// <summary>Largest first.</summary>
    public IReadOnlyList<Community> All { get; }

    public double Modularity { get; }

    /// <summary>The community of any node: that of its top-level type; -1 for a node outside any type.</summary>
    public int Of(int node) => _communityOfNode[node];

    /// <summary>The weight of the uses between two communities (both directions).</summary>
    public double Link(int a, int b) => _links.GetValueOrDefault(a <= b ? (a, b) : (b, a));

    public IEnumerable<(int A, int B, double Weight)> Links => _links.Select(l => (l.Key.Item1, l.Key.Item2, l.Value));

    /// <summary>The outermost type of a symbol (itself for a top-level type), or null outside types.</summary>
    public static CodeSymbol? TopLevelType(CodeSymbol symbol)
    {
        CodeSymbol? type = null;
        for (var s = symbol; s != null; s = s.Container)
        {
            if (s.Kind.IsType() || s.Kind == SymbolKind.Extension)
            {
                type = s;
            }
        }

        return type;
    }

    public static TypeCommunities Build(CodeGraph graph)
    {
        // Each node's top-level type, as an index among the types
        var types = new List<int>();
        var typeIndex = new Dictionary<int, int>();
        var topOf = new int[graph.Nodes.Count];
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            topOf[i] = -1;
            if (TopLevelType(graph.Nodes[i]) is { } top && graph.NodeOf(top) is { } topNode)
            {
                if (!typeIndex.TryGetValue(topNode, out var index))
                {
                    typeIndex[topNode] = index = types.Count;
                    types.Add(topNode);
                }

                topOf[i] = index;
            }
        }

        var edges = new List<(int, int, double)>();
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            foreach (var edge in graph.Out(i))
            {
                if (edge.Kind != EdgeKind.Contains && edge.Confidence != Confidence.NameOnly && topOf[edge.From] >= 0 && topOf[edge.To] >= 0 && topOf[edge.From] != topOf[edge.To])
                {
                    edges.Add((topOf[edge.From], topOf[edge.To], 1 + Math.Log(edge.Count)));
                }
            }
        }

        var membership = Communities.Louvain(types.Count, edges);
        var modularity = Communities.Modularity(types.Count, edges, membership);

        var rankOfType = new double[types.Count];
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            if (topOf[i] >= 0)
            {
                rankOfType[topOf[i]] += graph.Rank[i];
            }
        }

        var communities = membership
            .Select((c, t) => (Community: c, Type: t))
            .GroupBy(x => x.Community)
            .OrderBy(g => g.Key)
            .Select(g => new Community(
                g.Key,
                g.OrderByDescending(x => rankOfType[x.Type]).Select(x => types[x.Type]).ToList(),
                g.Sum(x => rankOfType[x.Type])))
            .ToList();

        var communityOfNode = topOf.Select(t => t < 0 ? -1 : membership[t]).ToArray();
        var links = new Dictionary<(int, int), double>();
        foreach (var (a, b, w) in edges)
        {
            var (ca, cb) = (membership[a], membership[b]);
            if (ca != cb)
            {
                var key = ca <= cb ? (ca, cb) : (cb, ca);
                links[key] = links.GetValueOrDefault(key) + w;
            }
        }

        return new TypeCommunities(communityOfNode, communities, links, modularity);
    }
}
