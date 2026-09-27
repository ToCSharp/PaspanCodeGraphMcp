namespace PaspanCodeGraph.Tests.CSharp;

/// <summary>The member references of this repository's own solution, compared with Roslyn.</summary>
[TestClass]
public sealed class SelfOracleTests
{
    [TestMethod]
    public void MemberReferences_MatchRoslyn()
    {
        var result = HierarchyComparison.MemberReferences(RoslynOracle.SelfSnapshot.Index, RoslynOracle.SelfCompilations);
        var report = result.Report();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-member-references-report.txt"), report.Split(" differ:")[0] + "\n" + string.Join("\n", result.Result.Differences));
        Assert.IsTrue(result.Result.Compared > 10000, report);
        Assert.IsTrue(result.Precision >= 0.95 && result.Recall >= 0.85, report);
    }
}
