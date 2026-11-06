using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;
namespace Wishlist.Server.Services;
public class AccountService(AppDb db) {
 public static string Hash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
 static string Token()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
 public async Task<AppUser> Login(TelegramIdentity identity) {
  await using var tx=await db.Database.BeginTransactionAsync();
  await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({identity.Id})");
  var user=await db.Users.SingleOrDefaultAsync(x=>x.TelegramId==identity.Id);
  if(user==null){user=new(){TelegramId=identity.Id,Name=identity.Name,Username=identity.Username};db.Users.Add(user);}else user.Username=identity.Username;
  await db.SaveChangesAsync();await tx.CommitAsync();return user;
 }
 public async Task<MeDto> Me(Guid id){
  var u=await db.Users.SingleOrDefaultAsync(x=>x.Id==id)??throw new RuleException("Войдите снова.",401);
  CompanyDto? company=null;
  if(u.CompanyId is {} cid){var c=await db.Companies.SingleAsync(x=>x.Id==cid);var members=await db.Users.Where(x=>x.CompanyId==cid).OrderBy(x=>x.Name).Select(x=>new PersonDto(x.Id,x.Name,null)).ToArrayAsync();company=new(c.Id,c.Name,c.OrganizerId==id,members);}
  return new(new(u.Id,u.Name,u.Contact??(u.Username!=null?"@"+u.Username:null)),company);
 }
 public async Task<InviteDto> Create(Guid id,string name){
  if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>100)throw new RuleException("Название компании: от 1 до 100 символов.",400);
  await using var tx=await db.Database.BeginTransactionAsync();var u=await LockedUser(id);
  if(u.CompanyId!=null)throw new RuleException("Вы уже в компании.");
  var token=Token();var c=new Company{Name=name.Trim(),OrganizerId=id,InviteHash=Hash(token),InviteRemaining=1};db.Companies.Add(c);await db.SaveChangesAsync();u.CompanyId=c.Id;await db.SaveChangesAsync();await tx.CommitAsync();return new(token);
 }
 public async Task Join(Guid id,string token){
  if(string.IsNullOrWhiteSpace(token)||token.Length>128)throw new RuleException("Неверное приглашение.",400);
  await using var tx=await db.Database.BeginTransactionAsync();var u=await LockedUser(id);var hash=Hash(token);var c=await db.Companies.FromSqlInterpolated($"SELECT * FROM \"Companies\" WHERE \"InviteHash\"={hash} FOR UPDATE").SingleOrDefaultAsync()??throw new RuleException("Приглашение недействительно.",404);
  if(u.CompanyId==c.Id){await tx.CommitAsync();return;}if(u.CompanyId!=null)throw new RuleException("Вы уже в другой компании.");
  if(c.InviteRemaining is <=0)throw new RuleException("Лимит приглашения исчерпан. Попросите организатора создать новую ссылку.",409);
  u.CompanyId=c.Id;if(c.InviteRemaining is {} remaining)c.InviteRemaining=remaining-1;await db.SaveChangesAsync();await tx.CommitAsync();
 }
 public async Task<InviteDto> Invite(Guid id,int? maxUses){if(maxUses is <1 or >100)throw new RuleException("Количество вступлений: от 1 до 100.",400);await using var tx=await db.Database.BeginTransactionAsync();var c=await db.Companies.FromSqlInterpolated($"SELECT * FROM \"Companies\" WHERE \"OrganizerId\"={id} FOR UPDATE").SingleOrDefaultAsync()??throw new RuleException("Приглашать может организатор.",403);var token=Token();c.InviteHash=Hash(token);c.InviteRemaining=maxUses;await db.SaveChangesAsync();await tx.CommitAsync();return new(token);}
 public async Task Profile(Guid id,ProfileInput input){if(string.IsNullOrWhiteSpace(input.Name)||input.Name.Trim().Length>100||(input.Contact?.Length??0)>200)throw new RuleException("Проверьте имя и контакт (до 200 символов).",400);var u=await db.Users.SingleAsync(x=>x.Id==id);u.Name=input.Name.Trim();u.Contact=string.IsNullOrWhiteSpace(input.Contact)?null:input.Contact.Trim();await db.SaveChangesAsync();}
 async Task<AppUser> LockedUser(Guid id)=>await db.Users.FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"Id\"={id} FOR UPDATE").SingleAsync();
}
