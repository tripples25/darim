using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;
using Wishlist.Server.Services;
namespace Wishlist.Tests;
public class PostgresFixture:IAsyncLifetime {
 public PostgreSqlContainer Container {get;}=new PostgreSqlBuilder("postgres:18-alpine").Build();
 public DbContextOptions<AppDb> Options=>new DbContextOptionsBuilder<AppDb>().UseNpgsql(Container.GetConnectionString()).Options;
 public async Task InitializeAsync(){await Container.StartAsync();await using var db=new AppDb(Options);await db.Database.MigrateAsync();}
 public Task DisposeAsync()=>Container.DisposeAsync().AsTask();
}
[CollectionDefinition("Postgres",DisableParallelization=true)]
public class PostgresCollection:ICollectionFixture<PostgresFixture>{}
[Collection("Postgres")]
public class PostgresTests(PostgresFixture fixture) {
 [Fact]
 public async Task DeletingCompanyRevokesItsLinksAndKeepsPersonalWishesAndReservations()
 {
  var (owner, alice, bob, carol, _) = await Setup();
  Guid companyId, otherCompanyId, giftId, linkId;
  string inviteToken, linkToken;
  await using (var db = new AppDb(fixture.Options))
  {
   var company = await db.Companies.SingleAsync(c => db.Memberships.Any(m => m.CompanyId == c.Id && m.UserId == owner));
   company.OrganizerId = owner;
   await db.SaveChangesAsync();
   companyId = company.Id;
   var accounts = new AccountService(db);
   otherCompanyId = (await accounts.Create(owner, "Другая компания")).CompanyId!.Value;
   inviteToken = (await accounts.Invite(owner, null, companyId)).Token;
   var gift = await new GiftService(db).Create(owner,
    new GiftWriteInput("Желание", Visibility: new(false, [companyId, otherCompanyId])));
   giftId = gift.Id;
   var links = new WishlistLinks(db);
   var link = await links.Create(owner, new(CompanyId: companyId), default);
   linkId = link.Link.Id;
   linkToken = link.Token;
   var pendingGift = await new GiftService(db).Create(owner,
    new GiftWriteInput("Желание с заявкой", Visibility: new(false, [companyId])));
   await links.Submit(carol, linkToken, pendingGift.Id, new(false), default);
   Assert.Single(await links.ForGuest(carol, default));
  }
  Assert.True(await Try(alice, giftId, "split"));
  Assert.True(await Try(bob, giftId, "join"));

  await using (var db = new AppDb(fixture.Options))
  {
   var accounts = new AccountService(db);
   var links = new WishlistLinks(db);
   var error = await Assert.ThrowsAsync<RuleException>(() => accounts.Delete(alice, companyId, links, default));
   Assert.Equal(404, error.Status);
   await accounts.Delete(owner, companyId, links, default);
  }

  await using (var db = new AppDb(fixture.Options))
  {
   var accounts = new AccountService(db);
   Assert.False(await db.Companies.AnyAsync(x => x.Id == companyId));
   Assert.Equal(otherCompanyId, (await accounts.Me(owner)).Company!.Id);
   Assert.Empty((await accounts.Me(alice)).Companies!);
   var gifts = new GiftService(db);
   var wish = Assert.Single(await gifts.Mine(owner), x => x.Id == giftId);
   Assert.Equal(otherCompanyId, Assert.Single(wish.Visibility!.CompanyIds));
   Assert.Equal(2, Assert.Single(await gifts.Giving(alice), x => x.Id == giftId).Participation!.Taken);
   Assert.False(await JoinInvite(carol, inviteToken));
   var links = new WishlistLinks(db);
   Assert.NotNull(Assert.Single(await links.List(owner, default), x => x.Id == linkId).Revoked);
   Assert.Empty(await links.ForGuest(carol, default));
   await Assert.ThrowsAsync<RuleException>(() => links.View(linkToken, default));
  }
 }

