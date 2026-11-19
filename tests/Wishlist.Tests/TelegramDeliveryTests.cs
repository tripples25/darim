using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wishlist.Contracts;
using Wishlist.Server.Data;
using Wishlist.Server.Services;

namespace Wishlist.Tests;


public class TelegramDeliveryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task DeliversGiftEventsAndBirthdaysToTheRightChats()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            DateTimeOffset.UtcNow, "Asia/Yekaterinburg").DateTime);
        var company = new Company { Name = "Telegram test", InviteHash = Guid.NewGuid().ToString() };
        var owner = new AppUser { Name = "Именинник", TelegramId = 41001, BirthDate = today.AddDays(4) };
        var coordinator = new AppUser { Name = "Координатор", TelegramId = 41002, Contact = "@coordinator" };
        var participant = new AppUser { Name = "Участник", TelegramId = 41003, Contact = "@participant" };
        var observer = new AppUser { Name = "Друг", TelegramId = 41004 };
        var outsider = new AppUser { Name = "Гость", TelegramId = 41005 };
        await using (var db = new AppDb(fixture.Options))
        {

            db.Companies.Add(company);
            db.Users.AddRange(owner, coordinator, participant, observer, outsider);
            db.Memberships.AddRange(new[] { owner, coordinator, participant, observer }.Select(user =>
                new CompanyMembership { UserId = user.Id, CompanyId = company.Id }));
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDb(fixture.Options))
        {
            var gifts = new GiftService(db);
            var gift = await gifts.Create(owner.Id, new GiftWriteInput("Тестовое желание",
                Visibility: new(false, [company.Id])));
            await gifts.Change(coordinator.Id, gift.Id, action: new("split", CompanyId: company.Id));
            await gifts.Change(participant.Id, gift.Id, action: new("join", CompanyId: company.Id));
            await gifts.Change(coordinator.Id, gift.Id, action: new("close-split"));
            await gifts.Change(owner.Id, gift.Id, input: new("Обновлённое желание"));
            await gifts.Change(owner.Id, gift.Id, visibility: new(false, []));
            await gifts.Change(owner.Id, gift.Id, archive: true);
        }

        await using var services = new ServiceCollection().AddScoped(_ => new AppDb(fixture.Options))
            .BuildServiceProvider();
        using var telegram = new CapturingTelegram();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Telegram:BotToken"] = "test-token" }).Build();
        using var worker = new TelegramNotifications(services.GetRequiredService<IServiceScopeFactory>(), telegram, config,
            TimeProvider.System, NullLogger<TelegramNotifications>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(timeout.Token);
        try
        {
            await using var db = new AppDb(fixture.Options);
            while (await db.Notices.AnyAsync(n => n.TelegramNextAttempt != null, timeout.Token))
                await Task.Delay(25, timeout.Token);

            Assert.Equal(13, telegram.Sent.Count);
            Assert.DoesNotContain(telegram.Sent, m => m.Chat == owner.TelegramId || m.Chat == outsider.TelegramId);
            Assert.StartsWith("Скоро день рождения", Assert.Single(telegram.Sent, m => m.Chat == observer.TelegramId).Text);
            foreach (var recipient in new[] { coordinator, participant })
                foreach (var prefix in new[] { "К распилу присоединился", "Сбор участников завершён", "Желание изменено",
                    "Видимость желания изменена", "Желание отправлено в архив", "Скоро день рождения" })
                    Assert.Single(telegram.Sent, m => m.Chat == recipient.TelegramId && m.Text.StartsWith(prefix));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class CapturingTelegram : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<(long Chat, string Text)> Sent { get; } = new();
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.telegram.org/bottest-token/sendMessage", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Sent.Enqueue((body.RootElement.GetProperty("chat_id").GetInt64(), body.RootElement.GetProperty("text").GetString()!));
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
        }
    }
}
