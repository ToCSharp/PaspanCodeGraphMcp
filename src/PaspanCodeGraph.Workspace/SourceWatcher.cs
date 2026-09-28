namespace PaspanCodeGraph.Workspace;

/// <summary>
/// Watches the directories of a workspace for changes to what a load reads: C# and C++ files, project and solution
/// files, MSBuild imports, <c>project.assets.json</c> and <c>compile_commands.json</c>. It reports once the changes stop for a moment, so that
/// saving many files (a branch switch, a formatter) gives one update.
/// </summary>
public sealed class SourceWatcher : IDisposable
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".projitems", ".sln", ".slnx",
        ".vcxproj", ".cpp", ".cc", ".cxx", ".c++", ".cp", ".cppm", ".ixx", ".c", ".h", ".hh", ".hpp", ".hxx", ".h++", ".inl", ".ipp", ".tpp", ".tcc",
    };

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _timer;
    private readonly TimeSpan _quiet;
    private readonly Action<IReadOnlyCollection<string>> _changed;
    private readonly Lock _lock = new();
    private HashSet<string> _pending = new(SymbolIndexBuilder.PathComparer);
    private bool _disposed;

    /// <param name="directories">Directories to watch with their subdirectories.</param>
    /// <param name="changed">
    /// Called on a thread-pool thread with the changed paths; an empty collection means that events were lost and
    /// anything may have changed.
    /// </param>
    public SourceWatcher(IEnumerable<string> directories, TimeSpan quiet, Action<IReadOnlyCollection<string>> changed)
    {
        _quiet = quiet;
        _changed = changed;
        _timer = new Timer(_ => Flush());
        Directories = Roots(directories);
        foreach (var directory in Directories)
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += (_, e) => OnChange(e.FullPath);
            watcher.Created += (_, e) => OnChange(e.FullPath);
            watcher.Deleted += (_, e) => OnChange(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                OnChange(e.OldFullPath);
                OnChange(e.FullPath);
            };
            watcher.Error += (_, _) => OnChange(null);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    /// <summary>The directories watched: those given, without the ones inside others.</summary>
    public IReadOnlyList<string> Directories { get; }

    /// <summary>The directories a workspace's load reads from: the solution's and every project's, and those of files outside them.</summary>
    public static IEnumerable<string> DirectoriesOf(WorkspaceSnapshot snapshot) =>
        new[] { snapshot.Directory }
            .Concat(snapshot.Projects.Select(p => p.Directory))
            .Concat(snapshot.Documents.Keys.Select(Path.GetDirectoryName).OfType<string>());

    /// <summary>Whether a change to <paramref name="path"/> can change a load.</summary>
    public static bool IsRelevant(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase) || name.Equals("compile_commands.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var separator = Path.DirectorySeparatorChar;
        if (path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}.git{separator}", StringComparison.Ordinal)
            || path.Contains($"{separator}.paspan{separator}", StringComparison.Ordinal))
        {
            return false;
        }

        // A directory: renaming or deleting one removes the files in it
        var extension = Path.GetExtension(path);
        return extension.Length == 0 ? !name.StartsWith('.') : Extensions.Contains(extension);
    }

    private void OnChange(string? path)
    {
        if (path != null && !IsRelevant(path))
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            // A lost event is recorded as the empty path
            _pending.Add(path ?? "");
            _timer.Change(_quiet, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Takes the changes seen but not reported yet, for a caller that cannot wait for the quiet time; they are then
    /// not reported.
    /// </summary>
    public bool TakePending()
    {
        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                return false;
            }

            _pending = new HashSet<string>(SymbolIndexBuilder.PathComparer);
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return true;
        }
    }

    private void Flush()
    {
        HashSet<string> changes;
        lock (_lock)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            changes = _pending;
            _pending = new HashSet<string>(SymbolIndexBuilder.PathComparer);
        }

        _changed(changes.Contains("") ? [] : changes);
    }

    /// <summary>The existing directories of <paramref name="directories"/> that are not inside another one.</summary>
    public static List<string> Roots(IEnumerable<string> directories)
    {
        var separator = Path.DirectorySeparatorChar.ToString();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var roots = new List<string>();
        foreach (var directory in directories
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .Where(Directory.Exists)
            .Distinct(SymbolIndexBuilder.PathComparer)
            .OrderBy(d => d.Length))
        {
            if (!roots.Any(r => directory.StartsWith(r.EndsWith(separator) ? r : r + separator, comparison)))
            {
                roots.Add(directory);
            }
        }

        return roots;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _timer.Dispose();
    }
}
