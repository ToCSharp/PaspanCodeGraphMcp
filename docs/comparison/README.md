# Compared with ZuCSharpMcp

ZuCSharpMcp is the Roslyn-based MCP server this project follows: the same tool names, arguments and answers, but
with Roslyn's compiler behind them (MSBuild evaluation through `dotnet msbuild`, an `AdhocWorkspace`,
`SymbolFinder`). Its answers are what the compiler binds, so it is the reference for accuracy here, with the
caveats below.

`compare.py` runs both servers over MCP on one solution: `workspace_load`, then `find_references` for the same
300 types and members, drawn at random (fixed seed) from the solution's declarations outside test files, first
with the references of related members (overrides, implementations, the members overridden or implemented) and
then without. Memory is the resident set of the server process. Name-only references of this server and implicit
references of ZuCSharpMcp (`foreach`, `await`) are left out.

```
python3 docs/comparison/compare.py Your.slnx --zu "dotnet /path/zu-csharp-mcp.dll" \
    --paspan paspan-code-graph-mcp --native /path/native/paspan-code-graph-mcp
```

## Results

Measured on 2026-09-28 in a Linux container with 4 cores, .NET 10.0.12, both servers built in Release. The
solutions: PaspanParsers (3 projects, 162 files), this repository (9 projects with the submodule's, 185 files) and
Newtonsoft.Json at 52fa3ae (4 projects, 951 files, about 200,000 lines; its projects were set to `net10.0` only,
as the SDK in the container has no .NET Framework reference packs).

Speed and memory (seconds and MB):

| | PaspanParsers | this repository | Newtonsoft.Json |
| --- | --- | --- | --- |
| Load, this server | 2.1 | 2.1 | 5.3 |
| Load, this server as a NativeAOT binary | 0.9 | 1.0 | 2.4 |
| Load from the graph cache | 0.4 | 0.5 | 0.8 |
| Load, ZuCSharpMcp | 1.1 | 2.3 | 1.8 |
| First answer after the load, this server | 0.02 | 0.03 | 0.02 |
| First answer after the load, ZuCSharpMcp | 2.1 | 2.0 | 3.9 |
| One `find_references`, this server (NativeAOT) | 0.0008 (0.0004) | 0.001 (0.0007) | 0.0013 (0.0009) |
| One `find_references`, ZuCSharpMcp | 0.043 | 0.054 | 0.077 |
| Memory after the queries, this server (NativeAOT) | 325 (247) | 347 (270) | 496 (369) |
| Memory after the load from the cache | 129 | 142 | 166 |
| Memory after the queries, ZuCSharpMcp | 408 | 484 | 537 |

ZuCSharpMcp loads projects and documents but compiles on the first question, so the time until the first answer is
the sum of its two rows: 3.2, 4.3 and 5.7 s, against 2.1, 2.1 and 5.3 s here (0.9, 1.0 and 2.4 s for the native
binary, 0.4 to 0.8 s from the cache). This server binds everything while loading and then answers from its index,
50 to 60 times faster per query (about 100 times as a native binary).

References, taking ZuCSharpMcp's as the truth (precision: the share of this server's references it also lists;
recall: the share of its references this server lists):

| | PaspanParsers | this repository | Newtonsoft.Json |
| --- | --- | --- | --- |
| References to types | 99.2% / 99.8% | 99.9% / 99.6% | 96.0% / 93.4% |
| References to members, the member alone | 95.8% / 99.0% | 99.6% / 99.5% | 93.6% / 98.0% |
| References to members, with related members | 96.1% / 90.6% | 99.8% / 91.4% | 93.3% / 85.6% |

Most differences are not binding errors of this server:

- **Code ZuCSharpMcp does not see.** It takes `DefineConstants` from MSBuild evaluation, which does not include the
  symbols the SDK adds for the target framework (`NET6_0_OR_GREATER` and the like); code under
  `#if ... || NET6_0_OR_GREATER` is inactive for it. Newtonsoft.Json's tests have many such blocks, and every
  reference there counts against this server's precision. On PaspanParsers, ZuCSharpMcp does not resolve the test
  project's reference to `Paspan` (hundreds of CS0246 errors in `CoreRegressionTests.cs` and `FluentTests.cs`), so
  the references in those files count the same way. Checked with ZuCSharpMcp's `go_to_definition` on those
  positions: it finds no symbol there, or only the enclosing declaration.
- **Related members of referenced assemblies.** With related members, ZuCSharpMcp also lists the references to the
  framework members a member overrides or implements: for an override of `ToString` or an implementation of
  `ICollection<T>.Count`, every `ToString()` or `.Count` in the solution. This server links overrides and
  implementations of workspace members only, which is most of the recall lost in that row; the member-alone row
  leaves this out.
- **Documentation comments.** ZuCSharpMcp counts `<see cref="..."/>`; this server does not index comments.

Running the comparison found two binding errors of this server, fixed with it: a project with several
`TargetFrameworks` lost its `DefineConstants` when they were conditioned on `$(TargetFramework)`, and a method hidden
with `new` (`JObject.Parse` over `JToken.Parse`) made the call ambiguous. On a first sample of 200 symbols of
Newtonsoft.Json, the recall of all references went from 65% to 85% with the first fix and to 91% with the second. What remains on Newtonsoft.Json includes overloads that differ by a nullable value type
(`new JValue((double?)null)`).

## Other differences

- ZuCSharpMcp needs `dotnet msbuild` at load time (a process per project, cached) and ships the Roslyn assemblies
  (21 MB built, 15 MB of them Roslyn); this server reads project files itself and is a 2 MB .NET tool package, or
  one 22 MB native binary.
- ZuCSharpMcp reports compiler diagnostics; this server reports syntax errors and how references were bound
  (`diagnostics`), and marks every reference with a confidence.
- The retrieval tools (`search_code`, `get_context`, `impact_analysis`, `find_path`, `module_map`, `annotate`),
  file watching with incremental updates and the graph cache have no counterpart in ZuCSharpMcp.
