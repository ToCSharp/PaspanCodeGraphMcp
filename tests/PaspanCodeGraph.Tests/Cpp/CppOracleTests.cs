using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraph.Tests.Cpp;

/// <summary>
/// The declarations and references of C++ code, compared with clang: the fixture (a small project with headers)
/// must match exactly; the corpus of the PaspanParsers C++ parser, one workspace per file, must come close.
/// </summary>
[TestClass]
public sealed class CppOracleTests
{
    public static string FixtureDirectory => Path.Combine(TestPaths.RepositoryRoot, "tests", "PaspanCodeGraph.Tests", "Cpp", "Fixture");

    private static readonly Lazy<(WorkspaceSnapshot Snapshot, ClangOracle Oracle)> Fixture = new(() =>
    {
        var snapshot = WorkspaceLoader.Load(FixtureDirectory, readReferences: false);
        var oracle = ClangOracle.Run(FixtureDirectory, Directory.GetFiles(Path.Combine(FixtureDirectory, "src"), "*.cpp").Order(StringComparer.Ordinal), [Path.Combine(FixtureDirectory, "include")]);
        return (snapshot, oracle);
    });

    private static readonly Lazy<List<(WorkspaceSnapshot Snapshot, ClangOracle Oracle)>> Corpus = new(() =>
    {
        var corpus = Path.Combine(TestPaths.RepositoryRoot, "external", "PaspanParsers", "src", "PaspanParsers.Tests", "Cpp", "Corpus");
        var result = new List<(WorkspaceSnapshot, ClangOracle)>();
        foreach (var file in Directory.GetFiles(corpus, "*.cpp").Order(StringComparer.Ordinal))
        {
            // One workspace per file: the files of the corpus declare the same names. The copies stay for the reports.
            var directory = Path.Combine(AppContext.BaseDirectory, "cpp-corpus", Path.GetFileNameWithoutExtension(file));
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, Path.GetFileName(file));
            File.Copy(file, copy);
            foreach (var include in Directory.GetFiles(corpus, "*.inc"))
            {
                File.Copy(include, Path.Combine(directory, Path.GetFileName(include)));
            }

            var oracle = ClangOracle.Run(directory, [copy], []);
            if (oracle.Failures.Count > 0)
            {
                continue;
            }

            result.Add((WorkspaceLoader.Load(directory, readReferences: false), oracle));
        }

        return result;
    });

    private static void WriteReport(string name, string report) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, name), report);

    [TestMethod]
    public void FixtureDeclarations_MatchClang()
    {
        ClangOracle.RequireClang();
        var (snapshot, oracle) = Fixture.Value;
        Assert.AreEqual(0, oracle.Failures.Count, string.Join("\n", oracle.Failures));
        Assert.AreEqual(0, snapshot.Documents.Values.Count(d => d.Failure != null), string.Join("\n", snapshot.Documents.Values.Select(d => d.Failure).OfType<string>()));
        var result = CppOracleComparison.Declarations(snapshot, oracle);
        WriteReport("cpp-fixture-declarations-report.txt", result.Report("declarations"));
        Assert.IsTrue(result.Compared > 80, result.Report("declarations"));
        Assert.AreEqual(0, result.Differences.Count, result.Report("declarations"));
    }

    [TestMethod]
    public void FixtureReferences_MatchClang()
    {
        ClangOracle.RequireClang();
        var (snapshot, oracle) = Fixture.Value;
        var result = CppOracleComparison.References(snapshot, oracle);
        WriteReport("cpp-fixture-references-report.txt", result.Report("references"));
        Assert.IsTrue(result.Compared > 80, result.Report("references"));
        Assert.AreEqual(0, result.Differences.Count, result.Report("references"));
    }

    [TestMethod]
    public void CorpusDeclarations_MatchClang()
    {
        ClangOracle.RequireClang();
        var results = Corpus.Value.Select(c => CppOracleComparison.Declarations(c.Snapshot, c.Oracle)).ToList();
        var total = new CppOracleComparison.Result(results.Sum(r => r.Compared), results.Sum(r => r.Matched), results.Sum(r => r.Extra), results.SelectMany(r => r.Differences).ToList());
        WriteReport("cpp-corpus-declarations-report.txt", total.Report("declarations"));
        Assert.IsTrue(results.Count >= 20 && total.Compared > 800, $"{results.Count} files; " + total.Report("declarations"));
        Assert.IsTrue(total.Recall >= 0.99 && total.Precision >= 0.99, total.Report("declarations"));
    }

    [TestMethod]
    public void CorpusReferences_MatchClang()
    {
        ClangOracle.RequireClang();
        var results = Corpus.Value.Select(c => CppOracleComparison.References(c.Snapshot, c.Oracle)).ToList();
        var total = new CppOracleComparison.Result(results.Sum(r => r.Compared), results.Sum(r => r.Matched), results.Sum(r => r.Extra), results.SelectMany(r => r.Differences).ToList());
        WriteReport("cpp-corpus-references-report.txt", total.Report("references"));
        Assert.IsTrue(total.Compared > 200, total.Report("references"));
        Assert.IsTrue(total.Precision >= 0.95 && total.Recall >= 0.9, total.Report("references"));
    }

    /// <summary>
    /// A workspace named by <c>CPP_ORACLE_WORKSPACE</c> (a folder, compile_commands.json or .vcxproj), compared with
    /// clang on its source files (those of <c>CPP_ORACLE_SOURCES</c>, a glob such as <c>src/*.cc</c>, or every source
    /// file) with the project's include directories; reports go to <c>cpp-external-*-report.txt</c>.
    /// </summary>
    [TestMethod]
    public void ExternalWorkspace_MatchesClang()
    {
        ClangOracle.RequireClang();
        var path = Environment.GetEnvironmentVariable("CPP_ORACLE_WORKSPACE");
        if (string.IsNullOrEmpty(path))
        {
            Assert.Inconclusive("Set CPP_ORACLE_WORKSPACE to compare a C++ workspace with clang.");
        }

        var snapshot = WorkspaceLoader.Load(path, readReferences: false);
        var pattern = Environment.GetEnvironmentVariable("CPP_ORACLE_SOURCES");
        var sources = snapshot.Documents.Keys
            .Where(f => !CppFiles.IsHeader(f))
            .Where(f => pattern == null || ProjectFileReader.WildcardRegex(SolutionDiscovery.NormalizeSeparators(pattern)).IsMatch(Path.GetRelativePath(snapshot.Directory, f)))
            .Order(StringComparer.Ordinal)
            .ToList();
        var project = snapshot.Projects.First();
        var oracle = ClangOracle.Run(snapshot.Directory, sources, project.IncludeDirectories);
        var declarations = CppOracleComparison.Declarations(snapshot, oracle);
        var references = CppOracleComparison.References(snapshot, oracle);
        var failed = snapshot.Documents.Values.Where(d => d.Failure != null).Select(d => d.Path + ": " + d.Failure).ToList();
        var summary = $"{snapshot.Documents.Count} files, {failed.Count} not parsed; clang failed on {oracle.Failures.Count} of {sources.Count}\n";
        WriteReport("cpp-external-declarations-report.txt", summary + declarations.Report("declarations") + "\n\nNot parsed:\n" + string.Join("\n", failed) + "\n\nClang failures:\n" + string.Join("\n", oracle.Failures));
        WriteReport("cpp-external-references-report.txt", summary + references.Report("references"));
        Console.WriteLine(summary + declarations.Report("declarations").Split('\n')[0] + "\n" + references.Report("references").Split('\n')[0]);
    }
}
