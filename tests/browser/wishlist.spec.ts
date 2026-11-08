import {createHmac} from 'node:crypto';
import {test,expect,Page,Browser} from '@playwright/test';
async function login(page:Page,name:string){await page.goto('/');await page.getByRole('button',{name,exact:true}).click();await expect(page.getByRole('heading',{name:'Все желания',exact:true})).toBeVisible();}
test('organizer can create an invitation with a usage limit',async({page})=>{
 await login(page,'Юлия');await page.getByRole('button',{name:'Пригласить',exact:true}).click();const dialog=page.getByRole('dialog');
 await expect(dialog.getByLabel('Количество вступлений')).toHaveValue('one');
 await dialog.getByLabel('Количество вступлений').selectOption('limited');
 await dialog.getByLabel('Сколько человек могут вступить').fill('2');
 await dialog.getByRole('button',{name:'Создать новую ссылку'}).click();
 await expect(dialog.getByText('Ссылка для 2 вступлений.')).toBeVisible();
 await expect(dialog.getByLabel('Ссылка на сайт')).toHaveValue(/\?invite=/);
});
async function create(page:Page,title:string,occasion='Новый год'){
 await page.getByRole('button',{name:'Добавить желание'}).click();const dialog=page.getByRole('dialog');await dialog.getByLabel('Что хочется?').fill(title);await dialog.getByText('Дополнительные параметры',{exact:true}).click();await dialog.getByLabel('Примерная цена').fill('12000');await dialog.getByLabel('Праздник',{exact:true}).selectOption(occasion);await dialog.getByRole('button',{name:'Сохранить желание'}).click();await expect(dialog).not.toBeVisible();return page.locator('article').filter({has:page.getByRole('heading',{name:title,exact:true})});
}
test('switches companies and shares the selected company without reservation details',async({page,browser})=>{
 await page.setViewportSize({width:390,height:844});
 await login(page,'Юлия');
 const title='Подарок для ссылки '+Date.now();
 const card=await create(page,title);
 await expect(card).toBeVisible();
 await page.getByRole('button',{name:'Мои компании'}).click();
 let dialog=page.getByRole('dialog');
 const company='Вторая компания '+Date.now();
 await dialog.getByLabel('Название',{exact:true}).fill(company);
 await dialog.getByRole('button',{name:'Создать',exact:true}).click();
 await expect(page.getByRole('dialog',{name:'Пригласить друзей'})).toBeVisible();
 await page.getByRole('button',{name:'Закрыть',exact:true}).click();
 const switcher=page.getByLabel('Текущая компания');
 await expect(switcher.locator('option:checked')).toHaveText(company);
 await expect(card).toHaveCount(0);
 await switcher.selectOption({label:'Близкие люди'});
 await expect(card).toBeVisible();
 await page.reload();
 await expect(card).toBeVisible();
 await page.getByRole('button',{name:'Мои желания',exact:true}).click();
 await page.getByRole('button',{name:'Поделиться списком'}).click();
 dialog=page.getByRole('dialog',{name:'Ссылки на желания'});
 await dialog.getByLabel('Какие желания открыть').selectOption({label:'Близкие люди'});
 await dialog.getByRole('button',{name:'Создать ссылку'}).click();
 const link=dialog.getByLabel('Ссылка',{exact:true});
 await expect(link).toHaveValue(/\/share#[A-Za-z0-9_-]{43}$/);
 const url=await link.inputValue();

 const guest=await browser.newPage({viewport:{width:390,height:844}});
 try{
  await guest.goto(url);
  const publicCard=guest.locator('article').filter({has:guest.getByRole('heading',{name:title,exact:true})});
  await expect(publicCard).toBeVisible();
  await expect(publicCard).not.toContainText(/занят|забронирован/);
  expect(await guest.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
  await guest.request.post('/auth/demo/1');
  await guest.reload();
  await publicCard.getByRole('button',{name:'Хочу подарить'}).click();
  await guest.getByRole('button',{name:'Отправить заявку'}).click();
  await expect(guest.getByRole('status')).toContainText('Запрос принят. Подарок мог быть уже занят.');
  await expect(publicCard.getByRole('button',{name:'Отменить мою заявку'})).toBeVisible();
 }finally{await guest.close();}
});
test('gift editor is centered and keeps optional values when collapsed',async({page})=>{
 const viewport={width:390,height:740};
 await page.setViewportSize(viewport);
 await login(page,'Юлия');
 await page.getByRole('button',{name:'Добавить желание'}).click();
 const dialog=page.getByRole('dialog',{name:'Новое желание'});
 await expect(dialog).toBeVisible();
 const box=(await dialog.boundingBox())!;
 expect(Math.abs(box.x+box.width/2-viewport.width/2)).toBeLessThan(2);
 expect(Math.abs(box.y+box.height/2-viewport.height/2)).toBeLessThan(2);
 const price=dialog.getByLabel('Примерная цена');
 const extras=dialog.locator('summary').filter({hasText:'Дополнительные параметры'});
 const title='Компактная форма '+Date.now();
 await expect(price).toBeHidden();
 await dialog.getByLabel('Что хочется?').fill(title);
 await extras.press('Enter');
 await price.fill('-1');
 await extras.press('Enter');
 await dialog.getByRole('button',{name:'Сохранить желание'}).click();
 await expect(price).toBeVisible();
 await price.fill('340');
 await extras.press('Enter');
 await dialog.getByRole('button',{name:'Сохранить желание'}).click();
 await expect(dialog).toBeHidden();
 const gifts=await (await page.request.get('/api/gifts/mine')).json();
 expect(gifts.find((gift:any)=>gift.details.title===title)?.details.price).toBe(340);
});
test('create, filter, reserve and hide reservation from recipient',async({page,browser})=>{
 await login(page,'Юлия');const title='Книга для проверки '+Date.now();const ownerCard=await create(page,title);await expect(ownerCard).toContainText('Близкие люди');await page.getByLabel('Праздник',{exact:true}).selectOption('Новый год');await expect(ownerCard).toBeVisible();
 const ctx=await browser.newContext();const donor=await ctx.newPage();await login(donor,'Макс');const card=donor.locator('article').filter({has:donor.getByRole('heading',{name:title,exact:true})});await card.getByRole('button',{name:'Подарю сам'}).click();await donor.getByRole('dialog').getByRole('button',{name:'Подтвердить'}).click();await expect(card).toContainText('Вы дарите этот подарок');await page.reload();await expect(ownerCard).toContainText('Близкие люди');await expect(ownerCard).not.toContainText('Кто-то уже дарит');
 const response=await page.request.get('/api/gifts/mine');const gifts=await response.json();const gift=gifts.find((g:any)=>g.details.title===title);expect(gift.participation).toBeNull();expect(gift.original).toBeNull();await ctx.close();
});
test('split, join, coordinate and buy together',async({page,browser})=>{
 await login(page,'Юлия');const title='Совместный подарок '+Date.now();await create(page,title);
 const ca=await browser.newContext();const a=await ca.newPage();await login(a,'Макс');const cardA=a.locator('article').filter({has:a.getByRole('heading',{name:title,exact:true})});await cardA.getByRole('button',{name:'Распилить',exact:true}).click();await a.getByRole('dialog').getByLabel('Количество участников').fill('2');await a.getByRole('button',{name:'Распилить и занять долю'}).click();await expect(cardA).toContainText('Занято 1 из 2');
 const cb=await browser.newContext();const b=await cb.newPage();await login(b,'Аня');const cardB=b.locator('article').filter({has:b.getByRole('heading',{name:title,exact:true})});await cardB.getByRole('button',{name:'Занять одну долю'}).click();await expect(cardB).toContainText('Занято 2 из 2');await a.reload();await cardA.getByRole('button',{name:'Участники и детали'}).click();await expect(a.getByRole('dialog')).toContainText('Аня');await expect(a.getByRole('dialog')).toContainText('координатор');await a.getByRole('button',{name:'Отметить купленным',exact:true}).click();await a.getByRole('button',{name:'Подтвердить',exact:true}).click();await expect(cardA).toContainText('Куплен');await ca.close();await cb.close();
});
test('editing occasion preserves donor history and sends notification',async({page,browser})=>{
 await login(page,'Юлия');const title='Изменяемое желание '+Date.now();const owner=await create(page,title);
 const ctx=await browser.newContext();const donor=await ctx.newPage();await login(donor,'Саша');const card=donor.locator('article').filter({has:donor.getByRole('heading',{name:title,exact:true})});await card.getByRole('button',{name:'Подарю сам'}).click();await donor.getByRole('button',{name:'Подтвердить',exact:true}).click();await expect(card).toContainText('Вы дарите');
 await owner.getByRole('button',{name:'Редактировать'}).click();await page.getByRole('dialog').getByLabel('Праздник',{exact:true}).selectOption('День рождения');await page.getByRole('button',{name:'Сохранить желание'}).click();await donor.reload();await card.getByRole('button',{name:'Детали подарка'}).click();await expect(donor.getByRole('dialog')).toContainText('Желание изменилось после брони');await donor.getByRole('button',{name:'Закрыть',exact:true}).click();await donor.getByRole('button',{name:'Уведомления',exact:true}).click();await expect(donor.getByRole('dialog')).toContainText('Желание изменено');await ctx.close();
});
test('mobile layout has no horizontal overflow',async({page})=>{await page.setViewportSize({width:390,height:844});await login(page,'Юлия');await expect(page.getByRole('button',{name:'Мои желания',exact:true})).toBeVisible();expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();await page.screenshot({path:'artifacts/mobile.png',fullPage:true});});
test('mobile filters can be collapsed and cleared without leaving the archive', async ({page}) => {
 await page.setViewportSize({width:320,height:640});
 await login(page,'Юлия');
 const toggle = page.getByRole('button',{name:/^Фильтры/});
 const price = page.getByLabel('Цена',{exact:true});
 const archive = page.getByRole('checkbox',{name:'Архив'});
 const search = page.getByRole('textbox',{name:'Поиск желаний'});
 await expect(price).toBeHidden();
 await toggle.click();
 await price.selectOption('5000');
 await toggle.click();
 await expect(toggle).toHaveAttribute('aria-expanded','false');
 await expect(toggle).toHaveAccessibleName('Фильтры, активно: 1');
 await archive.check();
 await search.fill('Несуществующее желание '+Date.now());
 await expect(page.getByRole('heading',{name:'Ничего не нашлось'})).toBeVisible();
 await page.getByRole('button',{name:'Сбросить фильтры'}).press('Enter');
 await expect(archive).toBeChecked();
 await expect(search).toBeFocused();
 await expect(search).toHaveValue('');
 await expect(toggle).toHaveAccessibleName('Фильтры');
});
test('desktop wishlist renders without JavaScript errors',async({page})=>{const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));await login(page,'Юлия');await expect(page.locator('article').first()).toBeVisible();await page.screenshot({path:'artifacts/desktop.png',fullPage:true});expect(errors).toEqual([]);});

