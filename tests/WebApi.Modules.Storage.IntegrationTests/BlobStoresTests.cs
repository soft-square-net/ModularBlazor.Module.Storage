using System.Net;
using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Modules.Storage.Infrastructure.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.IntegrationTests;

/// <summary>Which store a location gets, and the S3 store's pieces that need no server.</summary>
public sealed class BlobStoresTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "storage-tests-" + Guid.NewGuid().ToString("N"));
    private readonly BlobStores _stores;

    public BlobStoresTests()
    {
        var connections = new BlobConnectionsOptions();
        connections.Connections["disk"] = new BlobConnectionOptions { Kind = BlobConnectionKind.Local, Root = _root };
        connections.Connections["minio"] = new BlobConnectionOptions
        {
            ServiceUrl = "http://minio:9000", PublicUrl = "http://localhost:9000", AccessKey = "k", SecretKey = "s", ForcePathStyle = true
        };
        _stores = new BlobStores(Options.Create(new LocalBlobOptions { Root = _root }), Options.Create(connections));
    }

    public void Dispose()
    {
        _stores.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TheEmptyLocationIsTheLocalDisk() => _stores.Get("").ShouldBeOfType<FileSystemBlobStore>();

    [Fact]
    public void StoresAreKeptPerLocation()
    {
        _stores.Get("minio|files|").ShouldBeOfType<S3BlobStore>();
        _stores.Get("minio|files|").ShouldBeSameAs(_stores.Get("minio|files|"));
        _stores.Get("minio|other|").ShouldNotBeSameAs(_stores.Get("minio|files|"));
        _stores.Get("disk|b|p/").ShouldBeOfType<FileSystemBlobStore>().MinPartSize.ShouldBe(1);
    }

    [Fact]
    public void AnUnknownConnectionFails() =>
        Should.Throw<InvalidOperationException>(() => _stores.Get("gone|files|")).Message.ShouldContain("gone");

    [Fact]
    public async Task DownloadLinksArePresignedForThePublicEndpoint()
    {
        var url = await _stores.Get("minio|files|pre/").GetDownloadUrlAsync($"t/acme/2026/11/{Guid.NewGuid():N}", "تقرير 1.pdf", "application/pdf",
            inline: true, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        url.ShouldNotBeNull();
        url.GetLeftPart(UriPartial.Authority).ShouldBe("http://localhost:9000");
        url.AbsolutePath.ShouldStartWith("/files/pre/t/acme/2026/11/");
        var query = WebUtility.UrlDecode(url.Query);
        query.ShouldContain("response-content-type=application/pdf");
        query.ShouldContain("inline; filename=");
        query.ShouldContain("X-Amz-Expires=300");
    }

    [Fact]
    public void ContentDispositionKeepsAnyName() =>
        S3BlobStore.ContentDisposition("تقرير \"1\".pdf", inline: false)
            .ShouldBe("attachment; filename=\"_____ _1_.pdf\"; filename*=UTF-8''%D8%AA%D9%82%D8%B1%D9%8A%D8%B1%20%221%22.pdf");
}
