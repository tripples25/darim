namespace Wishlist.Contracts;

public record WishlistLinkInput(Guid? CompanyId = null, string? Label = null);
public record WishlistLinkDto(Guid Id, Guid? CompanyId, DateTimeOffset Created, DateTimeOffset? Revoked, string? Label);
public record WishlistLinkCreated(WishlistLinkDto Link, string Token);
public record PublicWishlistDto(string OwnerName, PublicWishDto[] Gifts);
public record PublicWishDto(Guid Id, GiftInput Details, string? PhotoUrl, Guid? PhotoVersion);
public record WishlistRequestInput(bool ShareUsername = false, string? Token = null);
public record WishlistRequestDto(Guid Id, Guid GiftId, string Title, DateTimeOffset Created, bool Confirmed);
public record WishlistInterestDto(Guid Id, Guid GiftId, string Title, DateTimeOffset Created, string? TelegramUsername, bool Notified);
