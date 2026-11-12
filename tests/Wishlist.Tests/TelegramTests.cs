using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wishlist.Core;
namespace Wishlist.Tests;
public class TelegramTests {
 const string Bot="123:test-secret";
 static readonly DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(1800000000);
 public static string Signed(long? date=null,string? user=null) {
  var fields=new SortedDictionary<string,string>(StringComparer.Ordinal){["auth_date"]=(date??Now.ToUnixTimeSeconds()).ToString(),["query_id"]="query",["user"]=user??JsonSerializer.Serialize(new{id=123456789012L,first_name="Юлия",username="julia"})};
  var secret=HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"),Encoding.UTF8.GetBytes(Bot));
  var check=string.Join("\n",fields.Select(x=>$"{x.Key}={x.Value}"));
  var hash=Convert.ToHexString(HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes(check))).ToLowerInvariant();
  return string.Join("&",fields.Select(x=>$"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"))+"&hash="+hash;
 }
 [Fact] public void AcceptsVerifiedTelegramIdentityWith64BitId() {var x=TelegramInitData.Validate(Signed(),Bot,Now);Assert.Equal(123456789012L,x.Id);Assert.Equal("Юлия",x.Name);}
 [Fact] public void ReadsAvatarFromSignedMiniAppUser(){
  const string photo="https://cdn4.telesco.pe/file/avatar.jpg";
  var input=Signed(user:JsonSerializer.Serialize(new{id=123456789012L,first_name="Юлия",photo_url=photo}));
  Assert.Equal(photo,TelegramInitData.Validate(input,Bot,Now).AvatarUrl);
 }
 [Fact] public void RejectsTamperedUser() => Assert.Throws<RuleException>(()=>TelegramInitData.Validate(Signed().Replace("julia","evil"),Bot,Now));
 [Fact] public void RejectsExpiredPayload() => Assert.Throws<RuleException>(()=>TelegramInitData.Validate(Signed(Now.ToUnixTimeSeconds()-301),Bot,Now));
 [Fact] public void RejectsFuturePayload() => Assert.Throws<RuleException>(()=>TelegramInitData.Validate(Signed(Now.ToUnixTimeSeconds()+60),Bot,Now));
 [Fact] public void RejectsDuplicateFields() => Assert.Throws<RuleException>(()=>TelegramInitData.Validate(Signed()+"&auth_date=1",Bot,Now));
 [Theory][InlineData("")][InlineData("hash=nope")][InlineData("user=%zz&hash=00")] public void RejectsMalformedPayload(string input) => Assert.Throws<RuleException>(()=>TelegramInitData.Validate(input,Bot,Now));
 [Fact] public void RejectsMissingUser() => Assert.Throws<RuleException>(()=>TelegramInitData.Validate(Signed(user:"{}"),Bot,Now));
}
