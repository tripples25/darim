using System.Net.Http.Json;
using System.Text.Json;
using Wishlist.Core;
namespace Wishlist.Server.Services;

public sealed class TelegramLoginBot(IHttpClientFactory clients,IConfiguration config,TelegramLoginCodes codes,
 TimeProvider clock,ILogger<TelegramLoginBot> logger):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken ct) {
  if(!config.GetValue<bool>("Telegram:CodeLoginEnabled"))return;
  var token=config["Telegram:BotToken"];
  if(string.IsNullOrWhiteSpace(token))return;
  using var client=clients.CreateClient("telegram-login");
  long offset=0;
  while(!ct.IsCancellationRequested){
   try{
    using var response=await client.PostAsJsonAsync($"https://api.telegram.org/bot{token}/getUpdates",new{offset,timeout=25,allowed_updates=new[]{"message"}},ct);
    response.EnsureSuccessStatusCode();
    using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    if(!json.RootElement.GetProperty("ok").GetBoolean())throw new HttpRequestException();
    foreach(var update in json.RootElement.GetProperty("result").EnumerateArray()){
     // Advance before processing: an ambiguous delivery must never regenerate a code on retry.
     offset=update.GetProperty("update_id").GetInt64()+1;
     if(!update.TryGetProperty("message",out var message))continue;
     await HandleMessage(message,client,token,ct);
    }
   }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
   catch(Exception ex)when(ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or RuleException){
    // Exceptions and response bodies may contain bot tokens or codes.
    logger.LogWarning("Telegram code-login polling failed; retrying in 5 seconds");
    await Task.Delay(TimeSpan.FromSeconds(5),ct);
   }
  }
 }
 public async Task HandleMessage(JsonElement message,HttpClient client,string token,CancellationToken ct) {
  if(!message.TryGetProperty("chat",out var chat)||chat.GetProperty("type").GetString()!="private"
   ||!message.TryGetProperty("from",out var from)||from.GetProperty("is_bot").GetBoolean()
   ||chat.GetProperty("id").GetInt64()!=from.GetProperty("id").GetInt64()
   ||!message.TryGetProperty("text",out var text))return;
  var date=DateTimeOffset.FromUnixTimeSeconds(message.GetProperty("date").GetInt64());
  if(date<clock.GetUtcNow().AddMinutes(-2)||date>clock.GetUtcNow().AddSeconds(30))return;
  var command=(text.GetString()??"").Split(' ',2)[0];
  var suffix=command.IndexOf('@');
  if(suffix>=0){if(!command[(suffix+1)..].Equals(config["Telegram:BotUsername"],StringComparison.OrdinalIgnoreCase))return;command=command[..suffix];}
  string? code=null;
  string reply;
  if(command=="/login"){
   var id=from.GetProperty("id").GetInt64();if(id<=0)return;
   var name=from.GetProperty("first_name").GetString()??"Участник";
   var identity=new TelegramIdentity(id,name[..Math.Min(name.Length,100)],from.TryGetProperty("username",out var username)?username.GetString():null);
   code=codes.Issue(identity);
   if(code==null)return;
   reply=$"Код для входа в Дарим: {code}\n\nВведите его на сайте в разделе «Войти по коду». Код действует 5 минут и только один раз. Новый код отменит предыдущий. Никому его не сообщайте. Если вы не входите в Дарим, просто проигнорируйте сообщение.";
  }else if(command=="/start"||command=="/help")reply="Чтобы войти в Дарим без переходов через Telegram, отправьте /login и введите полученный код на сайте. Подходит и для первого входа.";
  else return;
  try{
   using var sent=await client.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage",new{chat_id=chat.GetProperty("id").GetInt64(),text=reply,protect_content=true},ct);
   sent.EnsureSuccessStatusCode();
   using var result=JsonDocument.Parse(await sent.Content.ReadAsStringAsync(ct));
   if(!result.RootElement.GetProperty("ok").GetBoolean())throw new HttpRequestException();
  }catch{if(code!=null)codes.Revoke(code);throw;}
 }
}
