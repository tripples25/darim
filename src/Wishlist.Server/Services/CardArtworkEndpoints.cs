using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Wishlist.Core;
using Wishlist.Server.Data;

namespace Wishlist.Server.Services;

internal static class CardArtworkEndpoints
{
    private const string ManagerUsername = "sharphurt";
    private const int MaxBytes = 5 * 1024 * 1024;
    private const int MaxPixels = 4096 * 4096;
    private static readonly SemaphoreSlim DecodeSlot = new(1);
    private static readonly HashSet<string> Slots = new(StringComparer.Ordinal)
    {
        "background-light", "background-light-1", "background-light-2", "background-light-3",
        "background-dark", "background-dark-1", "background-dark-2", "background-dark-3",
        "overlay-1", "overlay-2", "overlay-3", "overlay-4",
    };

    public static void MapCardArtwork(this RouteGroupBuilder api)
    {
        var artwork = api.MapGroup("/card-artwork");
        artwork.MapGet("", Manifest);
        artwork.MapGet("/{slot}/{version:guid}.png", Image);
        artwork.MapPut("/{slot}", Replace).Accepts<byte[]>("image/png");
    }

    private static async Task<object> Manifest(HttpContext context, AppDb db, CancellationToken ct)
    {
        var versions = await db.CardArtwork.AsNoTracking().ToDictionaryAsync(x => x.Slot, x => x.Version, ct);
        return new { versions, canManage = await CanManage(context, db, ct) };
    }

    private static async Task<IResult> Image(string slot, Guid version, HttpContext context, AppDb db, CancellationToken ct)
    {
        var data = await db.CardArtwork.Where(x => x.Slot == slot && x.Version == version)
            .Select(x => x.Data).SingleOrDefaultAsync(ct);
        if (data is null)
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.File(data, "image/png");
    }

    private static async Task<IResult> Replace(string slot, HttpContext context, AppDb db, CancellationToken ct)
    {
        if (!await CanManage(context, db, ct))
            return Results.Forbid();
        if (!Slots.Contains(slot))
            return Results.NotFound();

        var original = await ReadPng(context.Request, ct);
        await DecodeSlot.WaitAsync(ct);
        byte[] data;
        try
        {
            data = OptimizePng(original);
        }
        finally
        {
            DecodeSlot.Release();
        }

        var version = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CardArtwork" ("Slot", "Version", "Data") VALUES ({slot}, {version}, {data})
            ON CONFLICT ("Slot") DO UPDATE SET "Version" = EXCLUDED."Version", "Data" = EXCLUDED."Data"
            """, ct);
        return Results.Ok(new { version, bytes = data.Length });
    }

    private static async Task<bool> CanManage(HttpContext context, AppDb db, CancellationToken ct)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return false;

        // AccountService sets Username from verified Telegram login, independently of the editable profile.
        var matches = await db.Users.Where(user => user.Username != null && user.Username.ToLower() == ManagerUsername)
            .Select(user => user.Id).Take(2).ToArrayAsync(ct);
        return matches.Length == 1 && matches[0] == userId;
    }

    private static async Task<byte[]> ReadPng(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBytes)
            throw TooLarge();
        if (!string.Equals(request.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
            throw InvalidPng();

        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > MaxBytes)
                throw TooLarge();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] OptimizePng(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var codec = SKCodec.Create(input);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || data.Length < 33)
            throw InvalidPng();

        var source = codec.Info;
        if (source.Width > 4096 || source.Height > 4096 || (long)source.Width * source.Height > MaxPixels)
            throw new RuleException("Максимальный размер изображения — 4096 × 4096 пикселей.", 400);

        var alpha = source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul;
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, alpha, source.ColorSpace);
        using var bitmap = new SKBitmap(info);
        if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw InvalidPng();

        // Preserve higher bit depths, animation and EXIF orientation when re-encoding would change them.
        if (data[24] > 8 || codec.FrameCount > 1 || codec.EncodedOrigin != SKEncodedOrigin.TopLeft)
            return data;

        using var pixels = bitmap.PeekPixels();
        using var encoded = pixels.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9));
        return encoded is not null && encoded.Size < data.Length ? encoded.ToArray() : data;
    }

    private static RuleException TooLarge() => new("Размер PNG должен быть не больше 5 МБ.", 413);
    private static RuleException InvalidPng() => new("Выберите корректный PNG-файл.", 400);
}
