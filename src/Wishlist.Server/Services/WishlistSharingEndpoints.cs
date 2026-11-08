using System.Security.Claims;
using Wishlist.Contracts;
using Wishlist.Core;

namespace Wishlist.Server.Services;

public static class WishlistSharingEndpoints
{
    public static void MapWishlistSharing(this WebApplication app, RouteGroupBuilder api)
    {
        var publicLists = app.MapGroup("/api/public/wishlist");
        publicLists.AddEndpointFilter(async (context, next) =>
        {
            var headers = context.HttpContext.Response.Headers;
            headers.CacheControl = "private, no-store";
            headers["Referrer-Policy"] = "no-referrer";
            return await next(context);
        });
        publicLists.MapGet("", (HttpContext ctx, WishlistLinks service, CancellationToken ct) =>
            service.View(Token(ctx), ct));
        publicLists.MapGet("/gifts/{giftId:guid}/photo/{version:guid}", async (
            Guid giftId, Guid version, HttpContext ctx, WishlistLinks service, CancellationToken ct) =>
        {
            var photo = await service.Photo(Token(ctx), giftId, version, ct);
            return Results.File(photo.Data, photo.ContentType);
        });

        var links = api.MapGroup("/wishlist-links");
        links.MapGet("", (HttpContext ctx, WishlistLinks service, CancellationToken ct) => service.List(UserId(ctx), ct));
        links.MapPost("", (HttpContext ctx, WishlistLinkInput input, WishlistLinks service, CancellationToken ct) =>
            service.Create(UserId(ctx), input, ct));
        links.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, WishlistLinks service, CancellationToken ct) =>
        {
            await service.Revoke(UserId(ctx), id, ct);
            return Results.NoContent();
        });
        links.MapPost("/gifts/{giftId:guid}/request", async (Guid giftId, HttpContext ctx,
            WishlistRequestInput input, WishlistLinks service, CancellationToken ct) =>
        {
            await service.Submit(UserId(ctx), input.Token ?? "", giftId, input, ct);
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        var requests = api.MapGroup("/wishlist-requests");
        requests.MapGet("", (HttpContext ctx, WishlistLinks service, CancellationToken ct) => service.ForGuest(UserId(ctx), ct));
        requests.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, WishlistLinks service, CancellationToken ct) =>
        {
            await service.Withdraw(UserId(ctx), id, ct);
            return Results.NoContent();
        });

        var interest = api.MapGroup("/wishlist-interest");
        interest.MapGet("", (HttpContext ctx, WishlistLinks service, CancellationToken ct) => service.ForDonor(UserId(ctx), ct));
        interest.MapPost("/{id:guid}/notify", async (Guid id, HttpContext ctx, WishlistLinks service, CancellationToken ct) =>
        {
            await service.Notify(UserId(ctx), id, ct);
            return Results.NoContent();
        });
    }

    private static string Token(HttpContext ctx)
    {
        const string prefix = "Wishlist ";
        var header = ctx.Request.Headers.Authorization.ToString();
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..] : "";
    }

    private static Guid UserId(HttpContext ctx) =>
        Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id : throw new RuleException("Войдите снова.", 401);
}
