using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Wishlist.Core;

namespace Wishlist.Server.Services;

public sealed class OhmywishesOptions(IConfiguration configuration)
{
    public const string Issuer = "https://api.ohmywishes.com";
    public const string Resource = Issuer + "/mcp";
    public const string Scope = "wishlist:read";
    public const string AuthorizationEndpoint = "https://ohmywishes.com/oauth/authorize";
    public const string TokenEndpoint = Issuer + "/v3/oauth/token";
    public const string CallbackPath = "/auth/ohmywishes/callback";
    public string? ClientId => configuration["Ohmywishes:ClientId"];
    public string? ClientSecret => configuration["Ohmywishes:ClientSecret"];
    public string? RedirectUri => configuration["Ohmywishes:RedirectUri"];
    public string TokenAuthMethod => configuration["Ohmywishes:TokenAuthMethod"] ?? "client_secret_basic";
    public bool WebApiEnabled => bool.TryParse(configuration["Ohmywishes:WebApiEnabled"], out var enabled) && enabled;
    public string? ReservationToken => configuration["Ohmywishes:ReservationToken"];
    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Ohmywishes:IntervalMinutes", 30)));
    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && Https(RedirectUri)
        && new Uri(RedirectUri!).AbsolutePath == CallbackPath
        && string.IsNullOrEmpty(new Uri(RedirectUri!).Query)
        && (TokenAuthMethod == "none" || (TokenAuthMethod is "client_secret_basic" or "client_secret_post"
            && !string.IsNullOrWhiteSpace(ClientSecret)));

    private static bool Https(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
}

public sealed class OhmywishesOAuth(OhmywishesOptions options, IDataProtectionProvider protection, HttpClient http)
{
    private const string Cookie = "wishlist.ohmywishes";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private readonly ITimeLimitedDataProtector stateProtector =
        protection.CreateProtector("ohmywishes.oauth-state.v1").ToTimeLimitedDataProtector();

    public string Start(HttpContext context, Guid user, Guid attempt)
    {
        if (!options.Enabled)
            throw new RuleException("Подключение Ohmywishes ещё не настроено.", 503);
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        context.Response.Cookies.Append(Cookie, stateProtector.Protect($"{user:N}.{attempt:N}.{state}.{verifier}", StateLifetime),
            new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax,
                Path = OhmywishesOptions.CallbackPath, MaxAge = StateLifetime });
        return QueryHelpers.AddQueryString(OhmywishesOptions.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = options.ClientId, ["redirect_uri"] = options.RedirectUri,
            ["scope"] = OhmywishesOptions.Scope, ["state"] = state, ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256", ["resource"] = OhmywishesOptions.Resource
        });
    }

    public async Task<(Guid Attempt, OhmywishesTokens Tokens)> Complete(HttpContext context, Guid user, CancellationToken ct)
    {
        context.Response.Cookies.Delete(Cookie, new CookieOptions { Path = OhmywishesOptions.CallbackPath, Secure = true });
        var query = context.Request.Query;
        if (!options.Enabled || !context.Request.Cookies.TryGetValue(Cookie, out var cookie)
            || query["code"].Count != 1 || string.IsNullOrEmpty(query["code"][0]) || query["state"].Count != 1
            || query.ContainsKey("error") || (query.TryGetValue("iss", out var issuer)
                && (issuer.Count != 1 || issuer[0] != OhmywishesOptions.Issuer)))
            throw new InvalidDataException("Invalid Ohmywishes authorization callback.");
        var state = stateProtector.Unprotect(cookie).Split('.');
        if (state.Length != 4 || state[0] != user.ToString("N") || !Guid.TryParseExact(state[1], "N", out var attempt) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state[2]), Encoding.UTF8.GetBytes(query["state"][0]!)))
            throw new InvalidDataException("Invalid Ohmywishes authorization state.");
        return (attempt, await Exchange(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = query["code"][0]!,
            ["redirect_uri"] = options.RedirectUri!, ["code_verifier"] = state[3]
        }, ct));
    }

    public Task<OhmywishesTokens> Refresh(string refreshToken, CancellationToken ct) => Exchange(new()
    {
        ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken
    }, ct);

    private async Task<OhmywishesTokens> Exchange(Dictionary<string, string> form, CancellationToken ct)
    {
        form["resource"] = OhmywishesOptions.Resource;
        using var request = new HttpRequestMessage(HttpMethod.Post, OhmywishesOptions.TokenEndpoint);
        if (options.TokenAuthMethod == "client_secret_basic")
        {
            var credentials = $"{WebUtility.UrlEncode(options.ClientId)}:{WebUtility.UrlEncode(options.ClientSecret)}";
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }
        else
        {
            form["client_id"] = options.ClientId!;
            if (options.TokenAuthMethod == "client_secret_post") form["client_secret"] = options.ClientSecret!;
        }
        request.Content = new FormUrlEncodedContent(form);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new OhmywishesAccessException();
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        if (OhmywishesMcp.String(json, "access_token") is not { Length: > 0 } access
            || !string.Equals(OhmywishesMcp.String(json, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid Ohmywishes token response.");
        var expiresIn = json.TryGetProperty("expires_in", out var expiry) && expiry.ValueKind == System.Text.Json.JsonValueKind.Number
            && expiry.TryGetInt32(out var seconds)
            ? Math.Clamp(seconds, 1, 86400) : 900;
        return new(access, OhmywishesMcp.String(json, "refresh_token"), DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }
}

public sealed record OhmywishesTokens(string Access, string? Refresh, DateTimeOffset Expires);
public sealed class OhmywishesAccessException : Exception;
