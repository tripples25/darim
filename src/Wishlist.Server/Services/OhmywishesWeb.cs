using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wishlist.Contracts;

namespace Wishlist.Server.Services;

public sealed partial class OhmywishesWeb(HttpClient http)
{
    private const string Origin = "https://ohmywishes.com";
    private const int PageSize = 20;

    public static PublicListLink? ParseLink(string? value)
    {
        if (value?.Length is not (> 0 and <= 512) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "ohmywishes.com"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return null;
        var match = ListPath().Match(uri.AbsolutePath);
        return match.Success ? new(match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    public async Task<PublicListSource?> Resolve(PublicListLink link, CancellationToken ct)
    {
        var account = await PublicAccount(link.User, ct);
        if (account == null) return null;
        var title = await PublicList(account, link.ListId, ct);
        return title == null ? null : new(account, link.ListId, title);
    }

    public async Task<Dictionary<string, GiftInput>> Snapshot(string account, string list, CancellationToken ct)
    {
        var snapshot = new Dictionary<string, GiftInput>(StringComparer.Ordinal);
        if (await PublicAccount(account, ct) != account || await PublicList(account, list, ct) == null) return snapshot;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < 2000; offset += PageSize)
        {
            var page = await Get($"users/{account}/wish-lists/{list}/wishes-v2?limit={PageSize}&offset={offset}", ct);
            if (page == null) return [];
            var items = Field(page.Value, "items");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > PageSize)
                throw new InvalidDataException("Invalid Ohmywishes web list page.");
            foreach (var item in items.EnumerateArray())
                if (!ids.Add(Id(item))) throw new InvalidDataException("Ohmywishes web list changed during pagination.");
            if (items.GetArrayLength() == PageSize) continue;

            foreach (var id in ids)
            {
                var response = await Get($"wishes/{id}", ct);
                if (response == null) continue;
                var wish = Item(response.Value);
                if (Id(wish) != id || Id(Field(wish, "creator")) != account)
                    throw new InvalidDataException("Unexpected Ohmywishes web wish or owner ID.");
                var visibility = OhmywishesMcp.String(wish, "visibility")
                    ?? throw new InvalidDataException("Missing Ohmywishes web wish visibility.");
                if (visibility is not ("public" or "inherit_public") || Flag(wish, "isPrivate") || Flag(wish, "isFulfilled"))
                    continue;
                var lists = Field(wish, "wishLists");
                if (lists.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Missing Ohmywishes web wish lists.");
                if (lists.EnumerateArray().Any(entry => Id(entry) == list) && OhmywishesMcp.ReadWish(wish) is { } input)
                    snapshot.Add(id, input);
            }
            // The profile or list can become private while card details are being fetched.
            return await PublicAccount(account, ct) == account && await PublicList(account, list, ct) != null ? snapshot : [];
        }
        throw new InvalidDataException("Ohmywishes web list exceeded the pilot pagination limit.");
    }

    private async Task<string?> PublicAccount(string user, CancellationToken ct)
    {
        var response = await Get($"users/{Uri.EscapeDataString(user)}", ct);
        if (response == null) return null;
        var profile = Item(response.Value);
        return Flag(profile, "isPrivate") ? null : Id(profile);
    }

    private async Task<string?> PublicList(string account, string list, CancellationToken ct)
    {
        if (!PublicId().IsMatch(account) || !PublicId().IsMatch(list))
            throw new InvalidDataException("Invalid Ohmywishes web source IDs.");
        var response = await Get($"users/{account}/wish-lists/{list}", ct);
        if (response == null) return null;
        var item = Item(response.Value);
        if (Id(item) != list) throw new InvalidDataException("Unexpected Ohmywishes web list ID.");
        if (OhmywishesMcp.String(item, "visibility") is { } visibility && visibility is not ("public" or "inherit" or "inherit_public"))
            return null;
        return OhmywishesMcp.String(item, "title") is { Length: > 0 and <= 200 } title
            ? title : throw new InvalidDataException("Invalid Ohmywishes web list title.");
    }

    private async Task<JsonElement?> Get(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{Origin}/api/v3/{path}", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static JsonElement Item(JsonElement json) => Field(json, "item");

    private static JsonElement Field(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value)
            ? value : throw new InvalidDataException($"Missing Ohmywishes web field: {name}.");

    private static string Id(JsonElement item) => OhmywishesMcp.String(item, "id") is { } id && PublicId().IsMatch(id)
        ? id : throw new InvalidDataException("Invalid Ohmywishes web ID.");

    private static bool Flag(JsonElement item, string name) => Field(item, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidDataException($"Invalid Ohmywishes web field: {name}.")
    };

    [GeneratedRegex(@"^/users/([A-Za-z0-9_][A-Za-z0-9_.-]{0,79})/lists/([a-f0-9]{24})/?$")]
    private static partial Regex ListPath();

    [GeneratedRegex(@"^[a-f0-9]{24}$")]
    private static partial Regex PublicId();

    public sealed record PublicListLink(string User, string ListId);
    public sealed record PublicListSource(string AccountId, string ListId, string Title);
}
