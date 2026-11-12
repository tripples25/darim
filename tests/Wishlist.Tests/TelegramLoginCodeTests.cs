using Wishlist.Core;
using Wishlist.Server.Services;
namespace Wishlist.Tests;
public class TelegramLoginCodeTests {
 readonly TelegramIdentity user = new(123, "Юлия", "julia");
 [Fact] public void CodeIsSingleUse() {
  var store=new TelegramLoginCodes(TimeProvider.System);var code=store.Issue(user)!;
  Assert.Equal(user,store.Consume(code.ToLowerInvariant()));
  Assert.Throws<RuleException>(()=>store.Consume(code));
 }
 [Fact] public void ReplacementInvalidatesPreviousCode() {
  var clock=new Clock();var store=new TelegramLoginCodes(clock);var first=store.Issue(user)!;
  Assert.Null(store.Issue(user));clock.Now=clock.Now.AddSeconds(31);
  var second=store.Issue(user)!;
  Assert.Throws<RuleException>(()=>store.Consume(first));Assert.Equal(user,store.Consume(second));
 }
 [Fact] public void CodeExpiresAtFiveMinutes() {
  var clock=new Clock();var store=new TelegramLoginCodes(clock);var code=store.Issue(user)!;
  clock.Now=clock.Now.AddMinutes(5);Assert.Throws<RuleException>(()=>store.Consume(code));
 }
 [Fact] public async Task OnlyOneConcurrentRedemptionSucceeds() {
  var store=new TelegramLoginCodes(TimeProvider.System);var code=store.Issue(user)!;
  var attempts=await Task.WhenAll(Enumerable.Range(0,20).Select(_=>Task.Run(()=>{try{store.Consume(code);return 1;}catch(RuleException){return 0;}})));
  Assert.Equal(1,attempts.Sum());
 }
 [Theory][InlineData("")][InlineData("123")][InlineData("zzzzzzzzzzzz")]
 public void InvalidCodesAreRejected(string code)=>Assert.Throws<RuleException>(()=>new TelegramLoginCodes(TimeProvider.System).Consume(code));
 [Fact] public void RevokeOnlyRevokesTheMatchingCode() {
  var clock=new Clock();var store=new TelegramLoginCodes(clock);var first=store.Issue(user)!;
  clock.Now=clock.Now.AddSeconds(31);var second=store.Issue(user)!;store.Revoke(first);
  Assert.Equal(user,store.Consume(second));
 }
 sealed class Clock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
}
