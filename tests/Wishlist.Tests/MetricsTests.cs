using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Wishlist.Tests;

public class MetricsTests
{
    private static readonly string Token = new('a', 64);

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public async Task ScrapeIsDisabledWithoutAValidConfiguredToken(string token)
    {
        using var app = Factory(token);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth-info")).StatusCode);
    }

    [Fact]
    public async Task ScrapeRequiresItsOwnTokenAndUsesBoundedLabels()
    {
        using var app = Factory(Token);
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metrics")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('b', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/metrics")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;

        var giftId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/gifts/{giftId}/actions?key=private-query", null)).StatusCode);
        using var unmatched = new HttpRequestMessage(new HttpMethod("CUSTOM"), "/api/private-path");
        await client.SendAsync(unmatched);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var metrics = await response.Content.ReadAsStringAsync();
        Assert.Contains("darim_http_requests_total{method=\"POST\",route=\"/api/gifts/{id:guid}/actions\",status_class=\"4xx\"} 1", metrics);
        Assert.Contains("darim_http_request_duration_seconds_count{method=\"POST\",route=\"/api/gifts/{id:guid}/actions\"} 1", metrics);
        Assert.Contains("method=\"OTHER\",route=\"<unmatched>\"", metrics);
        Assert.Contains("darim_ohmywishes_sync_total", metrics);
        Assert.DoesNotContain(giftId.ToString(), metrics);
        Assert.DoesNotContain("private-query", metrics);
        Assert.DoesNotContain("private-path", metrics);
        Assert.DoesNotContain("/metrics", metrics);
        Assert.DoesNotContain(Token, metrics);
    }

    private static WebApplicationFactory<Program> Factory(string token) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Auth:SigningKey", "test-only-signing-secret-at-least-thirty-two-bytes");
            builder.UseSetting("ConnectionStrings:Database", "Host=127.0.0.1;Database=unused");
            builder.UseSetting("Monitoring:Token", token);
        });
}
