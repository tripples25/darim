using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Wishlist.Client.Components;
using Wishlist.Client.Services;
namespace Wishlist.Tests;
public class CodeLoginFormTests:BunitContext {
 [Fact] public void DisabledFeatureDoesNotShowForm(){
  Services.AddSingleton(new Api(new HttpClient()));
  Assert.Empty(Render<CodeLogin>().FindAll("form"));
 }
 [Fact] public async Task SuccessfulLoginSubmitsCodeAndNotifiesPage(){
  var handler=new Handler();Services.AddSingleton(new Api(new HttpClient(handler){BaseAddress=new("https://localhost")}));
  bool signedIn=false;
  var form=Render<CodeLogin>(p=>p.Add(x=>x.Enabled,true).Add(x=>x.OnSignedIn,()=>signedIn=true));
  form.Find("input").Input("abcdef123456");await form.Find("form").SubmitAsync();
  form.WaitForAssertion(()=>Assert.True(signedIn));Assert.Equal("abcdef123456",handler.Code);Assert.Equal("",form.Find("input").GetAttribute("value"));
 }
 sealed class Handler:HttpMessageHandler {
  public string? Code;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
   var input=await request.Content!.ReadFromJsonAsync<Wishlist.Contracts.CodeLoginInput>(ct);Code=input!.Code;
   return new(HttpStatusCode.OK);
  }
 }
}
