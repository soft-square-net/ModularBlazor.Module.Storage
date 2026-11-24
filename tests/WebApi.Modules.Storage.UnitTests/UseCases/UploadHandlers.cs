using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Modules.Storage.Core.UploadAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;
using soft.square.net.WebApi.Modules.Storage.UseCases.Uploads;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.Tree;
using static soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases.HandlerFixture;

namespace soft.square.net.WebApi.Modules.Storage.UnitTests.UseCases;

/// <summary>Where an upload goes (the tenant's placement), its chunk size, and the quota.</summary>
public class StartUploadHandlerHandle
{
    private readonly HandlerFixture _f = new();
    private readonly IBlobStores _stores = Substitute.For<IBlobStores>();
    private readonly IBlobStore _blobs = Substitute.For<IBlobStore>();
    private readonly StorageOptions _options = new();
    private UploadSession? _started;

    public StartUploadHandlerHandle()
    {
        var acme = _f.Tenant.Tenant!;
        _f.Tenant.Required.Returns(acme);
        _stores.Get(Arg.Any<string>()).Returns(_blobs);
        _blobs.MinPartSize.Returns(1);
        _blobs.StartUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("upload-1");
        _f.Store.When(s => s.Add(Arg.Any<UploadSession>())).Do(call => _started = call.Arg<UploadSession>());
        _f.Store.UsageAsync(Arg.Any<CancellationToken>()).Returns((900L, 50L));
    }

    private StartUploadHandler Handler => new(_f.Store, _f.Access, _f.Caller, _stores, _f.Tenant, Options.Create(_options));

    private Item WritableRoot() => _f.Put(Root(Allow(PrincipalKind.Everyone, null, StorageRights.Modify)));

    [Fact]
    public async Task GoesToTheTenantsPlacement()
    {
        _options.Default = new BlobPlacement { Connection = "minio", Bucket = "files" };
        _options.Tenants["ACME"] = new BlobPlacement { Prefix = "/acme/" };

        var result = await Handler.Handle(new StartUploadCommand(WritableRoot().Id, "a.txt", 10, "text/plain", NameConflict.Fail), Ct);

        result.IsSuccess.ShouldBeTrue();
        _started.ShouldNotBeNull();
        _started.Location.ShouldBe("minio|files|acme/");
        _started.ProviderUploadId.ShouldBe("upload-1");
        _started.BlobKey.ShouldStartWith("t/acme/");
        _stores.Received().Get("minio|files|acme/");
    }

    [Fact]
    public async Task WithoutAConnectionTheLocalDiskIsUsed()
    {
        await Handler.Handle(new StartUploadCommand(WritableRoot().Id, "a.txt", 10, null, NameConflict.Fail), Ct);

        _started!.Location.ShouldBeNull();
    }

    [Fact]
    public async Task ChunksAreAtLeastTheStoresSmallestPart()
    {
        _options.ChunkSize = 1024;
        _blobs.MinPartSize.Returns(5 * 1024 * 1024);

        var result = await Handler.Handle(new StartUploadCommand(WritableRoot().Id, "big.bin", 20_000_000, null, NameConflict.Fail), Ct);

        result.Value.ChunkSize.ShouldBe(5 * 1024 * 1024);
    }

    [Fact]
    public async Task TheQuotaCountsKeptAndPendingBytes()
    {
        _options.Default = new BlobPlacement { Quota = 1000 };
        var root = WritableRoot();

        (await Handler.Handle(new StartUploadCommand(root.Id, "fits.txt", 50, null, NameConflict.Fail), Ct)).IsSuccess.ShouldBeTrue();
        var refused = await Handler.Handle(new StartUploadCommand(root.Id, "too-big.txt", 51, null, NameConflict.Fail), Ct);

        refused.Status.ShouldBe(ResultStatus.Conflict);
        refused.Errors.ShouldContain(e => e.Contains("quota"));
    }

    [Fact]
    public async Task NoQuotaMeansNoCount()
    {
        _options.Default = new BlobPlacement { Quota = 0 };

        (await Handler.Handle(new StartUploadCommand(WritableRoot().Id, "a.txt", 10_000, null, NameConflict.Fail), Ct)).IsSuccess.ShouldBeTrue();
        await _f.Store.DidNotReceive().UsageAsync(Arg.Any<CancellationToken>());
    }
}

public class BlobPlacements
{
    [Fact]
    public void ATenantsEntryOverridesTheDefaultSettingBySetting()
    {
        var options = new StorageOptions { Default = new BlobPlacement { Connection = "s3", Bucket = "shared", Quota = 100 } };
        options.Tenants["big"] = new BlobPlacement { Bucket = "big-co", Quota = 0 };

        var big = options.PlacementOf("BIG");
        big.Connection.ShouldBe("s3");
        big.Bucket.ShouldBe("big-co");
        big.Limit.ShouldBeNull();
        options.PlacementOf("other").Limit.ShouldBe(100);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("a", "a/")]
    [InlineData("\\a\\b\\", "a/b/")]
    public void PrefixesAreNormalized(string? prefix, string expected) => BlobLocations.NormalizePrefix(prefix).ShouldBe(expected);

    [Fact]
    public void LocationsRoundTrip()
    {
        var location = BlobLocations.Of(new BlobPlacement { Connection = " minio ", Bucket = "files", Prefix = "x" });
        location.ShouldBe("minio|files|x/");
        BlobLocations.Parse(location).ShouldBe(("minio", "files", "x/"));
        BlobLocations.Parse("").ShouldBe(("", "", ""));
        BlobLocations.Of(new BlobPlacement { Connection = "Local", Bucket = "ignored" }).ShouldBe("");
        Should.Throw<InvalidOperationException>(() => BlobLocations.Parse("nonsense"));
    }

    [Fact]
    public void BigFilesGetBiggerChunks()
    {
        var upload = UploadSession.Start(Guid.NewGuid(), "huge.bin", null, size: 10_000, chunkSize: 10, maxFileSize: 1_000_000,
            NameConflict.Fail, "t/acme/2026/11/x", "", "ana", Now, TimeSpan.FromHours(1));

        upload.ChunkSize.ShouldBe(20);
        upload.ChunkCount.ShouldBe(UploadSession.MaxParts);
    }
}
