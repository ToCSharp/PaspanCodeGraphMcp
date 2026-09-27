using System.Runtime.CompilerServices;
using PaspanCodeGraph;
using PaspanCodeGraph.Search;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraphMcp.Symbols;

/// <summary>
/// The code graph, search index and type communities of a snapshot's symbol index, built on first use and kept
/// as long as the index lives (an update that changes nothing keeps the index, and so these).
/// </summary>
public sealed class GraphRag
{
    private static readonly ConditionalWeakTable<SymbolIndex, GraphRag> Cache = new();

    private readonly Lazy<SearchIndex> _search;
    private readonly Lazy<TypeCommunities> _communities;

    private GraphRag(WorkspaceSnapshot snapshot)
    {
        Graph = CodeGraph.Build(snapshot.Index);
        var documents = snapshot.Documents;
        _search = new Lazy<SearchIndex>(() => SearchIndex.Build(Graph, file => documents.TryGetValue(file, out var document) ? document.Utf8 : ReadOnlyMemory<byte>.Empty));
        _communities = new Lazy<TypeCommunities>(() => TypeCommunities.Build(Graph));
    }

    public static GraphRag For(WorkspaceSnapshot snapshot) => Cache.GetValue(snapshot.Index, _ => new GraphRag(snapshot));

    public CodeGraph Graph { get; }

    public SearchIndex Search => _search.Value;

    public TypeCommunities Communities => _communities.Value;
}
