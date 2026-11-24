using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Modules.Storage.UseCases;
using soft.square.net.WebApi.Modules.Storage.UseCases.Processing;

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Processing;

/// <summary>
/// ClamAV's daemon (clamd) over TCP: <c>zINSTREAM</c>, the bytes in length-prefixed chunks, then a zero length; clamd
/// answers <c>stream: OK</c> or <c>stream: {threat} FOUND</c>. One connection per file. Settings: <c>Storage:ClamAV</c>.
/// clamd refuses streams over its <c>StreamMaxLength</c> (an error here: the file stays blocked), so set that at least
/// as high as <c>Storage:MaxFileSize</c>.
/// </summary>
internal sealed class ClamAvScanner(IOptions<StorageOptions> options) : IFileScanner
{
    private const int ChunkSize = 64 * 1024;

    public bool Enabled => options.Value.ClamAV.Enabled;

    public async Task<string?> ScanAsync(Stream content, CancellationToken ct)
    {
        var settings = options.Value.ClamAV;
        if (!settings.Enabled) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);

        using var client = new TcpClient();
        await client.ConnectAsync(settings.Host!, settings.Port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);

        var buffer = new byte[4 + ChunkSize];
        int read;
        while ((read = await content.ReadAsync(buffer.AsMemory(4, ChunkSize), timeout.Token)) > 0)
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer, read);
            await stream.WriteAsync(buffer.AsMemory(0, 4 + read), timeout.Token);
        }
        BinaryPrimitives.WriteInt32BigEndian(buffer, 0);
        await stream.WriteAsync(buffer.AsMemory(0, 4), timeout.Token);
        await stream.FlushAsync(timeout.Token);

        var reply = await ReadReplyAsync(stream, timeout.Token);
        return Parse(reply);
    }

    /// <summary>The threat in clamd's reply, null for OK; throws on an error reply (size limit, clamd trouble).</summary>
    internal static string? Parse(string reply)
    {
        var text = reply.TrimEnd('\0', '\n', '\r', ' ');
        if (text.EndsWith(" OK", StringComparison.Ordinal)) return null;
        if (text.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var start = text.IndexOf(": ", StringComparison.Ordinal);
            var threat = text[(start < 0 ? 0 : start + 2)..^" FOUND".Length].Trim();
            return threat.Length == 0 ? "unknown" : threat;
        }
        throw new IOException($"ClamAV could not scan the file: {text}");
    }

    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[256];
        while (true)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) break;
            for (var i = 0; i < n; i++)
            {
                if (one[i] == 0) return Encoding.UTF8.GetString([.. bytes]); // z-commands end with NUL
                bytes.Add(one[i]);
            }
            if (bytes.Count > 4096) break;
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }
}
