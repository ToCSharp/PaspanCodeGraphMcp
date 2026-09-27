# PaspanCodeGraphMcp

## Build and test

- Build: `dotnet build PaspanCodeGraphMcp.slnx`
- Run tests: `dotnet run --project tests/PaspanCodeGraph.Tests`

Do not use `dotnet test`: the test project uses Microsoft.Testing.Platform, and the VSTest-based `dotnet test` is rejected by the .NET 10 SDK.

## Layout

- `external/PaspanParsers` is a git submodule; the C# parser changes go to that repository, not here.
- `src/PaspanCodeGraph.Core`: `CodeSymbol` (with its hierarchy links) and `SymbolIndex` (search, declarations and references by file), no parser types.
- `src/PaspanCodeGraph.CSharp`: `CSharpSymbolCollector` turns PaspanParsers syntax trees into symbols and references in three passes (types, members, references); `CSharpBinder` binds type names; `CSharpHierarchy` links bases, overrides and interface implementations; `DocumentationIds` builds ids.
- `src/PaspanCodeGraph.Workspace`: solution discovery, project file reading without MSBuild, parallel parsing into a `WorkspaceSnapshot`.
- `src/PaspanCodeGraphMcp`: the stdio MCP server and its tools.
- `Directory.Build.props` lives in `src/` (and `tests/` imports it), not at the root, so that it does not apply to the submodule's projects.

## Oracles

- `DocumentationIdOracleTests` loads the PaspanParsers submodule's solution and checks ids and name locations against Roslyn's `ISymbol.GetDocumentationCommentId()` and `Locations`; `DeclarationFixtureTests` does the same for a file with declarations of every kind. Both must pass.
- `HierarchyOracleTests` (the PaspanParsers solution) and `HierarchyFixtureTests` (hard cases) compare base types, overrides, interface implementations and type references with Roslyn through `HierarchyComparison`. Both must pass.
- Ids follow XML documentation comments, not `DocumentationCommentId.CreateDeclarationId`, which in Roslyn 5 appends `~ReturnType` to every method.
