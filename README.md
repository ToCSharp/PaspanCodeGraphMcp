# PaspanCodeGraphMcp
A 100% managed C#, high-performance MCP server and GraphRAG engine powered by zero-allocation Span-based parser combinators for native codebase analysis by AI agents.

The server reads C# code with [PaspanParsers](https://github.com/ToCSharp/PaspanParsers), not Roslyn, and loads
solutions without MSBuild. It is read-only and talks MCP over stdio.

## Status

Stage 1 of the plan: loading a workspace and navigating its declarations. References, call graphs and
semantic analysis come later.

## Running

```
git clone --recurse-submodules https://github.com/ToCSharp/PaspanCodeGraphMcp.git
cd PaspanCodeGraphMcp
dotnet run --project src/PaspanCodeGraphMcp -- --workspace path/to/Your.slnx
```

MCP client configuration:

```json
{
  "mcpServers": {
    "paspan-code-graph": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/PaspanCodeGraphMcp/src/PaspanCodeGraphMcp", "--", "--workspace", "/path/to/Your.slnx"]
    }
  }
}
```

| Argument | Environment variable | Default |
| --- | --- | --- |
| `--workspace`, `-w` or the first argument: a .sln, .slnx, .csproj or a directory holding one | `PASPAN_WORKSPACE` | none: call `workspace_load` |
| `--configuration`, `-c` | `PASPAN_CONFIGURATION` | `Debug` |
| `--platform`, `-p` | `PASPAN_PLATFORM` | `AnyCPU` |
| | `PASPAN_LOG_LEVEL` | `Information` (logs go to stderr) |

## Tools

| Tool | What it returns |
| --- | --- |
| `workspace_load` | Loads a solution or project; per-project file counts, preprocessor symbols and load problems |
| `workspace_status` | What is loaded, counts, load time, memory |
| `diagnostics` | Projects that could not be read and syntax errors, with positions |
| `find_symbol` | Declarations by (dotted) name: substring, exact or `*`/`?` wildcards |
| `symbol_info` | Signature, documentation summary, accessibility, modifiers, parameters, base types, all declarations |
| `go_to_definition` | Declaration locations, every part of a partial type |
| `type_members` | Members of a type, optionally with those of base types declared in the workspace |
| `file_outline` | The declarations of a file in source order with nesting depth |

Symbols are named by documentation-comment ids (``T:Ns.Type`1``, `M:Ns.Type.Parse(System.String)`), by a position
(`src/File.cs:12:17`) or by a dotted name; a name that matches several declarations returns the candidates.
Ids are the ones in XML documentation comments. Predefined types, type parameters and the containing types are
exact; other named types in parameter lists are written as in the source (`List{System.Int32}` where Roslyn writes
`System.Collections.Generic.List{System.Int32}`). Ids from Roslyn's `DocumentationCommentId.CreateDeclarationId`,
which end with `~ReturnType`, are accepted too.

## How projects are read

Without MSBuild, a project file is evaluated for what matters to parsing: properties with `$(...)`, conditions of the
common forms (`'$(Configuration)|$(Platform)' == 'Debug|AnyCPU'`, `Exists(...)`, `and`, `or`), `Directory.Build.props`,
imports of files next to the project (such as `.projitems` of shared projects), `Compile` items with wildcards,
`Remove` and `Exclude`, `ProjectReference`, `DefineConstants`, `LangVersion` and the preprocessor symbols the SDK
derives from the configuration and target framework (`DEBUG`, `NET`, `NET8_0_OR_GREATER`...). SDK projects compile
`**/*.cs` except under `bin/`, `obj/` and folders starting with `.`. Targets, tasks and NuGet packages are not
evaluated, so sources generated during a build are missing.

## Development

- Build: `dotnet build PaspanCodeGraphMcp.slnx`
- Test: `dotnet run --project tests/PaspanCodeGraph.Tests`

PaspanParsers is a git submodule in `external/PaspanParsers`.
