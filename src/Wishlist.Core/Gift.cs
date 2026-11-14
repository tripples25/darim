using System.Text.Json;
using Wishlist.Contracts;
namespace Wishlist.Core;
public class RuleException(string message, int status = 409) : Exception(message) { public int Status { get; } = status; }
public class GiftShare
{
 public Guid GiftId { get; set; }
 public Guid UserId { get; set; }
 public int JoinOrder { get; set; }
 public bool Bought { get; set; }
 public string? OriginalJson { get; set; }
}
public class GiftPublication { public Guid GiftId { get; set; } public Guid CompanyId { get; set; } }
public class Gift {
 public Guid Id {get;set;} = Guid.NewGuid(); public Guid? CompanyId {get;set;} public Guid OwnerId {get;set;}
 public bool AllCompanies {get;set;}
 public bool Unlimited {get;set;}
 public List<GiftPublication> Publications {get;set;} = [];
 public string Title {get;set;} = ""; public string? Url {get;set;} public decimal? Price {get;set;} public string? Comment {get;set;}
 public string Occasion {get;set;} = "Без повода"; public DateOnly? OccasionDate {get;set;} public bool Archived {get;set;}
 public string Mode {get;set;} = "free"; public Guid? DonorId {get;set;} public int Slots {get;set;} public bool Bought {get;set;}
 public bool SplitOpen {get;set;}
 public string? OriginalJson {get;set;} public DateTimeOffset Created {get;set;} = DateTimeOffset.UtcNow;
 public string? OhmywishesKey {get;set;}
 public string? OhmywishesWebId {get;set;}
 public bool OhmywishesOccupied {get;set;}
 public string? OhmywishesReservationSource {get;set;}
 public DateTimeOffset? OhmywishesReservationChanged {get;set;}
 public bool OhmywishesHidden {get;set;}
 public bool OhmywishesDetailsEdited {get;set;}
 public bool OhmywishesPhotoEdited {get;set;}
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
 public void Restore(Guid userId) {Require(userId==OwnerId,"Вернуть желание из архива может только владелец.",403);Archived=false;}
 public void Act(Guid userId, GiftActionInput input, int otherMembers, bool hasContact) {
  Require(userId!=OwnerId,"Нельзя выбрать собственный подарок.",403);
  Require(!Archived||input.Action=="cancel","Желание уже в архиве.");Require(!Bought,"Подарок уже куплен.");
  if (Unlimited)
  {
   ActOnUnlimitedGift(userId, input.Action);
   return;
  }
  switch(input.Action) {
   case "reserve": Require(Mode=="free","Подарок уже занят.");Snapshot();Mode="solo";DonorId=userId;break;
   case "split":
    Require(Mode=="free","Подарок уже занят.");Require(hasContact,"Укажите контакт в профиле перед участием в распиле.",400);
    Require(otherMembers>=2,"Для распила нужны хотя бы два дарителя.",400);
    Snapshot();Mode="split";CompanyId=input.CompanyId;Slots=2;SplitOpen=true;DonorId=userId;Shares.Add(new(){GiftId=Id,UserId=userId,JoinOrder=1});break;
   case "join":
    Require(Mode=="split" && SplitOpen,"Сбор участников завершён.");Require(hasContact,"Укажите контакт в профиле.",400);
    Require(Shares.All(x=>x.UserId!=userId),"Вы уже участвуете.");Shares.Add(new(){GiftId=Id,UserId=userId,JoinOrder=Shares.Max(x=>x.JoinOrder)+1});break;
   case "leave":
    Require(Mode=="split" && Shares.Any(x=>x.UserId==userId),"Вы не участвуете.",403);
    LeaveSplit(userId);break;
   case "close-split":
    Require(Mode=="split" && DonorId==userId,"Сбор завершает координатор.",403);
    Require(SplitOpen && Shares.Count>=2,"Для завершения сбора нужны хотя бы два участника.");
    SplitOpen=false;Slots=Shares.Count;break;
   case "transfer":
    Require(Mode=="split" && DonorId==userId,"Передать координацию может только координатор.",403);
    Require(input.UserId!=userId && Shares.Any(x=>x.UserId==input.UserId),"Выберите другого участника распила.",400);DonorId=input.UserId;break;
   case "cancel":
    Require(Mode!="free" && DonorId==userId,"Отменить может только даритель или координатор.",403);
    if(Mode=="split")LeaveSplit(userId);else Release();break;
   case "buy":
    Require(Mode!="free" && DonorId==userId,"Покупку отмечает даритель или координатор.",403);
    Require(Mode!="split" || !SplitOpen && Shares.Count>=2,"Сначала завершите сбор участников.");Bought=true;break;
   default: throw new RuleException("Неизвестное действие.",400);
  }
 }
 private void Release() {Mode="free";CompanyId=null;DonorId=null;Slots=0;SplitOpen=false;Shares.Clear();OriginalJson=null;}
 private void LeaveSplit(Guid userId)
 {
  Shares.RemoveAll(x=>x.UserId==userId);
  if(Shares.Count==0){Release();return;}
  if(DonorId==userId)DonorId=Shares.OrderBy(x=>x.JoinOrder).ThenBy(x=>x.UserId).First().UserId;
  SplitOpen=true;Slots=Math.Max(2,Shares.Count);
 }
 private void ActOnUnlimitedGift(Guid userId, string action)
 {
  var reservation = Shares.SingleOrDefault(share => share.UserId == userId);
  switch (action)
  {
   case "reserve":
    Require(reservation == null, "Вы уже забронировали этот подарок.");
    Shares.Add(new() { GiftId = Id, UserId = userId, OriginalJson = JsonSerializer.Serialize(Details) });
    break;
   case "buy":
   case "cancel":
    if (reservation == null) throw new RuleException("У вас нет брони на этот подарок.", 403);
    Require(!reservation.Bought, "Подарок уже куплен.");
    if (action == "buy") reservation.Bought = true;
    else Shares.Remove(reservation);
    break;
   default:
    throw new RuleException("Для этого желания доступны отдельные брони.");
  }
 }

