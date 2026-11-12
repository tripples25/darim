using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wishlist.Core;
using Wishlist.Server.Services;
namespace Wishlist.Tests;
public class TelegramLoginBotTests {
 [Theory][InlineData("group",123)][InlineData("supergroup",123)][InlineData("private",456)]
 public async Task NeverSendsCodesOutsideSenderPrivateChat(string type,long chatId) {
  var handler=new Capture();using var client=new HttpClient(handler);var store=new TelegramLoginCodes(TimeProvider.System);
  var bot=Bot(store);using var msg=Message(type,chatId);
  await bot.HandleMessage(msg.RootElement,client,"test",default);Assert.Null(handler.Body);
 }
 [Fact] public async Task PrivateLoginIssuesRedeemableCode() {
  var handler=new Capture();using var client=new HttpClient(handler);var store=new TelegramLoginCodes(TimeProvider.System);
  using var msg=Message("private",123);await Bot(store).HandleMessage(msg.RootElement,client,"test",default);
  var code=Code(handler.Body!);Assert.Equal(123,store.Consume(code).Id);
 }
 [Fact] public async Task FailedDeliveryRevokesCode() {
  var handler=new Capture{Status=HttpStatusCode.BadGateway};using var client=new HttpClient(handler);var store=new TelegramLoginCodes(TimeProvider.System);
  using var msg=Message("private",123);
  await Assert.ThrowsAsync<HttpRequestException>(()=>Bot(store).HandleMessage(msg.RootElement,client,"test",default));
  Assert.Throws<RuleException>(()=>store.Consume(Code(handler.Body!)));
 }
 static string Code(string body){using var json=JsonDocument.Parse(body);return json.RootElement.GetProperty("text").GetString()!.Split('\n')[0].Split(' ').Last();}
 static JsonDocument Message(string type,long chatId)=>JsonDocument.Parse(JsonSerializer.Serialize(new {chat=new{id=chatId,type},from=new{id=123,is_bot=false,first_name="Юлия"},date=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),text="/login"}));
 static TelegramLoginBot Bot(TelegramLoginCodes store)=>new(null!,new ConfigurationBuilder().Build(),store,TimeProvider.System,NullLogger<TelegramLoginBot>.Instance);
 sealed class Capture:HttpMessageHandler {
  public string? Body;public HttpStatusCode Status=HttpStatusCode.OK;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Body=await request.Content!.ReadAsStringAsync(ct);return new(Status){Content=new StringContent("{\"ok\":true}",Encoding.UTF8,"application/json")};}
 }
}
