namespace Wishlist.Server.Data;

public sealed class CardArtwork
{
    public required string Slot { get; init; }
    public Guid Version { get; init; }
    public required byte[] Data { get; init; }
}
