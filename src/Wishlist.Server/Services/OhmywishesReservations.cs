using System.Net;
using System.Text.Json;

using Wishlist.Core;

namespace Wishlist.Server.Services;

public sealed class OhmywishesReservations(HttpClient http, OhmywishesOptions options)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(options.ReservationToken);

    public async Task Reserve(string id, CancellationToken ct, string? readPath = null)
    {
        if ((await Send(HttpMethod.Get, id, ct, readPath)).Occupied)
            throw new RuleException("Подарок уже забронирован в Ohmywishes.");
        if (!(await Send(HttpMethod.Post, id, ct)).Mine)
            throw Unavailable();
    }

    public async Task<ReservationState> Cancel(string id, CancellationToken ct, string? readPath = null)
    {
        var state = await Send(HttpMethod.Get, id, ct, readPath);
        if (!state.Mine) return state;
        state = await Send(HttpMethod.Delete, id, ct);
        return state.Mine ? throw Unavailable() : state;
    }

    public Task<ReservationState> Read(string id, string readPath, CancellationToken ct) =>
        Send(HttpMethod.Get, id, ct, readPath);

    private async Task<ReservationState> Send(HttpMethod method, string id, CancellationToken ct, string? readPath = null)
    {
        if (!Enabled || id.Length != 24 || !id.All(Uri.IsHexDigit)) throw Unavailable();
        var path = readPath ?? $"wishes/{id}";
        if (method != HttpMethod.Get) path += "/reservation";
        using var request = new HttpRequestMessage(method, $"https://ohmywishes.com/api/v3/{path}");
        request.Headers.Add("x-access-token", options.ReservationToken);
        request.Headers.Add("x-content-region", "russia");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("Darim/1.0 (+https://wishes.hype-ny.ru)");
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Conflict && method == HttpMethod.Post)
                throw new RuleException("Подарок уже забронирован в Ohmywishes.");
            if (!response.IsSuccessStatusCode) throw Unavailable();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var item = json.RootElement.GetProperty("item");
            if (item.GetProperty("id").GetString() != id) throw Unavailable();
            var occupied = item.GetProperty("isReservedBySomeone").GetBoolean();
            var mine = item.GetProperty("isReservedByMe").GetBoolean();
            if (mine && !occupied) throw Unavailable();
            return new(occupied, mine);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw Unavailable();
        }
    }

    internal static RuleException Unavailable() => new("Не удалось проверить бронь в Ohmywishes. Попробуйте позже.", 503);
    public sealed record ReservationState(bool Occupied, bool Mine);
}
