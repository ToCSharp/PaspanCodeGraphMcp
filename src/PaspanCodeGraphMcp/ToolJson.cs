using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using PaspanCodeGraphMcp.Symbols;
using PaspanCodeGraphMcp.Tools;

namespace PaspanCodeGraphMcp;

/// <summary>
/// The JSON of tool results, from source-generated metadata: a NativeAOT build cannot serialize by reflection,
/// so every type a tool returns (also through <c>object</c>) is listed on <see cref="ToolJsonContext"/>.
/// </summary>
public static class ToolJson
{
    /// <summary>The MCP SDK's options (camelCase, nulls left out) with the result types first.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Insert(0, ToolJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AmbiguousSymbol))]
[JsonSerializable(typeof(AnnotateResult))]
[JsonSerializable(typeof(BaseTypeDto))]
[JsonSerializable(typeof(BindingSummary))]
[JsonSerializable(typeof(CallSiteDto))]
[JsonSerializable(typeof(CalleeDto))]
[JsonSerializable(typeof(CommunitySummary))]
[JsonSerializable(typeof(ContextItem))]
[JsonSerializable(typeof(DefinitionResult))]
[JsonSerializable(typeof(DerivedTypeDto))]
[JsonSerializable(typeof(DiagnosticDto))]
[JsonSerializable(typeof(DiagnosticsResult))]
[JsonSerializable(typeof(FileOutlineResult))]
[JsonSerializable(typeof(FileReferences))]
[JsonSerializable(typeof(FindCalleesResult))]
[JsonSerializable(typeof(FindCallersResult))]
[JsonSerializable(typeof(FindImplementationsResult))]
[JsonSerializable(typeof(FindPathResult))]
[JsonSerializable(typeof(FindReferencesResult))]
[JsonSerializable(typeof(FindSymbolResult))]
[JsonSerializable(typeof(GetContextResult))]
[JsonSerializable(typeof(ImpactAnalysisResult))]
[JsonSerializable(typeof(ImpactGroup))]
[JsonSerializable(typeof(ImpactItem))]
[JsonSerializable(typeof(LoadReport))]
[JsonSerializable(typeof(LocationDto))]
[JsonSerializable(typeof(MemberDto))]
[JsonSerializable(typeof(ModuleMapResult))]
[JsonSerializable(typeof(NamespaceSummary))]
[JsonSerializable(typeof(OutlineItem))]
[JsonSerializable(typeof(ParameterDto))]
[JsonSerializable(typeof(PathStep))]
[JsonSerializable(typeof(ProblemDto))]
[JsonSerializable(typeof(ProjectReport))]
[JsonSerializable(typeof(ProjectSummary))]
[JsonSerializable(typeof(ReferenceDto))]
[JsonSerializable(typeof(RelatedSymbolDto))]
[JsonSerializable(typeof(RelationsDto))]
[JsonSerializable(typeof(SearchCodeResult))]
[JsonSerializable(typeof(SearchItem))]
[JsonSerializable(typeof(SymbolDto))]
[JsonSerializable(typeof(SymbolInfoResult))]
[JsonSerializable(typeof(TypeHierarchyResult))]
[JsonSerializable(typeof(TypeMembersResult))]
[JsonSerializable(typeof(UpdateReport))]
[JsonSerializable(typeof(WorkspaceStatus))]
public sealed partial class ToolJsonContext : JsonSerializerContext;

/// <summary>The format of <c>annotations.json</c>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<Annotation>))]
internal sealed partial class AnnotationJsonContext : JsonSerializerContext;
