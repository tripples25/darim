using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wishlist.Contracts;

namespace Wishlist.Server.Services;

public sealed class OhmywishesMcp(HttpClient http)
{
    public async Task<string> Account(string token, CancellationToken ct)
    {
        await using var session = await Connect(token, ct);
        return Id(await session.Tool("get_my_profile", new { }, ct));
    }

    public async Task<OhmywishesListDto[]> Lists(string token, CancellationToken ct)
    {
        await using var session = await Connect(token, ct);
        var lists = await Pages(session, "list_my_wishlists", null, ct);
        return lists.Where(item => String(item, "visibility") is "public" or "inherit" or "by_link")
            .Select(item => new OhmywishesListDto(Id(item), RequiredString(item, "title", "name"))).ToArray();
    }

    public async Task<Dictionary<string, GiftInput>> Snapshot(string token, string listId, CancellationToken ct)
    {
        await using var session = await Connect(token, ct);
        var lists = await Pages(session, "list_my_wishlists", null, ct);
        var selected = lists.SingleOrDefault(item => Id(item) == listId);
        if (selected.ValueKind == JsonValueKind.Undefined ||
            String(selected, "visibility") is "private" or "hidden" or "followers")
            return [];
        if (String(selected, "visibility") is not ("public" or "inherit" or "by_link"))
            throw new InvalidDataException("Unknown Ohmywishes list visibility.");
        var wishes = await Pages(session, "list_my_wishes", listId, ct);
        var snapshot = new Dictionary<string, GiftInput>(StringComparer.Ordinal);
        foreach (var item in wishes)
        {
            if (!Shareable(item)) continue;
            var id = Id(item);
            var detail = await session.Tool("get_my_wish", new { wishId = id }, ct);
            if (Id(detail) != id) throw new InvalidDataException("Unexpected Ohmywishes wish ID.");
            if (ReadWish(detail) is { } wish) snapshot[id] = wish;
        }
        return snapshot;
    }

    private static async Task<List<JsonElement>> Pages(Session session, string tool, string? listId, CancellationToken ct)
    {
        var items = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var offset = 0;
        while (true)
        {
            var args = new Dictionary<string, object> { ["limit"] = 30, ["offset"] = offset };
            if (listId != null) args["wishListId"] = listId;
            var page = await session.Tool(tool, args, ct);
            if (!Property(page, out var array, "items", "wishes", "wishlists", "wishLists") ||
                array.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Invalid Ohmywishes page.");
            foreach (var item in array.EnumerateArray())
            {
                if (!items.TryAdd(Id(item), item))
                    throw new InvalidDataException("Ohmywishes list changed during pagination.");
            }
            if (!Property(page, out var next, "nextOffset") || next.ValueKind == JsonValueKind.Null)
                return items.Values.ToList();
            if (next.ValueKind != JsonValueKind.Number || !next.TryGetInt32(out var value) || value <= offset || array.GetArrayLength() == 0)
                throw new InvalidDataException("Invalid Ohmywishes pagination cursor.");
            offset = value;
        }
    }

    private static bool Shareable(JsonElement item)
    {
        if (Property(item, out var hidden, "isPrivate", "private") && hidden.ValueKind == JsonValueKind.True)
            return false;
        return String(item, "effectiveVisibility", "visibility") switch
        {
            "public" or "inherit_public" or "by_link" => true,
            "private" or "inherit_private" or "hidden" or "followers" => false,
            _ => throw new InvalidDataException("Unknown Ohmywishes wish visibility.")
        };
    }

    public static GiftInput? ReadWish(JsonElement detail)
    {
        if (!Shareable(detail) || (Property(detail, out var fulfilled, "isFulfilled", "fulfilled")
            && fulfilled.ValueKind == JsonValueKind.True)) return null;
        var title = RequiredString(detail, "title").Trim();
        var description = String(detail, "description")?.Trim();
        var link = String(detail, "url", "externalUri", "link")?.Trim();
        if (link?.Length > 2048 || !Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) link = null;
        decimal? price = null;
        if (Property(detail, out var amount, "price") && String(amount, "currency") == "RUB"
            && Property(amount, out var numeric, "amount", "price") && numeric.ValueKind == JsonValueKind.Number
            && numeric.TryGetDecimal(out var value) && value is >= 0 and <= 100000000)
            price = decimal.Round(value, 2, MidpointRounding.AwayFromZero);
        return new(Limit(title, 160), link, price, description == null ? null : Limit(description, 2000));
    }

    private static string Limit(string value, int length) => value.Length <= length ? value
        : value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];

