namespace PaspanCodeGraph.Search;

/// <summary>
/// Groups the nodes of an undirected weighted graph into communities with the Louvain method: nodes move to the
/// neighboring community that most raises modularity, then each community becomes one node, until nothing moves.
/// Deterministic: nodes are visited in order.
/// </summary>
public static class Communities
{
    /// <summary>The community of each node, numbered from 0 by decreasing size.</summary>
    /// <param name="edges">Undirected edges; parallel edges add up and self-loops count once.</param>
    public static int[] Louvain(int nodeCount, IEnumerable<(int A, int B, double Weight)> edges, double resolution = 1.0)
    {
        var adjacency = Adjacency(nodeCount, edges);
        var membership = Enumerable.Range(0, nodeCount).ToArray();
        for (var level = 0; level < 20; level++)
        {
            var (partition, moved) = OneLevel(adjacency, resolution);
            if (!moved)
            {
                break;
            }

            // Renumber, map the original nodes, and fold each community into one node
            var numbers = new Dictionary<int, int>();
            foreach (var c in partition)
            {
                numbers.TryAdd(c, numbers.Count);
            }

            for (var i = 0; i < membership.Length; i++)
            {
                membership[i] = numbers[partition[membership[i]]];
            }

            var folded = new List<(int, int, double)>();
            for (var i = 0; i < adjacency.Length; i++)
            {
                foreach (var (j, w) in adjacency[i])
                {
                    if (i <= j)
                    {
                        folded.Add((numbers[partition[i]], numbers[partition[j]], w));
                    }
                }
            }

            adjacency = Adjacency(numbers.Count, folded);
        }

        // Largest community first
        var order = membership.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Min(c => c)).Select(g => g.Key).ToList();
        var rename = new Dictionary<int, int>();
        for (var i = 0; i < order.Count; i++)
        {
            rename[order[i]] = i;
        }

        return membership.Select(c => rename[c]).ToArray();
    }

    private static List<(int Node, double Weight)>[] Adjacency(int nodeCount, IEnumerable<(int A, int B, double Weight)> edges)
    {
        var sums = new Dictionary<(int, int), double>();
        foreach (var (a, b, w) in edges)
        {
            var key = a <= b ? (a, b) : (b, a);
            sums[key] = sums.GetValueOrDefault(key) + w;
        }

        var adjacency = new List<(int, double)>[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            adjacency[i] = [];
        }

        foreach (var ((a, b), w) in sums)
        {
            adjacency[a].Add((b, w));
            if (a != b)
            {
                adjacency[b].Add((a, w));
            }
        }

        return adjacency;
    }

    private static (int[] Partition, bool Moved) OneLevel(List<(int Node, double Weight)>[] adjacency, double resolution)
    {
        var n = adjacency.Length;
        var degree = new double[n];
        var totalWeight = 0.0;
        for (var i = 0; i < n; i++)
        {
            foreach (var (j, w) in adjacency[i])
            {
                // A self-loop counts twice in the degree, as in the modularity formula
                degree[i] += j == i ? 2 * w : w;
            }

            totalWeight += degree[i];
        }

        var community = Enumerable.Range(0, n).ToArray();
        if (totalWeight == 0)
        {
            return (community, false);
        }

        var total = (double[])degree.Clone();
        var moved = false;
        var weightTo = new Dictionary<int, double>();
        for (var pass = 0; pass < 50; pass++)
        {
            var movedInPass = false;
            for (var i = 0; i < n; i++)
            {
                var current = community[i];
                weightTo.Clear();
                foreach (var (j, w) in adjacency[i])
                {
                    if (j != i)
                    {
                        weightTo[community[j]] = weightTo.GetValueOrDefault(community[j]) + w;
                    }
                }

                // Take the node out, then put it where the gain is largest (staying if nothing is better)
                total[current] -= degree[i];
                var best = current;
                var bestGain = weightTo.GetValueOrDefault(current) - resolution * total[current] * degree[i] / totalWeight;
                foreach (var (c, w) in weightTo)
                {
                    var gain = w - resolution * total[c] * degree[i] / totalWeight;
                    if (gain > bestGain + 1e-12)
                    {
                        best = c;
                        bestGain = gain;
                    }
                }

                total[best] += degree[i];
                if (best != current)
                {
                    community[i] = best;
                    movedInPass = true;
                    moved = true;
                }
            }

            if (!movedInPass)
            {
                break;
            }
        }

        return (community, moved);
    }

    /// <summary>The modularity of a partition: how much denser the edges inside communities are than chance.</summary>
    public static double Modularity(int nodeCount, IEnumerable<(int A, int B, double Weight)> edges, int[] community)
    {
        var adjacency = Adjacency(nodeCount, edges);
        var totalWeight = 0.0;
        var degree = new double[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            foreach (var (j, w) in adjacency[i])
            {
                degree[i] += j == i ? 2 * w : w;
            }

            totalWeight += degree[i];
        }

        if (totalWeight == 0)
        {
            return 0;
        }

        var inside = 0.0;
        var totals = new Dictionary<int, double>();
        for (var i = 0; i < nodeCount; i++)
        {
            totals[community[i]] = totals.GetValueOrDefault(community[i]) + degree[i];
            foreach (var (j, w) in adjacency[i])
            {
                if (community[i] == community[j])
                {
                    inside += j == i ? 2 * w : w;
                }
            }
        }

        return inside / totalWeight - totals.Values.Sum(t => t * t) / (totalWeight * totalWeight);
    }
}
