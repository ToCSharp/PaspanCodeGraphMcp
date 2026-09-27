# PaspanCodeGraphMcp
A 100% managed C#, high-performance MCP server and GraphRAG engine powered by zero-allocation Span-based parser combinators for native codebase analysis by AI agents.

The server reads C# code with [PaspanParsers](https://github.com/ToCSharp/PaspanParsers), not Roslyn, and loads
solutions without MSBuild. It is read-only and talks MCP over stdio.

## Status

Stages 1 to 5 of the plan: loading a workspace, navigating its declarations, binding type names, the type
hierarchy (base types, overrides, interface implementations), references to types and members, the call
graph (callers and callees) from the types of expressions, and the types and members of referenced assemblies
(the framework and NuGet packages) read with System.Reflection.Metadata. The server watches the workspace and
updates the graph when files change, and keeps the graph on disk so that a restart does not parse everything again.

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
| `--no-watch`: do not watch the files | `PASPAN_WATCH=0` | watch |
| `--no-cache`: do not keep the graph on disk | `PASPAN_CACHE=0` | keep it |
| `--cache <file>`: where to keep the graph | `PASPAN_CACHE_PATH` | `.paspan/graph.bin` next to the solution |
| | `PASPAN_LOG_LEVEL` | `Information` (logs go to stderr) |

## Tools

| Tool | What it returns |
| --- | --- |
| `workspace_load` | Loads a solution or project; per-project file and referenced assembly counts, preprocessor symbols and load problems |
| `workspace_status` | What is loaded, counts (with referenced assemblies and their types), load time, how the last change was applied (files parsed and bound again), whether files are watched, how the graph cache was used, memory |
| `diagnostics` | Projects that could not be read and syntax errors, with positions; how member references were bound (exact, inferred, name-only share, external, unresolved) |
| `find_symbol` | Declarations by (dotted) name: substring, exact or `*`/`?` wildcards |
| `symbol_info` | Signature, documentation summary, accessibility, modifiers, parameters, all declarations, and relations: base class, interfaces, overridden and implemented members, counts of derived types, implementations and references |
| `go_to_definition` | Declaration locations, every part of a partial type; a position on a type reference goes to that type |
| `type_members` | Members of a type, optionally with those of its base types and interfaces declared in the workspace |
| `file_outline` | The declarations of a file in source order with nesting depth |
| `type_hierarchy` | Base class chain, interfaces and derived types, direct or transitive |
| `find_implementations` | Types implementing an interface or deriving from a class; members implementing an interface member or overriding a virtual one, at any depth |
| `find_references` | References to a type or member, grouped by file, with the line, the containing declaration and a confidence; for a virtual or interface member also those of its overrides, implementations and bases; a type or member of a referenced assembly by its id (`M:System.String.Split(System.Char[])`) |
| `find_callers` | Call sites of a method, constructor, property, indexer or event with the calling member; calls through a base or interface member it overrides or implements are marked indirect; members of referenced assemblies by their id |
| `find_callees` | What a member (or all members of a type) calls: workspace members with call counts, virtual and interface targets with their implementation counts, members of referenced assemblies by their ids, unbound calls by name |