    private static string Id(JsonElement item) => RequiredString(item, "id", "_id") is { Length: <= 200 } id
        ? id : throw new InvalidDataException("Invalid Ohmywishes ID.");

    private static string RequiredString(JsonElement item, params string[] names) =>
        String(item, names) is { } value && !string.IsNullOrWhiteSpace(value) ? value
            : throw new InvalidDataException("Missing Ohmywishes field.");

    internal static string? String(JsonElement item, params string[] names) =>
        Property(item, out var value, names) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Property(JsonElement item, out JsonElement value, params string[] names)
    {
        if (item.ValueKind == JsonValueKind.Object)
            foreach (var name in names)
                if (item.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    private async Task<Session> Connect(string token, CancellationToken ct)
    {
        var session = new Session(http, token);
        await session.Initialize(ct);
        return session;
    }

    private sealed class Session(HttpClient http, string token) : IAsyncDisposable
    {
        private string? sessionId;
        private string? version;
        private int sequence;

        public async Task Initialize(CancellationToken ct)
        {
            var result = await Send("initialize", new { protocolVersion = "2025-11-25",
                capabilities = new { }, clientInfo = new { name = "Darim", version = "1.0" } }, ct);
            version = RequiredString(result, "protocolVersion");
            if (version is not ("2025-11-25" or "2025-06-18" or "2025-03-26"))
                throw new InvalidDataException("Unsupported Ohmywishes MCP version.");
            await Send("notifications/initialized", new { }, ct, notification: true);
        }

        public async Task<JsonElement> Tool(string name, object arguments, CancellationToken ct)
        {
            var result = await Send("tools/call", new { name, arguments }, ct);
            if (Property(result, out var error, "isError") && error.ValueKind == JsonValueKind.True)
                throw new InvalidDataException($"Ohmywishes tool failed: {name}.");
            if (Property(result, out var structured, "structuredContent") && structured.ValueKind == JsonValueKind.Object)
                return structured;
            if (Property(result, out var content, "content") && content.ValueKind == JsonValueKind.Array
                && content.GetArrayLength() == 1 && String(content[0], "type") == "text"
                && String(content[0], "text") is { } text)
            {
                using var json = JsonDocument.Parse(text);
                return json.RootElement.Clone();
            }
            throw new InvalidDataException("Invalid Ohmywishes tool response.");
        }

        private HttpRequestMessage Request(HttpMethod method)
        {
            var request = new HttpRequestMessage(method, OhmywishesOptions.Resource);
            request.Headers.Authorization = new("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (sessionId != null) request.Headers.Add("Mcp-Session-Id", sessionId);
            if (version != null) request.Headers.Add("MCP-Protocol-Version", version);
            return request;
        }

        private async Task<JsonElement> Send(string method, object parameters, CancellationToken ct, bool notification = false)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            ct = timeout.Token;
            var id = ++sequence;
            var body = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
            if (!notification) body["id"] = id;
            using var request = Request(HttpMethod.Post);
            request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new OhmywishesAccessException();
            response.EnsureSuccessStatusCode();
            if (method == "initialize" && response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
                sessionId = ids.Single();
            if (notification) return default;
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                return RpcResult(await response.Content.ReadFromJsonAsync<JsonElement>(ct), id);

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            var data = new List<string>();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) data.Add(line[5..].TrimStart(' '));
                if (line.Length != 0 || data.Count == 0) continue;
                using var message = JsonDocument.Parse(string.Join('\n', data));
                data.Clear();
                if (Property(message.RootElement, out var responseId, "id") && responseId.ValueKind == JsonValueKind.Number
                    && responseId.GetInt32() == id) return RpcResult(message.RootElement, id);
            }
            throw new InvalidDataException("Ohmywishes MCP stream ended before the response.");
        }

        private static JsonElement RpcResult(JsonElement envelope, int id) =>
            Property(envelope, out var responseId, "id") && responseId.ValueKind == JsonValueKind.Number
            && responseId.GetInt32() == id && Property(envelope, out var result, "result") ? result.Clone()
            : throw new InvalidDataException("Invalid Ohmywishes MCP response.");

        public async ValueTask DisposeAsync()
        {
            if (sessionId == null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                using var request = Request(HttpMethod.Delete);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
        }
    }
}