test('Ohmywishes link errors stay next to the field after a refresh', async ({page}) => {
 await page.setViewportSize({width:320,height:600});
 await login(page,'Юлия');
 await page.route('**/api/ohmywishes', route => route.fulfill({json:{webApiEnabled:true}}));
 const message = 'Этот список недоступен без входа в Ohmywishes.';
 await page.route('**/api/ohmywishes/web-connect', route => route.fulfill({status:400,json:{title:message}}));
 await page.getByRole('button',{name:'Мой профиль',exact:true}).click();
 const dialog = page.getByRole('dialog',{name:'Мой профиль'});
 const section = dialog.getByRole('region',{name:'Ohmywishes'});
 const input = section.getByLabel('Ссылка на ваш список Ohmywishes');
 const link = 'https://ohmywishes.com/users/example/lists/test';
 await input.fill(link);
 await section.getByRole('button',{name:'Подключить по ссылке'}).click();
 await expect(section.getByRole('alert')).toHaveText(message);
 const refreshed = page.waitForResponse(response => new URL(response.url()).pathname === '/api/ohmywishes');
 await page.evaluate(() => window.dispatchEvent(new Event('focus')));
 await refreshed;
 await expect(dialog.getByRole('alert')).toHaveCount(1);
 await expect(input).toHaveValue(link);
 await expect(input).toHaveAccessibleDescription(/Этот список недоступен без входа/);
 await section.getByRole('alert').scrollIntoViewIfNeeded();
 await expect(dialog.getByRole('button',{name:'Закрыть',exact:true})).toBeInViewport();
});

