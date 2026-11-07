using System.Diagnostics;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;
using Wishlist.Server.Services;

var builder=WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(o=>o.UseNpgsql(builder.Configuration.GetConnectionString("Database")??throw new InvalidOperationException("Set ConnectionStrings__Database.")));
builder.Services.AddScoped<GiftService>();builder.Services.AddScoped<AccountService>();
builder.AddSessions();builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o=>{o.HeaderName="X-CSRF-TOKEN";o.Cookie.Name="wishlist.csrf";o.Cookie.SecurePolicy=builder.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;});
builder.Services.AddOpenApi();builder.Services.AddDataProtection().SetApplicationName("wishlist");
if(builder.Configuration["DataProtection:Path"] is {} keys)builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys));
builder.Services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("auth",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new(){PermitLimit=30,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
builder.Services.Configure<ForwardedHeadersOptions>(o=>{o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto; /* Kestrel is private behind the local proxy; configure its address explicitly. */if(System.Net.IPAddress.TryParse(builder.Configuration["Proxy:Address"],out var ip))o.KnownProxies.Add(ip);});
var app=builder.Build();
if (args.Contains("--migrate"))
{
    var started = Stopwatch.GetTimestamp();
    app.Logger.LogInformation("Database migration started");
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDb>().Database.MigrateAsync();
    app.Logger.LogInformation("Database migration completed in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    return;
}
var demo=app.Environment.IsDevelopment()&&app.Configuration.GetValue<bool>("Demo:Enabled");
if(demo){await using var scope=app.Services.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<AppDb>();await db.Database.MigrateAsync();await DemoData.Seed(db);}
app.UseForwardedHeaders();
var requestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Darim.Http");
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var health = path.Equals("/health", StringComparison.OrdinalIgnoreCase);
    if (!health && !path.StartsWithSegments("/api") && !path.StartsWithSegments("/auth") && !path.Equals("/logout"))
    {
        await next();
        return;
    }

    var started = Stopwatch.GetTimestamp();
    var status = StatusCodes.Status500InternalServerError;
    try
    {
        await next();
        status = context.Response.StatusCode;
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        status = 499;
        throw;
    }
    finally
    {
        if (!health || status >= 400)
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                ?? (health ? "/health" : "<unmatched>");
            var level = status >= 500 ? LogLevel.Error : status == 429 ? LogLevel.Warning : LogLevel.Information;
            requestLogger.Log(level, "{Method} {Route} responded {StatusCode} in {ElapsedMs:F1} ms (request {RequestId})",
                context.Request.Method, route, status, Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.TraceIdentifier);
        }
    }
});
app.Use(async(ctx,next)=>{
 ctx.Response.Headers["X-Content-Type-Options"]="nosniff";ctx.Response.Headers["Referrer-Policy"]="strict-origin-when-cross-origin";
 if(ctx.Request.Path.StartsWithSegments("/api")||ctx.Request.Path.StartsWithSegments("/auth"))ctx.Response.Headers.CacheControl="no-store";
 try{await next();}catch(RuleException e){await Results.Problem(e.Message,statusCode:e.Status,title:e.Message).ExecuteAsync(ctx);}catch(AntiforgeryValidationException){await Results.Problem("Обновите страницу и повторите действие.",statusCode:400).ExecuteAsync(ctx);}
});
if(!app.Environment.IsDevelopment())app.UseHsts();
app.UseStaticFiles();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
app.MapGet("/health",async(AppDb db)=>await db.Database.CanConnectAsync()?Results.Ok(new{status="ok"}):Results.StatusCode(503));
app.MapGet("/api/auth-info",()=>new AuthInfoDto(AuthSetup.TelegramConfigured(app.Configuration),demo,app.Configuration["Telegram:BotUsername"]));
app.MapGet("/auth/login",()=>AuthSetup.TelegramConfigured(app.Configuration)?Results.Challenge(new AuthenticationProperties{RedirectUri="/"},["Telegram"]):Results.Problem("Настройте Telegram-вход.",statusCode:503)).RequireRateLimiting("auth");
app.MapPost("/auth/mini",async(MiniLoginInput input,AccountService accounts)=>{
 var identity=TelegramInitData.Validate(input.InitData,app.Configuration["Telegram:BotToken"]??"",DateTimeOffset.UtcNow);
 var u=await accounts.Login(identity);return AuthSetup.MiniToken(u,app.Configuration);
}).RequireRateLimiting("auth");
if(demo)app.MapPost("/auth/demo/{index:int}",async(int index,HttpContext ctx,AppDb db)=>{
 if(index<0||index>=DemoData.Users.Length)return Results.BadRequest();var user=await db.Users.SingleAsync(x=>x.Id==DemoData.Users[index]);await ctx.SignInAsync("Cookies",AuthSetup.Principal(user));return Results.Ok();
});
var api=app.MapGroup("/api").RequireAuthorization();
api.AddEndpointFilter(async(context,next)=>{var ctx=context.HttpContext;if(ctx.Request.Method is not("GET" or "HEAD" or "OPTIONS")&&!ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);return await next(context);});
static Guid UserId(HttpContext ctx)=>Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier),out var id)?id:throw new RuleException("Войдите снова.",401);
api.MapGet("/csrf",(HttpContext ctx,IAntiforgery csrf)=>new CsrfDto(csrf.GetAndStoreTokens(ctx).RequestToken!));
api.MapGet("/me",(HttpContext ctx,AccountService svc)=>svc.Me(UserId(ctx)));
api.MapPut("/profile",async(HttpContext ctx,ProfileInput input,AccountService svc)=>{await svc.Profile(UserId(ctx),input);return Results.NoContent();});
api.MapPost("/company",(HttpContext ctx,CreateCompanyInput input,AccountService svc)=>svc.Create(UserId(ctx),input.Name));
api.MapPost("/company/join",async(HttpContext ctx,JoinCompanyInput input,AccountService svc)=>{await svc.Join(UserId(ctx),input.Token);return Results.NoContent();});
api.MapPost("/company/invite",(HttpContext ctx,InviteOptions? input,AccountService svc)=>svc.Invite(UserId(ctx),input is null?1:input.MaxUses));
api.MapGet("/gifts",(HttpContext ctx,GiftService svc)=>svc.List(UserId(ctx)));
api.MapPost("/gifts",(HttpContext ctx,GiftInput input,GiftService svc)=>svc.Create(UserId(ctx),input));
api.MapPut("/gifts/{id:guid}",(Guid id,HttpContext ctx,GiftInput input,GiftService svc)=>svc.Change(UserId(ctx),id,input));
api.MapPost("/gifts/{id:guid}/actions",(Guid id,HttpContext ctx,GiftActionInput input,GiftService svc)=>svc.Change(UserId(ctx),id,action:input));
api.MapPost("/gifts/{id:guid}/archive",(Guid id,HttpContext ctx,GiftService svc)=>svc.Change(UserId(ctx),id,archive:true));
api.MapGet("/notices",async(HttpContext ctx,AppDb db)=>{var id=UserId(ctx);return await db.Notices.Where(x=>x.UserId==id).OrderByDescending(x=>x.Created).Take(100).Select(x=>new NoticeDto(x.Id,x.Text,x.Created,x.Read)).ToArrayAsync();});
api.MapPost("/notices/read",async(HttpContext ctx,AppDb db)=>{var id=UserId(ctx);await db.Notices.Where(x=>x.UserId==id&&!x.Read).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Read,true));return Results.NoContent();});
api.MapPost("/logout",async(HttpContext ctx)=>{await ctx.SignOutAsync("Cookies");return Results.NoContent();});
app.MapOpenApi().RequireAuthorization();
app.MapStaticAssets();
app.MapFallbackToFile("index.html");
app.Run();
public partial class Program { }
