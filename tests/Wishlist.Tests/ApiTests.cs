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
using SkiaSharp;
using Wishlist.Contracts;
using Wishlist.Server.Data;
using Wishlist.Server.Services;
namespace Wishlist.Tests;
[Collection("Postgres")]
public class ApiTests(PostgresFixture db) {
 const string Key="test-only-signing-secret-at-least-thirty-two-bytes";

 [Fact]
 public async Task ExistingAccountCanJoinAgainWithoutConsumingAnotherInvite()
 {
  var first = await Seed();
  var second = await Seed();
  using var app = Factory();
  using var client = app.CreateClient();
  Bearer(client, second.owner);
  using var link = await client.PostAsJsonAsync("/api/company/invite", new InviteOptions(1, second.owner.CompanyId));
  link.EnsureSuccessStatusCode();
  var invite = await link.Content.ReadFromJsonAsync<InviteDto>();

  Bearer(client, first.donor);
  var request = new JoinCompanyInput(invite!.Token);
  Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/company/join", request)).StatusCode);
  Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/company/join", request)).StatusCode);
  var me = await client.GetFromJsonAsync<MeDto>("/api/me");
  Assert.Equal(new[] { first.owner.CompanyId!.Value, second.owner.CompanyId!.Value }.Order(),
   me!.Companies!.Select(company => company.Id).Order());
  Bearer(client, first.owner);
  Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/company/join", request)).StatusCode);
 }

 [Fact]
 public async Task OrganizerCanRotateOnlyTheSelectedCompanyInvite()
 {
  var first = await Seed();
  var second = await Seed();
  using var app = Factory();
  using var client = app.CreateClient();
  Bearer(client, first.owner);
  using var response = await client.PostAsJsonAsync("/api/company/invite", new InviteOptions(1, second.owner.CompanyId));
  Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
 }

 [Fact]
 public async Task CompanyFeedsRespectPrivateSelectedAndAllVisibility()
 {
  var first = await Seed();
  var second = await Seed();
  var outside = await Seed();
  await Join(first.owner, second.owner);
  using var app = Factory();
  using var client = app.CreateClient();
  Bearer(client, first.owner);
  var hidden = await CreateGift(client, "Личное", false);
  var selected = await CreateGift(client, "Для одной компании", false, second.owner.CompanyId!.Value);
  var shared = await CreateGift(client, "Для всех моих компаний", true);
  using var unauthorized = await client.PostAsJsonAsync("/api/gifts",
   new GiftWriteInput("Чужая компания", Visibility: new(false, [outside.owner.CompanyId!.Value])));
  Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
  var mine = (await client.GetFromJsonAsync<GiftDto[]>("/api/gifts/mine"))!;
  Assert.Contains(mine, gift => gift.Id == hidden.Id);

  Bearer(client, first.donor);
  Assert.Equal(new[] { first.gift, shared.Id }.Order(), (await GiftIds(client, first.owner.CompanyId)).Order());
  using var foreignFeed = await client.GetAsync($"/api/gifts?companyId={second.owner.CompanyId}");
  Assert.Equal(HttpStatusCode.Forbidden, foreignFeed.StatusCode);
  Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/gifts/{selected.Id}/actions", new GiftActionInput("reserve"))).StatusCode);
  Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/gifts/{hidden.Id}/actions", new GiftActionInput("reserve"))).StatusCode);
  Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/gifts/{hidden.Id}", new GiftInput("Скрытое"))).StatusCode);

  Bearer(client, second.donor);
  Assert.Equal(new[] { second.gift, selected.Id, shared.Id }.Order(), (await GiftIds(client, second.owner.CompanyId)).Order());
  Bearer(client, outside.donor);
  Assert.Equal(outside.gift, Assert.Single(await GiftIds(client, outside.owner.CompanyId)));
 }

