using Microsoft.EntityFrameworkCore;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;
namespace Wishlist.Server.Services;
public class GiftService(AppDb db) {
 public async Task<GiftDto[]> List(Guid userId) {
  var user=await User(userId);var people=await People(user.CompanyId!.Value);
  var gifts=await db.Gifts.AsNoTracking().Where(x=>x.CompanyId==user.CompanyId).Include(x=>x.Shares).OrderByDescending(x=>x.Created).ToListAsync();
  return gifts.Where(g=>!g.Archived || g.OwnerId==userId || g.Donors().Contains(userId)).Select(g=>g.View(userId,people)).ToArray();
 }
 public async Task<GiftDto> Create(Guid id,GiftInput input) {
  var u=await User(id);var g=Gift.New(u.CompanyId!.Value,id,input);db.Gifts.Add(g);await db.SaveChangesAsync();return g.View(id,await People(u.CompanyId.Value));
 }
 public async Task<GiftDto> Change(Guid id,Guid giftId,GiftInput? input=null,GiftActionInput? action=null,bool archive=false) {
  var u=await User(id);await using var tx=await db.Database.BeginTransactionAsync();
  // Every gift mutation takes the same row lock before loading shares: no stale split counts.
  var g=await db.Gifts.FromSqlInterpolated($"SELECT * FROM \"Gifts\" WHERE \"Id\"={giftId} AND \"CompanyId\"={u.CompanyId} FOR UPDATE").SingleOrDefaultAsync() ?? throw new RuleException("Подарок не найден.",404);
  await db.Entry(g).Collection(x=>x.Shares).LoadAsync();var before=g.Donors();
  var people=await People(u.CompanyId!.Value);
  if(input!=null)g.Edit(id,input);
  else if(archive)g.Archive(id);
  else if(action!=null)g.Act(id,action,people.Count-1,!string.IsNullOrWhiteSpace(u.Contact) || !string.IsNullOrWhiteSpace(u.Username));
  var recipients=before.Concat(g.Donors()).Distinct().Where(x=>x!=id && x!=g.OwnerId);
  var verb=input!=null?"Желание изменено":archive?"Желание отправлено в архив":action?.Action switch {"cancel"=>"Бронь или распил отменены","buy"=>"Подарок отмечен купленным","leave"=>"Участник вышел из распила","transfer"=>"Координатор распила изменён",_=>"Состав распила обновлён"};
  foreach(var recipient in recipients)db.Notices.Add(new(){UserId=recipient,Text=$"{verb}: «{g.Title}»."});
  await db.SaveChangesAsync();await tx.CommitAsync();return g.View(id,people);
 }
 async Task<AppUser> User(Guid id) {var u=await db.Users.SingleOrDefaultAsync(x=>x.Id==id)??throw new RuleException("Войдите снова.",401);if(u.CompanyId==null)throw new RuleException("Сначала вступите в компанию.",403);return u;}
 async Task<Dictionary<Guid,PersonDto>> People(Guid company) => await db.Users.Where(x=>x.CompanyId==company).ToDictionaryAsync(x=>x.Id,x=>new PersonDto(x.Id,x.Name,x.Contact??(x.Username!=null?"@"+x.Username:null)));
}
