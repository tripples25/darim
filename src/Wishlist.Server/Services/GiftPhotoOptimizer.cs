using SkiaSharp;
using Wishlist.Contracts;
using Wishlist.Core;

namespace Wishlist.Server.Services;

internal static class GiftPhotoOptimizer
{
    // Leave enough pixels for high-density screens while bounding decode memory for uploads.
    private const int MaxEdge = 1600;
    private const long MaxInputPixels = 64_000_000;
    private const int WebpQuality = 92;
    private static readonly SemaphoreSlim DecodeSlot = new(1);

    public static async Task<(byte[] Data, string ContentType)> Optimize(byte[] data, CancellationToken ct)
    {
        if (data.Length > GiftWriteInput.MaxPhotoBytes)
            throw new RuleException($"Фото должно занимать не больше {GiftWriteInput.MaxPhotoMegabytes} МБ.", 400);
        var type = GiftPhotoFormat.ContentType(data)
            ?? throw new RuleException("Выберите фото в формате JPEG, PNG или WebP.", 400);

        await DecodeSlot.WaitAsync(ct);
        try
        {
            return OptimizeImage(data, type);
        }
        finally
        {
            DecodeSlot.Release();
        }
    }

    private static (byte[] Data, string ContentType) OptimizeImage(byte[] data, string type)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var codec = SKCodec.Create(stream) ?? throw InvalidPhoto();
        var source = codec.Info;
        if (source.Width <= 0 || source.Height <= 0)
            throw InvalidPhoto();
        if ((long)source.Width * source.Height > MaxInputPixels)
            throw new RuleException("Разрешение фото должно быть не больше 64 мегапикселей.", 400);

        var edge = Math.Max(source.Width, source.Height);
        var resize = edge > MaxEdge;
        var scale = Math.Min(1d, (double)MaxEdge / edge);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        // Ask the decoder for a smaller bitmap when it can downsample without a full-size allocation.
        var decodedSize = codec.GetScaledDimensions(Math.Min(1f, (float)(2 * scale)));
        using var colorSpace = SKColorSpace.CreateSrgb();
        var alpha = source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var decodeInfo = new SKImageInfo(decodedSize.Width, decodedSize.Height, SKColorType.Rgba8888, alpha, colorSpace);
        using var decoded = new SKBitmap(decodeInfo);
        if (decoded.GetPixels() == IntPtr.Zero || codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success)
            throw InvalidPhoto();

        // Keep animated images intact rather than replacing their animation with the first frame.
        if (codec.FrameCount > 1)
            return (data, type);

        var origin = codec.EncodedOrigin;
        var swapped = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var outputInfo = new SKImageInfo(swapped ? height : width, swapped ? width : height, SKColorType.Rgba8888, alpha, colorSpace);
        using var output = new SKBitmap(outputInfo);
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.Transparent);
            decoded.SetImmutable();
            using var image = SKImage.FromBitmap(decoded);
            canvas.Concat(Orientation(origin, width, height));
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            canvas.DrawImage(image, new SKRect(0, 0, width, height), sampling);
        }

        // Preserve pixel detail in screenshots and transparency in product images.
        var lossless = !resize || type == "image/png" || alpha != SKAlphaType.Opaque;
        var options = lossless
            ? new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 60)
            : new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, WebpQuality);
        using var pixels = output.PeekPixels();
        using var encoded = SKWebpEncoder.Encode(pixels, options) ?? throw InvalidPhoto();
        return !resize && encoded.Size >= data.Length ? (data, type) : (encoded.ToArray(), "image/webp");
    }

    // EXIF can mirror an image as well as rotate it.
    private static SKMatrix Orientation(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity
    };

    private static RuleException InvalidPhoto() => new("Фотография повреждена. Выберите другой файл.", 400);
}
