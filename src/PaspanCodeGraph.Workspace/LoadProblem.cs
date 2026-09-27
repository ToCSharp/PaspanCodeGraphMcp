namespace PaspanCodeGraph.Workspace;

public enum ProblemSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A problem found while loading a workspace: a project that could not be read, a file that failed to parse.</summary>
/// <param name="File">The file the problem is in, if any.</param>
/// <param name="Line">1-based line in <paramref name="File"/>, or 0.</param>
/// <param name="Column">1-based column in <paramref name="File"/>, or 0.</param>
public sealed record LoadProblem(ProblemSeverity Severity, string Message, string? File = null, int Line = 0, int Column = 0)
{
    public static LoadProblem Warning(string message, string? file = null) => new(ProblemSeverity.Warning, message, file);

    public static LoadProblem Error(string message, string? file = null) => new(ProblemSeverity.Error, message, file);
}
