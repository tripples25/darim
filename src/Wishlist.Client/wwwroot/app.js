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
  return {initData:tg?.initData||'',invite:sessionStorage.getItem('wishlist.invite')||'',ohmywishes};
 },
 clearInvite:()=>sessionStorage.removeItem('wishlist.invite'),
 showDialog:id=>{const d=document.getElementById(id);if(d&&!d.open){d.showModal();if(window.Telegram?.WebApp?.initData)window.Telegram.WebApp.BackButton.show();}},
 closeDialog:id=>{document.getElementById(id)?.close();window.Telegram?.WebApp?.BackButton.hide();},
 copy:text=>navigator.clipboard.writeText(text),
 openExternal:url=>window.Telegram?.WebApp?.initData?window.Telegram.WebApp.openLink(url):window.open(url,'_blank','noopener,noreferrer'),
 watchFocus:ref=>{const fn=()=>{if(!document.hidden)ref.invokeMethodAsync('RefreshFromFocus');};document.addEventListener('visibilitychange',fn);window.addEventListener('focus',fn);return null;}
};

document.addEventListener('invalid', event => {
 const section = event.target.closest('.editor-extras');
 if (section) section.open = true;
}, true);
