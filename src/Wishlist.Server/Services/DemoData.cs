using Microsoft.EntityFrameworkCore;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;
namespace Wishlist.Server.Services;
public static class DemoData {
 public static readonly Guid[] Users=Enumerable.Range(1,4).Select(i=>Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}")).ToArray();
 public static async Task Seed(AppDb db){
  if(await db.Users.AnyAsync())return;
  var company=new Company{Name="Близкие люди",OrganizerId=Users[0],InviteHash=AccountService.Hash("demo-friends")};db.Companies.Add(company);await db.SaveChangesAsync();
  var names=new[]{"Юлия","Макс","Аня","Саша"};db.Users.AddRange(Users.Select((id,i)=>new AppUser{Id=id,TelegramId=9000000000L+i,Name=names[i],Contact="Напишите мне в общем чате",CompanyId=company.Id}));await db.SaveChangesAsync();
  var gifts=new[]{
   Gift.New(company.Id,Users[0],new("Кофемолка Timemore C3","https://www.timemore.com/",8500,"Чтобы утро начиналось с хорошего кофе ☕","День рождения",new(2026,12,12))),
   Gift.New(company.Id,Users[0],new("Сертификат на керамику",Price:4000,Comment:"Давно хочу попробовать гончарный круг",Occasion:"Новый год")),
   Gift.New(company.Id,Users[1],new("Наушники Sony WH-1000XM5","https://www.sony.com/",29000,"Чёрные или серебристые — оба нравятся","День рождения")),
   Gift.New(company.Id,Users[1],new("Настольная игра «Крылья»",Price:6500,Comment:"Будем играть всей компанией",Occasion:"Новый год")),
   Gift.New(company.Id,Users[2],new("Instax Mini 12","https://instax.com/",11000,"Лавандовый 💜 И немного плёнки", "Новый год")),
   Gift.New(company.Id,Users[2],new("Книга о современной архитектуре",Price:3500,Comment:"Буду рада вашему выбору")),
   Gift.New(company.Id,Users[3],new("Рюкзак для коротких поездок",Price:9000,Comment:"20–25 литров, без ярких логотипов",Occasion:"День рождения")),
   Gift.New(company.Id,Users[3],new("Билет на концерт",Price:5000,Comment:"Лучше пойдём вместе!",Occasion:"Новый год"))};
  gifts[2].Act(Users[2],new("split",3),3,true);gifts[2].Act(Users[3],new("join"),3,true);gifts[3].Act(Users[0],new("reserve"),3,true);db.Gifts.AddRange(gifts);await db.SaveChangesAsync();
 }
}
