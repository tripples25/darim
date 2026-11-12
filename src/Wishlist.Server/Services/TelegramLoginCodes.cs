using System.Security.Cryptography;
using System.Text;
using Wishlist.Core;
namespace Wishlist.Server.Services;

// Single-instance, ephemeral credentials. Store only hashes; restarting invalidates outstanding codes.
public sealed class TelegramLoginCodes(TimeProvider clock) {
 sealed record Entry(TelegramIdentity Identity,DateTimeOffset Issued);
 readonly Dictionary<string,Entry> codes=[];
 readonly Dictionary<long,DateTimeOffset> lastIssued=[];
 readonly object gate=new();
 static string Hash(string code)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
 public string? Issue(TelegramIdentity identity) {
  lock(gate){
   var now=clock.GetUtcNow();
   foreach(var key in codes.Where(x=>x.Value.Issued.AddMinutes(5)<=now).Select(x=>x.Key).ToArray())codes.Remove(key);
   foreach(var id in lastIssued.Where(x=>x.Value.AddMinutes(5)<=now).Select(x=>x.Key).ToArray())lastIssued.Remove(id);
   if(lastIssued.TryGetValue(identity.Id,out var issued)&&issued.AddSeconds(30)>now)return null;
   if(!lastIssued.ContainsKey(identity.Id)&&lastIssued.Count>=10000)throw new RuleException("Попробуйте запросить код позже.",429);
   var previous=codes.FirstOrDefault(x=>x.Value.Identity.Id==identity.Id);
   if(previous.Value!=null)codes.Remove(previous.Key);
   if(codes.Count>=10000)throw new RuleException("Попробуйте запросить код позже.",429);
   string code;do{code=Convert.ToHexString(RandomNumberGenerator.GetBytes(6));}while(codes.ContainsKey(Hash(code)));
   codes.Add(Hash(code),new(identity,now));lastIssued[identity.Id]=now;return code;
  }
 }
 public TelegramIdentity Consume(string? code) {
  var normalized=code?.Trim().ToUpperInvariant()??"";
  if(normalized.Length!=12||!normalized.All(char.IsAsciiHexDigit))throw Invalid();
  lock(gate){
   if(!codes.Remove(Hash(normalized),out var entry)||entry.Issued.AddMinutes(5)<=clock.GetUtcNow())throw Invalid();
   return entry.Identity;
  }
 }
 public void Revoke(string code){lock(gate)codes.Remove(Hash(code));}
 static RuleException Invalid()=>new("Код неверный или истёк. Отправьте боту /login и попробуйте снова.",401);
}
