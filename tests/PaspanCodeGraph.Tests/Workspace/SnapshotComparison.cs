using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.Workspace;

/// <summary>Compares two snapshots by everything their index holds: symbols, their details and links, and references.</summary>
internal static class SnapshotComparison
{
    public static List<string> Describe(WorkspaceSnapshot snapshot)
    {
        var lines = new List<string>();
        foreach (var s in snapshot.Index.Symbols.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            lines.Add(string.Join(" | ",
                s.Id, s.Kind, s.Name, s.Signature, s.Container?.Id, s.Namespace, s.Project, s.Assembly, s.Accessibility,
                string.Join(",", s.Modifiers),
                string.Join(",", s.Declarations.Select(d => $"{d.File}:{d.Start}-{d.End}@{d.Line}:{d.Column}")),
                string.Join(",", s.Members.Select(m => m.Id)),
                string.Join(",", s.BaseTypes),
                string.Join(",", s.TypeParameters),
                string.Join(",", s.Parameters.Select(p => $"{p.Modifier} {p.Type} {p.Name}={p.DefaultValue}")),
                s.Type,
                s.Documentation,
                string.Join(",", s.Bases.Select(Link)),
                string.Join(",", s.DerivedTypes.Select(d => d.Id)),
                s.ExplicitInterface is { } e ? Link(e) : null,
                s.Overrides?.Id,
                string.Join(",", s.OverriddenBy.Select(d => d.Id)),
                string.Join(",", s.Implements.Select(d => d.Id)),
                string.Join(",", s.ImplementedBy.Select(d => d.Id))));
        }

        foreach (var target in snapshot.Index.ReferenceTargetIds.Order(StringComparer.Ordinal))
        {
            foreach (var r in snapshot.Index.ReferencesTo(target).Select(r => $"{target} <- {r.File}:{r.Start}@{r.Line}:{r.Column} in {r.InMember} {r.Confidence}").Order(StringComparer.Ordinal))
            {
                lines.Add(r);
            }
        }

        foreach (var document in snapshot.Documents.Values.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            lines.Add($"{document.Path} {document.Project} {document.Utf8.Length} {document.Failure} {string.Join(";", document.Errors.Select(e => $"{e.Span.Start}-{e.Span.End} {e.Message}"))}");
        }

        return lines;
    }

    // An external symbol in a link is not kept by the graph cache; only whether there is one matters
    private static string Link(TypeLink link) => $"{link.Written}->{(link.Symbol is { IsExternal: false } s ? s.Id : "")}/{link.Id}<{string.Join(",", link.TypeArguments)}>";

    public static void AssertSame(WorkspaceSnapshot expected, WorkspaceSnapshot actual, string because)
    {
        var a = Describe(expected);
        var b = Describe(actual);
        var missing = a.Except(b).Take(10).ToList();
        var extra = b.Except(a).Take(10).ToList();
        if (missing.Count > 0 || extra.Count > 0 || a.Count != b.Count)
        {
            Assert.Fail($"{because}: {a.Count} lines expected, {b.Count} found.\nMissing:\n  {string.Join("\n  ", missing)}\nExtra:\n  {string.Join("\n  ", extra)}");
        }
    }
}
