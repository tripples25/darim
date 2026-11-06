using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wishlist.Server.Services;

namespace Wishlist.Tests;

public class OidcBackchannelTests {
 [Fact]
 public async Task BrowserLoginDecodesGzippedTelegramSigningKeys() {
  var endpointBuilder=WebApplication.CreateBuilder();
  endpointBuilder.WebHost.UseKestrel(o=>o.Listen(IPAddress.Loopback,0));
  await using var endpoint=endpointBuilder.Build();
  endpoint.MapGet("/jwks.json",async context=>{
   context.Response.ContentType="application/json";
   context.Response.Headers.ContentEncoding="gzip";
   await using var gzip=new GZipStream(context.Response.Body,CompressionMode.Compress,leaveOpen:true);
   await gzip.WriteAsync(Encoding.UTF8.GetBytes("{\"keys\":[]}"));
  });
  await endpoint.StartAsync();
  var appBuilder=WebApplication.CreateBuilder();
  appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> {
   ["Auth:SigningKey"]="test-only-signing-key-at-least-thirty-two-bytes",
   ["Telegram:ClientId"]="12345",
   ["Telegram:ClientSecret"]="test-only-client-secret"
  });
  AuthSetup.AddSessions(appBuilder);
  using var services=appBuilder.Services.BuildServiceProvider();
  var options=services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("Telegram");
  var text=await options.Backchannel.GetStringAsync(endpoint.Urls.Single()+"/jwks.json");
  using var document=JsonDocument.Parse(text);
  Assert.Equal(JsonValueKind.Array,document.RootElement.GetProperty("keys").ValueKind);
 }
}
