using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Wishlist.Server.Services;

namespace Wishlist.Tests;

public class RoutingTests
{
    private const string SigningKey = "routing-test-signing-secret-at-least-thirty-two-bytes";
    private static readonly Guid ActorId = Guid.Parse("2a61c028-bf63-45e6-9d09-7dcdfba24f71");

    [Fact]
    public async Task KnownPutWithUnsupportedContentTypeRemains415()
    {
        using var app = Factory();
        using var client = AuthenticatedClient(app);

        using var response = await client.PutAsync("/api/profile", new StringContent("synthetic", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task KnownPostWithUnsupportedContentTypeRemains415()
    {
        using var app = Factory();
        using var client = AuthenticatedClient(app);

        using var response = await client.PostAsync($"/api/gifts/{Guid.Parse("b65b2cb2-0d06-4da6-8e1f-23b2da1dfdb5")}/actions",
            new StringContent("synthetic", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task UnsupportedMethodOnKnownApiPathRemains405()
    {
        using var app = Factory();
        using var client = AuthenticatedClient(app);

        using var response = await client.PostAsync("/api/profile", JsonContent.Create(new { synthetic = true }));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task UnknownApiRouteStillReturnsAuthenticatedJsonNotFound()
    {
        using var app = Factory();
        using var anonymous = app.CreateClient();

        using var anonymousResponse = await anonymous.GetAsync("/api/no-such-route");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var client = AuthenticatedClient(app);
        using var response = await client.PostAsJsonAsync("/api/no-such-route", new { synthetic = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var missingAsset = await client.GetAsync("/assets/no-such-file.js");
        Assert.Equal(HttpStatusCode.NotFound, missingAsset.StatusCode);
    }

    [Fact]
    public async Task AuthCallbacksAndSpaRoutesStillReachTheirHandlers()
    {
        using var app = Factory(oidcEnabled: true);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var login = await client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("oauth.telegram.org", login.Headers.Location?.ToString());

        using var miniWithWrongContentType = await client.PostAsync("/auth/mini",
            new StringContent("synthetic", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, miniWithWrongContentType.StatusCode);

        using var wrongMethod = await client.PostAsync("/auth/login", JsonContent.Create(new { synthetic = true }));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);

        using var callback = await client.GetAsync("/auth/callback?error=access_denied&state=invalid");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/?login=failed", callback.Headers.Location?.OriginalString);

        using var unknownAuth = await client.GetAsync("/auth/no-such-route");
        Assert.Equal(HttpStatusCode.NotFound, unknownAuth.StatusCode);
        Assert.Equal("application/problem+json", unknownAuth.Content.Headers.ContentType?.MediaType);

        using var ui = await client.GetAsync("/wishlist/friends");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal("text/html", ui.Content.Headers.ContentType?.MediaType);

        using var favicon = await client.GetAsync("/favicon.png");
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        Assert.Equal("image/png", favicon.Content.Headers.ContentType?.MediaType);
    }

    private static WebApplicationFactory<Program> Factory(bool oidcEnabled = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseWebRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "Wishlist.Client", "wwwroot")));
            builder.UseSetting("Auth:SigningKey", SigningKey);
            builder.UseSetting("ConnectionStrings:Database", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
            builder.UseSetting("Telegram:BotToken", "123:test-only-token");
            builder.UseSetting("Telegram:ClientId", oidcEnabled ? "routing-test-client" : null);
            builder.UseSetting("Telegram:ClientSecret", oidcEnabled ? "routing-test-secret" : null);
            builder.UseSetting("Demo:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                foreach (var hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                    descriptor.ImplementationType is not null &&
                    (descriptor.ImplementationType.Name is "OhmywishesWorker" or "WishlistRequestWorker")).ToArray())
                {
                    services.Remove(hosted);
                }
            });
        });

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken());
        return client;
    }

    private static string CreateToken()
    {
        var token = new JwtSecurityToken(
            issuer: "wishlist",
            audience: "wishlist-mini",
            claims: [new Claim(ClaimTypes.NameIdentifier, ActorId.ToString())],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