 [Fact]
 public async Task UnlimitedGiftSupportsConcurrentIndependentReservations()
 {
  var (owner, alice, bob, carol, _) = await Setup();
  await using var db = new AppDb(fixture.Options);
  var service = new GiftService(db);
  var gift = await service.Create(owner, new GiftWriteInput("Цветы", Unlimited: true));

  Assert.All(await Task.WhenAll(Try(alice, gift.Id, "reserve"), Try(bob, gift.Id, "reserve")),
   reserved => Assert.True(reserved));
  Assert.False(await Try(alice, gift.Id, "reserve"));
  Assert.Equal("free", Assert.Single(await service.List(carol), card => card.Id == gift.Id).Participation!.Mode);
  Assert.Null(Assert.Single(await service.Mine(owner), card => card.Id == gift.Id).Participation);

  await service.Change(alice, gift.Id, action: new("buy"));
  await service.Change(bob, gift.Id, action: new("cancel"));
  Assert.True(Assert.Single(await service.Giving(alice)).Participation!.Bought);
  Assert.Empty(await service.Giving(bob));
  Assert.True(await Try(carol, gift.Id, "reserve"));
 }

 async Task<(Guid owner,Guid a,Guid b,Guid c,Guid gift)> Setup(){
  await using var db=new AppDb(fixture.Options);var company=new Company{Name="Друзья",InviteHash=Guid.NewGuid().ToString()};db.Companies.Add(company);await db.SaveChangesAsync();
  var users=Enumerable.Range(0,4).Select(_=>new AppUser{Name="Друг",TelegramId=Random.Shared.NextInt64(1,long.MaxValue),CompanyId=company.Id,Contact="@friend"}).ToArray();db.Users.AddRange(users);db.Memberships.AddRange(users.Select(u=>new CompanyMembership{UserId=u.Id,CompanyId=company.Id}));await db.SaveChangesAsync();
  var g=Gift.New(company.Id,users[0].Id,new("Кофемолка",Price:12000));db.Gifts.Add(g);await db.SaveChangesAsync();return(users[0].Id,users[1].Id,users[2].Id,users[3].Id,g.Id);
 }
 async Task<bool> Try(Guid user,Guid gift,string action,int? slots=null){await using var db=new AppDb(fixture.Options);try{await new GiftService(db).Change(user,gift,action:new(action,slots));return true;}catch(RuleException e)when(e.Status==409){return false;}}
 async Task<(Guid owner,Guid a,Guid b,Guid c)> InviteUsers(){await using var db=new AppDb(fixture.Options);var users=Enumerable.Range(0,4).Select(_=>new AppUser{Name="Участник",TelegramId=Random.Shared.NextInt64(1,long.MaxValue)}).ToArray();db.Users.AddRange(users);await db.SaveChangesAsync();await new AccountService(db).Create(users[0].Id,"Компания");return(users[0].Id,users[1].Id,users[2].Id,users[3].Id);}
 async Task<bool> JoinInvite(Guid user,string token){await using var db=new AppDb(fixture.Options);try{await new AccountService(db).Join(user,token);return true;}catch(RuleException){return false;}}
 [Fact] public async Task NewCompanyInitialInviteIsSingleUse(){await using var db=new AppDb(fixture.Options);var users=Enumerable.Range(0,3).Select(_=>new AppUser{Name="Участник",TelegramId=Random.Shared.NextInt64(1,long.MaxValue)}).ToArray();db.Users.AddRange(users);await db.SaveChangesAsync();var invite=await new AccountService(db).Create(users[0].Id,"Компания");Assert.True(await JoinInvite(users[1].Id,invite.Token));Assert.False(await JoinInvite(users[2].Id,invite.Token));}
 [Fact] public async Task ExistingUnrestrictedInviteRemainsUsable(){var token=Guid.NewGuid().ToString();await using var db=new AppDb(fixture.Options);var company=new Company{Name="Компания",InviteHash=AccountService.Hash(token)};var users=Enumerable.Range(0,2).Select(_=>new AppUser{Name="Участник",TelegramId=Random.Shared.NextInt64(1,long.MaxValue)}).ToArray();db.Companies.Add(company);db.Users.AddRange(users);await db.SaveChangesAsync();Assert.True(await JoinInvite(users[0].Id,token));Assert.True(await JoinInvite(users[1].Id,token));}
 [Fact] public async Task SingleUseInviteAllowsOnlyOneConcurrentNewMember(){var u=await InviteUsers();string token;await using(var db=new AppDb(fixture.Options)){token=(await new AccountService(db).Invite(u.owner,1)).Token;}var results=await Task.WhenAll(JoinInvite(u.a,token),JoinInvite(u.b,token));Assert.Single(results,x=>x);var winner=results[0]?u.a:u.b;Assert.True(await JoinInvite(winner,token));}
 [Fact] public async Task LimitedInviteCountsOnlySuccessfulNewMembers(){var u=await InviteUsers();string token;await using(var db=new AppDb(fixture.Options)){token=(await new AccountService(db).Invite(u.owner,2)).Token;}Assert.True(await JoinInvite(u.a,token));Assert.True(await JoinInvite(u.a,token));Assert.True(await JoinInvite(u.b,token));Assert.False(await JoinInvite(u.c,token));}
 [Fact] public async Task UnlimitedInviteCanBeUsedAndRotationInvalidatesOldLink(){var u=await InviteUsers();string oldToken,newToken;await using(var db=new AppDb(fixture.Options)){var svc=new AccountService(db);oldToken=(await svc.Invite(u.owner,null)).Token;newToken=(await svc.Invite(u.owner,null)).Token;}Assert.False(await JoinInvite(u.a,oldToken));Assert.True(await JoinInvite(u.a,newToken));Assert.True(await JoinInvite(u.b,newToken));Assert.True(await JoinInvite(u.c,newToken));}
 [Theory][InlineData(0)][InlineData(-1)][InlineData(101)] public async Task InvalidInviteLimitIsRejected(int limit){var u=await InviteUsers();await using var db=new AppDb(fixture.Options);await Assert.ThrowsAsync<RuleException>(()=>new AccountService(db).Invite(u.owner,limit));}
 [Fact] public async Task ConcurrentSoloReservationsHaveExactlyOneWinner(){var s=await Setup();var results=await Task.WhenAll(Try(s.a,s.gift,"reserve"),Try(s.b,s.gift,"reserve"));Assert.Single(results,x=>x);}
 [Fact]
 public async Task ConcurrentReservationsForDifferentVariantsHaveOneWinner()
 {
  var (owner, alice, bob, _, _) = await Setup();
  await using var db = new AppDb(fixture.Options);
  var gift = await new GiftService(db).Create(owner, new GiftWriteInput("Mouse",
   Variants: [new(null, "G305"), new(null, "Basilisk")]));

  var winners = await Task.WhenAll(Reserve(alice, gift.Variants![0]), Reserve(bob, gift.Variants[1]));
  Assert.Single(winners, won => won);

  async Task<bool> Reserve(Guid donor, GiftVariantDto variant)
  {
   await using var context = new AppDb(fixture.Options);
   try
   {
    var result = await new GiftService(context).Change(donor, gift.Id,
     action: new("reserve", VariantId: variant.Id, VariantsVersion: gift.VariantsVersion));
    Assert.Equal(variant, result.SelectedVariant);
    return true;
   }
   catch (RuleException error) when (error.Status == 409)
   {
    return false;
   }
  }
 }
 [Fact] public async Task OpenSplitAcceptsConcurrentParticipants(){var s=await Setup();await Try(s.a,s.gift,"split",2);var results=await Task.WhenAll(Try(s.b,s.gift,"join"),Try(s.c,s.gift,"join"));Assert.All(results,Assert.True);await using var db=new AppDb(fixture.Options);Assert.Equal(3,await db.Shares.CountAsync(x=>x.GiftId==s.gift));}
 [Fact] public async Task ConcurrentSoloAndSplitAreMutuallyExclusive(){var s=await Setup();var r=await Task.WhenAll(Try(s.a,s.gift,"reserve"),Try(s.b,s.gift,"split",2));Assert.Single(r,x=>x);}
 [Fact] public async Task ForeignCompanyCannotReadOrMutateGift(){var s=await Setup();await using var db=new AppDb(fixture.Options);var company=new Company{Name="Другие",InviteHash=Guid.NewGuid().ToString()};db.Companies.Add(company);await db.SaveChangesAsync();var u=new AppUser{Name="Чужой",CompanyId=company.Id,TelegramId=Random.Shared.NextInt64(1,long.MaxValue)};db.Users.Add(u);db.Memberships.Add(new(){UserId=u.Id,CompanyId=company.Id});await db.SaveChangesAsync();var svc=new GiftService(db);Assert.Empty(await svc.List(u.Id));var ex=await Assert.ThrowsAsync<RuleException>(()=>svc.Change(u.Id,s.gift,action:new("reserve")));Assert.Equal(404,ex.Status);}
 [Fact] public async Task OwnerIsNeverNotifiedOfReservations(){var s=await Setup();await Try(s.a,s.gift,"split",2);await Try(s.b,s.gift,"join");await using var db=new AppDb(fixture.Options);Assert.False(await db.Notices.AnyAsync(x=>x.UserId==s.owner));Assert.True(await db.Notices.AnyAsync(x=>x.UserId==s.a));}
 [Fact] public async Task EditingAndArchivingNotifyDonorsAndReleaseReservation(){var s=await Setup();await Try(s.a,s.gift,"reserve");await using(var db=new AppDb(fixture.Options)){var svc=new GiftService(db);await svc.Change(s.owner,s.gift,input:new("Другая модель",Occasion:"Новый год"));await svc.Change(s.owner,s.gift,archive:true);}
  await using var read=new AppDb(fixture.Options);Assert.Empty(await new GiftService(read).List(s.a));Assert.Equal(2,await read.Notices.CountAsync(x=>x.UserId==s.a));}
 [Fact] public async Task CoordinatorCancellationPromotesRemainingParticipant(){var s=await Setup();await Try(s.a,s.gift,"split",2);await Try(s.b,s.gift,"join");await Try(s.a,s.gift,"cancel");await using var db=new AppDb(fixture.Options);Assert.Equal(s.b,await db.Gifts.Where(x=>x.Id==s.gift).Select(x=>x.DonorId).SingleAsync());Assert.Single(await db.Shares.Where(x=>x.GiftId==s.gift).ToArrayAsync());Assert.Contains(await db.Notices.Where(x=>x.UserId==s.b).Select(x=>x.Text).ToListAsync(),x=>x.Contains("Координатор вышел"));}

