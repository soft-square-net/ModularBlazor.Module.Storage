using System.Reflection;
using Microsoft.EntityFrameworkCore;
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Data;
using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.GroupAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.ItemAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.PersonAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.ShareAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.UploadAggregate;

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Data;

/// <summary>
/// The module's tenant data: schema "storage" in each tenant's database. The entity mappings are the
/// <c>IEntityTypeConfiguration</c> classes in <c>Data/Config</c>; the migrations, one set per engine, are in
/// <c>Data/Migrations/&lt;Provider&gt;</c>.
/// </summary>
public sealed class StorageDbContext(DbContextOptions<StorageDbContext> options, ITenantContext tenant) : TenantDbContext(options, tenant)
{
    protected override string Schema => "storage";

    public DbSet<Item> Items => Set<Item>();
    public DbSet<StorageGroup> Groups => Set<StorageGroup>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<UploadSession> Uploads => Set<UploadSession>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<StoragePerson> People => Set<StoragePerson>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        if (Database.ProviderName == "Oracle.EntityFrameworkCore")
        {
            // Oracle maps a string longer than 2000 characters to NVARCHAR2(2000) only; the parts list can be longer.
            modelBuilder.Entity<UploadSession>().Property(u => u.Parts).HasColumnType("NCLOB");
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // SQLite (the integration tests) cannot compare or sort DateTimeOffset in SQL; the recycle bin, the sweeper and
        // upload expiry do. Stored as a number there, EF's documented workaround. The four real engines are unaffected.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
            configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
        }
    }
}
