using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Wishlist.Contracts;
namespace Wishlist.Client.Pages;
public partial class Home {
 bool ready,busy,showArchived,filtersExpanded;string? error,dialog,inviteLink,miniInviteLink;string tab="all",ownerFilter="",search="",occasion="",availability="",priceFilter="";
 AuthInfoDto? info;MeDto? me;GiftDto[] gifts=[];NoticeDto[] notices=[];GiftDto? selected;
 OhmywishesStatusDto? ohmywishes;OhmywishesListDto[]? ohmywishesLists;string ohmywishesSelection="";
 bool ohmywishesLoading;string? ohmywishesError;
 string ohmywishesWebLink="";
 string editTitle="",editUrl="",editComment="",editOccasion="Без повода",editCustomOccasion="";decimal? editPrice;DateTime? editDate;
 byte[]? editPhoto;
 string? editPhotoPreview,editPhotoError;
 bool removeEditPhoto,readingEditPhoto;
 int editorRevision,photoPickerKey;
 string profileName="",profileContact="",companyName="",joinToken="",transferTo="",confirmAction="",inviteLimitMode="one";int splitSlots=2,inviteLimit=2;int? issuedInviteLimit;
 readonly string[] demoNames=["Юлия","Макс","Аня","Саша"];
 readonly CancellationTokenSource lifetime=new();DotNetObjectReference<Home>? reference;Task? polling;
 ElementReference searchField;
 string TabTitle=>tab switch{"mine"=>"Мои желания","giving"=>"Я дарю",_=>"Все желания"};
 string TabDescription=>tab switch{"mine"=>"Расскажите, что вас порадует. Остальное — сюрприз.","giving"=>"Ваши будущие сюрпризы и подарки, которые вы дарите вместе.",_=>"То, что порадует ваших близких. Выбирайте и дарите от души."};
 bool HasFilters=>ownerFilter!=""||!string.IsNullOrWhiteSpace(search)||occasion!=""||availability!=""||priceFilter!="";
 int ActiveSelectFilters=>(occasion!=""?1:0)+(availability!=""?1:0)+(priceFilter!=""?1:0);
 IEnumerable<string> Occasions=>gifts.Select(g=>g.Details.Occasion).Append("Без повода").Append("День рождения").Append("Новый год").Distinct().Order();
 IEnumerable<GiftDto> Filtered(){
  var query=search.Trim();
  return gifts.Where(g=>g.Archived==showArchived)
   .Where(g=>tab!="mine"||g.Owner.Id==me?.User.Id).Where(g=>tab!="giving"||g.Participation?.IsMine==true)
   .Where(g=>tab!="all"||ownerFilter==""||g.Owner.Id.ToString()==ownerFilter)
   .Where(g=>occasion==""||g.Details.Occasion==occasion)
   .Where(g=>query==""||g.Details.Title.Contains(query,StringComparison.OrdinalIgnoreCase)||(g.Details.Comment?.Contains(query,StringComparison.OrdinalIgnoreCase)??false))
   .Where(g=>priceFilter==""||g.Details.Price is {} p&&p<=decimal.Parse(priceFilter))
   .Where(g=>tab=="mine"||availability==""||(availability=="free"&&g.Participation?.Mode=="free")||(availability=="split"&&g.Participation is {Mode:"split",Bought:false} s&&s.Taken<s.Slots&&!s.IsMine));
 }
 string DialogTitle=>dialog switch{"editor"=>selected==null?"Новое желание":"Редактировать желание","split"=>"Дарить вместе","details"=>"Ваш подарок","profile"=>"Мой профиль","invite"=>"Пригласить друзей","notices"=>"Уведомления","confirm"=>"Подтвердите действие",_=>""};
 string ConfirmText=>confirmAction switch{"reserve"=>"Забронировать подарок? Остальные увидят, что он занят. Получатель ничего не узнает.","archive"=>"Отправить желание в архив? Дарители получат уведомление, история покупки сохранится.","buy"=>"Подарок уже куплен? После подтверждения снять бронь или выйти из распила будет нельзя.","cancel"=>"Отменить бронь или весь распил? Подарок снова станет свободным, участники получат уведомление.","leave"=>"Освободить свою долю? Остальные участники получат уведомление.",_=>"Продолжить?"};
 static string Initial(string name)=>string.IsNullOrWhiteSpace(name)?"?":name.Trim()[..1].ToUpperInvariant();
 protected override async Task OnAfterRenderAsync(bool first){if(first){await Initialize();reference=DotNetObjectReference.Create(this);await JS.InvokeVoidAsync("wishlist.watchFocus",reference);polling=Poll();}}
 async Task Initialize(){
  try{error=null;info=await Api.Get<AuthInfoDto>("api/auth-info");var launch=await JS.InvokeAsync<LaunchInfo>("wishlist.init");joinToken=launch.Invite;
   if(!string.IsNullOrEmpty(launch.InitData)){var session=await Api.Post<MiniSessionDto>("auth/mini",new MiniLoginInput(launch.InitData));Api.SetMini(session.Token);}
   try{me=await Api.Get<MeDto>("api/me");}catch(Services.ApiException e)when(e.Status==HttpStatusCode.Unauthorized){me=null;}
   if(me!=null&&joinToken!=""){await Api.Change("api/company/join",new JoinCompanyInput(joinToken));await JS.InvokeVoidAsync("wishlist.clearInvite");joinToken="";me=await Api.Get<MeDto>("api/me");}
   await Load();
   if(me!=null && launch.Ohmywishes is "connected" or "failed" or "profile"){
    await OpenProfile();
    if(launch.Ohmywishes=="failed")ohmywishesError="Не удалось подключить Ohmywishes. Попробуйте ещё раз.";
   }
  }catch(Exception e)when(e is HttpRequestException or Services.ApiException or JSException){error=e.Message;}
  finally{ready=true;StateHasChanged();}
 }
 async Task Load(){
  if(me==null)return;
  me=await Api.Get<MeDto>("api/me");
  if(me.Company==null)return;
  gifts=await Api.Get<GiftDto[]>("api/gifts");
  if(selected!=null){
   var updated=gifts.FirstOrDefault(g=>g.Id==selected.Id);
   if(updated==null && selected.FromOhmywishes){
    selected=null;
    if(dialog is "details" or "split" or "confirm")await CloseDialog();
   }
   else selected=updated??selected;
  }
  notices=await Api.Get<NoticeDto[]>("api/notices");
  if(dialog=="profile")ohmywishes=await Api.Get<OhmywishesStatusDto>("api/ohmywishes");
 }
 async Task Refresh(){if(busy)return;try{error=null;await Load();}catch(Exception e)when(e is HttpRequestException or Services.ApiException){error=e.Message;}StateHasChanged();}
 [JSInvokable]public Task RefreshFromFocus()=>Refresh();
 async Task Poll(){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(25));try{while(await timer.WaitForNextTickAsync(lifetime.Token)){if(me?.Company!=null)await InvokeAsync(Refresh);}}catch(OperationCanceledException){}}
 async Task Run(Func<Task> work,bool close=false,bool ohmywishesAction=false){
  if(busy)return;
  busy=true;
  if(ohmywishesAction)ohmywishesError=null;
  else error=null;
  try{await work();if(close)await CloseDialog();await Load();}
  catch(Exception e)when(e is HttpRequestException or Services.ApiException or JSException){
   if(ohmywishesAction)ohmywishesError=e.Message;
   else{error=e.Message;Snackbar.Add(e.Message,Severity.Error);}
   if(e is Services.ApiException {Status:HttpStatusCode.Conflict})await RefreshAfterConflict();
  }finally{busy=false;StateHasChanged();}
 }
 async Task RefreshAfterConflict(){try{await Load();}catch(HttpRequestException){}catch(Services.ApiException){}}
 async Task DemoLogin(int i)=>await Run(async()=>{await Api.Change($"auth/demo/{i}");Api.Clear();me=await Api.Get<MeDto>("api/me");if(joinToken!=""){await Api.Change("api/company/join",new JoinCompanyInput(joinToken));await JS.InvokeVoidAsync("wishlist.clearInvite");joinToken="";me=await Api.Get<MeDto>("api/me");}});
 async Task Logout()=>await Run(async()=>{if(!Api.IsMini)await Api.Change("api/logout");Api.Clear();me=null;gifts=[];notices=[];});
 void SetTab(string value){tab=value;showArchived=false;filtersExpanded=false;ResetFilters();}
 void ResetFilters(){ownerFilter="";search="";occasion="";availability="";priceFilter="";}
 async Task ClearFilters(MouseEventArgs args){
  ResetFilters();
  if(args.Detail==0)await searchField.FocusAsync(preventScroll:true);
 }
 async Task Show(string value){dialog=value;error=null;StateHasChanged();await JS.InvokeVoidAsync("wishlist.showDialog","app-dialog");}
 async Task CloseDialog(){await JS.InvokeVoidAsync("wishlist.closeDialog","app-dialog");dialog=null;}
 void DialogClosed(){dialog=null;error=null;ResetPhotoDraft();}
 async Task OpenEditor(GiftDto? gift){selected=gift;ResetPhotoDraft();var d=gift?.Details;editTitle=d?.Title??"";editUrl=d?.Url??"";editPrice=d?.Price;editComment=d?.Comment??"";editDate=d?.OccasionDate?.ToDateTime(TimeOnly.MinValue);editOccasion=d?.Occasion??"Без повода";editCustomOccasion="";if(editOccasion is not("Без повода" or "День рождения" or "Новый год")){editCustomOccasion=editOccasion;editOccasion="custom";}await Show("editor");}
 void ResetPhotoDraft(){
  editorRevision++;photoPickerKey++;
  editPhoto=null;editPhotoPreview=null;editPhotoError=null;removeEditPhoto=false;readingEditPhoto=false;
 }
 async Task ChooseGiftPhoto(InputFileChangeEventArgs args){
  if(readingEditPhoto || busy)return;
  var revision=editorRevision;
  readingEditPhoto=true;editPhotoError=null;
  try{
   if(args.File.Size>GiftWriteInput.MaxPhotoBytes){
    editPhotoError="Фото должно занимать не больше 5 МБ.";
    return;
   }
   using var stream=args.File.OpenReadStream(GiftWriteInput.MaxPhotoBytes);
   using var buffer=new MemoryStream();
   await stream.CopyToAsync(buffer);
   if(revision!=editorRevision)return;
   var data=buffer.ToArray();
   if(GiftPhotoFormat.ContentType(data) is not {} type){
    editPhotoError="Выберите фото в формате JPEG, PNG или WebP.";
    return;
   }
   var valid=await JS.InvokeAsync<bool>("wishlist.canPreviewPhoto",data,type);
   if(revision!=editorRevision)return;
   if(!valid){editPhotoError="Фотография повреждена. Выберите другой файл.";return;}
   editPhoto=data;editPhotoPreview=$"data:{type};base64,{Convert.ToBase64String(data)}";removeEditPhoto=false;
  }catch(Exception ex)when(ex is IOException or JSException){
   if(revision==editorRevision)editPhotoError="Не удалось прочитать фото. Выберите файл ещё раз.";
  }finally{
   if(revision==editorRevision){readingEditPhoto=false;photoPickerKey++;}
  }
 }
 void RemoveGiftPhoto(){
  editPhoto=null;editPhotoPreview=null;editPhotoError=null;
  removeEditPhoto=selected?.PhotoVersion!=null;
 }
 async Task SaveGift(){
  if(readingEditPhoto)return;
  await Run(async()=>{
   var input=new GiftWriteInput(editTitle,editUrl,editPrice,editComment,editOccasion=="custom"?editCustomOccasion:editOccasion,
    editDate is {} date?DateOnly.FromDateTime(date):null,editPhoto,removeEditPhoto);
   await Api.Change(selected==null?"api/gifts":$"api/gifts/{selected.Id}",input,selected!=null);
   Snackbar.Add("Желание сохранено",Severity.Success);
  },true);
 }
 async Task GiftAction(GiftDto gift,string action){selected=gift;transferTo="";switch(action){case "edit":await OpenEditor(gift);break;case "details":await Show("details");break;case "split":if(me!.Company!.Members.Length<3){Snackbar.Add("Для распила нужны получатель и минимум два дарителя. Пригласите друзей.",Severity.Info);return;}if(string.IsNullOrWhiteSpace(me.User.Contact)){await OpenProfile();Snackbar.Add("Сначала укажите контакт для участников распила.",Severity.Info);return;}splitSlots=2;await Show("split");break;case "join":if(string.IsNullOrWhiteSpace(me!.User.Contact)){await OpenProfile();Snackbar.Add("Сначала укажите контакт.",Severity.Info);return;}await Run(()=>Api.Change($"api/gifts/{gift.Id}/actions",new GiftActionInput("join")));break;default:await Confirm(action);break;}}
 async Task Confirm(string action){confirmAction=action;await Show("confirm");}
 async Task ExecuteConfirmed()=>await Run(()=>Api.Change(confirmAction=="archive"?$"api/gifts/{selected!.Id}/archive":$"api/gifts/{selected!.Id}/actions",confirmAction=="archive"?null:new GiftActionInput(confirmAction)),true);
 async Task StartSplit()=>await Run(()=>Api.Change($"api/gifts/{selected!.Id}/actions",new GiftActionInput("split",splitSlots)),true);
 async Task Transfer()=>await Run(()=>Api.Change($"api/gifts/{selected!.Id}/actions",new GiftActionInput("transfer",UserId:Guid.Parse(transferTo))),true);
 async Task LogoutFromProfile(){await CloseDialog();await Logout();}
 async Task OpenProfile(){
  profileName=me!.User.Name;profileContact=me.User.Contact??"";
  ohmywishes=null;ohmywishesLists=null;ohmywishesWebLink="";ohmywishesError=null;
  ohmywishesLoading=me.Company!=null;
  await Show("profile");
  if(ohmywishesLoading)await LoadOhmywishesStatus();
 }
 async Task LoadOhmywishesStatus(){
  ohmywishesLoading=true;ohmywishesError=null;
  try{ohmywishes=await Api.Get<OhmywishesStatusDto>("api/ohmywishes");}
  catch(Exception ex)when(ex is HttpRequestException or Services.ApiException){ohmywishesError="Не удалось проверить подключение к Ohmywishes.";}
  finally{ohmywishesLoading=false;}
 }
 async Task ConnectOhmywishes()=>await Run(async()=>{
  var authorization=await Api.Post<OhmywishesAuthorization>("api/ohmywishes/connect");
  Navigation.NavigateTo(authorization.Url,forceLoad:true);
 },ohmywishesAction:true);
 async Task OpenOhmywishesInBrowser()=>await JS.InvokeVoidAsync("wishlist.openExternal",Navigation.BaseUri+"?ohmywishes=profile");
 async Task ConnectOhmywishesWeb()=>await Run(async()=>{
  await Api.Change("api/ohmywishes/web-connect",new OhmywishesWebSelection(ohmywishesWebLink));
  ohmywishesWebLink="";
 },ohmywishesAction:true);
 async Task LoadOhmywishesLists()=>await Run(async()=>{
  ohmywishesLists=await Api.Get<OhmywishesListDto[]>("api/ohmywishes/lists");
  ohmywishesSelection=ohmywishes?.ListId??"";
 },ohmywishesAction:true);
 async Task SelectOhmywishesList()=>await Run(async()=>{
  await Api.Change("api/ohmywishes/list",new OhmywishesSelection(ohmywishesSelection),put:true);
  ohmywishesLists=null;
 },ohmywishesAction:true);
 async Task DisconnectOhmywishes()=>await Run(async()=>{
  await Api.Change("api/ohmywishes/disconnect");
  ohmywishesLists=null;
 },ohmywishesAction:true);
 async Task SaveProfile()=>await Run(async()=>{await Api.Change("api/profile",new ProfileInput(profileName,profileContact),true);me=await Api.Get<MeDto>("api/me");Snackbar.Add("Профиль сохранён",Severity.Success);},true);
 async Task CreateCompany()=>await Run(async()=>{var invitation=await Api.Post<InviteDto>("api/company",new CreateCompanyInput(companyName));me=await Api.Get<MeDto>("api/me");issuedInviteLimit=1;SetInvite(invitation.Token);await Show("invite");});
 async Task JoinCompany()=>await Run(async()=>{var token=joinToken.Trim();if(Uri.TryCreate(token,UriKind.Absolute,out var uri)){var match=System.Text.RegularExpressions.Regex.Match(uri.Query,@"(?:invite=|startapp=join_)([a-zA-Z0-9_-]+)");token=match.Success?match.Groups[1].Value:token;}await Api.Change("api/company/join",new JoinCompanyInput(token));await JS.InvokeVoidAsync("wishlist.clearInvite");me=await Api.Get<MeDto>("api/me");});
 async Task OpenInvite(){inviteLink=null;miniInviteLink=null;inviteLimitMode="one";inviteLimit=2;await Show("invite");}
 async Task MakeInvite()=>await Run(async()=>{int? limit=inviteLimitMode switch{"one"=>1,"limited"=>inviteLimit,_=>null};var invitation=await Api.Post<InviteDto>("api/company/invite",new InviteOptions(limit));issuedInviteLimit=limit;SetInvite(invitation.Token);});
 void SetInvite(string token){inviteLink=Navigation.BaseUri+"?invite="+Uri.EscapeDataString(token);miniInviteLink=string.IsNullOrEmpty(info?.BotUsername)?null:$"https://t.me/{info.BotUsername}?startapp=join_{token}";}
 async Task Copy(string text){try{await JS.InvokeVoidAsync("wishlist.copy",text);Snackbar.Add("Ссылка скопирована",Severity.Success);}catch(JSException){Snackbar.Add("Выделите и скопируйте ссылку вручную.",Severity.Info);}}
 async Task OpenNotices(){await Show("notices");await Run(async()=>{await Api.Change("api/notices/read");});}
 public async ValueTask DisposeAsync(){await lifetime.CancelAsync();if(polling!=null)await polling;reference?.Dispose();lifetime.Dispose();}
 public record LaunchInfo(string InitData,string Invite,string? Ohmywishes=null);
}
