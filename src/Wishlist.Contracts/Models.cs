namespace Wishlist.Contracts;
public record PersonDto(Guid Id, string Name, string? Contact = null);
public record CompanyDto(Guid Id, string Name, bool IsOrganizer, PersonDto[] Members);
public record MeDto(PersonDto User, CompanyDto? Company, CompanyDto[]? Companies = null);
public record GiftVisibility(bool AllCompanies, Guid[] CompanyIds);
public record GiftInput(string Title, string? Url = null, decimal? Price = null, string? Comment = null, string Occasion = "Без повода", DateOnly? OccasionDate = null);
public record ParticipationDto(string Mode, bool Bought, int Slots, int Taken, bool IsMine, bool IsCoordinator, PersonDto[]? Participants, Guid? CoordinatorId = null);
public record GiftWriteInput(string Title, string? Url = null, decimal? Price = null, string? Comment = null,
    string Occasion = "Без повода", DateOnly? OccasionDate = null, byte[]? Photo = null, bool RemovePhoto = false,
    GiftVisibility? Visibility = null, bool Unlimited = false)
    : GiftInput(Title, Url, Price, Comment, Occasion, OccasionDate)
{
    public const int MaxPhotoMegabytes = 15;
    public const int MaxPhotoBytes = MaxPhotoMegabytes * 1024 * 1024;
}
public record GiftDto(Guid Id, PersonDto Owner, GiftInput Details, bool Archived, ParticipationDto? Participation, GiftInput? Original = null,
    bool FromOhmywishes = false, string? PhotoUrl = null, Guid? PhotoVersion = null, GiftVisibility? Visibility = null, bool Unlimited = false);
public record OhmywishesStatusDto(bool Enabled, bool Connected, bool Reconnect, string? ListId, string? ListTitle, DateTimeOffset? LastSuccess, bool Stale, bool WebApiEnabled = false, bool WebApiConnected = false);
public record OhmywishesListDto(string Id, string Title);
public record OhmywishesSelection(string ListId, Guid? CompanyId = null);
public record OhmywishesAuthorization(string Url);
public record OhmywishesWebSelection(string Url, Guid? CompanyId = null);
public record NoticeDto(Guid Id, string Text, DateTimeOffset Created, bool Read);
public record CreateCompanyInput(string Name);
public record InviteDto(string Token, Guid? CompanyId = null);
public record InviteOptions(int? MaxUses, Guid? CompanyId = null);
public record JoinCompanyInput(string Token);
public record CompanyJoinDto(Guid CompanyId);
public record ProfileInput(string Name, string? Contact);
public record GiftActionInput(string Action, int? Slots = null, Guid? UserId = null, Guid? CompanyId = null);
public record MiniLoginInput(string InitData);
public record MiniSessionDto(string Token, DateTimeOffset Expires);
public record CsrfDto(string Token);
public record AuthInfoDto(bool TelegramConfigured, bool DemoEnabled, string? BotUsername = null);
