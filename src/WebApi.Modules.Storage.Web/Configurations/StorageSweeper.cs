using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.UseCases;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.Web.Configurations;

/// <summary>
/// Sweeps every active tenant (<see cref="SweepCommand"/>) every <see cref="StorageOptions.SweepInterval"/>, starting a
/// minute after the module is loaded, in a tenant scope of the module's own provider. The runtime starts it with the
/// module and stops it before an unload or a swap. Every replica sweeps; a sweep is safe to run twice (a second replica
/// finds nothing left, or loses a save and tries again next time). Takes <see cref="ILoggerFactory"/>, not
/// <c>ILogger&lt;T&gt;</c>, so the host's container caches no module type.
/// </summary>
internal sealed class StorageSweeper(IServiceProvider services, ITenantDirectory tenants, IOptions<StorageOptions> options, TimeProvider clock,
    ILoggerFactory loggers) : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger("soft.square.net.WebApi.Modules.Storage.Sweeper");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.SweepInterval;
        if (interval <= TimeSpan.Zero) return;
        var wait = interval < TimeSpan.FromMinutes(1) ? interval : TimeSpan.FromMinutes(1);
        try
        {
            while (true)
            {
                await Task.Delay(wait, clock, stoppingToken);
                await SweepAllAsync(stoppingToken);
                wait = interval;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task SweepAllAsync(CancellationToken ct)
    {
        foreach (var tenant in tenants.All.Where(t => t.IsActive))
        {
            try
            {
                await using var scope = services.CreateTenantScope(tenant);
                var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SweepCommand(), ct);
                if (result.IsSuccess && result.Value is { } done && (done.ItemsPurged | done.BlobsDeleted | done.UploadsExpired) != 0)
                    _log.LogInformation("Storage sweep of {Tenant}: {Items} items purged, {Blobs} blobs ({Bytes} bytes) deleted, {Uploads} uploads expired.",
                        tenant.Id, done.ItemsPurged, done.BlobsDeleted, done.BytesFreed, done.UploadsExpired);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(e, "Storage sweep of {Tenant} failed; it runs again in {Interval}.", tenant.Id, options.Value.SweepInterval);
            }
        }
    }
}
