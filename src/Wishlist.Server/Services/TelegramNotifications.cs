using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Wishlist.Server.Data;

namespace Wishlist.Server.Services;

// The database queue survives restarts. Delivery is at-least-once: a crash after
// Telegram accepts a message but before commit can produce a duplicate.
public sealed class TelegramNotifications(IServiceScopeFactory scopes, IHttpClientFactory clients,
    IConfiguration config, TimeProvider clock, ILogger<TelegramNotifications> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var token = config["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token)) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                await QueueBirthdays(db, ct);
                for (var i = 0; i < 50; i++)
                    if (!await Deliver(db, token, ct)) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Telegram notification processing failed; will retry"); }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    private async Task QueueBirthdays(AppDb db, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(config["Telegram:BirthdayTimeZone"] ?? "Asia/Yekaterinburg");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        var days = Math.Clamp(config.GetValue("Telegram:BirthdayReminderDays", 7), 0, 365);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialize schedulers across replicas, including the annual deduplication marker.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(918241701)", ct);
        var users = await db.Users.Where(u => u.BirthDate != null).ToArrayAsync(ct);
        foreach (var user in users)
        {
            var birthday = NextBirthday(user.BirthDate!.Value, today);
            if (birthday.DayNumber - today.DayNumber > days || user.BirthdayReminderYear == birthday.Year) continue;
            var companies = db.Memberships.Where(m => m.UserId == user.Id).Select(m => m.CompanyId);
            var recipients = await db.Memberships.Where(m => companies.Contains(m.CompanyId) && m.UserId != user.Id)
                .Select(m => m.UserId).Distinct().ToArrayAsync(ct);
            foreach (var recipient in recipients)
                db.Notices.Add(new() { UserId = recipient, PersonId = user.Id, TelegramNextAttempt = clock.GetUtcNow(),
                    Text = $"🎂 Скоро день рождения у {user.Name} — {birthday:dd.MM}. Самое время выбрать подарок!" });
            user.BirthdayReminderYear = birthday.Year;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static DateOnly NextBirthday(DateOnly birth, DateOnly today)
    {
        DateOnly InYear(int year) => new(year, birth.Month, Math.Min(birth.Day, DateTime.DaysInMonth(year, birth.Month)));
        var date = InYear(today.Year);
        return date < today ? InYear(today.Year + 1) : date;
    }

    private object? Buttons(Notice notice, string? ownerName)
    {
        var configuredOrigin = config["Telegram:SiteOrigin"] ?? "https://wishes.hype-ny.ru";
        if (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps
            || origin.UserInfo.Length != 0) return null;

        var rows = new List<object[]>();
        object Button(string label, string kind, Guid id) =>
            new { text = label, url = new Uri(origin, $"/?open={kind}&id={id:D}").AbsoluteUri };

        if (notice.GiftId is { } gift)
        {
            if (!notice.OpenOwnerBoard) rows.Add([Button("Открыть желание", "gift", gift)]);
            if (notice.IncludesSplit) rows.Add([Button("Детали распила", "split", gift)]);
        }
        if ((notice.OpenOwnerBoard || notice.GiftId == null) && notice.PersonId is { } person)
            rows.Insert(0, [Button($"Другие желания {RussianName.Genitive(ownerName)}".TrimEnd(), "owner", person)]);
        return rows.Count == 0 ? null : new { inline_keyboard = rows };
    }

    private async Task<bool> Deliver(AppDb db, string token, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var notices = await db.Notices.FromSqlInterpolated($"""SELECT * FROM "Notices" WHERE "TelegramNextAttempt" <= {now} ORDER BY "TelegramNextAttempt" LIMIT 1 FOR UPDATE SKIP LOCKED""").ToArrayAsync(ct);
        if (notices.Length == 0) return false;
        var notice = notices[0];
        var chat = await db.Users.Where(u => u.Id == notice.UserId).Select(u => u.TelegramId).SingleAsync(ct);
        notice.TelegramNextAttempt = null;
        if (chat > 0)
        {
            var ownerName = (notice.OpenOwnerBoard || notice.GiftId == null) && notice.PersonId is { } owner
                ? await db.Users.Where(u => u.Id == owner).Select(u => u.Name).SingleOrDefaultAsync(ct)
                : null;
            using var client = clients.CreateClient("telegram-login");
            using var response = await client.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = chat, text = notice.Text, reply_markup = Buttons(notice, ownerName) }, ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Forbidden && response.StatusCode != System.Net.HttpStatusCode.BadRequest)
            {
                notice.TelegramAttempts++;
                notice.TelegramNextAttempt = now.AddMinutes(Math.Min(360, Math.Pow(2, Math.Min(notice.TelegramAttempts, 9))));
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return true;
    }
}
