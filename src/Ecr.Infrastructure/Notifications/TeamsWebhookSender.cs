// src/Ecr.Infrastructure/Notifications/TeamsWebhookSender.cs
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Notifications;
using Microsoft.AspNetCore.DataProtection;

namespace Ecr.Infrastructure.Notifications;

/// <summary>
/// Доставка в Teams вебхуком Workflows (Power Automate) — тілом Adaptive Card
/// (<c>BE-34</c>).
/// </summary>
/// <remarks>
/// ⛔ Класичні «Office 365 connectors» і їхній <c>MessageCard</c> Microsoft
/// виводить з експлуатації, тому формат тут — Adaptive Card у конверті
/// <c>message</c>, і ні на що інше відправник не спирається.
///
/// ⛔ Ретраїв усередині НЕМАЄ і не буде: ретраєм є наступний прогін
/// <c>NotificationJob</c>. Власний цикл повторів тут означав би, що одна
/// недоступна адреса тримає розсилку по ВСІХ каналах у собі десятки секунд, а
/// журнал доставок при цьому мовчить аж до кінця спроб.
///
/// ⚠ Адреса вебхука — це САМ СЕКРЕТ каналу (хто її знає, той пише в канал,
/// `BE-33`), тому вона не потрапляє ні в текст відмови, ні в журнал.
/// </remarks>
public sealed class TeamsWebhookSender(
    IHttpClientFactory clients, IDataProtectionProvider protection, WebhookUrlPolicy webhooks)
    : INotificationChannelSender
{
    /// <summary>Ім'я клієнта у <see cref="IHttpClientFactory"/>.</summary>
    /// <remarks>
    /// ⚠ Іменований клієнт, а не власний <c>new HttpClient</c>: створений
    /// вручну тримає з'єднання після зміни DNS, і розсилка йде на адресу, якої
    /// вже немає.
    /// </remarks>
    public const string HttpClientName = "Ecr.Notifications.Teams";

    /// <summary>Версія схеми картки, яку розуміють Workflows.</summary>
    public const string CardVersion = "1.5";

    /// <summary>
    /// Скільки чекати на вебхук, перш ніж вважати спробу невдалою.
    /// </summary>
    /// <remarks>
    /// Десять секунд: це HTTP до хмарного кінця, а не SMTP-сеанс (там 30 с).
    /// Довше означало б, що один мовчазний канал з'їдає бюджет прогону, у
    /// якому чекають своєї черги інші.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Той самий purpose, яким секрет захищався при збереженні (<c>BE-33</c>).</summary>
    private readonly IDataProtector _protector =
        protection.CreateProtector(DataProtectionNotificationSecretProtector.Purpose);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public NotificationChannelKind Kind => NotificationChannelKind.TeamsWebhook;

    /// <inheritdoc />
    public async Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);

        var url = AddressOf(channel);

        // ⛔ Перелік дозволених хостів перевіряється ЩЕ РАЗ, перед відправкою:
        // перевірка на збереженні (`BE-33`) стосувалася тієї конфігурації
        // процесу, що діяла ТОДІ. Без цього рядка звуження переліку не діяло б
        // на вже збережені канали — тобто там, де воно й потрібне.
        if (!webhooks.IsAllowed(url))
        {
            throw new InvalidOperationException(
                $"Канал «{channel.Name}»: адреса вебхука не належить до дозволених хостів "
                + "(Notifications:WebhookAllowedHostSuffixes).");
        }

        var client = clients.CreateClient(HttpClientName);

        // ⚠ Межа належить ВІДПРАВНИКОВІ, а не реєстрації в контейнері: інакше
        // забута лямбда в `AddHttpClient` мовчки повертала б типові 100 секунд.
        client.Timeout = Timeout;

        using var content = new StringContent(Card(channel, message), Encoding.UTF8, "application/json");
        using var response = await PostAsync(client, url, content, channel, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // ⛔ Ні адреси, ні тіла відповіді: перше — секрет, друге вебхук
            // може повернути з луною запиту. У журнал іде код і назва каналу.
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Канал «{channel.Name}»: вебхук відповів {(int)response.StatusCode}."));
        }
    }

    /// <summary>
    /// Сам виклик — із таймаутом, перетвореним на ЗВИЧАЙНУ відмову.
    /// </summary>
    /// <param name="client">Клієнт із уже виставленою межею.</param>
    /// <param name="url">Адреса вебхука.</param>
    /// <param name="content">Тіло запиту.</param>
    /// <param name="channel">Канал — лише заради назви у відмові.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Найтонше місце відправника. Вичерпаний <see cref="HttpClient.Timeout"/>
    /// кидає <see cref="TaskCanceledException"/>, тобто
    /// <see cref="OperationCanceledException"/> — те саме, чим сигналізує
    /// зупинка застосунку, яку диспетчер НАВМИСНЕ пропускає нагору. Без цього
    /// перетворення мовчазний вебхук обривав би весь прогін розсилки, не
    /// лишивши рядка <c>Failed</c> ні собі, ні каналам після себе. Відрізнити
    /// одне від одного можна лише за <paramref name="ct"/>.
    /// </remarks>
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, HttpContent content, NotificationChannel channel, CancellationToken ct)
    {
        try
        {
            return await client.PostAsync(new Uri(url), content, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Канал «{channel.Name}»: вебхук не відповів за {Timeout.TotalSeconds:0} с."),
                error);
        }
    }

    /// <summary>Розшифровує адресу вебхука із секрету каналу.</summary>
    /// <param name="channel">Канал.</param>
    private string AddressOf(NotificationChannel channel)
    {
        if (channel.SecretProtected is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"Канал «{channel.Name}»: адресу вебхука не задано (PUT …/channels/{{id}}/secret).");
        }

        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(channel.SecretProtected)).Trim();
        }
        catch (System.Security.Cryptography.CryptographicException error)
        {
            // ⚠ Кільце ключів DataProtection спільне і живе в базі (`MI-01`),
            // але відновлення бази без нього робить блоб нечитабельним. Це
            // стан конфігурації, а не дефект, і він має бути названий.
            throw new InvalidOperationException(
                $"Канал «{channel.Name}»: адресу вебхука не вдалося розшифрувати — "
                + "кільце ключів DataProtection змінилося, задайте адресу заново.",
                error);
        }
    }

    /// <summary>Тіло запиту: конверт <c>message</c> з однією Adaptive Card.</summary>
    /// <param name="channel">Канал — звідти заголовок картки.</param>
    /// <param name="message">Тема й текст.</param>
    private static string Card(NotificationChannel channel, NotificationMessage message)
    {
        var title = TitleOf(channel);

        var blocks = new List<Dictionary<string, object?>>();

        if (!string.IsNullOrWhiteSpace(title))
        {
            blocks.Add(Text(title!, "Large", bold: true));
        }

        blocks.Add(Text(message.Subject, "Medium", bold: true));
        blocks.Add(Text(message.Body, size: null, bold: false));

        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "message",
            ["attachments"] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["contentUrl"] = null,
                    ["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = CardVersion,
                        ["body"] = blocks,
                    },
                },
            },
        };

        return JsonSerializer.Serialize(envelope, Json);
    }

    /// <summary>Заголовок картки з несекретних параметрів каналу.</summary>
    /// <param name="channel">Канал.</param>
    /// <remarks>
    /// ⚠ Зіпсований <c>SettingsJson</c> не має валити розсилку: заголовок —
    /// оздоблення, а повідомлення про збій мусить дійти.
    /// </remarks>
    private static string? TitleOf(NotificationChannel channel)
    {
        try
        {
            return JsonSerializer
                .Deserialize<NotificationChannelSettings>(channel.SettingsJson, Json)?.Title;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> Text(string text, string? size, bool bold)
    {
        var block = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "TextBlock",
            ["text"] = text,
            ["wrap"] = true,
        };

        if (size is not null)
        {
            block["size"] = size;
        }

        if (bold)
        {
            block["weight"] = "Bolder";
        }

        return block;
    }
}