 [Fact] public async Task ClosingAndJoiningSerializeWithoutLosingParticipants()
 {
  var s=await Setup();await Try(s.a,s.gift,"split");await Try(s.b,s.gift,"join");
  await Task.WhenAll(Try(s.a,s.gift,"close-split"),Try(s.c,s.gift,"join"));
  await using var db=new AppDb(fixture.Options);
  var gift=await db.Gifts.Include(x=>x.Shares).SingleAsync(x=>x.Id==s.gift);
  Assert.False(gift.SplitOpen);Assert.Equal(gift.Shares.Count,gift.Slots);
  Assert.Equal(s.a,gift.DonorId);Assert.InRange(gift.Shares.Count,2,3);
 }
 [Fact] public async Task ConcurrentLastDeparturesReleaseGift()
 {
  var s=await Setup();await Try(s.a,s.gift,"split");await Try(s.b,s.gift,"join");
  Assert.All(await Task.WhenAll(Try(s.a,s.gift,"leave"),Try(s.b,s.gift,"leave")),Assert.True);
  await using var db=new AppDb(fixture.Options);
  var gift=await db.Gifts.Include(x=>x.Shares).SingleAsync(x=>x.Id==s.gift);
  Assert.Equal("free",gift.Mode);Assert.Null(gift.DonorId);Assert.Empty(gift.Shares);
 }
}
