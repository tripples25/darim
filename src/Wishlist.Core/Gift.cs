using System.Text.Json;
using Wishlist.Contracts;
namespace Wishlist.Core;
public class RuleException(string message, int status = 409) : Exception(message) { public int Status { get; } = status; }
public class GiftShare { public Guid GiftId { get; set; } public Guid UserId { get; set; } }
public class GiftPublication { public Guid GiftId { get; set; } public Guid CompanyId { get; set; } }
public class Gift {
 public Guid Id {get;set;} = Guid.NewGuid(); public Guid? CompanyId {get;set;} public Guid OwnerId {get;set;}
 public bool AllCompanies {get;set;}
 public List<GiftPublication> Publications {get;set;} = [];
 public string Title {get;set;} = ""; public string? Url {get;set;} public decimal? Price {get;set;} public string? Comment {get;set;}
 public string Occasion {get;set;} = "Без повода"; public DateOnly? OccasionDate {get;set;} public bool Archived {get;set;}
 public string Mode {get;set;} = "free"; public Guid? DonorId {get;set;} public int Slots {get;set;} public bool Bought {get;set;}
 public string? OriginalJson {get;set;} public DateTimeOffset Created {get;set;} = DateTimeOffset.UtcNow;
 public string? OhmywishesKey {get;set;}
 public string? OhmywishesWebId {get;set;}
 public bool OhmywishesOccupied {get;set;}
 public string? OhmywishesReservationSource {get;set;}
 public DateTimeOffset? OhmywishesReservationChanged {get;set;}
 public bool OhmywishesHidden {get;set;}
 public string? OhmywishesPhotoUrl {get;set;}
 public Guid? PhotoVersion {get;set;}
 public List<GiftShare> Shares {get;set;} = [];
 public GiftInput Details => new(Title,Url,Price,Comment,Occasion,OccasionDate);
 public static Gift New(Guid? companyId, Guid ownerId, GiftInput input) {
  var gift=new Gift {OwnerId=ownerId};
  gift.Edit(ownerId,input);
  if(companyId is {} company)gift.Publications.Add(new(){GiftId=gift.Id,CompanyId=company});
  return gift;
 }
 public void Edit(Guid userId, GiftInput input) {
  Require(userId==OwnerId,"Редактировать может только владелец.",403);
  Require(!Archived,"Желание уже в архиве.");
  Require(!string.IsNullOrWhiteSpace(input.Title) && input.Title.Trim().Length<=160,"Название: от 1 до 160 символов.",400);
  Require(input.Price is null or >=0 and <=100000000,"Некорректная цена.",400);
  Require((input.Comment?.Length??0)<=2000 && (input.Url?.Length??0)<=2048,"Слишком длинное описание или ссылка.",400);
  Require(!string.IsNullOrWhiteSpace(input.Occasion) && input.Occasion.Length<=80,"Укажите один повод длиной до 80 символов.",400);
  if(!string.IsNullOrWhiteSpace(input.Url)) Require(Uri.TryCreate(input.Url,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo),"Нужна ссылка http или https.",400);
  Title=input.Title.Trim();Url=string.IsNullOrWhiteSpace(input.Url)?null:input.Url.Trim();Price=input.Price;
  Comment=input.Comment?.Trim();Occasion=input.Occasion.Trim();OccasionDate=input.OccasionDate;
 }
 public void Archive(Guid userId) {Require(userId==OwnerId,"Архивировать может только владелец.",403);Archived=true;}
 public void Act(Guid userId, GiftActionInput input, int otherMembers, bool hasContact) {
  Require(userId!=OwnerId,"Нельзя выбрать собственный подарок.",403);
  Require(!Archived||input.Action=="cancel","Желание уже в архиве.");Require(!Bought,"Подарок уже куплен.");
  switch(input.Action) {
   case "reserve": Require(Mode=="free","Подарок уже занят.");Snapshot();Mode="solo";DonorId=userId;break;
   case "split":
    Require(Mode=="free","Подарок уже занят.");Require(hasContact,"Укажите контакт в профиле перед участием в распиле.",400);
    Require(input.Slots is >=2 && input.Slots<=otherMembers,"Число долей: от 2 до числа остальных участников.",400);
    Snapshot();Mode="split";CompanyId=input.CompanyId;Slots=input.Slots.GetValueOrDefault();DonorId=userId;Shares.Add(new(){GiftId=Id,UserId=userId});break;
   case "join":
    Require(Mode=="split" && Shares.Count<Slots,"Свободных долей нет.");Require(hasContact,"Укажите контакт в профиле.",400);
    Require(Shares.All(x=>x.UserId!=userId),"Вы уже участвуете.");Shares.Add(new(){GiftId=Id,UserId=userId});break;
   case "leave":
    Require(Mode=="split" && Shares.Any(x=>x.UserId==userId),"Вы не участвуете.",403);
    Require(DonorId!=userId,"Передайте координацию или отмените распил.");Shares.RemoveAll(x=>x.UserId==userId);break;
   case "transfer":
    Require(Mode=="split" && DonorId==userId,"Передать координацию может только координатор.",403);
    Require(input.UserId!=userId && Shares.Any(x=>x.UserId==input.UserId),"Выберите другого участника распила.",400);DonorId=input.UserId;break;
   case "cancel":
    Require(Mode!="free" && DonorId==userId,"Отменить может только даритель или координатор.",403);
    Mode="free";CompanyId=null;DonorId=null;Slots=0;Shares.Clear();OriginalJson=null;break;
   case "buy":
    Require(Mode!="free" && DonorId==userId,"Покупку отмечает даритель или координатор.",403);
    Require(Mode!="split" || Shares.Count==Slots,"Сначала должны быть заняты все доли.");Bought=true;break;
   default: throw new RuleException("Неизвестное действие.",400);
  }
 }
 void Snapshot() => OriginalJson=JsonSerializer.Serialize(Details);
 public GiftDto View(Guid viewer, IReadOnlyDictionary<Guid,PersonDto> people) {
  if(viewer==OwnerId)return new(Id,people[OwnerId] with {Contact=null},Details,Archived,null,FromOhmywishes:OhmywishesKey!=null,
   PhotoUrl:OhmywishesPhotoUrl,PhotoVersion:PhotoVersion,
   Visibility:new(AllCompanies,Publications.Select(x=>x.CompanyId).ToArray()));
  var mine=DonorId==viewer || Shares.Any(x=>x.UserId==viewer);
  var state=new ParticipationDto(Mode,Bought,Slots,Shares.Count,mine,DonorId==viewer,
   Mode=="split" && mine ? Shares.Select(x=>people[x.UserId]).ToArray():null,Mode=="split" && mine?DonorId:null);
  return new(Id,people[OwnerId] with {Contact=null},Details,Archived,state,mine && OriginalJson!=null?JsonSerializer.Deserialize<GiftInput>(OriginalJson):null,
   OhmywishesKey!=null,OhmywishesPhotoUrl,PhotoVersion);
 }
 static void Require(bool condition,string message,int status=409) {if(!condition)throw new RuleException(message,status);}
 public Guid[] Donors() => Shares.Select(x=>x.UserId).Concat(DonorId is {} d ? [d] : Array.Empty<Guid>()).Distinct().ToArray();
}
