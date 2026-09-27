using Microsoft.Extensions.Logging;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraphMcp;

/// <summary>
/// Owns the current <see cref="WorkspaceSnapshot"/>. Loads and updates are serialized and the snapshot is replaced
/// only when one completes, so tools never see a half-loaded workspace. With <see cref="ServerOptions.Watch"/> the
/// workspace's files are watched and changes update the snapshot; a tool call made while changes are waiting
/// runs the update first, so answers reflect the files on disk. With <see cref="ServerOptions.Cache"/> the graph is
/// kept in <c>.paspan/graph.bin</c> next to the solution and a load starts from it.
/// </summary>
public sealed class WorkspaceHost(ServerOptions options, ILogger<WorkspaceHost> log) : IDisposable
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile WorkspaceSnapshot? _snapshot;
    private volatile Task<WorkspaceSnapshot>? _pendingLoad;
    private SourceWatcher? _watcher;
    private volatile bool _changesPending;

    public WorkspaceSnapshot? Current => _snapshot;

    public bool IsLoading => _pendingLoad is { IsCompleted: false };

    /// <summary>Why the last load or update failed, if it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>How the last load used the graph cache.</summary>
    public string? CacheStatus { get; private set; }

    /// <summary>The directories watched for changes; empty when not watching.</summary>
    public IReadOnlyList<string> WatchedDirectories => _watcher?.Directories ?? [];

    public ServerOptions Options => options;

    /// <summary>
    /// Waits for a load or update in progress, runs the update for changes seen on disk, then returns the snapshot
    /// or throws a descriptive error.
    /// </summary>
    public async Task<WorkspaceSnapshot> RequireSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_watcher?.TakePending() == true)
        {
            _changesPending = true;
        }

        if (_changesPending && _snapshot != null)
        {
            _ = UpdateAsync(CancellationToken.None);
        }

        if (_pendingLoad is { IsCompleted: false } pending)
        {
            try
            {
                await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Reported through LastError
            }
        }

        return _snapshot ?? throw new InvalidOperationException(LastError is { } error
            ? $"The workspace failed to load: {error}. Call workspace_load with a .sln, .slnx or .csproj path."
            : "No workspace loaded. Call workspace_load with a .sln, .slnx or .csproj path, or start the server with --workspace.");
    }

    public void StartBackgroundLoad(string path, CancellationToken cancellationToken)
    {
        var task = LoadAsync(path, options.Configuration, options.Platform, cancellationToken);
        _ = task.ContinueWith(
            t => log.LogError(t.Exception!.GetBaseException(), "Workspace load failed"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public Task<WorkspaceSnapshot> LoadAsync(string path, string configuration, string platform, CancellationToken cancellationToken)
    {
        var task = LoadCoreAsync(path, configuration, platform, cancellationToken);
        _pendingLoad = task;
        return task;
    }

    /// <summary>Brings the current snapshot up to date with the files on disk.</summary>
    public Task<WorkspaceSnapshot> UpdateAsync(CancellationToken cancellationToken)
    {
        var task = UpdateCoreAsync(cancellationToken);
        _pendingLoad = task;
        return task;
    }

    private async Task<WorkspaceSnapshot> LoadCoreAsync(string path, string configuration, string platform, CancellationToken cancellationToken)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            log.LogInformation("Loading workspace {Path} ({Configuration}|{Platform})", path, configuration, platform);
            _changesPending = false;
            var snapshot = await Task.Run(
                () =>
                {
                    if (!options.Cache)
                    {
                        CacheStatus = "off";
                        return WorkspaceLoader.Load(path, configuration, platform, cancellationToken);
                    }

                    var root = SolutionDiscovery.Discover(path).RootPath;
                    var loaded = WorkspaceLoader.LoadCached(root, CacheFile(root), out var status, configuration, platform, cancellationToken);
                    CacheStatus = status;
                    return loaded;
                },
                cancellationToken).ConfigureAwait(false);
            _snapshot = snapshot;
            LastError = null;
            Watch(snapshot);
            log.LogInformation(
                "Loaded {Projects} projects, {Documents} files and {Symbols} symbols from {Path} in {Seconds:0.00}s (cache: {Cache})",
                snapshot.Projects.Count,
                snapshot.Documents.Count,
                snapshot.Index.Count,
                snapshot.RootPath,
                snapshot.Elapsed.TotalSeconds,
                CacheStatus);
            return snapshot;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastError = e.Message;
            throw;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<WorkspaceSnapshot> UpdateCoreAsync(CancellationToken cancellationToken)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _snapshot ?? throw new InvalidOperationException("No workspace loaded.");
            if (!_changesPending)
            {
                return previous;
            }

            _changesPending = false;
            var snapshot = await Task.Run(() => WorkspaceLoader.Update(previous, cancellationToken), cancellationToken).ConfigureAwait(false);
            _snapshot = snapshot;
            LastError = null;
            if (snapshot.Kind != SnapshotKind.Unchanged)
            {
                log.LogInformation(
                    "Updated {Path}: {Parsed} files parsed and {Bound} bound again in {Seconds:0.00}s",
                    snapshot.RootPath,
                    snapshot.ParsedFiles,
                    snapshot.BoundFiles,
                    snapshot.Elapsed.TotalSeconds);
                if (options.Cache && !WorkspaceLoader.TrySave(snapshot, CacheFile(snapshot.RootPath)))
                {
                    log.LogWarning("Could not write the graph cache {File}", CacheFile(snapshot.RootPath));
                }
            }

            Watch(snapshot);
            return snapshot;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The snapshot before stays; the next change tries again
            LastError = $"Update failed: {e.Message}";
            log.LogError(e, "Workspace update failed");
            throw;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private string CacheFile(string rootPath) => options.CachePath ?? GraphCache.DefaultPath(rootPath);

    /// <summary>Watches the snapshot's directories, keeping the watcher when they are the same.</summary>
    private void Watch(WorkspaceSnapshot snapshot)
    {
        if (!options.Watch)
        {
            return;
        }

        var directories = SourceWatcher.DirectoriesOf(snapshot).ToList();
        if (_watcher != null)
        {
            if (SourceWatcher.Roots(directories).SequenceEqual(_watcher.Directories, StringComparer.Ordinal))
            {
                return;
            }

            _watcher.Dispose();
        }

        try
        {
            _watcher = new SourceWatcher(directories, options.WatchDelay, OnFilesChanged);
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _watcher = null;
            log.LogWarning("Cannot watch {Path} for changes: {Message}", snapshot.RootPath, e.Message);
        }
    }

    private void OnFilesChanged(IReadOnlyCollection<string> paths)
    {
        _changesPending = true;
        log.LogDebug("Changes on disk: {Paths}", paths.Count == 0 ? "(events lost)" : string.Join(", ", paths.Take(10)));
        _ = UpdateAsync(CancellationToken.None).ContinueWith(
            _ => { },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>Stops watching; a load or update in progress still completes.</summary>
    public void Dispose() => _watcher?.Dispose();
}
