namespace Wishlist.Server.Data;

public sealed class CardArtwork
{
    public Guid Id { get; init; }
    public required string Category { get; init; }
    public string? BuiltIn { get; init; }
    public byte[]? Data { get; init; }
}
