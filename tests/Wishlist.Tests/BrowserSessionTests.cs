using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using Wishlist.Server.Data;
using Wishlist.Server.Services;

namespace Wishlist.Tests;

public class BrowserSessionTests
{
    [Fact]
    public async Task BrowserSignInIssuesCookieWithFourteenDayExpiry()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Auth:SigningKey"] = "test-only-signing-key-at-least-thirty-two-bytes";
        AuthSetup.AddSessions(builder);

        await using var app = builder.Build();
        app.MapPost("/login", (Microsoft.AspNetCore.Http.HttpContext context) =>
            context.SignInAsync("Cookies", AuthSetup.Principal(new AppUser { Name = "Test" })));
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.PostAsync("/login", null);
        response.EnsureSuccessStatusCode();

        var cookie = SetCookieHeaderValue.Parse(Assert.Single(response.Headers.GetValues("Set-Cookie")));
        Assert.Equal("wishlist.session", cookie.Name.Value);
        Assert.NotNull(cookie.Expires);
        var remaining = cookie.Expires.Value - DateTimeOffset.UtcNow;
        Assert.InRange(remaining, TimeSpan.FromDays(14) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(14));
    }
}
