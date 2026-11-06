using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace Wishlist.Server.Services;

// Pending logins belong to this single server and expire with the OIDC timeout.
// The normal protected properties still contain correlation, nonce and PKCE data.
public sealed class TelegramOidcStateStore : IDisposable {
 readonly MemoryCache cache=new(new MemoryCacheOptions{SizeLimit=1024});
 readonly object gate=new();

 public string Store(string protectedProperties) {
  var handle=WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
  cache.Set(handle,protectedProperties,new MemoryCacheEntryOptions {
   AbsoluteExpirationRelativeToNow=TimeSpan.FromMinutes(15),Size=1
  });
  if(!cache.TryGetValue(handle,out _))throw new InvalidOperationException("Too many pending Telegram logins. Please retry later.");
  return handle;
 }

 public string? Take(string? handle) {
  if(handle is null||handle.Length!=43)return null;
  lock(gate) {
   if(!cache.TryGetValue<string>(handle,out var protectedProperties))return null;
   cache.Remove(handle);
   return protectedProperties;
  }
 }

 public void Dispose()=>cache.Dispose();
}

public sealed class CachedOidcStateFormat(
 ISecureDataFormat<AuthenticationProperties> inner,
 TelegramOidcStateStore store) : ISecureDataFormat<AuthenticationProperties> {
 public string Protect(AuthenticationProperties data)=>Protect(data,null);
 public string Protect(AuthenticationProperties data,string? purpose)=>store.Store(inner.Protect(data,purpose));
 public AuthenticationProperties? Unprotect(string? protectedText)=>Unprotect(protectedText,null);
 public AuthenticationProperties? Unprotect(string? protectedText,string? purpose) {
  var value=store.Take(protectedText);
  return value is null?null:inner.Unprotect(value,purpose);
 }
}