test('Mini App validates signed launch and uses bearer session without cookies',async({page})=>{
 const fields=new Map([['auth_date',Math.floor(Date.now()/1000).toString()],['user',JSON.stringify({id:9000000000,first_name:'Юлия'})]]);
 const data=[...fields].sort(([a],[b])=>a.localeCompare(b)).map(([k,v])=>`${k}=${v}`).join('\n');
 const secret=createHmac('sha256','WebAppData').update('123:test-secret').digest();
 const hash=createHmac('sha256',secret).update(data).digest('hex');const initData=new URLSearchParams([...fields,['hash',hash]]).toString();
 await page.route('https://telegram.org/js/telegram-web-app.js',route=>route.fulfill({contentType:'text/javascript',body:`window.Telegram={WebApp:{initData:${JSON.stringify(initData)},initDataUnsafe:{},colorScheme:'dark',ready(){},expand(){},onEvent(){},BackButton:{onClick(){},show(){},hide(){}}}};`}));
 await page.goto('/');await expect(page.getByRole('heading',{name:'Все желания',exact:true})).toBeVisible();expect(await page.getAttribute('html','data-telegram-theme')).toBe('dark');
 const response=await page.request.get('/api/me');expect(response.status()).toBe(401);await page.getByRole('button',{name:'Добавить желание'}).click();await expect(page.getByRole('dialog')).toBeVisible();await page.getByRole('button',{name:'Закрыть',exact:true}).click();await page.screenshot({path:'artifacts/mini-app-dark.png',fullPage:true});
});
