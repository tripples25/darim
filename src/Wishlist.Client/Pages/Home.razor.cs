using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Wishlist.Contracts;
namespace Wishlist.Client.Pages;
public partial class Home {
 bool ready,busy,showArchived;string? error,dialog,inviteLink,miniInviteLink;string tab="all",ownerFilter="",search="",occasion="",availability="",priceFilter="";
 AuthInfoDto? info;MeDto? me;GiftDto[] gifts=[];NoticeDto[] notices=[];GiftDto? selected;
 string editTitle="",editUrl="",editComment="",editOccasion="Без повода",editCustomOccasion="";decimal? editPrice;DateTime? editDate;
 string profileName="",profileContact="",companyName="",joinToken="",transferTo="",confirmAction="";int splitSlots=2;
 readonly string[] demoNames=["Юлия","Макс","Аня","Саша"];
 readonly CancellationTokenSource lifetime=new();DotNetObjectReference<Home>? reference;Task? polling;
 string TabTitle=>tab switch{"mine"=>"Мои желания","giving"=>"Я дарю",_=>"Все желания"};
 string TabDescription=>tab switch{"mine"=>"Расскажите, что вас порадует. Остальное — сюрприз.","giving"=>"Ваши будущие сюрпризы и подарки, которые вы дарите вместе.",_=>"То, что порадует ваших близких. Выбирайте и дарите от души."};
 IEnumerable<string> Occasions=>gifts.Select(g=>g.Details.Occasion).Append("Без повода").Append("День рождения").Append("Новый год").Distinct().Order();
 IEnumerable<GiftDto> Filtered=>gifts.Where(g=>g.Archived==showArchived)
  .Where(g=>tab!="mine"||g.Owner.Id==me?.User.Id).Where(g=>tab!="giving"||g.Participation?.IsMine==true)
  .Where(g=>tab!="all"||ownerFilter==""||g.Owner.Id.ToString()==ownerFilter)
  .Where(g=>occasion==""||g.Details.Occasion==occasion)
  .Where(g=>search==""||g.Details.Title.Contains(search,StringComparison.OrdinalIgnoreCase)||(g.Details.Comment?.Contains(search,StringComparison.OrdinalIgnoreCase)??false))
  .Where(g=>priceFilter==""||g.Details.Price is {} p&&p<=decimal.Parse(priceFilter))
  .Where(g=>tab=="mine"||availability==""||(availability=="free"&&g.Participation?.Mode=="free")||(availability=="split"&&g.Participation is {Mode:"split",Bought:false} s&&s.Taken<s.Slots&&!s.IsMine));
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
  }catch(Exception e)when(e is HttpRequestException or Services.ApiException or JSException){error=e.Message;}
  finally{ready=true;StateHasChanged();}
 }
 async Task Load(){if(me==null)return;me=await Api.Get<MeDto>("api/me");if(me.Company!=null){gifts=await Api.Get<GiftDto[]>("api/gifts");notices=await Api.Get<NoticeDto[]>("api/notices");if(selected!=null)selected=gifts.FirstOrDefault(g=>g.Id==selected.Id)??selected;}}
 async Task Refresh(){if(busy)return;try{error=null;await Load();}catch(Exception e)when(e is HttpRequestException or Services.ApiException){error=e.Message;}StateHasChanged();}
 [JSInvokable]public Task RefreshFromFocus()=>Refresh();
 async Task Poll(){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(25));try{while(await timer.WaitForNextTickAsync(lifetime.Token)){if(me?.Company!=null)await InvokeAsync(Refresh);}}catch(OperationCanceledException){}}
 async Task Run(Func<Task> work,bool close=false){if(busy)return;busy=true;error=null;try{await work();if(close)await CloseDialog();await Load();}catch(Exception e)when(e is HttpRequestException or Services.ApiException or JSException){error=e.Message;Snackbar.Add(e.Message,Severity.Error);if(e is Services.ApiException {Status:HttpStatusCode.Conflict})await RefreshAfterConflict();}finally{busy=false;StateHasChanged();}}
 async Task RefreshAfterConflict(){try{await Load();}catch(HttpRequestException){}catch(Services.ApiException){}}
 async Task DemoLogin(int i)=>await Run(async()=>{await Api.Change($"auth/demo/{i}");Api.Clear();me=await Api.Get<MeDto>("api/me");if(joinToken!=""){await Api.Change("api/company/join",new JoinCompanyInput(joinToken));await JS.InvokeVoidAsync("wishlist.clearInvite");joinToken="";me=await Api.Get<MeDto>("api/me");}});
 async Task Logout()=>await Run(async()=>{if(!Api.IsMini)await Api.Change("api/logout");Api.Clear();me=null;gifts=[];notices=[];});
 void SetTab(string value){tab=value;ResetFilters();}
 void ResetFilters(){ownerFilter="";search="";occasion="";availability="";priceFilter="";showArchived=false;}
 async Task Show(string value){dialog=value;error=null;StateHasChanged();await JS.InvokeVoidAsync("wishlist.showDialog","app-dialog");}
 async Task CloseDialog(){await JS.InvokeVoidAsync("wishlist.closeDialog","app-dialog");dialog=null;}
 void DialogClosed(){dialog=null;error=null;}
 async Task OpenEditor(GiftDto? gift){selected=gift;var d=gift?.Details;editTitle=d?.Title??"";editUrl=d?.Url??"";editPrice=d?.Price;editComment=d?.Comment??"";editDate=d?.OccasionDate?.ToDateTime(TimeOnly.MinValue);editOccasion=d?.Occasion??"Без повода";if(editOccasion is not("Без повода" or "День рождения" or "Новый год")){editCustomOccasion=editOccasion;editOccasion="custom";}await Show("editor");}
 async Task SaveGift()=>await Run(async()=>{var input=new GiftInput(editTitle,editUrl,editPrice,editComment,editOccasion=="custom"?editCustomOccasion:editOccasion,editDate is {} date?DateOnly.FromDateTime(date):null);await Api.Change(selected==null?"api/gifts":$"api/gifts/{selected.Id}",input,selected!=null);Snackbar.Add("Желание сохранено",Severity.Success);},true);
 async Task GiftAction(GiftDto gift,string action){selected=gift;transferTo="";switch(action){case "edit":await OpenEditor(gift);break;case "details":await Show("details");break;case "split":if(me!.Company!.Members.Length<3){Snackbar.Add("Для распила нужны получатель и минимум два дарителя. Пригласите друзей.",Severity.Info);return;}if(string.IsNullOrWhiteSpace(me.User.Contact)){await OpenProfile();Snackbar.Add("Сначала укажите контакт для участников распила.",Severity.Info);return;}splitSlots=2;await Show("split");break;case "join":if(string.IsNullOrWhiteSpace(me!.User.Contact)){await OpenProfile();Snackbar.Add("Сначала укажите контакт.",Severity.Info);return;}await Run(()=>Api.Change($"api/gifts/{gift.Id}/actions",new GiftActionInput("join")));break;default:await Confirm(action);break;}}
 async Task Confirm(string action){confirmAction=action;await Show("confirm");}
 async Task ExecuteConfirmed()=>await Run(()=>Api.Change(confirmAction=="archive"?$"api/gifts/{selected!.Id}/archive":$"api/gifts/{selected!.Id}/actions",confirmAction=="archive"?null:new GiftActionInput(confirmAction)),true);
 async Task StartSplit()=>await Run(()=>Api.Change($"api/gifts/{selected!.Id}/actions",new GiftActionInput("split",splitSlots)),true);
 async Task Transfer()=>await Run(()=>Api.Change($"api/gifts/{selected!.Id}/actions",new GiftActionInput("transfer",UserId:Guid.Parse(transferTo))),true);
 async Task LogoutFromProfile(){await CloseDialog();await Logout();}
 async Task OpenProfile(){profileName=me!.User.Name;profileContact=me.User.Contact??"";await Show("profile");}
 async Task SaveProfile()=>await Run(async()=>{await Api.Change("api/profile",new ProfileInput(profileName,profileContact),true);me=await Api.Get<MeDto>("api/me");Snackbar.Add("Профиль сохранён",Severity.Success);},true);
 async Task CreateCompany()=>await Run(async()=>{var invitation=await Api.Post<InviteDto>("api/company",new CreateCompanyInput(companyName));me=await Api.Get<MeDto>("api/me");SetInvite(invitation.Token);await Show("invite");});
 async Task JoinCompany()=>await Run(async()=>{var token=joinToken.Trim();if(Uri.TryCreate(token,UriKind.Absolute,out var uri)){var match=System.Text.RegularExpressions.Regex.Match(uri.Query,@"(?:invite=|startapp=join_)([a-zA-Z0-9_-]+)");token=match.Success?match.Groups[1].Value:token;}await Api.Change("api/company/join",new JoinCompanyInput(token));await JS.InvokeVoidAsync("wishlist.clearInvite");me=await Api.Get<MeDto>("api/me");});
 async Task OpenInvite(){inviteLink=null;miniInviteLink=null;await Show("invite");}
 async Task MakeInvite()=>await Run(async()=>{var invitation=await Api.Post<InviteDto>("api/company/invite");SetInvite(invitation.Token);});
 void SetInvite(string token){inviteLink=Navigation.BaseUri+"?invite="+Uri.EscapeDataString(token);miniInviteLink=string.IsNullOrEmpty(info?.BotUsername)?null:$"https://t.me/{info.BotUsername}?startapp=join_{token}";}
 async Task Copy(string text){try{await JS.InvokeVoidAsync("wishlist.copy",text);Snackbar.Add("Ссылка скопирована",Severity.Success);}catch(JSException){Snackbar.Add("Выделите и скопируйте ссылку вручную.",Severity.Info);}}
 async Task OpenNotices(){await Show("notices");await Run(async()=>{await Api.Change("api/notices/read");});}
 public async ValueTask DisposeAsync(){await lifetime.CancelAsync();if(polling!=null)await polling;reference?.Dispose();lifetime.Dispose();}
 public record LaunchInfo(string InitData,string Invite);
}
