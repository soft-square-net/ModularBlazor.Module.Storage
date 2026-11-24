using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using soft.square.net.WebApi.Modules.Storage.UseCases.Processing;

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Processing;

/// <summary>
/// Thumbnails with ImageSharp: decoded at about the target size (JPEG decodes scaled down), turned upright from the
/// EXIF orientation, fitted inside a square of the given size and saved as WebP. Images over
/// <see cref="MaxPixels"/> are not decoded at all (a small file can claim a huge canvas).
/// </summary>
internal sealed class ImageSharpThumbnailer : IThumbnailer
{
    public const long MaxPixels = 64L * 1024 * 1024;

    public async Task<byte[]?> MakeAsync(Stream image, int size, CancellationToken ct)
    {
        // ImageSharp reads the header, then decodes: both need to seek, which S3 streams cannot.
        await using var source = image.CanSeek ? null : new MemoryStream();
        var input = image;
        if (source is not null)
        {
            await image.CopyToAsync(source, ct);
            source.Position = 0;
            input = source;
        }

        try
        {
            var info = await Image.IdentifyAsync(input, ct);
            if ((long)info.Width * info.Height > MaxPixels) return null;
            input.Position = 0;

            var decoder = new DecoderOptions { TargetSize = new Size(size, size) };
            using var picture = await Image.LoadAsync(decoder, input, ct);
            picture.Mutate(x => x.AutoOrient().Resize(new ResizeOptions { Size = new Size(size, size), Mode = ResizeMode.Max }));
            using var output = new MemoryStream();
            await picture.SaveAsync(output, new WebpEncoder { Quality = 80 }, ct);
            return output.ToArray();
        }
        catch (Exception e) when (e is ImageFormatException or UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            return null; // not an image ImageSharp can read: no thumbnail, not an error
        }
    }
}
