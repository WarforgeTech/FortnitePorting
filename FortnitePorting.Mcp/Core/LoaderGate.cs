using ModelContextProtocol;

namespace FortnitePorting.Mcp.Core;

/// <summary>
/// Readiness gating for <see cref="HeadlessLoader"/>, kept out of the loader itself so this
/// work package touches no file another agent owns.
///
/// IMPORTANT: never hand a per-request CancellationToken to <see cref="HeadlessLoader.WhenReady"/>.
/// That token is also forwarded into InitializeAsync, so cancelling it would abort the *shared*
/// archive load. Every wait here is done with Task.WaitAsync on a token-free WhenReady().
/// </summary>
public static class LoaderGate
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(2);

    public static bool IsReady(this HeadlessLoader loader) => loader.State is LoadState.Ready;

    public static string StageName(HeadlessLoader loader) => loader.State switch
    {
        LoadState.NotStarted => "Not Started",
        LoadState.Loading loading => loading.StageName,
        LoadState.Ready => "Ready",
        LoadState.Failed failed => $"Failed: {failed.Message}",
        _ => "Unknown"
    };

    public static float Percent(HeadlessLoader loader) => loader.State switch
    {
        LoadState.Loading loading => loading.Percent,
        LoadState.Ready => 100f,
        _ => 0f
    };

    /// <summary>
    /// Waits up to <paramref name="grace"/> for the archive. Returns false (never throws) when the
    /// archive is simply not ready yet; throws <see cref="McpException"/> if the load failed outright.
    /// </summary>
    public static async Task<bool> TryWaitReadyAsync(this HeadlessLoader loader, CancellationToken cancellationToken, TimeSpan? grace = null)
    {
        if (loader.IsReady()) return true;
        if (loader.State is LoadState.Failed failedEarly)
            throw new McpException($"The Fortnite archive failed to load: {failedEarly.Message}");

        // Token-free: starts the load if nobody has, and can never cancel it.
        var ready = loader.WhenReady();

        try
        {
            await ready.WaitAsync(grace ?? DefaultGrace, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new McpException($"The Fortnite archive failed to load: {e.Message}");
        }
    }

    /// <summary>Blocks until the archive is ready or the load fails. Used by --call / --selftest.</summary>
    public static async Task WaitReadyAsync(this HeadlessLoader loader)
    {
        await loader.WhenReady().ConfigureAwait(false);
    }
}
