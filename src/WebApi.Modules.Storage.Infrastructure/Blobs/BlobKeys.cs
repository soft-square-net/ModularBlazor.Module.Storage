using System.Text.RegularExpressions;

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Blobs;

/// <summary>
/// Blob keys as <c>Blob.NewKey</c> makes them (<c>t/acme/2026/11/0193...</c>), and a blob's thumbnail next to it
/// (<c>Blob.ThumbnailKey</c>: the key + <c>.thumb.webp</c>); the stores refuse anything else.
/// </summary>
internal static partial class BlobKeys
{
    public static string Require(string key) =>
        KeyPattern().IsMatch(key) ? key : throw new ArgumentException("Invalid blob key.", nameof(key));

    [GeneratedRegex(@"^t/[a-z0-9][a-z0-9._-]{0,63}/[0-9]{4}/[0-9]{2}/[0-9a-f]{32}(\.thumb\.webp)?$")]
    private static partial Regex KeyPattern();
}
