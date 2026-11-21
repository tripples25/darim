using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Wishlist.Server.Services;

namespace Wishlist.Tests;

public class OhmywishesFollowingTests
{
    private const string Account = "111111111111111111111111";
    private const string Wish = "333333333333333333333333";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowsOwnerBeforeReadingReservationsAndDoesNotRepeatSubscription(bool shared)
    {
        using var handler = new FollowingHandler();
        using var http = new HttpClient(handler);
        var web = Web(http);
        for (var i = 0; i < 2; i++)
        {
            var snapshot = await web.Snapshot(Account, OhmywishesWebTests.List, default,
                shared ? OhmywishesWebTests.Shared : null);
            Assert.True(Assert.Single(snapshot).Value.Occupied);
        }
        Assert.Equal(1, handler.Follows);
        Assert.Equal(2, handler.Profiles);
        Assert.Equal(2, handler.ReservationReads);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExistingSubscriptionOrPendingRequestDoesNotSendAnother(bool followed, bool pending)
    {
        using var handler = new FollowingHandler { Followed = followed, Pending = pending };
        using var http = new HttpClient(handler);
        Assert.Single(await Web(http).Snapshot(Account, OhmywishesWebTests.List, default));
        Assert.Equal(1, handler.Profiles);
        Assert.Equal(0, handler.Follows);
    }

    [Fact]
    public async Task ApprovalRequiredRequestIsNotRepeatedOnNextSnapshot()
    {
        using var handler = new FollowingHandler { ApprovalRequired = true };
        using var http = new HttpClient(handler);
        var web = Web(http);
        await web.Snapshot(Account, OhmywishesWebTests.List, default);
        await web.Snapshot(Account, OhmywishesWebTests.List, default);
        Assert.True(handler.Pending);
        Assert.Equal(1, handler.Follows);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task FailedFollowPreservesImportAndIsRetriedOnNextSnapshot(int status)
    {
        using var handler = new FollowingHandler { FollowStatus = (HttpStatusCode)status };
        using var http = new HttpClient(handler);
        var web = Web(http);
        Assert.Single(await web.Snapshot(Account, OhmywishesWebTests.List, default));
        Assert.Equal(1, handler.Follows);
        handler.FollowStatus = HttpStatusCode.NoContent;
        Assert.True(Assert.Single(await web.Snapshot(Account, OhmywishesWebTests.List, default)).Value.Occupied);
        Assert.Equal(2, handler.Follows);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("<html>unavailable</html>")]
    [InlineData("{\"item\":{\"id\":\"222222222222222222222222\",\"isFollowedByMe\":false,\"isFollowingRequestSent\":false}}")]
    [InlineData("{\"item\":{\"id\":\"111111111111111111111111\",\"isFollowedByMe\":\"false\",\"isFollowingRequestSent\":false}}")]
    public async Task InvalidProfileNeverTriggersSubscriptionAndPreservesImport(string profile)
    {
        using var handler = new FollowingHandler { ProfileJson = profile };
        using var http = new HttpClient(handler);
        Assert.Single(await Web(http).Snapshot(Account, OhmywishesWebTests.List, default));
        Assert.Equal(1, handler.Profiles);
        Assert.Equal(0, handler.Follows);
    }

    [Fact]
    public async Task DisabledCredentialsDoNotRequestProfileOrSubscription()
    {
        using var handler = new FollowingHandler();
        using var http = new HttpClient(handler);
        Assert.Single(await Web(http, token: null).Snapshot(Account, OhmywishesWebTests.List, default));
        Assert.Equal(0, handler.Profiles);
        Assert.Equal(0, handler.Follows);
    }

    [Fact]
    public async Task RevokedListDoesNotTriggerSubscription()
    {
        using var handler = new FollowingHandler();
        handler.Anonymous.ListRequiresSignIn = true;
        using var http = new HttpClient(handler);
        Assert.Empty(await Web(http).Snapshot(Account, OhmywishesWebTests.List, default));
        Assert.Equal(0, handler.Profiles);
        Assert.Equal(0, handler.Follows);
    }

    [Fact]
    public async Task CancellationDuringSubscriptionPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new FollowingHandler { CancelFollow = cancellation };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Web(http).Snapshot(Account, OhmywishesWebTests.List, cancellation.Token));
        Assert.Equal(0, handler.ReservationReads);
    }

    private static OhmywishesWeb Web(HttpClient http, string? token = "test-token")
    {
        var options = new OhmywishesOptions(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Ohmywishes:ReservationToken"] = token }).Build());
        return new(http, reservations: new(http, options));
    }

    private sealed class FollowingHandler : HttpMessageHandler
    {
        public OhmywishesWebTests.WebHandler Anonymous { get; } = new();
        private readonly HttpMessageInvoker anonymous;
        public FollowingHandler() => anonymous = new(Anonymous);
        public bool Followed { get; set; }
        public bool Pending { get; set; }
        public bool ApprovalRequired { get; set; }
        public HttpStatusCode FollowStatus { get; set; } = HttpStatusCode.NoContent;
        public string? ProfileJson { get; set; }
        public CancellationTokenSource? CancelFollow { get; set; }
        public int Profiles { get; private set; }
        public int Follows { get; private set; }
        public int ReservationReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.Headers.Contains("x-access-token")) return anonymous.SendAsync(request, ct);
            Assert.Equal("test-token", Assert.Single(request.Headers.GetValues("x-access-token")));
            Assert.Equal("russia", Assert.Single(request.Headers.GetValues("x-content-region")));
            Assert.Equal("ohmywishes.com", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath;
            if (path == $"/api/v3/users/{Account}")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Profiles++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = ProfileJson != null ? new StringContent(ProfileJson) : JsonContent.Create(new
                    {
                        item = new { id = Account, isFollowedByMe = Followed, isFollowingRequestSent = Pending }
                    })
                });
            }
            if (path == $"/api/v3/users/{Account}/following")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.True(ReservationReads < Profiles);
                Follows++;
                if (CancelFollow != null)
                {
                    CancelFollow.Cancel();
                    ct.ThrowIfCancellationRequested();
                }
                if ((int)FollowStatus is >= 200 and < 300)
                {
                    Followed = !ApprovalRequired;
                    Pending = ApprovalRequired;
                }
                return Task.FromResult(new HttpResponseMessage(FollowStatus));
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith(Wish, path);
            ReservationReads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    item = new { id = Wish, isReservedBySomeone = Followed, isReservedByMe = false }
                })
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anonymous.Dispose();
            base.Dispose(disposing);
        }
    }
}
