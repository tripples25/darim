using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Wishlist.Core;
using Wishlist.Server.Data;
using Wishlist.Server.Services;

namespace Wishlist.Tests;

public class OhmywishesReservationTests
{
    internal const string Id = "333333333333333333333333";

    [Fact]
    public async Task CreatesAndCancelsOnlyItsOwnReservation()
    {
        using var handler = new ReservationHandler();
        using var http = new HttpClient(handler);
        var options = new OhmywishesOptions(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Ohmywishes:ReservationToken"] = "test-token" }).Build());
        var client = new OhmywishesReservations(http, options);

        handler.OccupiedByOther = true;
        await Assert.ThrowsAsync<RuleException>(() => client.Reserve(Id, default));
        await client.Cancel(Id, default);
        Assert.Empty(handler.Writes);

        handler.OccupiedByOther = false;
        await client.Reserve(Id, default);
        await client.Cancel(Id, default);
        await client.Cancel(Id, default);
        Assert.Equal([HttpMethod.Post, HttpMethod.Delete], handler.Writes);
    }

    internal sealed class ReservationHandler : HttpMessageHandler
    {
        public List<HttpMethod> Writes { get; } = [];
        public bool OccupiedByOther { get; set; }
        private bool mine;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("test-token", Assert.Single(request.Headers.GetValues("x-access-token")));
            Assert.Equal("ohmywishes.com", request.RequestUri!.Host);
            Assert.Equal($"/api/v3/wishes/{Id}" + (request.Method == HttpMethod.Get ? "" : "/reservation"),
                request.RequestUri.AbsolutePath);
            if (request.Method != HttpMethod.Get)
            {
                Writes.Add(request.Method);
                mine = request.Method == HttpMethod.Post;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    item = new { id = Id, isReservedBySomeone = mine || OccupiedByOther, isReservedByMe = mine }
                })
            });
        }
    }
}

[Collection("Postgres")]
public class OhmywishesReservationPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ExternalConflictRollsBackLocalReservationAndStatusStaysHiddenFromOwner()
    {
        await using var db = new AppDb(fixture.Options);
        var owner = new AppUser { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) };
        var donor = new AppUser { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) };
        var company = new Company { Name = "Пилот", InviteHash = Guid.NewGuid().ToString() };
        var gift = Gift.New(company.Id, owner.Id, new("Книга"));
        gift.OhmywishesKey = Guid.NewGuid().ToString();
        gift.OhmywishesWebId = OhmywishesReservationTests.Id;
        db.AddRange(owner, donor, company, gift,
            new CompanyMembership { UserId = owner.Id, CompanyId = company.Id },
            new CompanyMembership { UserId = donor.Id, CompanyId = company.Id },
            new OhmywishesConnection { UserId = owner.Id, WebApi = true });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using var handler = new OhmywishesReservationTests.ReservationHandler { OccupiedByOther = true };
        using var http = new HttpClient(handler);
        var options = new OhmywishesOptions(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Ohmywishes:ReservationToken"] = "test-token",
                ["Ohmywishes:WebApiEnabled"] = "true"
            }).Build());
        var service = new GiftService(db, options, reservations: new(http, options),
            protection: new EphemeralDataProtectionProvider());

        var error = await Assert.ThrowsAsync<RuleException>(() => service.Change(donor.Id, gift.Id, action: new("reserve")));
        Assert.Equal(409, error.Status);
        Assert.Empty(await service.Giving(donor.Id));
        db.ChangeTracker.Clear();

        handler.OccupiedByOther = false;
        await service.Change(donor.Id, gift.Id, action: new("reserve"));
        Assert.True(Assert.Single(await service.Giving(donor.Id)).Participation!.IsMine);
        Assert.Null(Assert.Single(await service.Mine(owner.Id)).Participation);

        db.ChangeTracker.Clear();
        await service.Change(donor.Id, gift.Id, action: new("cancel"));
        Assert.Empty(await service.Giving(donor.Id));
        Assert.Equal([HttpMethod.Post, HttpMethod.Delete], handler.Writes);
    }
}
