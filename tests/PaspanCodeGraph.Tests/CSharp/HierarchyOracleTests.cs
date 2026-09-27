namespace PaspanCodeGraph.Tests.CSharp;

/// <summary>The hierarchy and the type references of the PaspanParsers solution, compared with Roslyn.</summary>
[TestClass]
public sealed class HierarchyOracleTests
{
    private static SymbolIndex Index => RoslynOracle.Snapshot.Index;

    [TestMethod]
    public void BaseTypes_MatchRoslyn()
    {
        var result = HierarchyComparison.BaseTypes(Index, RoslynOracle.Compilations);
        Assert.IsTrue(result.Compared > 100, result.Report("base types"));
        Assert.AreEqual(0, result.Differences.Count, result.Report("base types"));
    }

    [TestMethod]
    public void Overrides_MatchRoslyn()
    {
        var result = HierarchyComparison.Overrides(Index, RoslynOracle.Compilations);
        Assert.IsTrue(result.Compared > 80, result.Report("overrides"));
        Assert.AreEqual(0, result.Differences.Count, result.Report("overrides"));
    }

    [TestMethod]
    public void InterfaceImplementations_MatchRoslyn()
    {
        var result = HierarchyComparison.InterfaceImplementations(Index, RoslynOracle.Compilations);
        Assert.IsTrue(result.Compared >= 3, result.Report("implementations"));
        Assert.AreEqual(0, result.Differences.Count, result.Report("implementations"));
    }

    [TestMethod]
    public void TypeReferences_MatchRoslyn()
    {
        // A simple name can be a local or parameter, which are not tracked: a few references may be wrong
        var (result, recall, precision) = HierarchyComparison.TypeReferences(Index, RoslynOracle.Compilations);
        var report = $"recall {recall:P2}, precision {precision:P2}; " + result.Report("references");
        Assert.IsTrue(result.Compared > 5000, report);
        Assert.IsTrue(recall >= 0.999 && precision >= 0.999, report);
    }

    [TestMethod]
    public void MemberReferences_MatchRoslyn()
    {
        var result = HierarchyComparison.MemberReferences(Index, RoslynOracle.Compilations);
        var report = result.Report();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "member-references-report.txt"), report.Split(" differ:")[0] + "\n" + string.Join("\n", result.Result.Differences));
        Assert.IsTrue(result.Result.Compared > 10000, report);
        Assert.IsTrue(result.Precision >= 0.95 && result.Recall >= 0.85, report);
    }

    [TestMethod]
    public void ExternalMemberReferences_MatchRoslyn()
    {
        var result = HierarchyComparison.MemberReferences(Index, RoslynOracle.Compilations, external: true);
        var report = result.Report();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "external-references-report.txt"), report.Split(" differ:")[0] + "\n" + string.Join("\n", result.Result.Differences));
        Assert.IsTrue(result.Result.Compared > 5000, report);
        Assert.IsTrue(result.Precision >= 0.95 && result.Recall >= 0.9, report);
    }
}
