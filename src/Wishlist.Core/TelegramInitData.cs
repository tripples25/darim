using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Wishlist.Core;
public record TelegramIdentity(long Id,string Name,string? Username);
public static class TelegramInitData {
 public static TelegramIdentity Validate(string input,string botToken,DateTimeOffset now) {
  try {
   if(string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(input) || input.Length>16384)throw new FormatException();
   var fields=new SortedDictionary<string,string>(StringComparer.Ordinal);
   foreach(var pair in input.Split('&')) {var parts=pair.Split('=',2);if(parts.Length!=2 || !fields.TryAdd(WebUtility.UrlDecode(parts[0]),WebUtility.UrlDecode(parts[1])))throw new FormatException();}
   if(!fields.Remove("hash",out var hash) || hash.Length!=64)throw new FormatException();
   var key=HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"),Encoding.UTF8.GetBytes(botToken));
   var actual=HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(string.Join("\n",fields.Select(x=>$"{x.Key}={x.Value}"))));
   if(!CryptographicOperations.FixedTimeEquals(actual,Convert.FromHexString(hash)))throw new FormatException();
   var date=DateTimeOffset.FromUnixTimeSeconds(long.Parse(fields["auth_date"],CultureInfo.InvariantCulture));
   if(date<now.AddMinutes(-5) || date>now.AddSeconds(30))throw new FormatException();
   using var user=JsonDocument.Parse(fields["user"]);var root=user.RootElement;
   var id=root.GetProperty("id").GetInt64();var name=root.GetProperty("first_name").GetString();
   if(id<=0 || string.IsNullOrWhiteSpace(name))throw new FormatException();
   var username=root.TryGetProperty("username",out var un)?un.GetString():null;
   return new(id,name[..Math.Min(name.Length,100)],username);
  }catch(Exception e)when(e is FormatException or KeyNotFoundException or JsonException or InvalidOperationException or ArgumentException or OverflowException){throw new RuleException("Недействительные или просроченные данные Telegram. Откройте приложение заново.",401);}
 }
}