 [Fact]
 public async Task CompanyFeedCanExcludeCurrentUsersWishes()
 {
  var seed = await Seed();
  using var app = Factory();
  using var client = app.CreateClient();
  Bearer(client, seed.donor);
  var otherGift = await CreateGift(client, "Желание другого участника", true);
  Bearer(client, seed.owner);

  var feed = $"/api/gifts?companyId={seed.owner.CompanyId}";
  foreach (var path in new[] { feed, $"{feed}&includeMine=true" })
  {
   var all = (await client.GetFromJsonAsync<GiftDto[]>(path))!;
   Assert.Equal(new[] { seed.gift, otherGift.Id }.Order(), all.Select(gift => gift.Id).Order());
  }

  var filtered = (await client.GetFromJsonAsync<GiftDto[]>($"{feed}&includeMine=false"))!;
  Assert.Equal(otherGift.Id, Assert.Single(filtered).Id);
  var mine = (await client.GetFromJsonAsync<GiftDto[]>("/api/gifts/mine"))!;
  Assert.Equal(seed.gift, Assert.Single(mine).Id);
 }

 [Fact]
 public async Task GiftPublishedInTwoCompaniesHasOneReservationWinner()
 {
  var first = await Seed();
  var second = await Seed();
  await Join(first.owner, second.owner);
  using var app = Factory();
  using var author = app.CreateClient();
  Bearer(author, first.owner);
  var gift = await CreateGift(author, "Один подарок", false, first.owner.CompanyId!.Value, second.owner.CompanyId!.Value);

  using var firstClient = app.CreateClient();
  using var secondClient = app.CreateClient();
  Bearer(firstClient, first.donor);
  Bearer(secondClient, second.donor);
  var path = $"/api/gifts/{gift.Id}/actions";
  var results = await Task.WhenAll(firstClient.PostAsJsonAsync(path, new GiftActionInput("reserve")),
   secondClient.PostAsJsonAsync(path, new GiftActionInput("reserve")));
  Assert.Equal(HttpStatusCode.OK, Assert.Single(results, r => r.IsSuccessStatusCode).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict, Assert.Single(results, r => !r.IsSuccessStatusCode).StatusCode);
  foreach (var result in results) result.Dispose();
 }

 [Fact]
 public async Task SplitDetailsAndJoiningStayInTheCompanyThatOpenedIt()
 {
  var first = await Seed();
  var second = await Seed();
  await Join(first.owner, second.owner);
  await Join(second.owner, first.owner);
  using var app = Factory();
  using var client = app.CreateClient();
  Bearer(client, first.owner);
  var gift = await CreateGift(client, "Общий подарок", true);
  var path = $"/api/gifts/{gift.Id}/actions";
  Bearer(client, first.donor);
  using var split = await client.PostAsJsonAsync(path, new GiftActionInput("split", Slots: 2, CompanyId: first.owner.CompanyId));
  split.EnsureSuccessStatusCode();

  Bearer(client, second.donor);
  var gifts = await client.GetFromJsonAsync<GiftDto[]>($"/api/gifts?companyId={second.owner.CompanyId}");
  var occupied = Assert.Single(gifts!, item => item.Id == gift.Id).Participation!;
  Assert.Equal("occupied", occupied.Mode);
  Assert.Null(occupied.Participants);
  Assert.Equal(0, occupied.Slots);
  using var joined = await client.PostAsJsonAsync(path, new GiftActionInput("join", CompanyId: second.owner.CompanyId));
  Assert.Equal(HttpStatusCode.Forbidden, joined.StatusCode);
 }

