using Microsoft.Extensions.Logging;
using PaspanCodeGraph.Workspace;

namespace PaspanCodeGraphMcp;

/// <summary>
/// Owns the current <see cref="WorkspaceSnapshot"/>. Loads are serialized and the snapshot is replaced only when a
/// load completes, so tools never see a half-loaded workspace.
/// </summary>
public sealed class WorkspaceHost(ServerOptions options, ILogger<WorkspaceHost> log)
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile WorkspaceSnapshot? _snapshot;
    private volatile Task<WorkspaceSnapshot>? _pendingLoad;

    public WorkspaceSnapshot? Current => _snapshot;

    public bool IsLoading => _pendingLoad is { IsCompleted: false };

    /// <summary>Why the last load failed, if it did.</summary>
    public string? LastError { get; private set; }

    public ServerOptions Options => options;

    /// <summary>Waits for a load in progress, then returns the snapshot or throws a descriptive error.</summary>
    public async Task<WorkspaceSnapshot> RequireSnapshotAsync(CancellationToken cancellationToken)
    {
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

    private async Task<WorkspaceSnapshot> LoadCoreAsync(string path, string configuration, string platform, CancellationToken cancellationToken)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            log.LogInformation("Loading workspace {Path} ({Configuration}|{Platform})", path, configuration, platform);
            var snapshot = await Task.Run(() => WorkspaceLoader.Load(path, configuration, platform, cancellationToken), cancellationToken).ConfigureAwait(false);
            _snapshot = snapshot;
            LastError = null;
            log.LogInformation(
                "Loaded {Projects} projects, {Documents} files and {Symbols} symbols from {Path} in {Seconds:0.00}s",
                snapshot.Projects.Count,
                snapshot.Documents.Count,
                snapshot.Index.Count,
                snapshot.RootPath,
                snapshot.Elapsed.TotalSeconds);
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
}
