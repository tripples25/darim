using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wishlist.Contracts;
using Wishlist.Server.Data;
using Wishlist.Server.Services;
namespace Wishlist.Tests;
[Collection("Postgres")]
public class ApiTests(PostgresFixture db) {
 const string Key="test-only-signing-secret-at-least-thirty-two-bytes";
 [Fact]
 public async Task PhotoIsVersionedAndFollowsGiftVisibility()
 {
  var seed=await Seed();
  var png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYGAAAAAEAAH2FzhVAAAAAElFTkSuQmCC");
  using var app=Factory();
  using var client=app.CreateClient();
  Bearer(client,seed.owner);
  using var created=await client.PostAsJsonAsync("/api/gifts",new GiftWriteInput("Книга",Photo:png));
  created.EnsureSuccessStatusCode();
  var gift=(await created.Content.ReadFromJsonAsync<GiftDto>())!;
  Assert.NotNull(gift.PhotoVersion);
  var giftPath=$"/api/gifts/{gift.Id}";
  var photoPath=$"{giftPath}/photo/{gift.PhotoVersion}";
  Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync(giftPath,
   new GiftWriteInput("Книга",Photo:"<svg/>"u8.ToArray()))).StatusCode);
  Bearer(client,seed.donor);
  using var photo=await client.GetAsync(photoPath);
  photo.EnsureSuccessStatusCode();
  Assert.Equal("image/png",photo.Content.Headers.ContentType!.MediaType);
  Assert.True(photo.Headers.CacheControl!.NoStore);
  Assert.Equal(png,await photo.Content.ReadAsByteArrayAsync());
  Assert.Equal(HttpStatusCode.Forbidden,(await client.PutAsJsonAsync(giftPath,
   new GiftWriteInput("Книга",RemovePhoto:true))).StatusCode);
  client.DefaultRequestHeaders.Authorization=null;
  Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(photoPath)).StatusCode);

  Bearer(client,seed.owner);
  using var textEdit=await client.PutAsJsonAsync(giftPath,new GiftInput("Новая книга"));
  textEdit.EnsureSuccessStatusCode();
  Assert.Equal(gift.PhotoVersion,(await textEdit.Content.ReadFromJsonAsync<GiftDto>())!.PhotoVersion);
  using var replacement=await client.PutAsJsonAsync(giftPath,new GiftWriteInput("Новая книга",Photo:png));
  replacement.EnsureSuccessStatusCode();
  var replaced=(await replacement.Content.ReadFromJsonAsync<GiftDto>())!;
  Assert.NotEqual(gift.PhotoVersion,replaced.PhotoVersion);
  Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(photoPath)).StatusCode);
  using var removed=await client.PutAsJsonAsync(giftPath,new GiftWriteInput("Новая книга",RemovePhoto:true));
  removed.EnsureSuccessStatusCode();
  Assert.Null((await removed.Content.ReadFromJsonAsync<GiftDto>())!.PhotoVersion);
  Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"{giftPath}/photo/{replaced.PhotoVersion}")).StatusCode);

  using var restored=await client.PutAsJsonAsync(giftPath,new GiftWriteInput("Новая книга",Photo:png));
  restored.EnsureSuccessStatusCode();
  var restoredVersion=(await restored.Content.ReadFromJsonAsync<GiftDto>())!.PhotoVersion;
  (await client.PostAsync($"{giftPath}/archive",null)).EnsureSuccessStatusCode();
  Bearer(client,seed.donor);
  Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"{giftPath}/photo/{restoredVersion}")).StatusCode);
 }
 WebApplicationFactory<Program> Factory(string environment="Testing")=>new WebApplicationFactory<Program>().WithWebHostBuilder(b=>{b.UseEnvironment(environment);b.UseSetting("Auth:SigningKey",Key);b.UseSetting("ConnectionStrings:Database",db.Container.GetConnectionString());b.UseSetting("Telegram:BotToken","123:test-secret");b.UseSetting("Demo:Enabled","false");});
 async Task<(AppUser owner,AppUser donor,Guid gift)> Seed(){await using var ctx=new AppDb(db.Options);var c=new Company{Name="API company",InviteHash=Guid.NewGuid().ToString()};ctx.Companies.Add(c);await ctx.SaveChangesAsync();var owner=new AppUser{Name="Получатель",CompanyId=c.Id,TelegramId=Random.Shared.NextInt64(1,long.MaxValue)};var donor=new AppUser{Name="Даритель",CompanyId=c.Id,TelegramId=Random.Shared.NextInt64(1,long.MaxValue),Contact="@donor"};ctx.Users.AddRange(owner,donor);await ctx.SaveChangesAsync();var g=Wishlist.Core.Gift.New(c.Id,owner.Id,new("Сюрприз"));ctx.Gifts.Add(g);await ctx.SaveChangesAsync();return(owner,donor,g.Id);}
 static void Bearer(HttpClient client,AppUser u){var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Auth:SigningKey",Key}}).Build();client.DefaultRequestHeaders.Authorization=new("Bearer",AuthSetup.MiniToken(u,config).Token);}
 [Fact] public void BrowserUsesVerifiedTelegramIdRatherThanOidcSubject(){var claims=new ClaimsPrincipal(new ClaimsIdentity([new("sub","1234123412341234123"),new("id","987654321"),new("name","Юлия")]));var identity=AuthSetup.IdentityFromClaims(claims);Assert.Equal(987654321,identity.Id);}
 [Theory][InlineData("0")][InlineData("-1")][InlineData("invalid")] public void BrowserRejectsInvalidTelegramId(string id){var claims=new ClaimsPrincipal(new ClaimsIdentity([new("sub","1234"),new("id",id)]));Assert.Throws<Wishlist.Core.RuleException>(()=>AuthSetup.IdentityFromClaims(claims));}
 [Fact] public async Task AnonymousCannotReadWishlist(){using var app=Factory();using var client=app.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/gifts")).StatusCode);}
 [Fact] public async Task BearerMutationReturnsOwnerSafeView(){var s=await Seed();using var app=Factory();using var client=app.CreateClient();Bearer(client,s.donor);Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/gifts/{s.gift}/actions",new GiftActionInput("reserve"))).StatusCode);Bearer(client,s.owner);var dto=(await client.GetFromJsonAsync<GiftDto[]>("/api/gifts"))!.Single(x=>x.Id==s.gift);Assert.Null(dto.Participation);Assert.Null(dto.Original);}
 [Fact] public async Task InvalidBearerCannotFallBackToCookie(){using var app=Factory();using var client=app.CreateClient();client.DefaultRequestHeaders.Authorization=new("Bearer","tampered");Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/me")).StatusCode);}
 [Fact] public async Task ProductionNeverExposesDemoLogin(){using var app=Factory("Production");using var client=app.CreateClient();var r=await client.PostAsync("/auth/demo/0",null);Assert.False(r.IsSuccessStatusCode);}
 [Fact] public async Task InvalidMiniDataIsRejected(){using var app=Factory();using var client=app.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/auth/mini",new MiniLoginInput("hash=bad"))).StatusCode);}
 [Fact] public async Task ValidMiniDataCreatesSessionAndMatchesIdentity(){using var app=Factory();using var client=app.CreateClient();var raw=TelegramTests.Signed(DateTimeOffset.UtcNow.ToUnixTimeSeconds());var r=await client.PostAsJsonAsync("/auth/mini",new MiniLoginInput(raw));Assert.Equal(HttpStatusCode.OK,r.StatusCode);var session=(await r.Content.ReadFromJsonAsync<MiniSessionDto>())!;client.DefaultRequestHeaders.Authorization=new("Bearer",session.Token);var me=await client.GetFromJsonAsync<MeDto>("/api/me");Assert.Equal("Юлия",me!.User.Name);Assert.Null(me.Company);}
 [Fact] public async Task CookieMutationRequiresCsrfAndSucceedsWithToken(){var s=await Seed();using var app=Factory();using var client=app.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new Uri("https://localhost")});var tickets=app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>().Get("Cookies");var cookie=tickets.TicketDataFormat.Protect(new AuthenticationTicket(AuthSetup.Principal(s.owner),"Cookies"));client.DefaultRequestHeaders.Add("Cookie","wishlist.session="+cookie);
  Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/gifts",new GiftInput("Без CSRF"))).StatusCode);
  var csrf=(await client.GetFromJsonAsync<CsrfDto>("/api/csrf"))!;client.DefaultRequestHeaders.Add("X-CSRF-TOKEN",csrf.Token);Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/api/gifts",new GiftInput("С CSRF"))).StatusCode);
 }
 [Fact] public async Task InviteWithoutOptionsDefaultsToSingleUse(){var s=await Seed();var users=Enumerable.Range(0,2).Select(_=>new AppUser{Name="Гость",TelegramId=Random.Shared.NextInt64(1,long.MaxValue)}).ToArray();using(var ctx=new AppDb(db.Options)){var company=await ctx.Companies.FindAsync(s.owner.CompanyId);company!.OrganizerId=s.owner.Id;ctx.Users.AddRange(users);await ctx.SaveChangesAsync();}using var app=Factory();using var client=app.CreateClient();Bearer(client,s.owner);var invite=await (await client.PostAsync("/api/company/invite",null)).Content.ReadFromJsonAsync<InviteDto>();Bearer(client,users[0]);Assert.Equal(HttpStatusCode.NoContent,(await client.PostAsJsonAsync("/api/company/join",new JoinCompanyInput(invite!.Token))).StatusCode);Bearer(client,users[1]);Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/company/join",new JoinCompanyInput(invite.Token))).StatusCode);}
 [Fact] public async Task InviteJoinAndRotationRespectOrganizer(){var s=await Seed();using(var ctx=new AppDb(db.Options)){var company=await ctx.Companies.FindAsync(s.owner.CompanyId);company!.OrganizerId=s.owner.Id;await ctx.SaveChangesAsync();}
  using var app=Factory();using var client=app.CreateClient();Bearer(client,s.donor);Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsync("/api/company/invite",null)).StatusCode);Bearer(client,s.owner);var invite=await (await client.PostAsync("/api/company/invite",null)).Content.ReadFromJsonAsync<InviteDto>();
  var newUser=new AppUser{Name="Новый",TelegramId=Random.Shared.NextInt64(1,long.MaxValue)};using(var ctx=new AppDb(db.Options)){ctx.Users.Add(newUser);await ctx.SaveChangesAsync();}Bearer(client,newUser);Assert.Equal(HttpStatusCode.NoContent,(await client.PostAsJsonAsync("/api/company/join",new JoinCompanyInput(invite!.Token))).StatusCode);var me=await client.GetFromJsonAsync<MeDto>("/api/me");Assert.Equal(s.owner.CompanyId,me!.Company!.Id);Assert.All(me.Company.Members,x=>Assert.Null(x.Contact));
 }
}
