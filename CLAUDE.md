# PaspanCodeGraphMcp

## Build and test

- Build: `dotnet build PaspanCodeGraphMcp.slnx`
- Run tests: `dotnet run --project tests/PaspanCodeGraph.Tests`

Do not use `dotnet test`: the test project uses Microsoft.Testing.Platform, and the VSTest-based `dotnet test` is rejected by the .NET 10 SDK.

## Layout

- `external/PaspanParsers` is a git submodule; the C# parser changes go to that repository, not here.
- `src/PaspanCodeGraph.Core`: `CodeSymbol` (with its hierarchy links) and `SymbolIndex` (search, declarations and references by file), no parser types.
- `src/PaspanCodeGraph.CSharp`: `CSharpSymbolCollector` turns PaspanParsers syntax trees into symbols and references in three passes (types, members, references); `CSharpSymbolCollector.Bodies.cs` binds bodies (locals, expression types, overload resolution, lambdas); `CSharpBinder` binds type names, and `CSharpBinder.Semantics.cs` gives member types, member lookup and conversions over the `SemType` model (`SemanticTypes.cs`, with a small base class library model); `CSharpHierarchy` links bases, overrides and interface implementations; `DocumentationIds` builds ids.
- `src/PaspanCodeGraph.Metadata`: `MetadataCatalog` reads the public types and members of reference assemblies with System.Reflection.Metadata (members on first use), with documentation ids and signatures as `MetaType`.
- `src/PaspanCodeGraph.CSharp/CSharpBinder.External.cs`: types of referenced assemblies get `CodeSymbol`s outside the index (`IsExternal`), so member lookup and overload resolution treat them like workspace types; as `SemType` they stay `ExternalType` with a `Definition`.
- `src/PaspanCodeGraph.Workspace`: solution discovery, project file reading without MSBuild, the referenced assemblies of each project (`ReferenceAssemblies`, `DotnetLocator`), parallel parsing into a `WorkspaceSnapshot`. `WorkspaceLoader.Update` makes a new snapshot from an earlier one, binding again only the files `IncrementalState` picks (by the names of changed declarations); `GraphCache` writes a snapshot to `.paspan/graph.bin` and reads it back; `SourceWatcher` reports file changes, which `WorkspaceHost` turns into updates.
- `src/PaspanCodeGraph.Search`: `CodeGraph` (edges from references and the hierarchy, PageRank, shortest paths, dependents), `SearchIndex` (BM25 with `Tokenizer`), `Communities` (Louvain) and `TypeCommunities`; depends on Core only.
- `src/PaspanCodeGraphMcp`: the stdio MCP server and its tools; `GraphTools` holds the retrieval tools, over a `GraphRag` built once per `SymbolIndex`, and `AnnotationStore` the notes of `annotate`.
- `Directory.Build.props` lives in `src/` (and `tests/` imports it), not at the root, so that it does not apply to the submodule's projects.

## Oracles

- `DocumentationIdOracleTests` loads the PaspanParsers submodule's solution and checks ids and name locations against Roslyn's `ISymbol.GetDocumentationCommentId()` and `Locations`; `DeclarationFixtureTests` does the same for a file with declarations of every kind. Both must pass.
- `HierarchyOracleTests` (the PaspanParsers solution) and `HierarchyFixtureTests` (hard cases) compare base types, overrides, interface implementations, type references and member references with Roslyn through `HierarchyComparison`. Both must pass.
- Member references (`MemberReferences_MatchRoslyn`, also in `SelfOracleTests` on this repository's solution) must keep a precision of at least 95% and a recall of at least 85% for exact and inferred references; the fixture must match exactly. The differences are written to `member-references-report.txt` (and `self-member-references-report.txt`) in the test output directory.
- References to members of referenced assemblies (`ExternalMemberReferences_MatchRoslyn`, in both oracles) must keep a precision of at least 95% and a recall of at least 90%; the differences go to `external-references-report.txt` and `self-external-references-report.txt`. `MetadataCatalogTests` checks the ids read from reference assemblies against Roslyn.
- `IncrementalUpdateTests` edits a copy of this repository and checks that an update, and a load from the graph cache, give the same graph as a full load (`SnapshotComparison`). A new kind of symbol detail or of implicit member use needs a place in `IncrementalState` (the declaration print, or the names that bind everything again), and a new `CodeSymbol` field needs one in `GraphCache` (with a new `SchemaVersion`).
- `SearchQualityTests` asks 20 questions about this repository; the expected symbol must be in the first five `search_code` results for at least 80% of them (`search-quality-report.txt`). When a question starts failing after a rename, update its expected name rather than the ranking.
- Ids follow XML documentation comments, not `DocumentationCommentId.CreateDeclarationId`, which in Roslyn 5 appends `~ReturnType` to every method.
