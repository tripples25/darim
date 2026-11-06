using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wishlist.Server.Services;

namespace Wishlist.Tests;

public class OidcStateTests {
 static ServiceProvider Services() {
  var builder=WebApplication.CreateBuilder();
  builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> {
   ["Auth:SigningKey"]="test-only-signing-key-at-least-thirty-two-bytes",
   ["Telegram:ClientId"]="12345", ["Telegram:ClientSecret"]="test-only-secret"
  });
  AuthSetup.AddSessions(builder);
  return builder.Services.BuildServiceProvider();
 }
 static ISecureDataFormat<AuthenticationProperties> Format(IServiceProvider services)=>
  services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("Telegram").StateDataFormat;
 static AuthenticationProperties Properties()=>new(new Dictionary<string,string?> {
  [".xsrf"]="browser-correlation-value",
  ["code_verifier"]=new string('v',64),
  ["nonce"]="test-nonce"
 }){RedirectUri="/"};

 [Fact] public void StateIsShortAndPreservesCorrelationPkceAndReturnUrl() {
  using var services=Services();var format=Format(services);
  var state=format.Protect(Properties());
  Assert.InRange(state.Length,32,64);
  var restored=format.Unprotect(state);
  Assert.NotNull(restored);
  Assert.Equal("/",restored.RedirectUri);
  Assert.Equal("browser-correlation-value",restored.Items[".xsrf"]);
  Assert.Equal(new string('v',64),restored.Items["code_verifier"]);
  Assert.Equal("test-nonce",restored.Items["nonce"]);
 }
 [Fact] public void StateCannotBeReplayed() {
  using var services=Services();var format=Format(services);
  var state=format.Protect(Properties());
  Assert.NotNull(format.Unprotect(state));
  Assert.Null(format.Unprotect(state));
 }
 [Fact] public void TamperedStateDoesNotConsumeTheValidLogin() {
  using var services=Services();var format=Format(services);
  var state=format.Protect(Properties());
  var changed=state[..^1]+(state[^1]=='A'?'B':'A');
  Assert.Null(format.Unprotect(changed));
  Assert.NotNull(format.Unprotect(state));
 }
 [Fact] public async Task ConcurrentCallbacksConsumeStateOnlyOnce() {
  using var services=Services();var format=Format(services);
  var state=format.Protect(Properties());
  var results=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>format.Unprotect(state))));
  Assert.Single(results,x=>x is not null);
 }
}
