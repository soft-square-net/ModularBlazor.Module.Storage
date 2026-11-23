using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.Infrastructure.Data;

namespace soft.square.net.WebApi.Modules.Storage.IntegrationTests;

/// <summary>
/// One in-memory SQLite database with the module's model (EnsureCreated; the per-engine migrations are checked with
/// <c>dotnet ef migrations has-pending-model-changes</c>). Each <see cref="For"/> call is a new context, as a new
/// request gets, for the given tenant.
/// </summary>
public sealed class StorageDatabase : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<StorageDbContext> _options;

    public StorageDatabase()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<StorageDbContext>().UseSqlite(_connection).Options;
        using var context = For("acme");
        context.Database.EnsureCreated();
    }

    public StorageDbContext For(string? tenantId) => new(_options, new TestTenant(tenantId));

    public void Dispose() => _connection.Dispose();

    private sealed class TestTenant(string? id) : ITenantContext
    {
        public TenantInfo? Tenant { get; } = id is null ? null : new TenantInfo(id, id, TenantStatus.Active, [], "", "");
        public string? ResolvedBy => "test";
    }
}
