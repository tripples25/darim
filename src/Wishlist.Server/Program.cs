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
builder.Services.AddSingleton<DarimMetrics>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TelegramLoginCodes>();
if(!builder.Environment.IsEnvironment("Testing"))builder.Services.AddHostedService<TelegramLoginBot>();
builder.Services.AddHttpClient("telegram-login",client=>client.Timeout=TimeSpan.FromSeconds(40)).RemoveAllLoggers()
 .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false});
builder.Services.AddSingleton<OhmywishesOptions>();
builder.Services.AddScoped<OhmywishesSync>();
builder.Services.AddHostedService<OhmywishesWorker>();
builder.Services.AddScoped<WishlistLinks>();
builder.Services.AddHostedService<WishlistRequestWorker>();
builder.Services.AddHttpClient<OhmywishesOAuth>(client=>client.Timeout=TimeSpan.FromSeconds(15))
 .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false});
builder.Services.AddHttpClient<OhmywishesMcp>(client=>client.Timeout=TimeSpan.FromSeconds(15))
 .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false});
builder.Services.AddHttpClient<OhmywishesWeb>(client=>client.Timeout=TimeSpan.FromSeconds(15))
 .RemoveAllLoggers()
 .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false});
builder.Services.AddHttpClient<OhmywishesReservations>(client => client.Timeout = TimeSpan.FromSeconds(8))
 .RemoveAllLoggers()
 .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<OhmywishesPhotos>()
 .RemoveAllLoggers()
 .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
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
app.UseRouting();
var metrics = app.Services.GetRequiredService<DarimMetrics>();
var requestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Darim.Http");
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.Equals("/metrics", StringComparison.OrdinalIgnoreCase) || path.Equals("/metrics/", StringComparison.OrdinalIgnoreCase))
    {
        await metrics.Scrape(context);
        return;
    }

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
        var elapsed = Stopwatch.GetElapsedTime(started);
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (route is null || !route.StartsWith('/')) route = health ? "/health" : "<unmatched>";
        metrics.RecordRequest(context.Request.Method, route, status, elapsed.TotalSeconds);
        if (!health || status >= 400)
        {
            var level = status >= 500 ? LogLevel.Error : status == 429 ? LogLevel.Warning : LogLevel.Information;
            requestLogger.Log(level, "{Method} {Route} responded {StatusCode} in {ElapsedMs:F1} ms (request {RequestId})",
                context.Request.Method, route, status, elapsed.TotalMilliseconds, context.TraceIdentifier);
        }
    }
});
app.Use(async(ctx,next)=>{
 ctx.Response.Headers["X-Content-Type-Options"]="nosniff";ctx.Response.Headers["Referrer-Policy"]="strict-origin-when-cross-origin";
 if(ctx.Request.Path.StartsWithSegments("/share")){
  ctx.Response.Headers["Referrer-Policy"]="no-referrer";
  ctx.Response.Headers["X-Robots-Tag"]="noindex, nofollow";
 }
 if(ctx.Request.Path.StartsWithSegments("/api")||ctx.Request.Path.StartsWithSegments("/auth"))ctx.Response.Headers.CacheControl="no-store";
 try{await next();}catch(RuleException e){await Results.Problem(e.Message,statusCode:e.Status,title:e.Message).ExecuteAsync(ctx);}catch(AntiforgeryValidationException){await Results.Problem("Обновите страницу и повторите действие.",statusCode:400).ExecuteAsync(ctx);}
});
if(!app.Environment.IsDevelopment())app.UseHsts();
app.UseStaticFiles();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
app.MapGet("/health",async(AppDb db)=>await db.Database.CanConnectAsync()?Results.Ok(new{status="ok"}):Results.StatusCode(503));
app.MapGet("/api/auth-info",()=>new AuthInfoDto(AuthSetup.TelegramConfigured(app.Configuration),demo,app.Configuration["Telegram:BotUsername"],app.Configuration.GetValue<bool>("Telegram:CodeLoginEnabled")&&!string.IsNullOrWhiteSpace(app.Configuration["Telegram:BotToken"])));
app.MapGet("/auth/login",(string? returnTo)=>AuthSetup.TelegramConfigured(app.Configuration)?Results.Challenge(new AuthenticationProperties{RedirectUri=returnTo=="share"?"/share":"/"},["Telegram"]):Results.Problem("Настройте Telegram-вход.",statusCode:503)).RequireRateLimiting("auth");
app.MapPost("/auth/code",async(CodeLoginInput input,HttpContext ctx,TelegramLoginCodes codes,AccountService accounts)=>{
 if(!app.Configuration.GetValue<bool>("Telegram:CodeLoginEnabled")||string.IsNullOrWhiteSpace(app.Configuration["Telegram:BotToken"]))return Results.StatusCode(503);
 // Login CSRF: only the site's own browser may establish a cookie session.
 if(!Uri.TryCreate(ctx.Request.Headers.Origin.ToString(),UriKind.Absolute,out var origin)
  ||origin.GetLeftPart(UriPartial.Authority)!=$"{ctx.Request.Scheme}://{ctx.Request.Host}"
  ||ctx.Request.Headers["Sec-Fetch-Site"]=="cross-site")return Results.BadRequest();
 var identity=codes.Consume(input.Code);
 var user=await accounts.Login(identity);
 await ctx.SignInAsync("Cookies",AuthSetup.Principal(user));
 return Results.Ok();
}).RequireRateLimiting("auth");
app.MapPost("/auth/mini",async(MiniLoginInput input,AccountService accounts)=>{
 var identity=TelegramInitData.Validate(input.InitData,app.Configuration["Telegram:BotToken"]??"",DateTimeOffset.UtcNow);
 var u=await accounts.Login(identity);return AuthSetup.MiniToken(u,app.Configuration);
}).RequireRateLimiting("auth");
if(demo)app.MapPost("/auth/demo/{index:int}",async(int index,HttpContext ctx,AppDb db)=>{
 if(index<0||index>=DemoData.Users.Length)return Results.BadRequest();var user=await db.Users.SingleAsync(x=>x.Id==DemoData.Users[index]);await ctx.SignInAsync("Cookies",AuthSetup.Principal(user));return Results.Ok();
});
var api=app.MapGroup("/api").RequireAuthorization();
api.AddEndpointFilter(async(context,next)=>{var ctx=context.HttpContext;if(ctx.Request.Method is not("GET" or "HEAD" or "OPTIONS")&&!ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);return await next(context);});
app.MapWishlistSharing(api);
api.MapCardArtwork();
static Guid UserId(HttpContext ctx)=>Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier),out var id)?id:throw new RuleException("Войдите снова.",401);
app.MapGet(OhmywishesOptions.CallbackPath,async(HttpContext ctx,OhmywishesOAuth oauth,OhmywishesSync sync)=>{
 ctx.Response.Headers["Referrer-Policy"]="no-referrer";
 var browser=await ctx.AuthenticateAsync("Cookies");
 if(!browser.Succeeded || !Guid.TryParse(browser.Principal?.FindFirstValue(ClaimTypes.NameIdentifier),out var id))
  return Results.Redirect("/?ohmywishes=failed");
 try{
  var (attempt,tokens)=await oauth.Complete(ctx,id,ctx.RequestAborted);
  await sync.Connect(id,attempt,tokens,ctx.RequestAborted);
  return Results.Redirect("/?ohmywishes=connected");
 }catch(Exception ex)when(OhmywishesSync.IsProviderFailure(ex,ctx.RequestAborted)||ex is RuleException){
  sync.LogFailure(id,"mcp","oauth-callback",ex);
  return Results.Redirect("/?ohmywishes=failed");
 }
}).RequireRateLimiting("auth");
api.MapGet("/ohmywishes",(HttpContext ctx,OhmywishesSync sync)=>sync.Status(UserId(ctx),ctx.RequestAborted));
api.MapPost("/ohmywishes/connect",async(HttpContext ctx,OhmywishesSync sync,OhmywishesOAuth oauth)=>{
 var id=UserId(ctx);
 var browser=await ctx.AuthenticateAsync("Cookies");
 if(ctx.User.Identity?.AuthenticationType!="Cookies" || !browser.Succeeded)
  throw new RuleException("Подключите Ohmywishes в браузере.",403);
 var attempt=await sync.BeginAuthorization(id,ctx.RequestAborted);
 return new OhmywishesAuthorization(oauth.Start(ctx,id,attempt));
}).RequireRateLimiting("auth");
api.MapPost("/ohmywishes/web-connect",async(HttpContext ctx,OhmywishesWebSelection input,OhmywishesSync sync)=>{
 await sync.ConnectWeb(UserId(ctx),input.Url,ctx.RequestAborted,input.CompanyId);
 return Results.NoContent();
}).RequireRateLimiting("auth");
api.MapGet("/ohmywishes/lists",(HttpContext ctx,OhmywishesSync sync)=>sync.Lists(UserId(ctx),ctx.RequestAborted));
api.MapPut("/ohmywishes/list",async(HttpContext ctx,OhmywishesSelection input,OhmywishesSync sync)=>{
 await sync.Select(UserId(ctx),input.ListId,ctx.RequestAborted,input.CompanyId);
 return Results.NoContent();
});
api.MapPost("/ohmywishes/disconnect",async(HttpContext ctx,OhmywishesSync sync)=>{
 await sync.Disconnect(UserId(ctx),ctx.RequestAborted);
 return Results.NoContent();
});
api.MapGet("/csrf",(HttpContext ctx,IAntiforgery csrf)=>new CsrfDto(csrf.GetAndStoreTokens(ctx).RequestToken!));
api.MapGet("/me",(HttpContext ctx,AccountService svc)=>svc.Me(UserId(ctx)));
api.MapPut("/profile",async(HttpContext ctx,ProfileInput input,AccountService svc)=>{await svc.Profile(UserId(ctx),input);return Results.NoContent();});
api.MapPost("/company",(HttpContext ctx,CreateCompanyInput input,AccountService svc)=>svc.Create(UserId(ctx),input.Name));
api.MapPost("/company/join",async(HttpContext ctx,JoinCompanyInput input,bool? returnCompany,AccountService svc)=>{
 var id=await svc.Join(UserId(ctx),input.Token);
 return returnCompany==true?Results.Ok(new CompanyJoinDto(id)):Results.NoContent();
});
api.MapPost("/company/invite",(HttpContext ctx,InviteOptions? input,AccountService svc)=>svc.Invite(UserId(ctx),input is null?1:input.MaxUses,input?.CompanyId));
api.MapGet("/gifts", (HttpContext ctx, Guid? companyId, GiftService svc, bool includeMine = true) =>
    svc.List(UserId(ctx), companyId, includeMine));
