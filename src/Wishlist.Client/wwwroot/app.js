window.wishlist = {
 init: function () {
  const tg=window.Telegram?.WebApp;
  if(tg?.initData){
   tg.ready();tg.expand();
   const apply=()=>{
    document.documentElement.dataset.telegramTheme=tg.colorScheme;
    document.documentElement.style.setProperty('--tg-safe-top',(tg.contentSafeAreaInset?.top||0)+'px');
    document.documentElement.style.setProperty('--tg-safe-bottom',(tg.contentSafeAreaInset?.bottom||0)+'px');
   };apply();tg.onEvent('themeChanged',apply);tg.onEvent('contentSafeAreaChanged',apply);
   tg.BackButton.onClick(()=>{const d=document.querySelector('dialog[open]');if(d){d.close();tg.BackButton.hide();}else history.back();});
  }
  const url=new URL(location.href);
  const fromUrl=url.searchParams.get('invite');
  const ohmywishes=url.searchParams.get('ohmywishes');
  if(ohmywishes){url.searchParams.delete('ohmywishes');history.replaceState(history.state,'',url);}
  const fromTg=tg?.initDataUnsafe?.start_param; // Navigation hint only; never identity or authorization.
  if(fromUrl)sessionStorage.setItem('wishlist.invite',fromUrl);
  else if(fromTg?.startsWith('join_'))sessionStorage.setItem('wishlist.invite',fromTg.slice(5));
  return {initData:tg?.initData||'',invite:sessionStorage.getItem('wishlist.invite')||'',ohmywishes,tab:url.searchParams.get('tab')};
 },
 clearInvite:()=>sessionStorage.removeItem('wishlist.invite'),
 loadCompany:user=>{try{return localStorage.getItem('wishlist.company.'+user);}catch{return null;}},
 saveCompany:(user,id)=>{try{localStorage.setItem('wishlist.company.'+user,id);}catch{}},
 shareToken:()=>{
  const token=location.hash.slice(1);
  if(token){
   try{sessionStorage.setItem('wishlist.shareToken',token);}catch{}
   history.replaceState(history.state,'',location.pathname+location.search);
   return token;
  }
  try{return sessionStorage.getItem('wishlist.shareToken')||'';}catch{return '';}
 },
 saveShareIntent:id=>{try{sessionStorage.setItem('wishlist.shareIntent',id);}catch{}},
 takeShareIntent:()=>{
  try{const id=sessionStorage.getItem('wishlist.shareIntent');sessionStorage.removeItem('wishlist.shareIntent');return id;}catch{return null;}
 },
 showDialog:id=>{
  const dialog=document.getElementById(id);
  if(!dialog)return;
  dialog.querySelector('.dialog-content').scrollTop=0;
  if(dialog.open){dialog.querySelector('#dialog-title').focus({preventScroll:true});return;}
  dialog.showModal();
  if(window.Telegram?.WebApp?.initData)window.Telegram.WebApp.BackButton.show();
 },
 closeDialog:id=>{document.getElementById(id)?.close();window.Telegram?.WebApp?.BackButton.hide();},
 copy:text=>navigator.clipboard.writeText(text),
 openExternal:url=>window.Telegram?.WebApp?.initData?window.Telegram.WebApp.openLink(url):window.open(url,'_blank','noopener,noreferrer'),
 watchFocus:ref=>{const fn=()=>{if(!document.hidden)ref.invokeMethodAsync('RefreshFromFocus');};document.addEventListener('visibilitychange',fn);window.addEventListener('focus',fn);return null;},
 canPreviewPhoto:async(data,type)=>{
  const url=URL.createObjectURL(new Blob([data],{type}));
  try{
   const image=new Image();
   image.src=url;
   await image.decode();
   return true;
  }catch{return false;}
  finally{URL.revokeObjectURL(url);}
 },
 watchGiftPhoto:(image,path,token,scheme)=>{
  const abort=new AbortController();
  let objectUrl;
  const observer=new IntersectionObserver(async entries=>{
   if(!entries.some(entry=>entry.isIntersecting))return;
   observer.disconnect();
   try{
    const headers=token?{Authorization:scheme+' '+token}:{};
    const response=await fetch(path,{headers,signal:abort.signal});
    if(!response.ok || !response.headers.get('content-type')?.startsWith('image/'))return;
    const blob=await response.blob();
    if(abort.signal.aborted)return;
    objectUrl=URL.createObjectURL(blob);
    image.src=objectUrl;
    await image.decode();
    if(!abort.signal.aborted)image.hidden=false;
   }catch{
    if(objectUrl)URL.revokeObjectURL(objectUrl);
   }
  },{rootMargin:'240px'});
  // Observe the parent while the empty image is hidden and has no box.
  observer.observe(image.closest('.gift-card') ?? image.parentElement);
  return {dispose(){
   observer.disconnect();
   abort.abort();
   if(objectUrl)URL.revokeObjectURL(objectUrl);
  }};
 }
};

document.addEventListener('invalid', event => {
 const section = event.target.closest('.editor-extras');
 if (section) section.open = true;
}, true);
