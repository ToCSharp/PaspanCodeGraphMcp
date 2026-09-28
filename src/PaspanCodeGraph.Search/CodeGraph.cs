namespace PaspanCodeGraph.Search;

/// <summary>What an edge of the <see cref="CodeGraph"/> stands for.</summary>
public enum EdgeKind
{
    /// <summary>A member calls a method, constructor or operator.</summary>
    Calls,

    /// <summary>A member uses a property, field, event or indexer.</summary>
    Uses,

    /// <summary>A member or type names a type (parameters, locals, casts, base lists, attributes).</summary>
    ReferencesType,

    /// <summary>A class derives from a base class.</summary>
    Inherits,

    /// <summary>A type implements or extends an interface.</summary>
    Implements,

    /// <summary>A member overrides a base member.</summary>
    Overrides,

    /// <summary>A member implements an interface member.</summary>
    ImplementsMember,

    /// <summary>A type declares a member or a nested type.</summary>
    Contains,
}

/// <param name="Count">How many references the edge stands for.</param>
/// <param name="Confidence">The surest of those references; <see cref="Confidence.Exact"/> for the other kinds.</param>
/// <param name="First">The first of those references, for showing where the edge comes from.</param>
public sealed record GraphEdge(int From, int To, EdgeKind Kind, int Count, Confidence Confidence, SymbolReference? First);

/// <summary>
/// The workspace's types and members as a directed graph: references from a member (or a type's base list) to
/// what it uses, base types, overrides, interface implementations and containment. Built once per
/// <see cref="SymbolIndex"/>; immutable.
/// </summary>
public sealed class CodeGraph
{
    private readonly Dictionary<string, int> _numbers;
    private readonly List<GraphEdge>[] _out;
    private readonly List<GraphEdge>[] _in;
    private readonly Lazy<double[]> _rank;
    private readonly Lazy<double[]> _percentiles;

    private CodeGraph(List<CodeSymbol> nodes)
    {
        Nodes = nodes;
        _numbers = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            _numbers[nodes[i].Id] = i;
        }

