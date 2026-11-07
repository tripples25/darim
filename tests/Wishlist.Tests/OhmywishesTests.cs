using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;
using Wishlist.Server.Services;

namespace Wishlist.Tests;

public class OhmywishesTests
{
    [Fact]
    public void MapsOnlyShareableOpenWishesAndDoesNotTreatForeignCurrencyAsRubles()
    {
        using var visible = JsonDocument.Parse("""{"id":"wish","title":"Книга","visibility":"public","price":{"amount":25,"currency":"USD"},"isFulfilled":false}""");
        using var hidden = JsonDocument.Parse("""{"id":"wish","title":"Книга","visibility":"private","isFulfilled":false}""");
        Assert.Null(OhmywishesMcp.ReadWish(visible.RootElement)!.Price);
        Assert.Null(OhmywishesMcp.ReadWish(hidden.RootElement));
    }

    [Fact]
    public void MissingVisibilityFailsTheSnapshot()
    {
        using var wish = JsonDocument.Parse("""{"id":"wish","title":"Книга"}""");
        Assert.Throws<InvalidDataException>(() => OhmywishesMcp.ReadWish(wish.RootElement));
    }

    [Fact]
    public async Task SnapshotFollowsCursorSkipsHiddenWishesAndFailsOnAnIncompletePage()
    {
        using var provider = new FakeOhmywishes();
        using var http = new HttpClient(provider);
        var mcp = new OhmywishesMcp(http);

        var wishes = await mcp.Snapshot("access", "list", CancellationToken.None);
        Assert.Equal(new[] { 0, 1 }, provider.Offsets);
        Assert.Equal("Книга", Assert.Single(wishes).Value.Title);
        Assert.Equal(550m, wishes["wish"].Price);
        Assert.Equal(1, provider.DetailCalls);

        provider.FailSecondPage = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => mcp.Snapshot("access", "list", CancellationToken.None));
        Assert.Equal(1, provider.DetailCalls);
    }

    [Fact]
    public void DatabaseSnapshotMatchesModel()
    {
        using var db = new AppDbFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrator = db.GetService<IMigrator>();
        var up = migrator.GenerateScript();
        Assert.Contains("CREATE TABLE \"OhmywishesConnections\"", up);
        var down = migrator.GenerateScript("20261107120000_OhmywishesSync", "20261106190000_InviteRemaining");
        Assert.Contains("DELETE FROM \"Gifts\" WHERE \"OhmywishesHidden\"", down);
    }

    [Fact]
    public async Task OAuthCallbackRequiresOriginalUserAndExchangesCodeWithPkce()
    {
        using var handler = new FakeTokenEndpoint();
        using var http = new HttpClient(handler);
        var oauth = new OhmywishesOAuth(Options(), new EphemeralDataProtectionProvider(), http);
        var user = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var start = new DefaultHttpContext();
        var authorization = new Uri(oauth.Start(start, user, attempt));
        var query = QueryHelpers.ParseQuery(authorization.Query);
        Assert.Equal("wishlist:read", query["scope"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        var callback = new DefaultHttpContext();
        callback.Request.Headers.Cookie = start.Response.Headers.SetCookie.ToString().Split(';')[0];
        callback.Request.QueryString = QueryString.Create(new Dictionary<string, string?>
        {
            ["code"] = "code", ["state"] = query["state"].ToString(), ["iss"] = OhmywishesOptions.Issuer
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => oauth.Complete(callback, Guid.NewGuid(), CancellationToken.None));
        Assert.Null(handler.Form);
        var authorized = await oauth.Complete(callback, user, CancellationToken.None);
        Assert.Equal(attempt, authorized.Attempt);
        Assert.Equal("refresh", authorized.Tokens.Refresh);
        var verifier = handler.Form!["code_verifier"].ToString();
        Assert.Equal(query["code_challenge"].ToString(),
            WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    internal static OhmywishesOptions Options() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Ohmywishes:ClientId"] = "darim",
            ["Ohmywishes:TokenAuthMethod"] = "none",
            ["Ohmywishes:RedirectUri"] = "https://darim.example/auth/ohmywishes/callback"
        }).Build());

    private sealed class FakeTokenEndpoint : HttpMessageHandler
    {
        public Dictionary<string, Microsoft.Extensions.Primitives.StringValues>? Form { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(OhmywishesOptions.TokenEndpoint, request.RequestUri?.AbsoluteUri);
            Form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { access_token = "access", refresh_token = "refresh", token_type = "Bearer", expires_in = 900 })
            };
        }
    }
}

