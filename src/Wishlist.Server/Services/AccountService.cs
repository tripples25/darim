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
  var user=await db.Users.SingleOrDefaultAsync(x=>x.Id==id)??throw new RuleException("Войдите снова.",401);
  var companies=await (from membership in db.Memberships
                       join company in db.Companies on membership.CompanyId equals company.Id
                       where membership.UserId==id
                       orderby company.Name, company.Id
                       select company).ToArrayAsync();
  var ids=companies.Select(x=>x.Id).ToArray();
  var members=await (from membership in db.Memberships
                     join member in db.Users on membership.UserId equals member.Id
                     where ids.Contains(membership.CompanyId)
                     orderby member.Name
                     select new {membership.CompanyId, Person=new PersonDto(member.Id,member.Name,null)}).ToListAsync();
  var byCompany=members.ToLookup(x=>x.CompanyId,x=>x.Person);
  var views=companies.Select(c=>new CompanyDto(c.Id,c.Name,c.OrganizerId==id,byCompany[c.Id].ToArray())).ToArray();
  return new(new(user.Id,user.Name,user.Contact??(user.Username!=null?"@"+user.Username:null)),
   views.FirstOrDefault(c=>c.Id==user.CompanyId)??views.FirstOrDefault(),views);
 }

 public async Task<InviteDto> Create(Guid id,string name){
  if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>100)throw new RuleException("Название компании: от 1 до 100 символов.",400);
  await using var tx=await db.Database.BeginTransactionAsync();
  var user=await LockedUser(id);
  var token=Token();
  var company=new Company{Name=name.Trim(),OrganizerId=id,InviteHash=Hash(token),InviteRemaining=1};
  db.Companies.Add(company);
  db.Memberships.Add(new(){UserId=id,CompanyId=company.Id});
  user.CompanyId??=company.Id;
  await db.SaveChangesAsync();
  await tx.CommitAsync();
  return new(token,company.Id);
 }

 public async Task<Guid> Join(Guid id,string token){
  if(string.IsNullOrWhiteSpace(token)||token.Length>128)throw new RuleException("Неверное приглашение.",400);
  await using var tx=await db.Database.BeginTransactionAsync();
  var user=await LockedUser(id);
  var hash=Hash(token);
  var company=await db.Companies.FromSqlInterpolated($"SELECT * FROM \"Companies\" WHERE \"InviteHash\"={hash} FOR UPDATE")
   .SingleOrDefaultAsync()??throw new RuleException("Приглашение недействительно.",404);
  if(!await db.Memberships.AnyAsync(x=>x.UserId==id&&x.CompanyId==company.Id)){
   if(company.InviteRemaining is <=0)throw new RuleException("Лимит приглашения исчерпан. Попросите организатора создать новую ссылку.",409);
   db.Memberships.Add(new(){UserId=id,CompanyId=company.Id});
   user.CompanyId??=company.Id;
   if(company.InviteRemaining is {} remaining)company.InviteRemaining=remaining-1;
   await db.SaveChangesAsync();
  }
  await tx.CommitAsync();
  return company.Id;
 }

 public async Task<InviteDto> Invite(Guid id,int? maxUses,Guid? companyId=null){
  if(maxUses is <1 or >100)throw new RuleException("Количество вступлений: от 1 до 100.",400);
  var target=await ResolveCompany(id,companyId);
  await using var tx=await db.Database.BeginTransactionAsync();
  var company=await db.Companies.FromSqlInterpolated($"SELECT * FROM \"Companies\" WHERE \"Id\"={target} AND \"OrganizerId\"={id} FOR UPDATE")
   .SingleOrDefaultAsync()??throw new RuleException("Приглашать может организатор.",403);
  var token=Token();
  company.InviteHash=Hash(token);
  company.InviteRemaining=maxUses;
  await db.SaveChangesAsync();
  await tx.CommitAsync();
  return new(token,company.Id);
 }

 internal async Task<Guid> ResolveCompany(Guid userId,Guid? companyId){
  var memberships=db.Memberships.Where(x=>x.UserId==userId);
  if(companyId is {} requested){
   if(await memberships.AnyAsync(x=>x.CompanyId==requested))return requested;
   throw new RuleException("Вы не участник этой компании.",403);
  }
  var candidates=await memberships.OrderBy(x=>x.CompanyId).Select(x=>x.CompanyId).Take(2).ToArrayAsync();
  if(candidates.Length==1)return candidates[0];
  throw new RuleException("Выберите компанию.",403);
 }

 public async Task Profile(Guid id,ProfileInput input){if(string.IsNullOrWhiteSpace(input.Name)||input.Name.Trim().Length>100||(input.Contact?.Length??0)>200)throw new RuleException("Проверьте имя и контакт (до 200 символов).",400);var u=await db.Users.SingleAsync(x=>x.Id==id);u.Name=input.Name.Trim();u.Contact=string.IsNullOrWhiteSpace(input.Contact)?null:input.Contact.Trim();await db.SaveChangesAsync();}
 async Task<AppUser> LockedUser(Guid id)=>await db.Users.FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"Id\"={id} FOR UPDATE").SingleAsync();
}
