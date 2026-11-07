using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;

namespace Wishlist.Server.Services;

public sealed class OhmywishesSync(AppDb db, OhmywishesOptions options, OhmywishesOAuth oauth,
    OhmywishesMcp mcp, IDataProtectionProvider protection, ILogger<OhmywishesSync> logger, OhmywishesWeb web)
{
    private readonly IDataProtector tokenProtector = protection.CreateProtector("ohmywishes.tokens.v1");
    private readonly IDataProtector webLinkProtector = protection.CreateProtector("ohmywishes.share-link.v1");

    public async Task<OhmywishesStatusDto> Status(Guid userId, CancellationToken ct)
    {
        var connection = await db.OhmywishesConnections.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        var webEnabled = options.WebApiTelegramId is { } pilot && await db.Users.AnyAsync(
            x => x.Id == userId && x.TelegramId == pilot && x.CompanyId != null, ct);
        var webConnected = connection?.WebApi == true;
        return new(options.Enabled, webConnected || connection?.ProtectedTokens != null,
            connection is { WebApi: false, ProtectedTokens: null },
            connection?.ListId, connection?.ListTitle, connection?.LastSuccess,
            connection?.Stale == true || (!(webConnected ? webEnabled : options.Enabled) && connection?.ListId != null),
            webEnabled, webConnected);
    }

    public async Task ConnectWeb(Guid userId, string? url, CancellationToken ct)
    {
        var pilot = options.WebApiTelegramId;
        if (pilot == null || !await db.Users.AnyAsync(
            x => x.Id == userId && x.TelegramId == pilot && x.CompanyId != null, ct))
            throw new RuleException("Подключение недоступно.", 404);
        var link = OhmywishesWeb.ParseLink(url)
            ?? throw new RuleException("Скопируйте ссылку через «Поделиться» в отдельном списке Ohmywishes.", 400);
        var previous = await db.OhmywishesConnections.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (previous is { WebApi: false }) throw new RuleException("Сначала отключите текущее подключение Ohmywishes.");
        OhmywishesWeb.PublicListSource? source;
        try { source = await web.Resolve(link, ct); }
        catch (Exception ex) when (IsProviderFailure(ex, ct))
        { throw new RuleException("Не удалось прочитать список Ohmywishes. Попробуйте позже.", 502); }
        if (source == null) throw new RuleException("Этот список недоступен без входа в Ohmywishes.", 400);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockCompanyMember(userId, ct);
        if (!options.AllowsWebApi(user.TelegramId)) throw new RuleException("Подключение недоступно.", 404);
        var connection = await db.OhmywishesConnections.FindAsync([userId], ct);
        if (connection?.Revision != previous?.Revision)
            throw new RuleException("Подключение изменилось. Попробуйте ещё раз.");
        if (connection == null)
        {
            connection = new() { UserId = userId, WebApi = true };
            db.OhmywishesConnections.Add(connection);
        }
        else if (connection.AccountId != source.AccountId || connection.ListId != source.ListId)
        {
            await HideGifts(userId, ct);
            ResetList(connection);
        }
        user.OhmywishesAttempt = null;
        connection.AccountId = source.AccountId;
        connection.ListId = source.ListId;
        connection.ListTitle = source.Title;
        connection.ProtectedTokens = source.ShareToken == null ? null : webLinkProtector.Protect(source.ShareToken);
        connection.Revision = Guid.NewGuid();
        connection.NextSync = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<Guid> BeginAuthorization(Guid userId, CancellationToken ct)
    {
        if (!options.Enabled) throw new RuleException("Подключение Ohmywishes ещё не настроено.", 503);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockCompanyMember(userId, ct);
        var attempt = Guid.NewGuid();
        user.OhmywishesAttempt = attempt;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return attempt;
    }

    public async Task Connect(Guid userId, Guid attempt, OhmywishesTokens tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tokens.Refresh)) throw new InvalidDataException("Missing Ohmywishes refresh token.");
        var account = await mcp.Account(tokens.Access, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockCompanyMember(userId, ct);
        if (user.OhmywishesAttempt != attempt) throw new RuleException("Подключение изменилось. Начните заново.");
        user.OhmywishesAttempt = null;
        var connection = await db.OhmywishesConnections.FindAsync([userId], ct);
        if (connection == null)
        {
            connection = new() { UserId = userId, AccountId = account };
            db.OhmywishesConnections.Add(connection);
        }
        else if (connection.WebApi || connection.AccountId != account)
        {
            await HideGifts(userId, ct);
            connection.AccountId = account;
            ResetList(connection);
        }
        connection.Revision = Guid.NewGuid();
        connection.WebApi = false;
        StoreTokens(connection, tokens);
        connection.NextSync = connection.ListId == null ? null : DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<OhmywishesListDto[]> Lists(Guid userId, CancellationToken ct) => (await ReadLists(userId, ct)).Lists;

    public async Task Select(Guid userId, string? listId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(listId) || listId.Length > 200)
            throw new RuleException("Выберите список Ohmywishes.", 400);
        var (revision, lists) = await ReadLists(userId, ct);
        var selected = lists.SingleOrDefault(x => x.Id == listId)
            ?? throw new RuleException("Этот список недоступен для импорта.", 400);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockCompanyMember(userId, ct);
        var connection = await db.OhmywishesConnections.FindAsync([userId], ct);
        if (connection?.Revision != revision)
            throw new RuleException("Подключение изменилось. Откройте список снова.");
        if (connection.ListId != selected.Id)
        {
            await HideGifts(userId, ct);
            ResetList(connection);
        }
        connection.ListId = selected.Id;
        connection.ListTitle = selected.Title;
        connection.Revision = Guid.NewGuid();
        connection.NextSync = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task Disconnect(Guid userId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockCompanyMember(userId, ct);
        user.OhmywishesAttempt = null;
        if (await db.OhmywishesConnections.FindAsync([userId], ct) is { } connection)
            db.OhmywishesConnections.Remove(connection);
        await HideGifts(userId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<(Guid Revision, OhmywishesListDto[] Lists)> ReadLists(Guid userId, CancellationToken ct)
    {
        if (!options.Enabled) throw new RuleException("Подключение Ohmywishes ещё не настроено.", 503);
        var revision = await db.OhmywishesConnections.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => (Guid?)x.Revision).SingleOrDefaultAsync(ct)
            ?? throw new RuleException("Подключите Ohmywishes.");
        SyncJob? job = null;
        try
        {
            job = await Claim(userId, revision, scheduled: false, ct)
                ?? throw new RuleException("Переподключите Ohmywishes.");
            return (revision, await mcp.Lists(job.Access!, ct));
        }
        catch (Exception ex) when (IsProviderFailure(ex, ct))
        {
            var reconnect = ex is OhmywishesAccessException;
            if (reconnect && job != null) await RecordFailure(userId, revision, job.ProtectedTokens, true, ct);
            throw new RuleException(reconnect ? "Переподключите Ohmywishes." : "Не удалось получить списки Ohmywishes.", reconnect ? 409 : 502);
        }
    }

    public async Task RunDue(CancellationToken ct)
    {
        var mcpEnabled = options.Enabled;
        var pilot = options.WebApiTelegramId;
        if (!mcpEnabled && pilot == null) return;
        var due = await db.OhmywishesConnections.AsNoTracking()
            .Where(x => (mcpEnabled && !x.WebApi) || (x.WebApi && pilot != null
                && db.Users.Any(user => user.Id == x.UserId && user.TelegramId == pilot)))
            .Where(x => x.NextSync <= DateTimeOffset.UtcNow).OrderBy(x => x.NextSync).Take(20)
            .Select(x => new { x.UserId, x.Revision }).ToListAsync(ct);
        foreach (var entry in due)
        {
            SyncJob? job = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                job = await Claim(entry.UserId, entry.Revision, scheduled: true, deadline.Token);
                if (job?.ListId != null)
                {
                    var snapshot = job.WebApi
                        ? await web.Snapshot(job.AccountId, job.ListId, deadline.Token, job.Access)
                        : await mcp.Snapshot(job.Access!, job.ListId, deadline.Token);
                    await Apply(entry.UserId, entry.Revision, job.AccountId, snapshot, deadline.Token);
                }
            }
            catch (Exception ex) when (IsProviderFailure(ex, ct))
            {
                logger.LogWarning("Ohmywishes refresh failed for user {UserId} ({Failure}).", entry.UserId, ex.GetType().Name);
                await RecordFailure(entry.UserId, entry.Revision, job?.ProtectedTokens,
                    ex is OhmywishesAccessException or CryptographicException, ct);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<SyncJob?> Claim(Guid userId, Guid revision, bool scheduled, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockCompanyMember(userId, ct);
        var connection = await db.OhmywishesConnections.FindAsync([userId], ct);
        if (connection?.Revision != revision ||
            (scheduled && (connection.NextSync == null || connection.NextSync > DateTimeOffset.UtcNow)))
            return null;

        if (connection.WebApi)
        {
            if (!scheduled || !options.AllowsWebApi(user.TelegramId)) return null;
            var shareToken = connection.ProtectedTokens == null ? null : webLinkProtector.Unprotect(connection.ProtectedTokens);
            connection.NextSync = DateTimeOffset.UtcNow.AddMinutes(Math.Max(3, options.Interval.TotalMinutes));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            var webJob = new SyncJob(shareToken, connection.AccountId, connection.ListId, connection.ProtectedTokens, true);
            db.ChangeTracker.Clear();
            return webJob;
        }
        if (!options.Enabled || connection.ProtectedTokens == null) return null;

        OhmywishesTokens tokens;
        try
        {
            tokens = JsonSerializer.Deserialize<OhmywishesTokens>(tokenProtector.Unprotect(connection.ProtectedTokens))
                ?? throw new InvalidDataException("Invalid stored Ohmywishes tokens.");
            if (tokens.Expires <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                if (tokens.Refresh == null) throw new OhmywishesAccessException();
                var refreshed = await oauth.Refresh(tokens.Refresh, ct);
                tokens = refreshed with { Refresh = refreshed.Refresh ?? tokens.Refresh };
                StoreTokens(connection, tokens);
            }
        }
        catch (Exception ex) when (ex is OhmywishesAccessException or CryptographicException)
        {
            await Invalidate(connection, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
            return null;
        }
        if (scheduled) connection.NextSync = DateTimeOffset.UtcNow.AddMinutes(Math.Max(3, options.Interval.TotalMinutes));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var job = new SyncJob(tokens.Access, connection.AccountId, connection.ListId, connection.ProtectedTokens, false);
        db.ChangeTracker.Clear();
        return job;
    }

    private async Task Apply(Guid userId, Guid revision, string accountId, Dictionary<string, GiftInput> snapshot, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockCompanyMember(userId, ct);
        var connection = await db.OhmywishesConnections.FindAsync([userId], ct);
        if (connection?.Revision != revision ||
            (connection.WebApi ? !options.AllowsWebApi(user.TelegramId) : connection.ProtectedTokens == null)) return;

        var mapped = snapshot.ToDictionary(item => Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { accountId, item.Key }))), item => item.Value);
        var existing = await LockGifts(userId, ct);
        foreach (var gift in existing)
        {
            if (gift.CompanyId != user.CompanyId || !mapped.Remove(gift.OhmywishesKey!, out var input))
            {
                Hide(gift);
                continue;
            }
            if (gift.Details != input)
            {
                gift.Edit(userId, input);
                Notify(gift, "Желание изменилось в Ohmywishes.");
            }
            gift.OhmywishesHidden = false;
        }
        foreach (var item in mapped)
        {
            var gift = Gift.New(user.CompanyId!.Value, userId, item.Value);
            gift.OhmywishesKey = item.Key;
            db.Gifts.Add(gift);
        }
        connection.LastSuccess = DateTimeOffset.UtcNow;
        connection.NextSync = connection.LastSuccess.Value.Add(options.Interval);
        connection.Stale = false;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task RecordFailure(Guid userId, Guid revision, string? usedTokens, bool reconnect, CancellationToken ct)
    {
        // A failed refresh or snapshot can leave tracked data from a rolled-back transaction.
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockCompanyMember(userId, ct);
        var connection = await db.OhmywishesConnections.FindAsync([userId], ct);
        if (connection?.Revision != revision || (usedTokens != null && connection.ProtectedTokens != usedTokens)) return;
        connection.Stale = true;
        if (reconnect) await Invalidate(connection, ct);
        else if (connection.ListId != null)
            connection.NextSync = DateTimeOffset.UtcNow.Add(options.Interval);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task Invalidate(OhmywishesConnection connection, CancellationToken ct)
    {
        connection.ProtectedTokens = null;
        connection.NextSync = null;
        connection.Revision = Guid.NewGuid();
        connection.Stale = true;
        await HideGifts(connection.UserId, ct);
    }

    private async Task<AppUser> LockCompanyMember(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FromSqlInterpolated($"""SELECT * FROM "Users" WHERE "Id"={userId} FOR NO KEY UPDATE""")
            .SingleOrDefaultAsync(ct);
        return user?.CompanyId != null ? user : throw new RuleException("Сначала вступите в компанию.", 403);
    }

    private async Task<List<Gift>> LockGifts(Guid userId, CancellationToken ct)
    {
        var gifts = await db.Gifts.FromSqlInterpolated($"""
            SELECT * FROM "Gifts" WHERE "OwnerId"={userId} AND "OhmywishesKey" IS NOT NULL ORDER BY "Id" FOR UPDATE
            """).ToListAsync(ct);
        var ids = gifts.Select(x => x.Id).ToArray();
        await db.Shares.Where(x => ids.Contains(x.GiftId)).LoadAsync(ct);
        return gifts;
    }

    private async Task HideGifts(Guid userId, CancellationToken ct)
    {
        foreach (var gift in await LockGifts(userId, ct)) Hide(gift);
    }

    private void Hide(Gift gift)
    {
        if (gift.OhmywishesHidden) return;
        gift.OhmywishesHidden = true;
        Notify(gift, "Одно из желаний Ohmywishes, которое вы выбрали, больше недоступно.");
    }

    private void Notify(Gift gift, string text)
    {
        foreach (var donor in gift.Donors())
            db.Notices.Add(new() { UserId = donor, Text = text });
    }

    private void StoreTokens(OhmywishesConnection connection, OhmywishesTokens tokens) =>
        connection.ProtectedTokens = tokenProtector.Protect(JsonSerializer.Serialize(tokens));

    private static void ResetList(OhmywishesConnection connection)
    {
        connection.ListId = null;
        connection.ListTitle = null;
        connection.LastSuccess = null;
        connection.Stale = false;
    }

    internal static bool IsProviderFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or InvalidDataException or JsonException or OhmywishesAccessException or CryptographicException
        || ex is OperationCanceledException && !ct.IsCancellationRequested;

    private sealed record SyncJob(string? Access, string AccountId, string? ListId, string? ProtectedTokens, bool WebApi);
}

public sealed class OhmywishesWorker(IServiceScopeFactory scopes,
    ILogger<OhmywishesWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OhmywishesSync>().RunDue(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Ohmywishes worker could not run ({Failure}).", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
