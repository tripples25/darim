namespace Wishlist.Server.Data;

public sealed class OhmywishesConnection
{
    public Guid UserId { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
    public string AccountId { get; set; } = "";
    public string? ProtectedTokens { get; set; }
    public bool WebApi { get; set; }
    public string? ListId { get; set; }
    public string? ListTitle { get; set; }
    public DateTimeOffset? NextSync { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public bool Stale { get; set; }
}