 [Fact]
 public async Task GuestWishlistHidesReservationsAndConfirmsOnlyAfterTwoHours()
 {
  var seed = await Seed();
  var guest = new AppUser { Name = "Гость", Username = "guest", TelegramId = Random.Shared.NextInt64(1, long.MaxValue) };
  await using (var ctx = new AppDb(db.Options))
  {
   ctx.Users.Add(guest);
   await ctx.SaveChangesAsync();
  }
  var clock = new TestClock(DateTimeOffset.UtcNow);
  using var app = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
  using var ownerClient = app.CreateClient();
  using var guestClient = app.CreateClient();
  using var donorClient = app.CreateClient();
  Bearer(ownerClient, seed.owner);
  Bearer(guestClient, guest);
  Bearer(donorClient, seed.donor);
  var privateGift = await CreateGift(ownerClient, "Личный подарок", false);
  using var linkResponse = await ownerClient.PostAsJsonAsync("/api/wishlist-links", new WishlistLinkInput(seed.owner.CompanyId, "Для друзей"));
  linkResponse.EnsureSuccessStatusCode();
  var link = (await linkResponse.Content.ReadFromJsonAsync<WishlistLinkCreated>())!;
  const string publicPath = "/api/public/wishlist";
  var requestPath = $"/api/wishlist-links/gifts/{seed.gift}/request";
  using var publicClient = app.CreateClient();
  publicClient.DefaultRequestHeaders.Authorization = new("Wishlist", link.Token);
  using var initial = await publicClient.GetAsync(publicPath);
  initial.EnsureSuccessStatusCode();
  var firstView = await initial.Content.ReadFromJsonAsync<JsonElement>();
  Assert.Contains(firstView.GetProperty("gifts").EnumerateArray(), gift => gift.GetProperty("id").GetGuid() == seed.gift);
  Assert.DoesNotContain(firstView.GetProperty("gifts").EnumerateArray(), gift => gift.GetProperty("id").GetGuid() == privateGift.Id);
  Assert.True(initial.Headers.CacheControl!.NoStore);

  using var requested = await guestClient.PostAsJsonAsync(requestPath, new WishlistRequestInput(Token: link.Token));
  Assert.Equal(HttpStatusCode.NoContent, requested.StatusCode);
  using var repeated = await guestClient.PostAsJsonAsync(requestPath, new WishlistRequestInput(Token: link.Token));
  Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
  var actionPath = $"/api/gifts/{seed.gift}/actions";
  Assert.Equal(HttpStatusCode.Conflict, (await donorClient.PostAsJsonAsync(actionPath, new GiftActionInput("reserve"))).StatusCode);

  clock.Advance(TimeSpan.FromMinutes(119));
  await ProcessDue();
  var before = Assert.Single((await guestClient.GetFromJsonAsync<WishlistRequestDto[]>("/api/wishlist-requests"))!);
  Assert.False(before.Confirmed);
  clock.Advance(TimeSpan.FromMinutes(1));
  await ProcessDue();
  var after = Assert.Single((await guestClient.GetFromJsonAsync<WishlistRequestDto[]>("/api/wishlist-requests"))!);
  Assert.True(after.Confirmed);
  Assert.Single((await guestClient.GetFromJsonAsync<NoticeDto[]>("/api/notices"))!);
  Assert.Empty((await ownerClient.GetFromJsonAsync<NoticeDto[]>("/api/notices"))!);
  var publicView = await publicClient.GetFromJsonAsync<JsonElement>(publicPath);
  Assert.Equal(firstView.GetRawText(), publicView.GetRawText());
  var ownerView = (await ownerClient.GetFromJsonAsync<GiftDto[]>("/api/gifts"))!.Single(gift => gift.Id == seed.gift);
  Assert.Null(ownerView.Participation);

  using var revoked = await ownerClient.DeleteAsync($"/api/wishlist-links/{link.Link.Id}");
  Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
  Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync(publicPath)).StatusCode);
  Assert.True(Assert.Single((await guestClient.GetFromJsonAsync<WishlistRequestDto[]>("/api/wishlist-requests"))!).Confirmed);

  async Task ProcessDue()
  {
   await using var scope = app.Services.CreateAsyncScope();
   await using var ctx = scope.ServiceProvider.GetRequiredService<AppDb>();
   await new WishlistLinks(ctx, timeProvider: clock).RunDue(default);
  }
 }

 [Fact]
 public async Task OccupiedGuestRequestsStayBlindAndUseTheirOriginalDeadline()
 {
  var seed=await Seed();
  var guest=new AppUser{Name="Гость",Username="wishlist_guest",TelegramId=Random.Shared.NextInt64(1,long.MaxValue)};
  await using(var ctx=new AppDb(db.Options)){
   ctx.Users.Add(guest);
   await ctx.SaveChangesAsync();
  }
  var clock=new TestClock(DateTimeOffset.UtcNow);
  using var app=Factory().WithWebHostBuilder(b=>b.ConfigureServices(s=>s.AddSingleton<TimeProvider>(clock)));
  using var owner=app.CreateClient();
  using var donor=app.CreateClient();
  using var visitor=app.CreateClient();
  Bearer(owner,seed.owner);
  Bearer(donor,seed.donor);
  Bearer(visitor,guest);
  var released=await CreateGift(owner,"Позже освободится",true);
  var bought=await CreateGift(owner,"Уже куплен",true);
  foreach(var id in new[]{seed.gift,released.Id,bought.Id})
   (await donor.PostAsJsonAsync($"/api/gifts/{id}/actions",new GiftActionInput("reserve"))).EnsureSuccessStatusCode();
  (await donor.PostAsJsonAsync($"/api/gifts/{bought.Id}/actions",new GiftActionInput("buy"))).EnsureSuccessStatusCode();
  var link=(await (await owner.PostAsJsonAsync("/api/wishlist-links",new WishlistLinkInput(seed.owner.CompanyId)))
   .Content.ReadFromJsonAsync<WishlistLinkCreated>())!;
  var input=new WishlistRequestInput(ShareUsername:true,Token:link.Token);
  var ids=new[]{seed.gift,released.Id,bought.Id};
  foreach(var id in ids){
   var response=await visitor.PostAsJsonAsync($"/api/wishlist-links/gifts/{id}/request",input);
   Assert.Equal(HttpStatusCode.NoContent,response.StatusCode);
   Assert.Empty(await response.Content.ReadAsStringAsync());
  }
  Assert.Equal(HttpStatusCode.Forbidden,(await owner.PostAsJsonAsync($"/api/wishlist-links/gifts/{seed.gift}/request",input)).StatusCode);
  var interests=(await donor.GetFromJsonAsync<WishlistInterestDto[]>("/api/wishlist-interest"))!;
  var interest=Assert.Single(interests,x=>x.GiftId==seed.gift);
  Assert.Equal("@wishlist_guest",interest.TelegramUsername);
  for(var i=0;i<2;i++)
   Assert.Equal(HttpStatusCode.NoContent,(await donor.PostAsync($"/api/wishlist-interest/{interest.Id}/notify",null)).StatusCode);
  Assert.Equal(HttpStatusCode.NoContent,(await visitor.PostAsJsonAsync($"/api/wishlist-links/gifts/{seed.gift}/request",input)).StatusCode);
  Assert.Single((await visitor.GetFromJsonAsync<NoticeDto[]>("/api/notices"))!);
  Assert.DoesNotContain((await donor.GetFromJsonAsync<WishlistInterestDto[]>("/api/wishlist-interest"))!,x=>x.GiftId==seed.gift);

  clock.Advance(TimeSpan.FromMinutes(60));
  (await donor.PostAsJsonAsync($"/api/gifts/{released.Id}/actions",new GiftActionInput("cancel"))).EnsureSuccessStatusCode();
  Assert.Empty((await visitor.GetFromJsonAsync<GiftDto[]>("/api/gifts/giving"))!);
  clock.Advance(TimeSpan.FromMinutes(60));
  for(var i=0;i<2;i++){
   await using var scope=app.Services.CreateAsyncScope();
   await scope.ServiceProvider.GetRequiredService<WishlistLinks>().RunDue(default);
  }
  Assert.Equal(released.Id,Assert.Single((await visitor.GetFromJsonAsync<GiftDto[]>("/api/gifts/giving"))!).Id);
  Assert.Equal(2,(await visitor.GetFromJsonAsync<NoticeDto[]>("/api/notices"))!.Length);
  Assert.Empty((await owner.GetFromJsonAsync<NoticeDto[]>("/api/notices"))!);
 }

 sealed class TestClock(DateTimeOffset utcNow) : TimeProvider
 {
  private long ticks = utcNow.UtcTicks;
  public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
  public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
 }

 async Task Join(AppUser joining, AppUser organizer)
 {
  await using var ctx = new AppDb(db.Options);
  var accounts = new AccountService(ctx);
  var invite = await accounts.Invite(organizer.Id, 1, organizer.CompanyId);
  await accounts.Join(joining.Id, invite.Token);
 }

 static async Task<GiftDto> CreateGift(HttpClient client, string title, bool allCompanies, params Guid[] companyIds)
 {
  using var response = await client.PostAsJsonAsync("/api/gifts", new GiftWriteInput(title, Visibility: new(allCompanies, companyIds)));
  response.EnsureSuccessStatusCode();
  return (await response.Content.ReadFromJsonAsync<GiftDto>())!;
 }

 static async Task<Guid[]> GiftIds(HttpClient client, Guid? companyId)
 {
  var gifts = await client.GetFromJsonAsync<GiftDto[]>($"/api/gifts?companyId={companyId}");
  return gifts!.Select(gift => gift.Id).ToArray();
 }
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
  Assert.True(photo.Headers.CacheControl!.NoStore);
  using var smallPhoto=SKBitmap.Decode(await photo.Content.ReadAsByteArrayAsync());
  Assert.Equal(new SKSizeI(1,1),new SKSizeI(smallPhoto.Width,smallPhoto.Height));
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

 [Fact]
 public async Task OwnerCanRestoreArchivedGiftWithoutLosingReservation()
 {
  var seed=await Seed();
  using var app=Factory();
  using var client=app.CreateClient();
  var giftPath=$"/api/gifts/{seed.gift}";

  Bearer(client,seed.donor);
  (await client.PostAsJsonAsync($"{giftPath}/actions",new GiftActionInput("reserve"))).EnsureSuccessStatusCode();
  Bearer(client,seed.owner);
  (await client.PostAsync($"{giftPath}/archive",null)).EnsureSuccessStatusCode();

  Bearer(client,seed.donor);
  using var denied=await client.PostAsync($"{giftPath}/restore",null);
  Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
  var archived=Assert.Single((await client.GetFromJsonAsync<GiftDto[]>("/api/gifts/giving"))!);
  Assert.True(archived.Archived);

  Bearer(client,seed.owner);
  using var response=await client.PostAsync($"{giftPath}/restore",null);
  response.EnsureSuccessStatusCode();
  Assert.False((await response.Content.ReadFromJsonAsync<GiftDto>())!.Archived);

  Bearer(client,seed.donor);
  var restored=Assert.Single((await client.GetFromJsonAsync<GiftDto[]>("/api/gifts/giving"))!);
  Assert.Equal(seed.gift,restored.Id);
  Assert.False(restored.Archived);
  Assert.True(restored.Participation!.IsMine);
 }

 [Fact]
 public async Task LargeUploadIsStoredSmallerWithProportionsAndTransparencyIntact()
 {
  var seed=await Seed();
  using var app=Factory();
  app.UseKestrel(options => options.Listen(IPAddress.Loopback,0));
  using var client=app.CreateClient();
  Bearer(client,seed.owner);

  using var source=new SKBitmap(2400,1600);
  using (var canvas=new SKCanvas(source))
  using (var paint=new SKPaint { Color=SKColors.Red })
  {
   canvas.Clear(SKColors.Transparent);
   canvas.DrawRect(0,0,1200,1600,paint);
  }
  using var pixels=source.PeekPixels();
  using var png=pixels.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.NoFilters,0));
  Assert.NotNull(png);
  Assert.InRange(png.Size,14*1024*1024,15*1024*1024);

  using var created=await client.PostAsJsonAsync("/api/gifts",new GiftWriteInput("Фото",Photo:png.ToArray()));
  created.EnsureSuccessStatusCode();
  var gift=(await created.Content.ReadFromJsonAsync<GiftDto>())!;
  using var response=await client.GetAsync($"/api/gifts/{gift.Id}/photo/{gift.PhotoVersion}");
  response.EnsureSuccessStatusCode();
  Assert.Equal("image/webp",response.Content.Headers.ContentType!.MediaType);
  var saved=await response.Content.ReadAsByteArrayAsync();
  Assert.True(saved.Length < png.Size / 10);
  using var result=SKBitmap.Decode(saved);
  Assert.Equal(1600,result.Width);
  Assert.Equal(1067,result.Height);
  Assert.Equal(SKColors.Red,result.GetPixel(100,100));
  Assert.Equal(0,result.GetPixel(1500,100).Alpha);

  var oversized=new byte[15*1024*1024+1];
  using var rejected=await client.PutAsJsonAsync($"/api/gifts/{gift.Id}",new GiftWriteInput("Фото",Photo:oversized));
  Assert.Equal(HttpStatusCode.BadRequest,rejected.StatusCode);
  Assert.Contains("15 МБ",(await rejected.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);
 }

 [Fact]
 public async Task ResizedJpegKeepsItsExifOrientation()
 {
  var seed=await Seed();
  using var app=Factory();
  using var client=app.CreateClient();
  Bearer(client,seed.owner);
  using var source=new SKBitmap(2400,1200,true);
  using (var canvas=new SKCanvas(source))
  using (var paint=new SKPaint { Color=SKColors.Red })
  {
   canvas.Clear(SKColors.Blue);
   canvas.DrawRect(0,0,1200,1200,paint);
  }
  using var jpeg=source.Encode(SKEncodedImageFormat.Jpeg,95);
  var bytes=jpeg.ToArray();
  // APP1 EXIF tag 0x0112 with value 6: rotate 90 degrees clockwise.
  var exif=Convert.FromHexString("FFE1002245786966000049492A0008000000010012010300010000000600000000000000");
  using var created=await client.PostAsJsonAsync("/api/gifts",new GiftWriteInput("Портрет",Photo:[.. bytes[..2], .. exif, .. bytes[2..]]));
  created.EnsureSuccessStatusCode();
  var gift=(await created.Content.ReadFromJsonAsync<GiftDto>())!;
  using var result=SKBitmap.Decode(await client.GetByteArrayAsync($"/api/gifts/{gift.Id}/photo/{gift.PhotoVersion}"));
  Assert.Equal(800,result.Width);
  Assert.Equal(1600,result.Height);
  Assert.True(result.GetPixel(400,200).Red > 240);
  Assert.True(result.GetPixel(400,1400).Blue > 240);
 }
 WebApplicationFactory<Program> Factory(string environment="Testing")=>new WebApplicationFactory<Program>().WithWebHostBuilder(b=>{b.UseEnvironment(environment);b.UseSetting("Auth:SigningKey",Key);b.UseSetting("ConnectionStrings:Database",db.Container.GetConnectionString());b.UseSetting("Telegram:BotToken","123:test-secret");b.UseSetting("Demo:Enabled","false");});
 async Task<(AppUser owner,AppUser donor,Guid gift)> Seed(){await using var ctx=new AppDb(db.Options);var c=new Company{Name="API company",InviteHash=Guid.NewGuid().ToString()};var owner=new AppUser{Name="Получатель",CompanyId=c.Id,TelegramId=Random.Shared.NextInt64(1,long.MaxValue)};var donor=new AppUser{Name="Даритель",CompanyId=c.Id,TelegramId=Random.Shared.NextInt64(1,long.MaxValue),Contact="@donor"};c.OrganizerId=owner.Id;ctx.AddRange(c,owner,donor);ctx.Memberships.AddRange(new CompanyMembership{UserId=owner.Id,CompanyId=c.Id},new CompanyMembership{UserId=donor.Id,CompanyId=c.Id});var g=Wishlist.Core.Gift.New(c.Id,owner.Id,new("Сюрприз"));ctx.Gifts.Add(g);await ctx.SaveChangesAsync();return(owner,donor,g.Id);}
 static void Bearer(HttpClient client,AppUser u){var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Auth:SigningKey",Key}}).Build();client.DefaultRequestHeaders.Authorization=new("Bearer",AuthSetup.MiniToken(u,config).Token);}
 [Theory][InlineData(false)][InlineData(true)]
 public async Task BotCodeCreatesOrRestoresSameAccount(bool existing){
  using var app=Factory().WithWebHostBuilder(b=>b.UseSetting("Telegram:CodeLoginEnabled","true"));
  using var client=app.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new Uri("https://localhost")});
  var telegramId=Random.Shared.NextInt64(1,long.MaxValue);Guid? original=null;
  if(existing){using var scope=app.Services.CreateScope();original=(await scope.ServiceProvider.GetRequiredService<AccountService>().Login(new(telegramId,"Юлия","julia"))).Id;}
  var codes=app.Services.GetRequiredService<TelegramLoginCodes>();var code=codes.Issue(new(telegramId,"Юлия","julia"));
  client.DefaultRequestHeaders.Add("Origin","https://evil.example");
  Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/auth/code",new{Code=code})).StatusCode);
  client.DefaultRequestHeaders.Remove("Origin");client.DefaultRequestHeaders.Add("Origin","https://localhost");
  Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/auth/code",new{Code=code})).StatusCode);
  var me=await client.GetFromJsonAsync<MeDto>("/api/me");Assert.Equal("Юлия",me!.User.Name);
  if(existing)Assert.Equal(original,me.User.Id);
  Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/auth/code",new{Code=code})).StatusCode);
 }
 [Fact] public void BrowserUsesVerifiedTelegramIdRatherThanOidcSubject(){var claims=new ClaimsPrincipal(new ClaimsIdentity([new("sub","1234123412341234123"),new("id","987654321"),new("name","Юлия")]));var identity=AuthSetup.IdentityFromClaims(claims);Assert.Equal(987654321,identity.Id);}
 [Fact] public async Task TelegramAvatarIsSavedAndExposedOnlyToSplitParticipants(){
  const string avatar="https://cdn4.telesco.pe/file/avatar.jpg";
  var s=await Seed();
  await using(var ctx=new AppDb(db.Options)){
   var identity=AuthSetup.IdentityFromClaims(new ClaimsPrincipal(new ClaimsIdentity(
    [new("id",s.donor.TelegramId.ToString()),new("name",s.donor.Name),new("picture",avatar)])));
   await new AccountService(ctx).Login(identity);
   var member=new AppUser{Name="Зритель",CompanyId=s.owner.CompanyId,TelegramId=Random.Shared.NextInt64(1,long.MaxValue)};
   ctx.Users.Add(member);
   ctx.Memberships.Add(new(){UserId=member.Id,CompanyId=s.owner.CompanyId!.Value});
   await ctx.SaveChangesAsync();
   using var app=Factory();
   using var client=app.CreateClient();
   Bearer(client,s.donor);
   using var response=await client.PostAsJsonAsync($"/api/gifts/{s.gift}/actions",new GiftActionInput("split",2));
   response.EnsureSuccessStatusCode();
   var created=await response.Content.ReadFromJsonAsync<GiftDto>();
   Assert.Equal(avatar,Assert.Single(created!.Participation!.Participants!).AvatarUrl);
   Bearer(client,member);
   var visible=(await client.GetFromJsonAsync<GiftDto[]>("/api/gifts"))!.Single(gift=>gift.Id==s.gift);
   Assert.Null(visible.Participation!.Participants);
  }
 }
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

