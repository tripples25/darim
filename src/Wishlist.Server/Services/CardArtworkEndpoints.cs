using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Wishlist.Core;
using Wishlist.Server.Data;

namespace Wishlist.Server.Services;

internal static class CardArtworkEndpoints
{
    private const int MaxBytes = 5 * 1024 * 1024;
    private const int MaxPixels = 4096 * 4096;
    private static readonly SemaphoreSlim DecodeSlot = new(1);

    public static void MapCardArtwork(this RouteGroupBuilder api)
    {
        var artwork = api.MapGroup("/card-artwork");
        artwork.MapGet("", Manifest);
        artwork.MapGet("/{id:guid}.png", Image);
        artwork.MapPost("/{category}", Upload).Accepts<byte[]>("image/png");
        artwork.MapDelete("/{id:guid}", Remove);
    }

    private static async Task<object> Manifest(HttpContext context, AppDb db, CancellationToken ct)
    {
        var images = await db.CardArtwork.OrderBy(image => image.Id)
            .Select(image => new { image.Id, image.Category, image.BuiltIn }).ToArrayAsync(ct);
        return new { images, canManage = await CanManage(context, db, ct) };
    }

    private static async Task<IResult> Image(Guid id, HttpContext context, AppDb db, CancellationToken ct)
    {
        var data = await db.CardArtwork.Where(x => x.Id == id)
            .Select(x => x.Data).SingleOrDefaultAsync(ct);
        if (data is null)
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.File(data, "image/png");
    }

    private static async Task<IResult> Upload(string category, HttpContext context, AppDb db, CancellationToken ct)
    {
        if (!await CanManage(context, db, ct))
            return Results.Forbid();
        if (category is not ("light" or "dark" or "overlay"))
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

        var image = new CardArtwork { Id = Guid.NewGuid(), Category = category, Data = data };
        db.CardArtwork.Add(image);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/card-artwork/{image.Id}.png", new { image.Id, image.Category, bytes = data.Length });
    }

    private static async Task<IResult> Remove(Guid id, HttpContext context, AppDb db, CancellationToken ct)
    {
        if (!await CanManage(context, db, ct))
            return Results.Forbid();

        await db.CardArtwork.Where(image => image.Id == id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    private static async Task<bool> CanManage(HttpContext context, AppDb db, CancellationToken ct)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return false;

        var configuredIds = context.RequestServices.GetRequiredService<IConfiguration>()
            ["CardArtwork:ManagerTelegramIds"];
        if (string.IsNullOrWhiteSpace(configuredIds))
            return false;

        var managerIds = configuredIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => long.TryParse(value, out var id) && id > 0 ? id : (long?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        if (managerIds.Count == 0)
            return false;

        var telegramId = await db.Users.Where(user => user.Id == userId)
            .Select(user => (long?)user.TelegramId).SingleOrDefaultAsync(ct);
        return telegramId.HasValue && managerIds.Contains(telegramId.Value);
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
