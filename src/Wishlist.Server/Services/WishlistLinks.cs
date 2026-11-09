using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;
using static Wishlist.Server.Data.WishlistReservation;

namespace Wishlist.Server.Services;

public sealed class WishlistLinks(AppDb db, OhmywishesOptions? options = null, TimeProvider? timeProvider = null,
    DarimMetrics? metrics = null)
{
    private DbSet<WishlistLink> Links => db.Set<WishlistLink>();
    private DbSet<WishlistReservation> Requests => db.Set<WishlistReservation>();
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();

    public async Task<WishlistLinkDto[]> List(Guid ownerId, CancellationToken ct) =>
        await Links.Where(x => x.OwnerId == ownerId).OrderByDescending(x => x.Created)
            .Select(x => new WishlistLinkDto(x.Id, x.CompanyId, x.Created, x.Revoked, x.Label)).ToArrayAsync(ct);

    public async Task<WishlistLinkCreated> Create(Guid ownerId, WishlistLinkInput input, CancellationToken ct)
    {
        var label = input.Label?.Trim();
        if (label?.Length > 80) throw new RuleException("Название ссылки должно быть не длиннее 80 символов.", 400);
        if (input.CompanyId is { } companyId &&
            !await db.Memberships.AnyAsync(x => x.UserId == ownerId && x.CompanyId == companyId, ct))
            throw new RuleException("Компания не найдена.", 404);

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var link = new WishlistLink
        {
            OwnerId = ownerId,
            CompanyId = input.CompanyId,
            Label = string.IsNullOrEmpty(label) ? null : label,
            Created = Now,
            TokenHash = AccountService.Hash(token)
        };
        Links.Add(link);
        await db.SaveChangesAsync(ct);
        return new(new(link.Id, link.CompanyId, link.Created, link.Revoked, link.Label), token);
    }

    public async Task Revoke(Guid ownerId, Guid linkId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var link = await Links.FromSqlInterpolated($"""
            SELECT * FROM "WishlistLinks" WHERE "Id"={linkId} AND "OwnerId"={ownerId} FOR UPDATE
            """).SingleOrDefaultAsync(ct) ?? throw MissingLink();
        if (link.Revoked != null) return;

        // Submissions take a shared lock on this link before the gift lock. Thus this
        // is the complete set of its pending gifts, even when submissions race revocation.
        var gifts = await Requests.Where(x => x.LinkId == link.Id && (x.Status == Held || x.Status == Waiting))
            .Select(x => x.GiftId).Distinct().OrderBy(x => x).ToArrayAsync(ct);
        link.Revoked = Now;
        foreach (var giftId in gifts)
        {
            if (await LockGift(giftId, ct) is { } gift) await Reconcile(gift, ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<PublicWishlistDto> View(string token, CancellationToken ct)
    {
        var link = LinkByToken(token);
        var owner = await (from active in link
                           join user in db.Users on active.OwnerId equals user.Id
                           select user.Name).SingleOrDefaultAsync(ct) ?? throw MissingLink();
        var gifts = await VisibleGifts(link).AsNoTracking().OrderByDescending(x => x.Created).ToArrayAsync(ct);
        var managedOwners = await ManagedWebOwners().Where(id => link.Any(x => x.OwnerId == id)).ToArrayAsync(ct);
        return new(owner, gifts.Select(x => new PublicWishDto(x.Id, x.Details, x.OhmywishesPhotoUrl, x.PhotoVersion, !x.Unlimited && (x.OhmywishesKey == null || !managedOwners.Contains(x.OwnerId)))).ToArray());
    }

    public async Task<GiftPhoto> Photo(string token, Guid giftId, Guid version, CancellationToken ct)
    {
        var gifts = VisibleGifts(LinkByToken(token)).Where(x => x.Id == giftId && x.PhotoVersion == version);
        return await db.GiftPhotos.AsNoTracking().Where(x => gifts.Any(g => g.Id == x.GiftId))
            .SingleOrDefaultAsync(ct) ?? throw MissingLink();
    }

    public async Task Submit(Guid guestId, string token, Guid giftId, WishlistRequestInput input, CancellationToken ct)
    {
        var hash = HashToken(token);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var link = await Links.FromSqlInterpolated($"""
            SELECT * FROM "WishlistLinks" WHERE "TokenHash"={hash} AND "Revoked" IS NULL FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(ct) ?? throw MissingLink();
        var gift = await LockGift(giftId, ct) ?? throw MissingLink();
        if (!await VisibleGifts(ActiveLinks().Where(x => x.Id == link.Id)).AnyAsync(x => x.Id == giftId, ct))
            throw MissingLink();
        if (gift.Unlimited || gift.OhmywishesKey != null && await ManagedWebOwners().ContainsAsync(gift.OwnerId, ct))
            throw new RuleException("Этот подарок можно забронировать в компании.");
        if (gift.OwnerId == guestId) throw new RuleException("Нельзя выбрать собственный подарок.", 403);
        if (!await db.Users.AnyAsync(x => x.Id == guestId, ct)) throw new RuleException("Войдите снова.", 401);

        var held = await Reconcile(gift, ct);
        var now = Now;
        var prior = await Requests.Where(x => x.GiftId == giftId && x.GuestId == guestId &&
            (x.Status != Closed || x.Due > now)).OrderByDescending(x => x.Sequence).ToListAsync(ct);
        if (gift.DonorId != guestId && !prior.Any(x => x.Status != Closed || x.Due > now))
        {
            // Save any just-closed request before inserting another live request for the same guest.
            if (prior.Count > 0) await db.SaveChangesAsync(ct);
            var request = new WishlistReservation
            {
                LinkId = link.Id,
                GiftId = giftId,
                GuestId = guestId,
                ShareUsername = input.ShareUsername,
                Created = now,
                Due = now.AddHours(2)
            };
            if (gift.Mode == "free" && !held) request.Status = Held;
            else AlertDonor(gift, request);
            Requests.Add(request);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<WishlistRequestDto[]> ForGuest(Guid guestId, CancellationToken ct) =>
        await (from request in Requests
               join gift in db.Gifts on request.GiftId equals gift.Id
               where request.GuestId == guestId && request.Status != Closed
               orderby request.Sequence descending
               select new WishlistRequestDto(request.Id, gift.Id, gift.Title, request.Created, request.Status == Confirmed))
            .ToArrayAsync(ct);

    public async Task Withdraw(Guid guestId, Guid requestId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var giftId = await Requests.Where(x => x.Id == requestId && x.GuestId == guestId)
            .Select(x => (Guid?)x.GiftId).SingleOrDefaultAsync(ct);
        if (giftId == null) throw MissingRequest();
        var gift = await LockGift(giftId.Value, ct) ?? throw MissingRequest();
        var request = await Requests.SingleAsync(x => x.Id == requestId, ct);
        if (request.Status == Closed) return;
        if (request.Status == Confirmed && gift.DonorId == guestId)
            gift.Act(guestId, new("cancel"), 0, false);
        Close(request);
        await Reconcile(gift, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<WishlistInterestDto[]> ForDonor(Guid donorId, CancellationToken ct)
    {
        var now = Now;
        return await (from request in Requests
                      join link in ActiveLinks() on request.LinkId equals link.Id
                      join gift in AvailableForLinks() on request.GiftId equals gift.Id
                      join guest in db.Users on request.GuestId equals guest.Id
                      where request.Status == Waiting && request.Due > now
                          && gift.DonorId == donorId && gift.OwnerId != donorId && request.GuestId != donorId
                          && gift.OwnerId == link.OwnerId
                          && (gift.AllCompanies || (link.CompanyId != null && gift.Publications.Any(p => p.CompanyId == link.CompanyId)))
                      orderby request.Sequence descending
                      select new WishlistInterestDto(request.Id, gift.Id, gift.Title, request.Created,
                          request.ShareUsername && guest.Username != null && guest.Username != "" ? "@" + guest.Username : null,
                          request.Notified != null)).ToArrayAsync(ct);
    }

    public async Task Notify(Guid donorId, Guid requestId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var giftId = await Requests.Where(x => x.Id == requestId).Select(x => (Guid?)x.GiftId).SingleOrDefaultAsync(ct);
        if (giftId == null) throw MissingRequest();
        var gift = await LockGift(giftId.Value, ct) ?? throw MissingRequest();
        if (gift.DonorId != donorId || gift.OwnerId == donorId) throw MissingRequest();
        var request = await Requests.SingleAsync(x => x.Id == requestId, ct);
        if (request.Notified != null && request.InterestRecipientId == donorId) return;
        var now = Now;
        if (request.Status != Waiting || request.Due <= now || request.GuestId == donorId ||
            !await VisibleGifts(ActiveLinks().Where(x => x.Id == request.LinkId))
            .AnyAsync(x => x.Id == gift.Id, ct)) throw MissingRequest();

        request.Notified = now;
        request.InterestRecipientId = donorId;
        Close(request);
        AddNotice(request.GuestId, $"По вашему запросу на «{gift.Title}» сообщили, что подарок уже выбран.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public IQueryable<Guid> ActiveHoldIds() => Requests.Where(x => x.Status == Held).Select(x => x.GiftId);

    // Gift mutations and the worker take this same row lock before touching the queue.
    public Task<Gift?> LockGift(Guid giftId, CancellationToken ct) => db.Gifts.FromSqlInterpolated($"""
        SELECT * FROM "Gifts" WHERE "Id"={giftId} FOR UPDATE
        """).SingleOrDefaultAsync(ct);

    public async Task EnsureAvailable(Gift gift, CancellationToken ct)
    {
        if (await Reconcile(gift, ct)) throw new RuleException("Подарок уже занят.");
    }

    // The caller holds the gift row lock and saves in the same transaction.
    public async Task<bool> Reconcile(Gift gift, CancellationToken ct)
    {
        var requests = await Requests.Where(x => x.GiftId == gift.Id && x.Status != Closed)
            .OrderBy(x => x.Sequence).ToListAsync(ct);
        var pending = requests.Where(x => x.Status is Waiting or Held).ToArray();
        foreach (var request in requests.Where(x => x.Status == Confirmed && x.GuestId != gift.DonorId)) Close(request);
        var now = Now;
        var eligible = pending.Length == 0 ? [] : await EligibleLinks(gift, pending, ct);
        foreach (var request in pending.Where(x => !eligible.Contains(x.LinkId)))
        {
            Close(request);
            AddNotice(request.GuestId, $"Запрос на «{gift.Title}» больше недоступен.");
        }

        if (gift.Mode != "free")
        {
            ResolveOccupied(gift, pending, now);
            return false;
        }
        var candidate = pending.SingleOrDefault(x => x.Status == Held) ??
            pending.FirstOrDefault(x => x.Status == Waiting);
        if (candidate != null && candidate.Due <= now)
        {
            gift.Act(candidate.GuestId, new("reserve"), 0, false);
            candidate.Status = Confirmed;
            AddNotice(candidate.GuestId, $"Вы выбрали подарок «{gift.Title}». Бронь подтверждена.");
            ResolveOccupied(gift, pending, now);
            return false;
        }

        if (candidate?.Status != Held && requests.Any(x =>
            x.Status != Held && db.Entry(x).Property(r => r.Status).OriginalValue == Held))
        {
            // Free the filtered unique index before replacing a closed hold. The caller
            // still owns the gift lock and both writes commit in the same transaction.
            await db.SaveChangesAsync(ct);
        }
        if (candidate == null) return false;
        candidate.Status = Held;
        return true;
    }

    public async Task RunDue(CancellationToken ct)
    {
        var now = Now;
        var active = ActiveLinks();
        var available = AvailableForLinks();
        var holds = ActiveHoldIds();
        var due = await (from request in Requests
                         join gift in db.Gifts on request.GiftId equals gift.Id
                         where (request.Status == Held || request.Status == Waiting) &&
                             (request.Due <= now || (gift.Mode == "free" && !holds.Contains(gift.Id))
                             || !available.Any(x => x.Id == gift.Id)
                             || !active.Any(link => link.Id == request.LinkId &&
                                 (gift.AllCompanies || (link.CompanyId != null && gift.Publications.Any(p => p.CompanyId == link.CompanyId)))))
                         select gift.Id).Distinct().OrderBy(x => x).Take(100).ToArrayAsync(ct);
        foreach (var id in due)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            DateTimeOffset? scheduled = null;
            if (await LockGift(id, ct) is { } gift)
            {
                scheduled = await Requests.Where(x => x.GiftId == id && (x.Status == Held || x.Status == Waiting) &&
                    x.Due <= now).MinAsync(x => (DateTimeOffset?)x.Due, ct);
                await Reconcile(gift, ct);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics?.RecordWishlistRequest(false, scheduled is { } at ? Math.Max(0, (Now - at).TotalSeconds) : null);
            db.ChangeTracker.Clear();
        }
    }

    private IQueryable<WishlistLink> ActiveLinks() => Links.Where(x => x.Revoked == null &&
        (x.CompanyId == null || db.Memberships.Any(m => m.UserId == x.OwnerId && m.CompanyId == x.CompanyId)));

    private IQueryable<WishlistLink> LinkByToken(string token)
    {
        var hash = HashToken(token);
        return ActiveLinks().Where(x => x.TokenHash == hash);
    }

    private IQueryable<Guid> ManagedWebOwners()
    {
        var enabled = options?.WebApiEnabled == true && !string.IsNullOrWhiteSpace(options.ReservationToken);
        return db.OhmywishesConnections.Where(x => enabled && x.WebApi).Select(x => x.UserId);
    }

    private IQueryable<Gift> AvailableForLinks()
    {
        var webEnabled = options?.WebApiEnabled == true;
        return db.Gifts.Where(x => !x.Archived && !x.OhmywishesHidden &&
            (x.OhmywishesKey == null || webEnabled || !db.OhmywishesConnections.Any(s => s.UserId == x.OwnerId && s.WebApi)));
    }

    private IQueryable<Gift> VisibleGifts(IQueryable<WishlistLink> links) => AvailableForLinks().Where(gift =>
        links.Any(link => gift.OwnerId == link.OwnerId &&
            (gift.AllCompanies || (link.CompanyId != null && gift.Publications.Any(p => p.CompanyId == link.CompanyId)))));

    private async Task<HashSet<Guid>> EligibleLinks(Gift gift, WishlistReservation[] pending, CancellationToken ct)
    {
        if (gift.Unlimited || gift.OhmywishesKey != null && await ManagedWebOwners().ContainsAsync(gift.OwnerId, ct)) return [];
        if (gift.Archived || gift.OhmywishesHidden || (gift.OhmywishesKey != null && options?.WebApiEnabled != true &&
            await db.OhmywishesConnections.AnyAsync(s => s.UserId == gift.OwnerId && s.WebApi, ct))) return [];

        if (!gift.AllCompanies) await db.Entry(gift).Collection(x => x.Publications).LoadAsync(ct);
        var companies = gift.Publications.Select(x => x.CompanyId).ToHashSet();
        var ids = pending.Select(x => x.LinkId).Distinct().ToArray();
        var links = await ActiveLinks().Where(x => x.OwnerId == gift.OwnerId && ids.Contains(x.Id)).ToListAsync(ct);
        return links.Where(x => x.Revoked == null && (gift.AllCompanies ||
            (x.CompanyId is { } companyId && companies.Contains(companyId)))).Select(x => x.Id).ToHashSet();
    }

    private void AlertDonor(Gift gift, WishlistReservation request)
    {
        if (gift.DonorId is not { } donor || donor == request.GuestId || donor == request.InterestRecipientId) return;
        request.InterestRecipientId = donor;
        AddNotice(donor, $"Гость заинтересовался подарком «{gift.Title}».");
    }

    private void AddNotice(Guid userId, string text) => db.Notices.Add(new() { UserId = userId, Text = text, Created = Now });

    private void ResolveOccupied(Gift gift, WishlistReservation[] pending, DateTimeOffset now)
    {
        foreach (var request in pending.Where(x => x.Status is Waiting or Held))
        {
            if (request.Due <= now)
            {
                Close(request);
                continue;
            }
            request.Status = Waiting;
            AlertDonor(gift, request);
        }
    }

    private static void Close(WishlistReservation request) => request.Status = Closed;

    private static string HashToken(string token) => token.Length == 43 &&
        token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? AccountService.Hash(token) : throw MissingLink();
    private static RuleException MissingLink() => new("Список желаний недоступен.", 404);
    private static RuleException MissingRequest() => new("Запрос не найден.", 404);
}

public sealed class WishlistRequestWorker(IServiceScopeFactory scopes, ILogger<WishlistRequestWorker> logger,
    DarimMetrics metrics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<WishlistLinks>().RunDue(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                metrics.RecordWishlistRequest(true);
                logger.LogWarning("Wishlist request worker could not run ({Failure}).", ex.GetType().Name);
            }
        }
    }
}
