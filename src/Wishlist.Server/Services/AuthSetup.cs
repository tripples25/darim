using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;
namespace Wishlist.Server.Services;
public static class AuthSetup {
 public static TelegramIdentity IdentityFromClaims(ClaimsPrincipal principal){
  // OIDC sub identifies the subject at this client; Telegram profile id matches Mini App user.id.
  if(!long.TryParse(principal.FindFirst("id")?.Value,out var id)||id<=0)throw new RuleException("Invalid Telegram identity.",401);
  var name=principal.FindFirst("name")?.Value??"Участник";
  return new(id,name[..Math.Min(name.Length,100)],principal.FindFirst("preferred_username")?.Value);
 }
 public static bool TelegramConfigured(IConfiguration c)=>!string.IsNullOrWhiteSpace(c["Telegram:ClientId"])&&!string.IsNullOrWhiteSpace(c["Telegram:ClientSecret"]);
 public static ClaimsPrincipal Principal(AppUser u)=>new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,u.Id.ToString()),new(ClaimTypes.Name,u.Name)],"Cookies"));
 public static void AddSessions(this WebApplicationBuilder b){
  var secret=b.Configuration["Auth:SigningKey"];
  if(string.IsNullOrWhiteSpace(secret)||Encoding.UTF8.GetByteCount(secret)<32)throw new InvalidOperationException("Set Auth__SigningKey to a random secret of at least 32 bytes.");
  var auth=b.Services.AddAuthentication(o=>{o.DefaultScheme="Session";o.DefaultChallengeScheme="Session";})
   .AddPolicyScheme("Session","Session",o=>o.ForwardDefaultSelector=ctx=>ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?"Bearer":"Cookies")
   .AddCookie("Cookies",o=>{o.Cookie.Name="wishlist.session";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Lax;o.Cookie.SecurePolicy=b.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;o.ExpireTimeSpan=TimeSpan.FromDays(14);o.SlidingExpiration=true;o.Events.OnRedirectToLogin=ctx=>{ctx.Response.StatusCode=401;return Task.CompletedTask;};o.Events.OnRedirectToAccessDenied=ctx=>{ctx.Response.StatusCode=403;return Task.CompletedTask;};})
   .AddJwtBearer("Bearer",o=>{o.MapInboundClaims=false;o.TokenValidationParameters=new(){ValidateIssuer=true,ValidIssuer="wishlist",ValidateAudience=true,ValidAudience="wishlist-mini",ValidateLifetime=true,ClockSkew=TimeSpan.FromSeconds(15),ValidateIssuerSigningKey=true,IssuerSigningKey=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),ValidAlgorithms=[SecurityAlgorithms.HmacSha256],NameClaimType=ClaimTypes.Name,RoleClaimType=ClaimTypes.Role};});
  if(TelegramConfigured(b.Configuration))auth.AddOpenIdConnect("Telegram",o=>{
   o.SignInScheme="Cookies";o.Authority="https://oauth.telegram.org";o.ClientId=b.Configuration["Telegram:ClientId"];o.ClientSecret=b.Configuration["Telegram:ClientSecret"];o.ResponseType="code";o.UsePkce=true;o.CallbackPath="/auth/callback";o.Scope.Clear();o.Scope.Add("openid");o.Scope.Add("profile");o.GetClaimsFromUserInfoEndpoint=false;o.SaveTokens=false;o.MapInboundClaims=false;
   o.Events.OnTokenValidated=async ctx=>{
    try{var identity=IdentityFromClaims(ctx.Principal!);var accounts=ctx.HttpContext.RequestServices.GetRequiredService<AccountService>();var user=await accounts.Login(identity);ctx.Principal=Principal(user);}
    catch(RuleException){ctx.Fail("Invalid Telegram identity.");}
   };
   o.Events.OnRemoteFailure=ctx=>{ctx.HandleResponse();ctx.Response.Redirect("/?login=failed");return Task.CompletedTask;};
  });
 }
 public static MiniSessionDto MiniToken(AppUser u,IConfiguration c){var expires=DateTimeOffset.UtcNow.AddHours(1);var jwt=new JwtSecurityToken("wishlist","wishlist-mini",[new(ClaimTypes.NameIdentifier,u.Id.ToString())],expires:expires.UtcDateTime,signingCredentials:new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(c["Auth:SigningKey"]!)),SecurityAlgorithms.HmacSha256));return new(new JwtSecurityTokenHandler().WriteToken(jwt),expires);}
}
