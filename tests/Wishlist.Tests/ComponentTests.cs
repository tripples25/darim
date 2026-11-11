using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Wishlist.Client.Components;
using Wishlist.Contracts;
namespace Wishlist.Tests;
public class ComponentTests:BunitContext {
 public ComponentTests(){Services.AddMudServices();JSInterop.Mode=JSRuntimeMode.Loose;}
 [Fact] public void OwnGiftNeverRendersReservationButtons(){var g=new GiftDto(Guid.NewGuid(),new(Guid.NewGuid(),"Юлия"),new("Книга",Occasion:"Новый год"),false,null);var card=Render<GiftCard>(p=>p.Add(x=>x.Gift,g).Add(x=>x.IsOwner,true).Add(x=>x.VisibilityText,"Только я"));Assert.Contains("Только я",card.Markup);Assert.DoesNotContain("Подарю сам",card.Markup);Assert.DoesNotContain("Распилить",card.Markup);Assert.Contains("Новый год",card.Markup);}
 [Fact] public void FreeGiftOffersBothModes(){var g=new GiftDto(Guid.NewGuid(),new(Guid.NewGuid(),"Юлия"),new("Книга"),false,new("free",false,0,0,false,false,null));var card=Render<GiftCard>(p=>p.Add(x=>x.Gift,g));Assert.Contains("Подарю сам",card.Markup);Assert.Contains("Распилить",card.Markup);}
 [Fact] public void FullSplitDoesNotOfferJoin(){var g=new GiftDto(Guid.NewGuid(),new(Guid.NewGuid(),"Юлия"),new("Книга"),false,new("split",false,2,2,false,false,null));var card=Render<GiftCard>(p=>p.Add(x=>x.Gift,g));Assert.DoesNotContain("Занять одну долю",card.Markup);Assert.Contains("Занято 2 из 2",card.Markup);}
 [Fact]
 public void ImportedGiftOffersEditingToOwnerAndReservationToDonor()
 {
  var gift=new GiftDto(Guid.NewGuid(),new(Guid.NewGuid(),"Юлия"),new("Книга"),false,null,FromOhmywishes:true);
  var owner=Render<GiftCard>(p=>p.Add(x=>x.Gift,gift).Add(x=>x.IsOwner,true));
  Assert.Contains("Ohmywishes",owner.Markup);
  Assert.Equal("Редактировать",Assert.Single(owner.FindAll(".card-actions button")).TextContent.Trim());
  Assert.Single(owner.FindAll("button.gift-title"));
  var donor=Render<GiftCard>(p=>p.Add(x=>x.Gift,gift with { Participation=new("free",false,0,0,false,false,null) }));
  Assert.Contains("Ohmywishes",donor.Markup);
  Assert.Contains("Подарю сам",donor.Markup);
  Assert.Contains("Распилить",donor.Markup);
 }
}

public class MembershipRefreshTests:BunitContext {
 [Fact] public async Task NewMembersEnableSplitWithoutReload(){
  var handler=new MembershipHandler();Services.AddMudServices();Services.AddSingleton(new HttpClient(handler){BaseAddress=new Uri("http://localhost")});Services.AddScoped<Wishlist.Client.Services.Api>();JSInterop.Mode=JSRuntimeMode.Loose;
  JSInterop.Setup<Wishlist.Client.Pages.Home.LaunchInfo>("wishlist.init").SetResult(new("",""));
  var home=Render<Wishlist.Client.Pages.Home>();home.WaitForAssertion(()=>Assert.NotEmpty(home.FindAll("article")));
  handler.Count=3;await home.InvokeAsync(()=>home.Instance.RefreshFromFocus());
  home.FindAll("button").Single(x=>x.TextContent.Trim()=="Распилить").Click();
  home.WaitForAssertion(()=>Assert.Equal("2",home.Find("input[min='2']").GetAttribute("max")));
 }
 class MembershipHandler:HttpMessageHandler {
  public int Count=1;readonly Guid user=Guid.NewGuid(),other=Guid.NewGuid(),company=Guid.NewGuid(),gift=Guid.NewGuid();
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
   var me=new PersonDto(user,"Я","@me");var members=Enumerable.Range(0,Count).Select(i=>i==0?me:new PersonDto(i==1?other:Guid.NewGuid(),"Друг")).ToArray();
   object data=request.RequestUri!.AbsolutePath switch {
    "/api/auth-info"=>new AuthInfoDto(false,false),
    "/api/me"=>new MeDto(me,new(company,"Друзья",true,members)),
    "/api/gifts"=>new[]{new GiftDto(gift,new(other,"Друг"),new("Подарок"),false,new("free",false,0,0,false,false,null))},
    "/api/gifts/mine" or "/api/gifts/giving"=>Array.Empty<GiftDto>(),
    "/api/notices"=>Array.Empty<NoticeDto>(),_=>throw new InvalidOperationException(request.RequestUri.ToString())};
   return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=System.Net.Http.Json.JsonContent.Create(data)});
  }
 }
}
