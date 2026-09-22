// tests/Ecr.Infrastructure.Tests/Notifications/TeamsWebhookSenderTests.cs
using System.Net;
using System.Text.Json;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Notifications;
using Ecr.TestKit;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Ecr.Infrastructure.Tests.Notifications;

/// <summary>
/// <c>BE-34</c>: відправник вебхука Teams.
/// </summary>
/// <remarks>
/// ⛔ Жодного справжнього мережевого виклику: усе йде через підмінний
/// <see cref="HttpMessageHandler"/>. Тест, який ходить у мережу, перевіряє
/// мережу, а не код — і падає тоді, коли з кодом усе гаразд.
///
/// ⚠ Адреса вебхука — САМ секрет каналу (`BE-33`), тому перевіряється ще й те,
/// чого у відмовах бути НЕ МАЄ.
/// </remarks>
public sealed class TeamsWebhookSenderTests
{
    private const string Allowed = "https://ncoc.webhook.office.com/webhookb2/abc/IncomingWebhook/xyz";
    private static readonly DateTime Now = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Повідомлення_йде_на_адресу_з_секрету_тілом_Adaptive_Card()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var factory = new SingleClientFactory(handler);
        var protection = new EphemeralDataProtectionProvider();

        var sender = new TeamsWebhookSender(factory, protection, Policy());
        var channel = Channel(protection, Allowed, """{"title":"ECR — контур NCOC"}""");

        await sender.SendAsync(
            channel, new NotificationMessage("ECR: збоїв за період — 2", "[collection] SRC-01: Failed."),
            CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri(Allowed), handler.Uri);
        Assert.Equal("application/json", handler.ContentType);

        // ⚠ Розбір JSON, а не пошук підрядка: типовий кодувальник
        // System.Text.Json екранує кирилицю в \uXXXX, і перевірка «містить
        // рядок» була б зеленою лише для латиниці — тобто доводила б не те.
        using var body = JsonDocument.Parse(handler.Body!);
        var attachment = body.RootElement.GetProperty("attachments")[0];

        Assert.Equal("message", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());

        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal(TeamsWebhookSender.CardVersion, card.GetProperty("version").GetString());

        var blocks = card.GetProperty("body");
        Assert.Equal("ECR — контур NCOC", blocks[0].GetProperty("text").GetString());
        Assert.Equal("ECR: збоїв за період — 2", blocks[1].GetProperty("text").GetString());
        Assert.Equal("[collection] SRC-01: Failed.", blocks[2].GetProperty("text").GetString());

        // Межа належить відправникові, а не реєстрації в контейнері, і число
        // закріплене літералом: інакше твердження рухалося б разом із константою.
        Assert.Equal(TimeSpan.FromSeconds(10), TeamsWebhookSender.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), factory.Client.Timeout);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Відмова_вебхука_500_дає_виняток_без_адреси()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var protection = new EphemeralDataProtectionProvider();

        var sender = new TeamsWebhookSender(new SingleClientFactory(handler), protection, Policy());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync(
                Channel(protection, Allowed), new NotificationMessage("s", "b"), CancellationToken.None));

        Assert.Contains("500", error.Message, StringComparison.Ordinal);

        // ⛔ Найважливіше твердження цього тесту: текст відмови лягає в журнал
        // доставок, а журнал читають люди без права на секрет.
        Assert.DoesNotContain("webhookb2", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("office.com", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Таймаут_клієнта_НЕ_виглядає_як_скасування_прогону()
    {
        // ⛔ Саме так поводиться HttpClient, коли вичерпано Timeout: кидає
        // TaskCanceledException, а це OperationCanceledException. Якби
        // відправник пропускав його як є, мовчазний вебхук обривав би розсилку
        // по ВСІХ каналах — замість одного рядка `Failed` для себе.
        var handler = new FakeHandler(_ => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout.",
            new TimeoutException()));

        var protection = new EphemeralDataProtectionProvider();
        var sender = new TeamsWebhookSender(new SingleClientFactory(handler), protection, Policy());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync(
                Channel(protection, Allowed), new NotificationMessage("s", "b"), CancellationToken.None));

        Assert.IsNotAssignableFrom<OperationCanceledException>(error);
        Assert.Contains("10", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Адреса_поза_переліком_суфіксів_не_йде_в_мережу_взагалі()
    {
        // ⛔ Перевірка стоїть ПЕРЕД відправкою, а не після: перелік суфіксів
        // могли звузити розгортанням уже після того, як канал зберегли.
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var protection = new EphemeralDataProtectionProvider();

        var sender = new TeamsWebhookSender(new SingleClientFactory(handler), protection, Policy());
        var channel = Channel(protection, "https://evil.example.com/webhookb2/abc");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync(channel, new NotificationMessage("s", "b"), CancellationToken.None));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Канал_без_секрету_не_йде_в_мережу_взагалі()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var protection = new EphemeralDataProtectionProvider();

        var sender = new TeamsWebhookSender(new SingleClientFactory(handler), protection, Policy());
        var channel = new NotificationChannel(
            NotificationChannelKind.TeamsWebhook, "Teams", "{}", Now, byUserId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync(channel, new NotificationMessage("s", "b"), CancellationToken.None));

        Assert.Equal(0, handler.Calls);
    }

    private static WebhookUrlPolicy Policy() => new(["webhook.office.com"]);

    private static NotificationChannel Channel(
        EphemeralDataProtectionProvider protection, string url, string settingsJson = "{}")
    {
        var channel = new NotificationChannel(
            NotificationChannelKind.TeamsWebhook, "Teams", settingsJson, Now, byUserId: null);

        var blob = protection
            .CreateProtector(DataProtectionNotificationSecretProtector.Purpose)
            .Protect(System.Text.Encoding.UTF8.GetBytes(url));

        channel.ReplaceSecret(blob, Now, byUserId: null);

        return channel;
    }

    /// <summary>Фабрика, що віддає ОДИН клієнт — щоб тест міг подивитися на нього після виклику.</summary>
    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient Client { get; } = new(handler);

        public HttpClient CreateClient(string name) => Client;
    }

    /// <summary>Підмінний транспорт: записує запит і віддає те, що звелів тест.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? ContentType { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Calls++;
            Method = request.Method;
            Uri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.MediaType;

            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return respond(request);
        }
    }
}
