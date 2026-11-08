namespace Wishlist.Contracts;

public static class GiftPhotoFormat
{
    public static string? ContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
            return "image/jpeg";
        if (data.Length >= 24 && data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && data[12..16].SequenceEqual("IHDR"u8))
            return "image/png";
        if (data.Length >= 20 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)
            && (data[12..16].SequenceEqual("VP8 "u8) || data[12..16].SequenceEqual("VP8L"u8) || data[12..16].SequenceEqual("VP8X"u8)))
            return "image/webp";
        return null;
    }
}
