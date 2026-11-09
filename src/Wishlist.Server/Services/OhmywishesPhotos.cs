using Wishlist.Contracts;

namespace Wishlist.Server.Services;

public sealed class OhmywishesPhotos(HttpClient http, ILogger<OhmywishesPhotos>? logger = null)
{
    public async Task<ImportedPhoto?> Download(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "cdn.ohmywish.es" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var max = GiftWriteInput.MaxPhotoBytes;
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > max)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var image = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (image.Length + read > max) return null;
                image.Write(buffer, 0, read);
            }

            var bytes = image.ToArray();
            return GiftPhotoFormat.ContentType(bytes) is { } type ? new(bytes, type) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger?.LogWarning("Ohmywishes photo download failed ({Reason}).", ex.GetType().Name);
            return null;
        }
    }

    public sealed record ImportedPhoto(byte[] Data, string ContentType);
}