Symbols are named by documentation-comment ids (``T:Ns.Type`1``, `M:Ns.Type.Parse(System.String)`), by a position
(`src/File.cs:12:17`) or by a dotted name; a name that matches several declarations returns the candidates.
Ids are the ones in XML documentation comments. Type names in ids are bound, so workspace types and types from
referenced assemblies are written with their full names (`System.Collections.Generic.List{System.Int32}`), as
Roslyn writes them; a type whose assembly was not found stays as written in the source. Ids from Roslyn's
`DocumentationCommentId.CreateDeclarationId`, which end with `~ReturnType`, are accepted too.

## Binding without a compiler

Type names bind as C# binds them, with the workspace's own declarations: nested types of the containing types and
their bases, then for each namespace from the innermost outward its types, its namespaces, aliases, types of used
namespaces and nested types of `using static` types; global usings, `ImplicitUsings` and `<Using>` items apply to
the whole project. Overrides and interface implementations are matched by name and parameter types, with the type
parameters of generic bases substituted, and explicit implementations first. References are recorded in
signatures, base lists, constraints, attributes, usings and bodies.

Bodies are bound with the types of expressions: locals, parameters, patterns, `foreach` and query variables, and
lambda parameters typed from the delegate they convert to are tracked; member lookup follows base classes and type
parameter constraints with generic arguments substituted; overload resolution picks among candidates by argument
count, named arguments, `params`, conversions and the tie-break rules (non-generic, non-expanded, more specific),
and infers method type arguments from arguments and lambda results; extension methods of the namespaces in scope,
`using static`, object and collection initializers, indexers, constructor initializers, primary constructors,
target-typed `new()`, deconstruction and records are handled.

Types from referenced assemblies take part in all of this like workspace types: their members, base types,
interfaces, constraints, nested types, extension methods, conversion operators and delegate signatures are read
from the assemblies, so that chains such as `text.Trim().Split(',').Select(...)` keep their types and calls on
them bind to the right overload. References to their members are recorded with their documentation ids
(`external:M:System.String.Trim`). A small model of the base class library (collections, LINQ, tasks, strings)
remains for types whose assembly was not found.

Each member reference has a confidence: `Exact` when every type involved is known, `Inferred` when a type was
inferred (a lambda parameter of a LINQ call, an argument of unknown type), `NameOnly` when the receiver's type is
unknown and the reference goes to every workspace member with that name and a fitting parameter count (or when
overloads could not be told apart). Tools show `Exact` and `Inferred` by default and count `NameOnly` separately.

Tests compare the results with Roslyn on the PaspanParsers solution, on this repository's own solution and on a
fixture of hard cases: ids of every declaration, base types, overrides, interface implementations, every identifier
Roslyn binds to a workspace type, and every reference to a workspace member (names, constructor calls, indexers).
For member references, exact and inferred ones reach a recall of 98.3% and a precision of 100% on PaspanParsers,
98.1% and 100% on this repository, and 100% on the fixture. References to members of referenced assemblies reach
99.2% and 99.7% on PaspanParsers, and 97.7% and 99.2% on this repository. Reading the assemblies brought the
share of name-only member references of this repository from 19% to 4.5%, and the calls that could not be bound
at all from 444 to 4; loading it takes about 1.3 s instead of 0.6 s. The ids of every public type and member of
`System.Runtime`, `System.Collections`, `System.Linq` and `System.Collections.Immutable` match Roslyn's.

## Updates and the graph cache

A change to a file (saved, created, deleted or renamed, in the solution's or a project's directory) updates the
graph once changes stop for 300 ms; a tool called earlier runs the update first, so answers match the files on
disk. An update reads the solution and project files again, parses only the files whose content changed, and finds
the references again only in the changed files and in the files that mention the name of a declaration that
changed (added, removed, or with another signature, type or modifiers). A change that can affect binding without
the name being written (a type's base list, an operator, an indexer, members used by `foreach`, `await`, `using`,
deconstruction or queries, a `global using`, a project option, the referenced assemblies) finds the references of
every file again. `IncrementalUpdateTests` and a random edit run (200 renames, removals and modifier changes on this
repository) check that an update gives the same graph as a full load. On this repository a full load takes about
1.5 s and an update after editing a method body about 0.3 s.

The graph (symbols, references, and for each file its content hash, declarations and the names it mentions) is
written to `.paspan/graph.bin` next to the solution after a load and after each update. The next load reads it in
about 0.2 s instead of loading in full, then compares the files, projects and referenced assemblies with the disk
and updates what changed. A cache written by another build of the server or for another configuration is ignored.

## How projects are read

Without MSBuild, a project file is evaluated for what matters to parsing: properties with `$(...)`, conditions of the
common forms (`'$(Configuration)|$(Platform)' == 'Debug|AnyCPU'`, `Exists(...)`, `and`, `or`), `Directory.Build.props`,
imports of files next to the project (such as `.projitems` of shared projects), `Compile` items with wildcards,
`Remove` and `Exclude`, `ProjectReference`, `DefineConstants`, `LangVersion` and the preprocessor symbols the SDK
derives from the configuration and target framework (`DEBUG`, `NET`, `NET8_0_OR_GREATER`...). SDK projects compile
`**/*.cs` except under `bin/`, `obj/` and folders starting with `.`. Targets and tasks are not evaluated, so
sources generated during a build are missing.

The assemblies a project compiles against are found the same way: the reference packs of the SDK
(`dotnet/packs/Microsoft.NETCore.App.Ref`, and `Microsoft.AspNetCore.App.Ref` for web projects or a
`FrameworkReference`), the package assemblies `obj/project.assets.json` lists after a restore or, without it, those
of the `PackageReference` items and their dependencies in the NuGet cache (with the `Reference` items their
`.targets` files add), and `Reference` items with a `HintPath`. The SDK is found through `DOTNET_ROOT`, then
`dotnet` on the `PATH`. Missing packs and packages are reported as load problems.

## Development

- Build: `dotnet build PaspanCodeGraphMcp.slnx`
- Test: `dotnet run --project tests/PaspanCodeGraph.Tests`

PaspanParsers is a git submodule in `external/PaspanParsers`.
