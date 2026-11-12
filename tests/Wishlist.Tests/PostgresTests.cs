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

 [Fact]
 public async Task SoloReservationPreventsChangingGiftToUnlimited()
 {
  var s = await Setup();
  await Try(s.a, s.gift, "reserve");

  await using (var db = new AppDb(fixture.Options))
   await Assert.ThrowsAsync<RuleException>(() => new GiftService(db).Change(s.owner, s.gift,
    input: new GiftWriteInput("Кофемолка", Unlimited: true)));

  await using var read = new AppDb(fixture.Options);
  var gift = Assert.Single(await new GiftService(read).Mine(s.owner));
  Assert.False(gift.Unlimited);
  Assert.Equal("solo", gift.Participation?.Mode);
 }

 [Fact]
 public async Task SplitParticipantsPreventChangingGiftToUnlimited()
 {
  var s = await Setup();
  await Try(s.a, s.gift, "split", 2);
  await Try(s.b, s.gift, "join");

  await using (var db = new AppDb(fixture.Options))
   await Assert.ThrowsAsync<RuleException>(() => new GiftService(db).Change(s.owner, s.gift,
    input: new GiftWriteInput("Кофемолка", Unlimited: true)));

  await using var read = new AppDb(fixture.Options);
  var gift = Assert.Single(await new GiftService(read).Mine(s.owner));
  Assert.False(gift.Unlimited);
  Assert.Equal("split", gift.Participation?.Mode);
  Assert.Equal(2, Assert.Single(await new GiftService(read).Giving(s.a)).Participation!.Participants!.Length);
 }

 [Fact]
 public async Task IndependentUnlimitedReservationsPreventChangingGiftToOrdinary()
 {
  var (owner, alice, bob, _, _) = await Setup();
  Guid giftId;
  await using (var db = new AppDb(fixture.Options))
  {
   var gift = await new GiftService(db).Create(owner, new GiftWriteInput("Цветы", Unlimited: true));
   giftId = gift.Id;
  }
  await Try(alice, giftId, "reserve");
  await Try(bob, giftId, "reserve");

  await using (var db = new AppDb(fixture.Options))
   await Assert.ThrowsAsync<RuleException>(() => new GiftService(db).Change(owner, giftId,
    input: new GiftWriteInput("Цветы", Unlimited: false)));

  await using (var db = new AppDb(fixture.Options))
   await new GiftService(db).Change(owner, giftId,
    input: new GiftWriteInput("Цветы обновлённые", Unlimited: true));

  await using var read = new AppDb(fixture.Options);
  var service = new GiftService(read);
  Assert.True(Assert.Single(await service.Giving(alice)).Unlimited);
  Assert.Equal("Цветы обновлённые", Assert.Single(await service.Giving(alice)).Details.Title);
  Assert.True(Assert.Single(await service.Giving(bob)).Unlimited);
 }

 [Fact]
 public async Task BoughtGiftCannotChangeUnlimitedType()
 {
  var s = await Setup();
  await Try(s.a, s.gift, "reserve");
  await using (var db = new AppDb(fixture.Options))
   await new GiftService(db).Change(s.a, s.gift, action: new("buy"));

  await using (var db = new AppDb(fixture.Options))
   await Assert.ThrowsAsync<RuleException>(() => new GiftService(db).Change(s.owner, s.gift,
    input: new GiftWriteInput("Кофемолка", Unlimited: true)));

  await using var read = new AppDb(fixture.Options);
  Assert.False(Assert.Single(await new GiftService(read).Giving(s.a)).Unlimited);
  Assert.True(Assert.Single(await new GiftService(read).Giving(s.a)).Participation!.Bought);
 }

 [Fact]
 public async Task ArchivedGiftCannotChangeUnlimitedType()
 {
  var s = await Setup();
  await Try(s.a, s.gift, "reserve");
  await using (var db = new AppDb(fixture.Options))
   await new GiftService(db).Change(s.owner, s.gift, archive: true);

  await using (var db = new AppDb(fixture.Options))
   await Assert.ThrowsAsync<RuleException>(() => new GiftService(db).Change(s.owner, s.gift,
    input: new GiftWriteInput("Кофемолка", Unlimited: true)));

  await using var read = new AppDb(fixture.Options);
  var gift = Assert.Single(await new GiftService(read).Giving(s.a));
  Assert.False(gift.Unlimited);
  Assert.True(gift.Archived);
  Assert.Equal("solo", gift.Participation!.Mode);
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
 [Fact] public async Task LastSplitSlotHasExactlyOneWinner(){var s=await Setup();await Try(s.a,s.gift,"split",2);var results=await Task.WhenAll(Try(s.b,s.gift,"join"),Try(s.c,s.gift,"join"));Assert.Single(results,x=>x);await using var db=new AppDb(fixture.Options);Assert.Equal(2,await db.Shares.CountAsync(x=>x.GiftId==s.gift));}
 [Fact] public async Task ConcurrentSoloAndSplitAreMutuallyExclusive(){var s=await Setup();var r=await Task.WhenAll(Try(s.a,s.gift,"reserve"),Try(s.b,s.gift,"split",2));Assert.Single(r,x=>x);}
 [Fact] public async Task ForeignCompanyCannotReadOrMutateGift(){var s=await Setup();await using var db=new AppDb(fixture.Options);var company=new Company{Name="Другие",InviteHash=Guid.NewGuid().ToString()};db.Companies.Add(company);await db.SaveChangesAsync();var u=new AppUser{Name="Чужой",CompanyId=company.Id,TelegramId=Random.Shared.NextInt64(1,long.MaxValue)};db.Users.Add(u);db.Memberships.Add(new(){UserId=u.Id,CompanyId=company.Id});await db.SaveChangesAsync();var svc=new GiftService(db);Assert.Empty(await svc.List(u.Id));var ex=await Assert.ThrowsAsync<RuleException>(()=>svc.Change(u.Id,s.gift,action:new("reserve")));Assert.Equal(404,ex.Status);}
 [Fact] public async Task OwnerIsNeverNotifiedOfReservations(){var s=await Setup();await Try(s.a,s.gift,"split",2);await Try(s.b,s.gift,"join");await using var db=new AppDb(fixture.Options);Assert.False(await db.Notices.AnyAsync(x=>x.UserId==s.owner));Assert.True(await db.Notices.AnyAsync(x=>x.UserId==s.a));}
 [Fact] public async Task EditingAndArchivingNotifyDonorsAndPreserveHistory(){var s=await Setup();await Try(s.a,s.gift,"reserve");await using(var db=new AppDb(fixture.Options)){var svc=new GiftService(db);await svc.Change(s.owner,s.gift,input:new("Другая модель",Occasion:"Новый год"));await svc.Change(s.owner,s.gift,archive:true);}
  await using var read=new AppDb(fixture.Options);var dto=Assert.Single(await new GiftService(read).List(s.a));Assert.True(dto.Archived);Assert.Equal("Кофемолка",dto.Original!.Title);Assert.Equal(2,await read.Notices.CountAsync(x=>x.UserId==s.a));}
 [Fact] public async Task CancellationDeletesSharesAndNotifiesFormerParticipants(){var s=await Setup();await Try(s.a,s.gift,"split",2);await Try(s.b,s.gift,"join");await Try(s.a,s.gift,"cancel");await using var db=new AppDb(fixture.Options);Assert.False(await db.Shares.AnyAsync(x=>x.GiftId==s.gift));Assert.Contains(await db.Notices.Where(x=>x.UserId==s.b).Select(x=>x.Text).ToListAsync(),x=>x.Contains("отменены"));}
}