[Collection("Postgres")]
public class OhmywishesPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ImportUpdatesReservedGiftAtomicallyAndHidesItWhenPrivateOrDisconnected()
    {
        using var provider = new FakeOhmywishes();
        using var http = new HttpClient(provider);
        await using var db = new AppDb(fixture.Options);
        var company = new Company { Name = "Ohmywishes test", InviteHash = Guid.NewGuid().ToString() };
        var otherCompany = new Company { Name = "Other company", InviteHash = Guid.NewGuid().ToString() };
        var owner = User(company);
        var donor = User(company);
        var otherDonor = User(company);
        var outsider = User(otherCompany);
        db.AddRange(company, otherCompany, owner, donor, otherDonor, outsider);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var protection = new EphemeralDataProtectionProvider();
        var options = OhmywishesTests.Options();
        var sync = new OhmywishesSync(db, options, new(options, protection, http), new(http), protection,
            NullLogger<OhmywishesSync>.Instance, new(http));
        var tokens = new OhmywishesTokens("access", "refresh", DateTimeOffset.UtcNow.AddHours(1));
        var attempt = await sync.BeginAuthorization(owner.Id, default);
        await sync.Connect(owner.Id, attempt, tokens, default);
        await sync.Select(owner.Id, "list", default);
        await sync.RunDue(default);
        var service = new GiftService(db);
        var gift = Assert.Single(await service.List(owner.Id));
        Assert.True(gift.FromOhmywishes);
        Assert.Null(gift.Participation);
        Assert.Empty(await service.List(outsider.Id));
        Assert.DoesNotContain("refresh", (await db.OhmywishesConnections.AsNoTracking().SingleAsync(x => x.UserId == owner.Id)).ProtectedTokens!);

        var reservations = await Task.WhenAll(TryReserve(donor.Id), TryReserve(otherDonor.Id));
        var winner = Assert.Single(reservations.OfType<Guid>());
        await service.Change(winner, gift.Id, action: new GiftActionInput("cancel"));
        db.ChangeTracker.Clear();
        await service.Change(donor.Id, gift.Id, action: new GiftActionInput("split", 2));
        await service.Change(otherDonor.Id, gift.Id, action: new GiftActionInput("join"));
        await service.Change(donor.Id, gift.Id, action: new GiftActionInput("buy"));
        db.ChangeTracker.Clear();

        provider.Title = "Новая книга";
        provider.FailSecondPage = true;
        await RunNow();
        Assert.Equal("Книга", Assert.Single(await service.List(owner.Id)).Details.Title);
        Assert.True((await sync.Status(owner.Id, default)).Stale);
        provider.FailSecondPage = false;
        await RunNow();
        var updated = Assert.Single(await service.List(donor.Id));
        Assert.Equal(gift.Id, updated.Id);
        Assert.Equal("Новая книга", updated.Details.Title);
        Assert.Equal("Книга", updated.Original?.Title);
        Assert.True(updated.Participation?.Bought);
        Assert.Equal(2, updated.Participation?.Taken);

        provider.WishPrivate = true;
        await RunNow();
        Assert.Empty(await service.List(donor.Id));
        Assert.Empty(await service.List(owner.Id));
        Assert.Equal(404, (await Assert.ThrowsAsync<RuleException>(
            () => service.Change(donor.Id, gift.Id, action: new GiftActionInput("cancel")))).Status);
        Assert.True(await db.Notices.AnyAsync(x => x.UserId == donor.Id && x.Text.Contains("больше недоступно")));
        provider.WishPrivate = false;
        await RunNow();
        Assert.Equal(gift.Id, Assert.Single(await service.List(donor.Id)).Id);

        var pending = await sync.BeginAuthorization(owner.Id, default);
        await sync.Disconnect(owner.Id, default);
        Assert.Empty(await service.List(donor.Id));
        Assert.False(await db.OhmywishesConnections.AnyAsync(x => x.UserId == owner.Id));
        await Assert.ThrowsAsync<RuleException>(() => sync.Connect(owner.Id, pending, tokens, default));
        Assert.True((await db.Gifts.AsNoTracking().SingleAsync(x => x.Id == gift.Id)).Bought);

        async Task RunNow()
        {
            await db.OhmywishesConnections.Where(x => x.UserId == owner.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.NextSync, DateTimeOffset.UtcNow));
            await sync.RunDue(default);
        }

        async Task<Guid?> TryReserve(Guid donorId)
        {
            await using var separate = new AppDb(fixture.Options);
            try
            {
                await new GiftService(separate).Change(donorId, gift.Id, action: new GiftActionInput("reserve"));
                return donorId;
            }
            catch (RuleException exception) when (exception.Status == 409)
            {
                return null;
            }
        }
    }

    private static AppUser User(Company company) => new()
    {
        CompanyId = company.Id, Name = "Участник", Contact = "@donor", TelegramId = Random.Shared.NextInt64(1, long.MaxValue)
    };
}

