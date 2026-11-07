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
        var shared = !PublicId().IsMatch(link.ListId);
        var account = await Account(link.User, shared, ct);
        if (account == null) return null;
        return await ReadList(account, link.ListId, shared, ct);
    }

    public async Task<Dictionary<string, GiftInput>> Snapshot(string account, string list, CancellationToken ct, string? shareToken = null)
    {
        var snapshot = new Dictionary<string, GiftInput>(StringComparer.Ordinal);
        var shared = shareToken != null;
        var reference = shareToken ?? list;
        if (!await ListAvailable(account, list, reference, shared, ct)) return snapshot;
        var wishesPath = $"users/{account}/wish-lists/{reference}/wishes-v2";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < 2000; offset += PageSize)
        {
            var page = await Get($"{wishesPath}?limit={PageSize}&offset={offset}", ct);
            if (page == null) return [];
            var items = Field(page.Value, "items");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > PageSize)
                throw new InvalidDataException("Invalid Ohmywishes web list page.");
            foreach (var item in items.EnumerateArray())
                if (!ids.Add(Id(item))) throw new InvalidDataException("Ohmywishes web list changed during pagination.");
            if (items.GetArrayLength() == PageSize) continue;

            foreach (var id in ids)
            {
                var response = await Get(shared ? $"{wishesPath}/{id}" : $"wishes/{id}", ct);
                if (response == null) continue;
                var wish = Item(response.Value);
                if (Id(wish) != id || Id(Field(wish, "creator")) != account)
                    throw new InvalidDataException("Unexpected Ohmywishes web wish or owner ID.");
                if (shared)
                {
                    // The scoped route authorizes each card using the same share token as the list.
                    var sharedVisibility = OhmywishesMcp.String(wish, "visibility");
                    if ((sharedVisibility is null or "public" or "inherit_public" or "by_link" or "inherit_by_link")
                        && (!wish.TryGetProperty("isPrivate", out _) || !Flag(wish, "isPrivate"))
                        && (!wish.TryGetProperty("isFulfilled", out _) || !Flag(wish, "isFulfilled")))
                        snapshot.Add(id, OhmywishesMcp.ReadWishFields(wish));
                    continue;
                }
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
            return await ListAvailable(account, list, reference, shared, ct) ? snapshot : [];
        }
        throw new InvalidDataException("Ohmywishes web list exceeded the pilot pagination limit.");
    }

    private async Task<bool> ListAvailable(string account, string list, string reference, bool shared, CancellationToken ct) =>
        (shared || await Account(account, false, ct) == account)
        && (await ReadList(account, reference, shared, ct))?.ListId == list;

    private async Task<string?> Account(string user, bool shared, CancellationToken ct)
    {
        var response = await Get($"users/{Uri.EscapeDataString(user)}", ct);
        if (response == null) return null;
        var profile = Item(response.Value);
        return !shared && Flag(profile, "isPrivate") ? null : Id(profile);
    }

    private async Task<PublicListSource?> ReadList(string account, string reference, bool shared, CancellationToken ct)
    {
        if (!PublicId().IsMatch(account) || !(shared ? ShareId().IsMatch(reference) : PublicId().IsMatch(reference)))
            throw new InvalidDataException("Invalid Ohmywishes web source IDs.");
        var response = await Get($"users/{account}/wish-lists/{reference}", ct);
        if (response == null) return null;
        var item = Item(response.Value);
        var id = Id(item);
        if (!shared && id != reference) throw new InvalidDataException("Unexpected Ohmywishes web list ID.");
        var visibility = OhmywishesMcp.String(item, "visibility");
        if (visibility is not (null or "public" or "inherit" or "inherit_public")
            && !(shared && visibility == "by_link"))
            return null;
        var title = OhmywishesMcp.String(item, "title") is { Length: > 0 and <= 200 } name
            ? name : throw new InvalidDataException("Invalid Ohmywishes web list title.");
        return new(account, id, title, shared ? reference : null);
    }

    private async Task<JsonElement?> Get(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Origin}/api/v3/{path}");
        request.Headers.UserAgent.ParseAdd("Darim/1.0 (+https://wishes.hype-ny.ru)");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // An HTML gateway denial says nothing about the list's sharing permissions.
            if (response.StatusCode == HttpStatusCode.Forbidden &&
                string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                response.EnsureSuccessStatusCode();
            return null;
        }
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

    private const string SharePattern = "[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}";

    [GeneratedRegex(@"^/users/([A-Za-z0-9_][A-Za-z0-9_.-]{0,79})/lists/([a-f0-9]{24}|" + SharePattern + @")/?$")]
    private static partial Regex ListPath();

    [GeneratedRegex(@"^[a-f0-9]{24}$")]
    private static partial Regex PublicId();

    [GeneratedRegex("^" + SharePattern + "$")]
    private static partial Regex ShareId();

    public sealed record PublicListLink(string User, string ListId);
    public sealed record PublicListSource(string AccountId, string ListId, string Title, string? ShareToken);
}
