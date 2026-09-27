# PaspanCodeGraphMcp
A 100% managed C#, high-performance MCP server and GraphRAG engine powered by zero-allocation Span-based parser combinators for native codebase analysis by AI agents.

The server reads C# code with [PaspanParsers](https://github.com/ToCSharp/PaspanParsers), not Roslyn, and loads
solutions without MSBuild. It is read-only and talks MCP over stdio.

## Status

Stages 1 to 3 of the plan: loading a workspace, navigating its declarations, binding type names, the type
hierarchy (base types, overrides, interface implementations), references to types and members, and the call
graph (callers and callees) from the types of expressions.

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
| `diagnostics` | Projects that could not be read and syntax errors, with positions; how member references were bound (exact, inferred, name-only share, external, unresolved) |
| `find_symbol` | Declarations by (dotted) name: substring, exact or `*`/`?` wildcards |
| `symbol_info` | Signature, documentation summary, accessibility, modifiers, parameters, all declarations, and relations: base class, interfaces, overridden and implemented members, counts of derived types, implementations and references |
| `go_to_definition` | Declaration locations, every part of a partial type; a position on a type reference goes to that type |
| `type_members` | Members of a type, optionally with those of its base types and interfaces declared in the workspace |
| `file_outline` | The declarations of a file in source order with nesting depth |
| `type_hierarchy` | Base class chain, interfaces and derived types, direct or transitive |
| `find_implementations` | Types implementing an interface or deriving from a class; members implementing an interface member or overriding a virtual one, at any depth |
| `find_references` | References to a type or member, grouped by file, with the line, the containing declaration and a confidence; for a virtual or interface member also those of its overrides, implementations and bases |
| `find_callers` | Call sites of a method, constructor, property, indexer or event with the calling member; calls through a base or interface member it overrides or implements are marked indirect |
| `find_callees` | What a member (or all members of a type) calls: workspace members with call counts, virtual and interface targets with their implementation counts, members of referenced types by the id they would have, unbound calls by name |

Symbols are named by documentation-comment ids (``T:Ns.Type`1``, `M:Ns.Type.Parse(System.String)`), by a position
(`src/File.cs:12:17`) or by a dotted name; a name that matches several declarations returns the candidates.
Ids are the ones in XML documentation comments. Type names in ids are bound, so workspace types are written with
their full names; types from referenced assemblies are written as in the source (`List{System.Int32}` where Roslyn
writes `System.Collections.Generic.List{System.Int32}`). Ids from Roslyn's `DocumentationCommentId.CreateDeclarationId`, which end with `~ReturnType`,
are accepted too.

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
target-typed `new()`, deconstruction and records are handled. Types from referenced assemblies are not read yet;
a small model of the base class library (collections, LINQ, tasks, strings) gives the types that bodies use most.

Each member reference has a confidence: `Exact` when every type involved is known, `Inferred` when a type was
inferred (a lambda parameter of a LINQ call, an argument of unknown type), `NameOnly` when the receiver's type is
unknown and the reference goes to every workspace member with that name and a fitting parameter count (or when
overloads could not be told apart). Tools show `Exact` and `Inferred` by default and count `NameOnly` separately.

Tests compare the results with Roslyn on the PaspanParsers solution, on this repository's own solution and on a
fixture of hard cases: ids of every declaration, base types, overrides, interface implementations, every identifier
Roslyn binds to a workspace type, and every reference to a workspace member (names, constructor calls, indexers).
For member references, exact and inferred ones reach a recall of 98.3% and a precision of 99.99% on PaspanParsers,
97.6% and 99.98% on this repository, and 100% on the fixture.

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
