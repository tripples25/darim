using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Wishlist.Contracts;
namespace Wishlist.Client.Services;
public class Api(HttpClient http) {
 string? token,csrf;
 public bool IsMini=>token!=null;
 public void SetMini(string value){token=value;csrf=null;}
 public void Clear(){token=null;csrf=null;}
 public async Task<T> Get<T>(string path,string? shareToken=null) {using var r=await Send(HttpMethod.Get,path,shareToken:shareToken);return (await r.Content.ReadFromJsonAsync<T>())!;}
 public async Task<T> Post<T>(string path,object? body=null){using var r=await Send(HttpMethod.Post,path,body);return (await r.Content.ReadFromJsonAsync<T>())!;}
 public async Task Change(string path,object? body=null,bool put=false){using var r=await Send(put?HttpMethod.Put:HttpMethod.Post,path,body);}
 public async Task Delete(string path){using var r=await Send(HttpMethod.Delete,path);}
 public ValueTask<IJSObjectReference> WatchGiftPhoto(IJSRuntime js,ElementReference image,Guid giftId,Guid version,string? shareToken=null)=>
  js.InvokeAsync<IJSObjectReference>("wishlist.watchGiftPhoto",image,shareToken==null?$"/api/gifts/{giftId}/photo/{version}":$"/api/public/wishlist/gifts/{giftId}/photo/{version}",shareToken??token,shareToken==null?"Bearer":"Wishlist");
 async Task<HttpResponseMessage> Send(HttpMethod method,string path,object? body=null,string? shareToken=null){
  if(method!=HttpMethod.Get && path.StartsWith("api/")&&token==null&&csrf==null)csrf=(await Get<CsrfDto>("api/csrf")).Token;
  using var req=new HttpRequestMessage(method,path);
  if(shareToken!=null)req.Headers.Authorization=new AuthenticationHeaderValue("Wishlist",shareToken);
  else if(token!=null)req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
  if(csrf!=null&&shareToken==null)req.Headers.Add("X-CSRF-TOKEN",csrf);
  if(body!=null)req.Content=JsonContent.Create(body);
  var response=await http.SendAsync(req);
  if(response.IsSuccessStatusCode)return response;
  var status=response.StatusCode;var message=status==HttpStatusCode.Unauthorized?"Сессия закончилась. Войдите снова или откройте Mini App заново.":"Не удалось выполнить действие. Попробуйте ещё раз.";
  try{using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());if(json.RootElement.TryGetProperty("title",out var title))message=title.GetString()??message;}catch(JsonException){}
  response.Dispose();throw new ApiException(message,status);
 }
}
public class ApiException(string message,HttpStatusCode status):Exception(message){public HttpStatusCode Status{get;}=status;}
