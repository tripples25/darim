import { test, expect } from '@playwright/test';

test('code login stays on the site and allows retry after a bad code', async ({ page }) => {
 await page.route('**/api/auth-info', route => route.fulfill({json:{telegramConfigured:true,demoEnabled:false,botUsername:'darim_test_bot',codeLoginEnabled:true}}));
 await page.route('**/auth/code', async route => {
  expect(route.request().postDataJSON()).toEqual({code:'ABCDEF123456'});
  await route.fulfill({status:401,json:{title:'Код неверный или истёк. Отправьте боту /login и попробуйте снова.'}});
 });
 await page.goto('/?invite=keep-this-invite');
 await page.getByText('Войти по коду',{exact:true}).click();
 await expect(page.getByRole('link',{name:'@darim_test_bot'})).toHaveAttribute('href','https://t.me/darim_test_bot');
 await expect(page.getByRole('button',{name:'Войти',exact:true})).toBeDisabled();
 await page.getByLabel('Код из Telegram').fill('ABCDEF123456');
 await page.getByRole('button',{name:'Войти',exact:true}).click();
 await expect(page.getByRole('alert')).toContainText('Код неверный или истёк');
 await expect(page.getByRole('button',{name:'Войти',exact:true})).toBeEnabled();
 await expect(page).toHaveURL(/invite=keep-this-invite/);
 await page.setViewportSize({width:390,height:844});
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
 await page.screenshot({path:'artifacts/code-login-mobile.png',fullPage:true});
});
