import { defineConfig } from '@playwright/test';
export default defineConfig({
 timeout:90000,expect:{timeout:15000},testDir:'./tests/browser',workers:1,fullyParallel:false,retries:0,
 use:{baseURL:'http://127.0.0.1:5080',headless:true,trace:'retain-on-failure',launchOptions:{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE}},
 webServer:{command:process.env.DARIM_PUBLISHED_TEST?'dotnet Wishlist.Server.dll --urls http://127.0.0.1:5080':'dotnet run --project src/Wishlist.Server --no-launch-profile --urls http://127.0.0.1:5080',cwd:process.env.DARIM_PUBLISHED_TEST?'./artifacts/publish':'.',url:'http://127.0.0.1:5080/health',reuseExistingServer:!process.env.CI,timeout:120000,env:{ASPNETCORE_ENVIRONMENT:'Development',Telegram__BotToken:'123:test-secret'}},
 projects:[{name:'chromium',use:{viewport:{width:1440,height:1000}}}],
});
