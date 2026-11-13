using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wishlist.Contracts;
using Wishlist.Core;
using Wishlist.Server.Data;

namespace Wishlist.Server.Services;

public class GiftService(AppDb db, OhmywishesOptions? options = null, WishlistLinks? sharing = null,
    OhmywishesReservations? reservations = null, IDataProtectionProvider? protection = null)
{
    public async Task<GiftDto[]> List(Guid userId, Guid? companyId = null, bool includeMine = true)
    {
        await User(userId);
        var company = await new AccountService(db).ResolveCompany(userId, companyId);
        var memberships = db.Memberships.Where(x => x.CompanyId == company);
        return await Read(userId, VisibleGifts().Where(g =>
            (includeMine || g.OwnerId != userId) &&
            memberships.Any(m => m.UserId == g.OwnerId) &&
            (g.AllCompanies || g.Publications.Any(p => p.CompanyId == company)) &&
            (!g.Archived || g.OwnerId == userId || g.DonorId == userId || g.Shares.Any(s => s.UserId == userId))), company);
    }

    public Task<GiftDto[]> Mine(Guid userId) => Read(userId, VisibleGifts().Where(g => g.OwnerId == userId));

    public Task<GiftDto[]> Giving(Guid userId) => Read(userId, db.Gifts.Where(g =>
        g.DonorId == userId || g.Shares.Any(s => s.UserId == userId)));

    private async Task<GiftDto[]> Read(Guid userId, IQueryable<Gift> query, Guid? company = null)
    {
        var gifts = await query.AsNoTracking().Include(g => g.Shares).Include(g => g.Publications)
            .AsSplitQuery().OrderByDescending(g => g.Created).ThenBy(g => g.Id).ToArrayAsync();
        var ids = gifts.Select(g => g.Id).ToArray();
        HashSet<Guid> held = sharing == null ? [] : (await sharing.ActiveHoldIds().Where(id => ids.Contains(id)).ToListAsync()).ToHashSet();
        var people = await People(gifts);
        return gifts.Select(g => View(g, userId, people, company, held.Contains(g.Id))).ToArray();
    }

    public async Task<GiftDto> Create(Guid id, GiftInput input, CancellationToken ct = default)
    {
        await User(id);
        var gift = Gift.New(null, id, input);
        db.Gifts.Add(gift);
        if (input is GiftWriteInput write)
        {
            gift.Unlimited = write.Unlimited;
            if (write.Visibility != null) await UpdateVisibility(gift, write.Visibility);
            else
            {
                var memberships = await db.Memberships.Where(x => x.UserId == id).Select(x => x.CompanyId).Take(2).ToArrayAsync();
                if (memberships.Length == 1) await UpdateVisibility(gift, new(false, memberships));
            }
            await UpdatePhoto(gift, write, ct);
        }
        await db.SaveChangesAsync();
        return gift.View(id, await People([gift]));
    }

    public async Task<GiftPhoto?> Photo(Guid userId, Guid giftId, Guid version, CancellationToken ct)
    {
        var membership = db.Memberships.Where(m => m.UserId == userId);
        var visible = db.Gifts.Where(g => g.Id == giftId && g.PhotoVersion == version &&
            (g.DonorId == userId || g.Shares.Any(s => s.UserId == userId) ||
             VisibleGifts().Any(v => v.Id == g.Id &&
                 (v.OwnerId == userId || !v.Archived && membership.Any(m =>
                     db.Memberships.Any(owner => owner.UserId == v.OwnerId && owner.CompanyId == m.CompanyId)
                     && (v.AllCompanies || v.Publications.Any(p => p.CompanyId == m.CompanyId)))))));
        return await db.GiftPhotos.AsNoTracking().Where(p => visible.Any(g => g.Id == p.GiftId)).SingleOrDefaultAsync(ct);
    }

    public async Task<GiftDto> Change(Guid id, Guid giftId, GiftInput? input = null, GiftActionInput? action = null,
        bool? archive = null, GiftVisibility? visibility = null, CancellationToken ct = default)
    {
        var user = await User(id);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // All reservation paths, including links, serialize on this row.
        var gift = await db.Gifts.FromSqlInterpolated($"""SELECT * FROM "Gifts" WHERE "Id"={giftId} FOR UPDATE""")
            .SingleOrDefaultAsync(ct) ?? throw new RuleException("Подарок не найден.", 404);
        await db.Entry(gift).Collection(g => g.Shares).LoadAsync(ct);
        await db.Entry(gift).Collection(g => g.Publications).LoadAsync(ct);
        var before = gift.Donors();
        Guid? company = null;
        string? reservePath = null, cancelPath = null;
        var restored = false;

        if (action != null)
        {
            var continuing = action.Action is "leave" or "transfer" or "cancel" or "buy" && before.Contains(id);
            if (id == gift.OwnerId) throw new RuleException("Нельзя выбрать собственный подарок.", 403);
            if (!continuing)
            {
                company = await new AccountService(db).ResolveCompany(id, action.CompanyId);
                var ownerInCompany = await db.Memberships.AnyAsync(x => x.UserId == gift.OwnerId && x.CompanyId == company, ct);
                if (!ownerInCompany || (!gift.AllCompanies && !gift.Publications.Any(p => p.CompanyId == company)) ||
                    !await VisibleGifts().AnyAsync(g => g.Id == giftId, ct))
                    throw new RuleException("Подарок не найден.", 404);
                if (action.Action == "join" && gift.CompanyId != company)
                    throw new RuleException("Распил открыт в другой компании.", 403);
            }
            else company = gift.CompanyId;

            if (sharing != null && action.Action is ("reserve" or "split"))
                await sharing.EnsureAvailable(gift, ct);
            var participants = company == null ? [] : await db.Memberships.Where(x => x.CompanyId == company)
                .Select(x => x.UserId).ToArrayAsync(ct);
            var web = gift.OhmywishesKey != null && reservations?.Enabled == true && options?.WebApiEnabled == true
                ? await db.OhmywishesConnections.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == gift.OwnerId && x.WebApi, ct)
                : null;
            if (web != null && action.Action is ("reserve" or "split"))
            {
                reservePath = ReservationPath(gift, web);
                gift.OhmywishesReservationSource = ReservationProtector().Protect(reservePath);
                gift.OhmywishesOccupied = true;
            }
            if (action.Action == "cancel" && gift.OhmywishesReservationSource is { } source)
            {
                cancelPath = Unprotect(ReservationProtector(), source);
                gift.OhmywishesReservationSource = null;
                gift.OhmywishesOccupied = false;
            }
            gift.Act(id, action with { CompanyId = company }, participants.Length - 1,
                !string.IsNullOrWhiteSpace(user.Contact) || !string.IsNullOrWhiteSpace(user.Username));
        }
        else
        {
            if (!await VisibleGifts().AnyAsync(g => g.Id == giftId, ct)) throw new RuleException("Подарок не найден.", 404);
            if (id != gift.OwnerId)
            {
                var ownCompanies = db.Memberships.Where(m => m.UserId == id).Select(m => m.CompanyId);
                var published = gift.Publications.Select(p => p.CompanyId).ToArray();
                var visible = !gift.Archived && await db.Memberships.AnyAsync(m => m.UserId == gift.OwnerId &&
                    ownCompanies.Contains(m.CompanyId) && (gift.AllCompanies || published.Contains(m.CompanyId)), ct);
                throw visible ? new RuleException("Изменить желание может только владелец.", 403)
                    : new RuleException("Подарок не найден.", 404);
            }
            if (gift.OhmywishesKey != null && archive == true)
                throw new RuleException("Архивируйте желание в Ohmywishes.");
            if (input is GiftWriteInput importedWrite && gift.OhmywishesKey != null && importedWrite.Unlimited)
                throw new RuleException("Для импортированного желания нельзя включить отдельные брони.", 400);
            if (input is GiftWriteInput changedType && changedType.Unlimited != gift.Unlimited)
                throw new RuleException("Тип желания нельзя изменить после создания.", 400);
            if (input != null)
            {
                var previousDetails = gift.Details;
                gift.Edit(id, input);
                if (gift.OhmywishesKey != null && gift.Details != previousDetails)
                    gift.OhmywishesDetailsEdited = true;
                if (input is GiftWriteInput write)
                {
                    await UpdatePhoto(gift, write, ct);
                    if (gift.OhmywishesKey != null && (write.Photo != null || write.RemovePhoto))
                        gift.OhmywishesPhotoEdited = true;
                    gift.Unlimited = write.Unlimited;
                    if (write.Visibility != null) await UpdateVisibility(gift, write.Visibility);
                }
            }
            if (archive == true) gift.Archive(id);
            if (archive == false && gift.Archived)
            {
                gift.Restore(id);
                restored = true;
            }
            if (visibility != null) await UpdateVisibility(gift, visibility);
        }

        var recipients = before.Concat(gift.Donors()).Distinct().Where(x => x != id && x != gift.OwnerId);
        var verb = input != null ? "Желание изменено" : archive == true ? "Желание отправлено в архив"
            : restored ? "Желание возвращено из архива" : action?.Action switch
        {
            "cancel" => "Бронь или распил отменены",
            "buy" => "Подарок отмечен купленным",
            "leave" => "Участник вышел из распила",
            "transfer" => "Координатор распила изменён",
            _ => "Состав распила обновлён"
        };
        if (input != null || archive == true || restored || action != null && !gift.Unlimited)
            foreach (var recipient in recipients)
                db.Notices.Add(new() { UserId = recipient, Text = $"{verb}: «{gift.Title}»." });
        if (sharing != null) await sharing.Reconcile(gift, ct);
        await db.SaveChangesAsync(ct);
        // The same remote wish can be imported by different Darim users.
        if (reservePath != null || cancelPath != null)
        {
            var remoteId = (reservePath ?? cancelPath!).Split('/')[^1];
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({remoteId}, 0))", ct);
        }
        // Validate and stage all local changes before mutating the provider. Once the
        // external call starts, a disconnected caller must not interrupt the commit.
        if (reservePath != null)
            await reservations!.Reserve(gift.OhmywishesWebId!, CancellationToken.None, reservePath);
        if (cancelPath != null)
        {
            var state = await (reservations ?? throw OhmywishesReservations.Unavailable())
                .Cancel(cancelPath.Split('/')[^1], CancellationToken.None, cancelPath);
            gift.OhmywishesOccupied = state.Occupied;
        }
        if (reservePath != null || cancelPath != null)
        {
            gift.OhmywishesReservationChanged = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        await tx.CommitAsync(reservePath != null || cancelPath != null ? CancellationToken.None : ct);
        return View(gift, id, await People([gift]), company, false);
    }

    private string ReservationPath(Gift gift, OhmywishesConnection connection)
    {
        if (gift.OhmywishesWebId is not { } id) throw OhmywishesReservations.Unavailable();
        if (connection.ProtectedTokens == null) return $"wishes/{id}";
        var token = Unprotect(protection!.CreateProtector("ohmywishes.share-link.v1"), connection.ProtectedTokens);
        return $"users/{connection.AccountId}/wish-lists/{token}/wishes-v2/{id}";
    }

    private IDataProtector ReservationProtector() => protection!.CreateProtector("ohmywishes.reservation-source.v1");

    private static string Unprotect(IDataProtector protector, string value)
    {
        try { return protector.Unprotect(value); }
        catch (System.Security.Cryptography.CryptographicException) { throw OhmywishesReservations.Unavailable(); }
    }

    private async Task UpdateVisibility(Gift gift, GiftVisibility visibility)
    {
        if (visibility.CompanyIds == null || visibility.AllCompanies && visibility.CompanyIds.Length > 0)
            throw new RuleException("Укажите один вариант видимости.", 400);
        var requested = visibility.CompanyIds.ToHashSet();
        var count = await db.Memberships.CountAsync(m => m.UserId == gift.OwnerId && requested.Contains(m.CompanyId));
        if (count != requested.Count) throw new RuleException("Выберите свои компании.", 403);
        gift.AllCompanies = visibility.AllCompanies;
        gift.Publications.RemoveAll(p => !requested.Contains(p.CompanyId));
        foreach (var company in requested.Except(gift.Publications.Select(p => p.CompanyId)))
            gift.Publications.Add(new() { GiftId = gift.Id, CompanyId = company });
    }

    private GiftDto View(Gift gift, Guid user, IReadOnlyDictionary<Guid, PersonDto> people, Guid? company, bool held)
    {
        var dto = gift.View(user, people);
        if (dto.Participation == null) return dto;
        var synchronized = gift.OhmywishesWebId != null && reservations?.Enabled == true && options?.WebApiEnabled == true;
        if (synchronized && gift.Mode == "free" && gift.OhmywishesOccupied)
            return dto with { Participation = new("external", false, 0, 0, false, false, null) };
        if (held || company != null && gift.Mode == "split" && gift.CompanyId != company)
            return dto with { Participation = new("occupied", gift.Bought, 0, 0, false, false, null), Original = null };
        return dto;
    }

 async Task UpdatePhoto(Gift gift,GiftWriteInput input,CancellationToken ct) {
  if(input.Photo==null && !input.RemovePhoto)return;
  if(input.RemovePhoto){
   if(input.Photo!=null)throw new RuleException("Выберите замену или удаление фотографии.",400);
   if(gift.PhotoVersion==null)return;
   var existing=await db.GiftPhotos.FindAsync([gift.Id],ct);
   if(existing!=null)db.GiftPhotos.Remove(existing);
   gift.PhotoVersion=null;
   return;
  }
  var (data,type)=await GiftPhotoOptimizer.Optimize(input.Photo!,ct);
  var photo=gift.PhotoVersion==null?null:await db.GiftPhotos.FindAsync([gift.Id],ct);
  if(photo==null)db.GiftPhotos.Add(new(){GiftId=gift.Id,Data=data,ContentType=type});
  else{photo.Data=data;photo.ContentType=type;}
  gift.PhotoVersion=Guid.NewGuid();
 }

    private IQueryable<Gift> VisibleGifts()
    {
        var webEnabled = options?.WebApiEnabled == true;
        return db.Gifts.Where(g => !g.OhmywishesHidden &&
            (g.OhmywishesKey == null || !db.OhmywishesConnections.Any(c => c.UserId == g.OwnerId && c.WebApi && !webEnabled)));
    }

    private async Task<AppUser> User(Guid id) =>
        await db.Users.SingleOrDefaultAsync(x => x.Id == id) ?? throw new RuleException("Войдите снова.", 401);

    private Task<Dictionary<Guid, PersonDto>> People(IEnumerable<Gift> gifts)
    {
        var ids = gifts.SelectMany(g => g.Donors().Append(g.OwnerId)).Distinct().ToArray();
        return db.Users.Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => new PersonDto(x.Id, x.Name,
                x.Contact ?? (x.Username != null ? "@" + x.Username : null), x.TelegramAvatarUrl));
    }
}
