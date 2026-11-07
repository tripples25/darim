using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;
using Wishlist.Server.Services;

namespace Wishlist.Tests;

public class OhmywishesWebTests
{
    private const string Account = "111111111111111111111111";
    internal const string List = "222222222222222222222222";
    private const string Wish = "333333333333333333333333";
    private const string Shared = "9f8b1234-d875-471b-85c3-51068cdfce67";

    [Theory]
    [InlineData("https://ohmywishes.com.evil.test/users/dream/lists/222222222222222222222222")]
    [InlineData("https://ohmywishes.com/users/dream")]
    [InlineData("https://ohmywishes.com/users/dream/lists/%2Fapi")]
    [InlineData("http://ohmywishes.com/users/dream/lists/222222222222222222222222")]
    public void RejectsLinksOutsidePublicListRoute(string url) =>
        Assert.Null(OhmywishesWeb.ParseLink(url));

    [Fact]
    public async Task PublicListCanBeReadWithoutCredentialsAndPrivateWishesAreExcluded()
    {
        using var handler = new WebHandler();
        using var http = new HttpClient(handler);
        var web = new OhmywishesWeb(http);
        var link = OhmywishesWeb.ParseLink($"https://ohmywishes.com/users/dream/lists/{List}")!;
        var source = await web.Resolve(link, default);
        Assert.Equal(Account, source!.AccountId);
        var snapshot = await web.Snapshot(Account, List, default);
        Assert.Equal("Книга", Assert.Single(snapshot).Value.Title);
        handler.ListRequiresSignIn = true;
        Assert.Empty(await web.Snapshot(Account, List, default));
        handler.ListRequiresSignIn = false;
        handler.Visibility = "private";
        Assert.Empty(await web.Snapshot(Account, List, default));
        handler.ProfilePrivate = true;
        Assert.Null(await web.Resolve(link, default));
    }

    [Fact]
    public async Task SharedLinkUsesOnlyListScopedReadsAndStopsImportWhenRevoked()
    {
        using var handler = new WebHandler { ProfilePrivate = true };
        using var http = new HttpClient(handler);
        var web = new OhmywishesWeb(http);
        var link = OhmywishesWeb.ParseLink($"https://ohmywishes.com/users/dream/lists/{Shared}")!;
        var source = (await web.Resolve(link, default))!;
        Assert.Equal(List, source.ListId);
        Assert.Equal(Shared, source.ShareToken);
        var snapshot = await web.Snapshot(source.AccountId, source.ListId, default, source.ShareToken);
        Assert.Equal("По ссылке", Assert.Single(snapshot).Value.Title);
        Assert.Equal(0, handler.GlobalWishRequests);
        handler.ListRequiresSignIn = true;
        Assert.Empty(await web.Snapshot(source.AccountId, source.ListId, default, source.ShareToken));
    }

    [Fact]
    public void PilotRequiresBothFlagAndMatchingTelegramId()
    {
        OhmywishesOptions Options(string? enabled, string? user) => new(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Ohmywishes:WebApiEnabled"] = enabled, ["Ohmywishes:WebApiTelegramId"] = user
            }).Build());
        Assert.False(Options("false", "42").AllowsWebApi(42));
        Assert.False(Options("true", null).AllowsWebApi(42));
        Assert.False(Options("true", "42").AllowsWebApi(43));
        Assert.True(Options("true", "42").AllowsWebApi(42));
    }

    internal sealed class WebHandler : HttpMessageHandler
    {
        public string Visibility { get; set; } = "public";
        public bool ProfilePrivate { get; set; }
        public bool ListRequiresSignIn { get; set; }
        public int Requests { get; private set; }
        public int GlobalWishRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("ohmywishes.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            var root = $"/api/v3/users/{Account}/wish-lists/{List}";
            var sharedRoot = $"/api/v3/users/{Account}/wish-lists/{Shared}";
            if (ListRequiresSignIn && (request.RequestUri.AbsolutePath == root || request.RequestUri.AbsolutePath == sharedRoot))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (request.RequestUri.AbsolutePath == $"/api/v3/wishes/{Wish}") GlobalWishRequests++;
            object result = request.RequestUri.AbsolutePath switch
            {
                "/api/v3/users/dream" or $"/api/v3/users/{Account}" => new { item = new { id = Account, isPrivate = ProfilePrivate } },
                var path when path == root => new { item = new { id = List, title = "Подарки" } },
                var path when path == root + "/wishes-v2" => new { items = new[] { new { id = Wish } } },
                var path when path == sharedRoot => new { item = new { id = List, title = "Подарки", visibility = "by_link" } },
                var path when path == sharedRoot + "/wishes-v2" => new { items = new[] { new { id = Wish } } },
                var path when path == sharedRoot + $"/wishes-v2/{Wish}" => new { item = new { id = Wish, title = "По ссылке", creator = new { id = Account } } },
                $"/api/v3/wishes/{Wish}" => new { item = new { id = Wish, title = "Книга", visibility = Visibility,
                    isPrivate = Visibility == "private", isFulfilled = false, creator = new { id = Account },
                    wishLists = new[] { new { id = List } } } },
                _ => throw new InvalidOperationException("Unexpected web API route.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
    }
}

[Collection("Postgres")]
public class OhmywishesWebPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task OnlyAllowlistedOwnerConnectsAndDisablingPilotHidesImportedGift()
    {
        await using var db = new AppDb(fixture.Options);
        var company = new Company { Name = "Pilot", InviteHash = Guid.NewGuid().ToString() };
        var owner = new AppUser { CompanyId = company.Id, TelegramId = Random.Shared.NextInt64(1, long.MaxValue) };
        var donor = new AppUser { CompanyId = company.Id, TelegramId = Random.Shared.NextInt64(1, long.MaxValue) };
        db.AddRange(company, owner, donor);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ohmywishes:WebApiEnabled"] = "true",
            ["Ohmywishes:WebApiTelegramId"] = owner.TelegramId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        var options = new OhmywishesOptions(config);
        using var handler = new OhmywishesWebTests.WebHandler();
        using var http = new HttpClient(handler);
        var protection = new EphemeralDataProtectionProvider();
        var sync = new OhmywishesSync(db, options, new(options, protection, http), new(http), protection,
            NullLogger<OhmywishesSync>.Instance, new(http));
        var link = $"https://ohmywishes.com/users/dream/lists/{OhmywishesWebTests.List}";

        Assert.Equal(404, (await Assert.ThrowsAsync<RuleException>(() => sync.ConnectWeb(donor.Id, link, default))).Status);
        Assert.Equal(0, handler.Requests);
        await sync.ConnectWeb(owner.Id, link, default);
        await sync.RunDue(default);
        var gifts = new GiftService(db, options);
        var imported = Assert.Single(await gifts.List(donor.Id));
        Assert.True(imported.FromOhmywishes);
        await gifts.Change(donor.Id, imported.Id, action: new GiftActionInput("reserve"));

        config["Ohmywishes:WebApiEnabled"] = "false";
        await db.OhmywishesConnections.Where(x => x.UserId == owner.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.NextSync, DateTimeOffset.UtcNow));
        var requests = handler.Requests;
        await sync.RunDue(default);
        Assert.Equal(requests, handler.Requests);
        Assert.Empty(await gifts.List(donor.Id));
        Assert.Equal(404, (await Assert.ThrowsAsync<RuleException>(() =>
            gifts.Change(donor.Id, imported.Id, action: new GiftActionInput("cancel")))).Status);
    }
}
