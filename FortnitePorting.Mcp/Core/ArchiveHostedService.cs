using Microsoft.Extensions.Hosting;
using Serilog;

namespace FortnitePorting.Mcp.Core;

/// <summary>
/// Kicks the archive load off in the background.
///
/// StartAsync MUST NOT block: IHost starts hosted services sequentially and awaits each one, and
/// the MCP stdio session is itself a hosted service. Awaiting the ~7 s archive load here would
/// stall the transport and the `initialize` handshake would never be answered.
/// </summary>
public sealed class ArchiveHostedService(HeadlessLoader loader, DisplayNameIndex names) : IHostedService
{
    private Task? _run;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // CancellationToken.None on purpose: the shared load must never be cancelled by a caller.
        _run = Task.Run(async () =>
        {
            try
            {
                await loader.WhenReady();
                Log.Information("Archive ready; {Count:N0} asset registry entries", loader.AssetRegistry.Count);

                // Fire-and-forget: search works name-only until each category lands, and this
                // must never be able to take the server down.
                names.StartBackgroundBuild();
            }
            catch (Exception e)
            {
                Log.Error(e, "Background archive load failed");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_run is null) return;

        try { await _run.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
        catch { /* shutting down anyway */ }
    }
}