        _out = new List<GraphEdge>[nodes.Count];
        _in = new List<GraphEdge>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            _out[i] = [];
            _in[i] = [];
        }

        _rank = new Lazy<double[]>(ComputeRank);
        _percentiles = new Lazy<double[]>(ComputePercentiles);
    }

    public IReadOnlyList<CodeSymbol> Nodes { get; }

    public int EdgeCount { get; private set; }

    public int? NodeOf(string id) => _numbers.TryGetValue(id, out var number) ? number : null;

    public int? NodeOf(CodeSymbol symbol) => NodeOf(symbol.Id);

    public IReadOnlyList<GraphEdge> Out(int node) => _out[node];

    public IReadOnlyList<GraphEdge> In(int node) => _in[node];

    /// <summary>
    /// PageRank over the edges that are uses (not containment), with a member's rank flowing also to its type:
    /// how central a symbol is. Sums to 1.
    /// </summary>
    public IReadOnlyList<double> Rank => _rank.Value;

    /// <summary>The rank of a node as a percentile among all nodes, from 0 (least central) to 1.</summary>
    public double RankPercentile(int node) => _percentiles.Value[node];

    private double[] ComputePercentiles()
    {
        var rank = Rank;
        var order = Enumerable.Range(0, Nodes.Count).OrderBy(i => rank[i]).ToArray();
        var result = new double[Nodes.Count];
        for (var i = 0; i < order.Length; i++)
        {
            result[order[i]] = order.Length <= 1 ? 1 : (double)i / (order.Length - 1);
        }

        return result;
    }

    public static CodeGraph Build(SymbolIndex index)
    {
        var nodes = index.Symbols
            .Where(s => !s.IsExternal && s.Kind != SymbolKind.Namespace && s.Declarations.Count > 0)
            .ToList();
        var graph = new CodeGraph(nodes);
        var edges = new Dictionary<(int, int, EdgeKind), (int Count, Confidence Confidence, SymbolReference? First)>();

        void Add(int from, int to, EdgeKind kind, Confidence confidence, SymbolReference? reference)
        {
            if (from == to)
            {
                return;
            }

            var key = (from, to, kind);
            if (edges.TryGetValue(key, out var existing))
            {
                edges[key] = (existing.Count + 1, (Confidence)Math.Min((int)existing.Confidence, (int)confidence), existing.First);
            }
            else
            {
                edges[key] = (1, confidence, reference);
            }
        }

        foreach (var targetId in index.ReferenceTargetIds)
        {
            if (graph.NodeOf(targetId) is not { } to)
            {
                continue;
            }

            var target = nodes[to];
            var kind = target.Kind.IsType() ? EdgeKind.ReferencesType
                : target.Kind.IsCallable() ? EdgeKind.Calls
                : EdgeKind.Uses;
            foreach (var reference in index.ReferencesTo(targetId))
            {
                if (reference.InMember is { } inMember && graph.NodeOf(inMember) is { } from)
                {
                    Add(from, to, kind, reference.Confidence, reference);
                }
            }
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var symbol = nodes[i];
            if (symbol.Container is { } container && graph.NodeOf(container) is { } owner)
            {
                Add(owner, i, EdgeKind.Contains, Confidence.Exact, null);
            }

            foreach (var link in symbol.Bases)
            {
                if (link.Symbol is { } baseType && graph.NodeOf(baseType) is { } to)
                {
                    Add(i, to, baseType.Kind == SymbolKind.Interface ? EdgeKind.Implements : EdgeKind.Inherits, Confidence.Exact, null);
                }
            }

            if (symbol.Overrides is { } overridden && graph.NodeOf(overridden) is { } overriddenNode)
            {
                Add(i, overriddenNode, EdgeKind.Overrides, Confidence.Exact, null);
            }

            foreach (var implemented in symbol.Implements)
            {
                if (graph.NodeOf(implemented) is { } to)
                {
                    Add(i, to, EdgeKind.ImplementsMember, Confidence.Exact, null);
                }
            }
        }

        foreach (var ((from, to, kind), (count, confidence, first)) in edges)
        {
            var edge = new GraphEdge(from, to, kind, count, confidence, first);
            graph._out[from].Add(edge);
            graph._in[to].Add(edge);
        }

        graph.EdgeCount = edges.Count;
        return graph;
    }

    private double[] ComputeRank()
    {
        var n = Nodes.Count;
        if (n == 0)
        {
            return [];
        }

        // Weighted out-links: uses count by the log of their number; a member also passes rank to its type
        var links = new List<(int To, double Weight)>[n];
        for (var i = 0; i < n; i++)
        {
            links[i] = [];
            foreach (var edge in _out[i])
            {
                if (edge.Kind != EdgeKind.Contains && edge.Confidence != Confidence.NameOnly)
                {
                    links[i].Add((edge.To, 1 + Math.Log(edge.Count)));
                }
            }

            foreach (var edge in _in[i])
            {
                if (edge.Kind == EdgeKind.Contains)
                {
                    links[i].Add((edge.From, 0.5));
                }
            }
        }

        var totals = links.Select(l => l.Sum(x => x.Weight)).ToArray();
        const double damping = 0.85;
        var rank = Enumerable.Repeat(1.0 / n, n).ToArray();
        var next = new double[n];
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var dangling = 0.0;
            for (var i = 0; i < n; i++)
            {
                if (totals[i] == 0)
                {
                    dangling += rank[i];
                }
            }

            var baseline = (1 - damping) / n + damping * dangling / n;
            Array.Fill(next, baseline);
            for (var i = 0; i < n; i++)
            {
                if (totals[i] == 0)
                {
                    continue;
                }

                var share = damping * rank[i] / totals[i];
                foreach (var (to, weight) in links[i])
                {
                    next[to] += share * weight;
                }
            }

            (rank, next) = (next, rank);
        }

        return rank;
    }

    /// <summary>
    /// The shortest path of uses from <paramref name="from"/> to <paramref name="to"/>: calls, uses and type
    /// references, and from a base or interface member to the members that override or implement it (a call can
    /// dispatch there); a type reference only as the last step, to a type. Null when there is none within
    /// <paramref name="maxDepth"/> edges.
    /// </summary>
    public List<GraphEdge>? ShortestPath(int from, int to, int maxDepth, bool includeNameOnly = false)
    {
        var previous = new Dictionary<int, GraphEdge?> { [from] = null };
        var frontier = new List<int> { from };
        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var nextFrontier = new List<int>();
            foreach (var node in frontier)
            {
                foreach (var edge in Forward(node, includeNameOnly))
                {
                    if (previous.ContainsKey(edge.To) || (edge.Kind == EdgeKind.ReferencesType && edge.To != to))
                    {
                        continue;
                    }

                    previous[edge.To] = edge;
                    if (edge.To == to)
                    {
                        var path = new List<GraphEdge>();
                        for (var e = edge; e != null; e = previous[e.From])
                        {
                            path.Add(e);
                        }

                        path.Reverse();
                        return path;
                    }

                    nextFrontier.Add(edge.To);
                }
            }

            frontier = nextFrontier;
        }

        return from == to ? [] : null;
    }

    /// <summary>The edges a path of uses follows out of <paramref name="node"/>, with dispatch edges reversed.</summary>
    private IEnumerable<GraphEdge> Forward(int node, bool includeNameOnly)
    {
        foreach (var edge in _out[node])
        {
            if (edge.Kind is EdgeKind.Calls or EdgeKind.Uses or EdgeKind.ReferencesType
                && (includeNameOnly || edge.Confidence != Confidence.NameOnly))
            {
                yield return edge;
            }
        }

        // A call to a virtual or interface member can run its overrides and implementations
        foreach (var edge in _in[node])
        {
            if (edge.Kind is EdgeKind.Overrides or EdgeKind.ImplementsMember)
            {
                yield return edge with { From = edge.To, To = edge.From };
            }
        }
    }

    /// <summary>
    /// What depends on <paramref name="node"/>, breadth first up to <paramref name="maxDepth"/>: the members that use
    /// it and, transitively, those that use them; for a type also the members that use its members and the types
    /// derived from it; for a virtual or interface member also its overrides and implementations, whose signatures
    /// must follow it. Each dependent comes with its depth and the edge it was reached by.
    /// </summary>
    public List<(int Node, int Depth, GraphEdge Edge)> Dependents(int node, int maxDepth, bool includeNameOnly = false)
    {
        var result = new List<(int, int, GraphEdge)>();
        var seen = new HashSet<int> { node };
        var frontier = new List<int> { node };

        // A type is used through its members: they start the search with it
        foreach (var edge in _out[node])
        {
            if (edge.Kind == EdgeKind.Contains && seen.Add(edge.To))
            {
                frontier.Add(edge.To);
            }
        }

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var nextFrontier = new List<int>();
            foreach (var current in frontier)
            {
                foreach (var edge in _in[current])
                {
                    var dependent = edge.Kind switch
                    {
                        EdgeKind.Calls or EdgeKind.Uses or EdgeKind.ReferencesType => includeNameOnly || edge.Confidence != Confidence.NameOnly,
                        EdgeKind.Inherits or EdgeKind.Implements or EdgeKind.Overrides or EdgeKind.ImplementsMember => true,
                        _ => false,
                    };
                    if (dependent && seen.Add(edge.From))
                    {
                        result.Add((edge.From, depth, edge));
                        nextFrontier.Add(edge.From);
                    }
                }
            }

            frontier = nextFrontier;
        }

        return result;
    }
}
