using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.UseCases;
using soft.square.net.WebApi.Modules.Storage.UseCases.Processing;

namespace soft.square.net.WebApi.Modules.Storage.Web.Configurations;

/// <summary>Uploads tell the processor which tenant has new files (<see cref="IStorageWork"/>); the processor waits on it.</summary>
internal sealed class StorageWork : IStorageWork
{
    private readonly Channel<string> _tenants = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true // a full queue: the next round finds them anyway
    });

    public void Notify(string tenantId) => _tenants.Writer.TryWrite(tenantId);

    public ChannelReader<string> Reader => _tenants.Reader;
}

/// <summary>
/// Scans new files and makes thumbnails (<see cref="ProcessCommand"/>): for a tenant right after its uploads, and for
/// every active tenant every <see cref="StorageOptions.ProcessInterval"/> (files uploaded on another replica, retries
/// after the scanner was down). Runs in a tenant scope of the module's provider, like <see cref="StorageSweeper"/>;
/// two replicas processing the same blob only repeat work (the second save loses or finds nothing to do).
/// </summary>
internal sealed class StorageProcessor(IServiceProvider services, ITenantDirectory tenants, StorageWork work, IOptions<StorageOptions> options,
    ILoggerFactory loggers) : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger("soft.square.net.WebApi.Modules.Storage.Processor");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.ProcessInterval;
        if (interval <= TimeSpan.Zero) return;
        var nextRound = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = nextRound - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    using var timer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timer.CancelAfter(wait);
                    try
                    {
                        var tenantId = await work.Reader.ReadAsync(timer.Token);
                        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tenantId };
                        while (work.Reader.TryRead(out var more)) named.Add(more);
                        foreach (var id in named)
                            if (tenants.Find(id) is { IsActive: true } tenant) await ProcessAsync(tenant, stoppingToken);
                        continue;
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* the round is due */ }
                }
                foreach (var tenant in tenants.All.Where(t => t.IsActive)) await ProcessAsync(tenant, stoppingToken);
                nextRound = DateTimeOffset.UtcNow + interval;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ProcessAsync(TenantInfo tenant, CancellationToken ct)
    {
        try
        {
            // A batch per call; keep going while batches come back full.
            for (var round = 0; round < 50; round++)
            {
                await using var scope = services.CreateTenantScope(tenant);
                var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProcessCommand(), ct);
                if (!result.IsSuccess || result.Value is not { } done) break;
                if (done.Scanned + done.Thumbnails > 0)
                    _log.LogInformation("Storage processing of {Tenant}: {Scanned} scanned ({Infected} infected), {Thumbnails} thumbnails.",
                        tenant.Id, done.Scanned, done.Infected, done.Thumbnails);
                if (done.Scanned < ProcessHandler.Batch && done.Thumbnails < ProcessHandler.Batch) break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(e, "Storage processing of {Tenant} failed; it runs again later.", tenant.Id);
        }
    }
}