api.MapGet("/gifts/mine",(HttpContext ctx,GiftService svc)=>svc.Mine(UserId(ctx)));
api.MapGet("/gifts/giving",(HttpContext ctx,GiftService svc)=>svc.Giving(UserId(ctx)));
api.MapPost("/gifts",(HttpContext ctx,GiftWriteInput input,GiftService svc)=>svc.Create(UserId(ctx),input,ctx.RequestAborted));
api.MapPut("/gifts/{id:guid}",(Guid id,HttpContext ctx,GiftWriteInput input,GiftService svc)=>svc.Change(UserId(ctx),id,input,ct:ctx.RequestAborted));
api.MapPut("/gifts/{id:guid}/visibility",(Guid id,HttpContext ctx,GiftVisibility input,GiftService svc)=>svc.Change(UserId(ctx),id,visibility:input,ct:ctx.RequestAborted));
api.MapGet("/gifts/{id:guid}/photo/{version:guid}",async (Guid id,Guid version,HttpContext ctx,GiftService svc)=>{
 ctx.Response.Headers.CacheControl="private, no-store";
 ctx.Response.Headers.XContentTypeOptions="nosniff";
 var photo=await svc.Photo(UserId(ctx),id,version,ctx.RequestAborted);
 return photo==null?Results.NotFound():Results.File(photo.Data,photo.ContentType);
});
api.MapPost("/gifts/{id:guid}/actions",(Guid id,HttpContext ctx,GiftActionInput input,GiftService svc)=>svc.Change(UserId(ctx),id,action:input,ct:ctx.RequestAborted));
api.MapPost("/gifts/{id:guid}/archive",(Guid id,HttpContext ctx,GiftService svc)=>svc.Change(UserId(ctx),id,archive:true,ct:ctx.RequestAborted));
api.MapPost("/gifts/{id:guid}/restore",(Guid id,HttpContext ctx,GiftService svc)=>svc.Change(UserId(ctx),id,archive:false,ct:ctx.RequestAborted));
api.MapGet("/notices",async(HttpContext ctx,AppDb db)=>{var id=UserId(ctx);return await db.Notices.Where(x=>x.UserId==id).OrderByDescending(x=>x.Created).Take(100).Select(x=>new NoticeDto(x.Id,x.Text,x.Created,x.Read)).ToArrayAsync();});
api.MapPost("/notices/read",async(HttpContext ctx,AppDb db)=>{var id=UserId(ctx);await db.Notices.Where(x=>x.UserId==id&&!x.Read).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Read,true));return Results.NoContent();});
api.MapPost("/logout",async(HttpContext ctx)=>{await ctx.SignOutAsync("Cookies");return Results.NoContent();});
app.MapOpenApi().RequireAuthorization();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Darim API v1");
        options.UseRequestInterceptor("""
            (request) => {
                const url = new URL(request.url, window.location.href);
                const method = request.method?.toUpperCase() ?? "GET";
                if (url.origin === window.location.origin && url.pathname.startsWith("/api/") &&
                    !["GET", "HEAD", "OPTIONS"].includes(method)) {
                    return fetch("/api/csrf").then(async (response) => {
                        if (!response.ok) throw new Error("Войдите в Дарим и обновите Swagger UI.");
                        const { token } = await response.json();
                        request.headers["X-CSRF-TOKEN"] = token;
                        return request;
                    });
                }
                return request;
            }
            """);
    });
}
app.UseEndpoints(_ => { });
app.Run(async context =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await context.ChallengeAsync();
            return;
        }

        await Results.Problem(statusCode: StatusCodes.Status404NotFound).ExecuteAsync(context);
        return;
    }

    if (path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase))
    {
        await Results.Problem(statusCode: StatusCodes.Status404NotFound).ExecuteAsync(context);
        return;
    }

    if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
        !Path.HasExtension(path.Value))
    {
        var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
        if (!index.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using var stream = index.CreateReadStream();
        await Results.Stream(stream, "text/html").ExecuteAsync(context);
        return;
    }

    context.Response.StatusCode = StatusCodes.Status404NotFound;
});
app.Run();
public partial class Program { }