internal sealed class FakeOhmywishes : HttpMessageHandler
{
    public bool FailSecondPage { get; set; }
    public bool WishPrivate { get; set; }
    public string Title { get; set; } = "Книга";
    public int DetailCalls { get; private set; }
    public List<int> Offsets { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("access", request.Headers.Authorization?.Parameter);
        var rpc = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
        var method = rpc.GetProperty("method").GetString();
        if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
        var id = rpc.GetProperty("id").GetInt32();
        if (method == "initialize") return Response(new { protocolVersion = "2025-11-25" });
        Assert.Equal("2025-11-25", Assert.Single(request.Headers.GetValues("MCP-Protocol-Version")));
        Assert.Equal("tools/call", method);
        var parameters = rpc.GetProperty("params");
        var arguments = parameters.GetProperty("arguments");
        switch (parameters.GetProperty("name").GetString())
        {
            case "get_my_profile":
                return Tool(new { id = "account" });
            case "list_my_wishlists":
                return Tool(new { items = new[] { new { id = "list", title = "Подарки", visibility = "public" } } });
            case "list_my_wishes":
                Assert.Equal("list", arguments.GetProperty("wishListId").GetString());
                Assert.Equal(30, arguments.GetProperty("limit").GetInt32());
                var offset = arguments.GetProperty("offset").GetInt32();
                Offsets.Add(offset);
                if (offset == 0)
                    return Tool(new { items = new[] { new { id = "wish", visibility = WishPrivate ? "private" : "public" } }, nextOffset = 1 });
                Assert.Equal(1, offset);
                if (FailSecondPage) return new(HttpStatusCode.ServiceUnavailable);
                return Tool(new { items = new[] { new { id = "hidden", visibility = "private" } }, nextOffset = (int?)null });
            case "get_my_wish":
                DetailCalls++;
                Assert.Equal("wish", arguments.GetProperty("wishId").GetString());
                return Tool(new { id = "wish", title = Title, visibility = "public", price = new { amount = 550, currency = "RUB" } });
            default:
                throw new InvalidOperationException("Unexpected tool.");
        }

        HttpResponseMessage Tool(object content) => Response(new { structuredContent = content });
        HttpResponseMessage Response(object result) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, result })
        };
    }
}