 void Snapshot() => OriginalJson=JsonSerializer.Serialize(Details);
 public GiftDto View(Guid viewer, IReadOnlyDictionary<Guid,PersonDto> people) {
  var owner = people[OwnerId] with { Contact = null, AvatarUrl = null };
  if(viewer==OwnerId)return new(Id,owner,Details,Archived,null,FromOhmywishes:OhmywishesKey!=null,
   PhotoVersion:PhotoVersion,
   Visibility:new(AllCompanies,Publications.Select(x=>x.CompanyId).ToArray()), Unlimited:Unlimited);
  if (Unlimited)
  {
   var reservation = Shares.SingleOrDefault(share => share.UserId == viewer);
   var reserved = reservation != null;
   var participation = new ParticipationDto(reserved ? "solo" : "free", reservation?.Bought == true,
    0, 0, reserved, reserved, null);
   var original = reservation?.OriginalJson is { } json ? JsonSerializer.Deserialize<GiftInput>(json) : null;
   return new(Id, owner, Details, Archived, participation, original,
    PhotoVersion: PhotoVersion, Unlimited: true);
  }
  var mine=DonorId==viewer || Shares.Any(x=>x.UserId==viewer);
  var state=new ParticipationDto(Mode,Bought,Slots,Shares.Count,mine,DonorId==viewer,
   Mode=="split" && mine ? Shares.OrderBy(x=>x.JoinOrder).ThenBy(x=>x.UserId).Select(x=>people[x.UserId]).ToArray():null,Mode=="split" && mine?DonorId:null,Mode=="split" && SplitOpen);
  return new(Id,owner,Details,Archived,state,mine && OriginalJson!=null?JsonSerializer.Deserialize<GiftInput>(OriginalJson):null,
   OhmywishesKey!=null,PhotoVersion:PhotoVersion);
 }
 static void Require(bool condition,string message,int status=409) {if(!condition)throw new RuleException(message,status);}
 public Guid[] Donors() => Shares.Select(x=>x.UserId).Concat(DonorId is {} d ? [d] : Array.Empty<Guid>()).Distinct().ToArray();
}
