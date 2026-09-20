// src/Ecr.Infrastructure/Notifications/SmtpChannelSender.cs
using System.Text.Json;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Notifications;

namespace Ecr.Infrastructure.Notifications;

/// <summary>
/// Доставка в канал <see cref="NotificationChannelKind.Smtp"/> поверх транспорту
/// процесу (<see cref="INotificationSender"/>).
/// </summary>
/// <remarks>
/// ⛔ Доти SMTP-канал у матриці правил давав рядок <c>Failed</c> «відправника не
/// зареєстровано»: чесно, але марно — правило на нього завести було можна, а
/// дійти воно не могло нікуди.
///
/// ⚠ Розподіл обов'язків тут не очевидний і названий навмисно. СЕРВЕР бере
/// транспорт процесу (<c>Smtp:Host</c>, <c>Smtp:From</c>, секрет за іменем —
/// `ФВ-6.11`), а КАНАЛ дає адресатів і заголовок. Окремий <c>SmtpClient</c> на
/// канал означав би ще одне місце, де живуть облікові дані пошти, і саме туди
/// їх поклали б відкритим текстом.
///
/// ✎ 2026-09-20: раніше цей абзац пояснював, чому відправник ІГНОРУЄ
/// <c>settings.host</c> і <c>settings.port</c> каналу. Тепер ігнорувати нічого:
/// контракт таких полів не має, а спроба їх зберегти — <c>422</c>
/// (<c>NotificationChannelSettingsInput.NamesTransport</c>). Канали, записані
/// до зміни, читаються далі: зайві члени старого JSON пропускаються.
///
/// ⚠ Канал без адресатів — це відмова, а не тиша: диспетчер перетворить її на
/// рядок <c>Failed</c> із цим текстом, і в журналі доставок буде видно, ЩО саме
/// налаштовано не до кінця.
/// </remarks>
public sealed class SmtpChannelSender(INotificationSender transport) : INotificationChannelSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public NotificationChannelKind Kind => NotificationChannelKind.Smtp;

    /// <inheritdoc />
    public async Task SendAsync(
        NotificationChannel channel, NotificationMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);

        if (!transport.IsConfigured)
        {
            throw new InvalidOperationException(
                $"Канал «{channel.Name}»: транспорт SMTP процесу не налаштовано (Smtp:Host).");
        }

        var settings = SettingsOf(channel);

        var recipients = (settings.Recipients ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList();

        if (recipients.Count == 0)
        {
            throw new InvalidOperationException(
                $"Канал «{channel.Name}»: адресатів не задано (PUT …/channels/{{id}}).");
        }

        await transport
            .SendAsync(recipients, SubjectOf(settings, message), message.Body, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Несекретні параметри каналу; зіпсований JSON — порожні.</summary>
    /// <param name="channel">Канал.</param>
    /// <remarks>
    /// ⚠ Зіпсований <c>SettingsJson</c> тут НЕ ковтається мовчки, на відміну від
    /// заголовка картки Teams: без адресатів відправляти нікуди, тому далі
    /// спрацює перевірка порожнього переліку і канал отримає названу відмову.
    /// </remarks>
    private static NotificationChannelSettings SettingsOf(NotificationChannel channel)
    {
        try
        {
            return JsonSerializer.Deserialize<NotificationChannelSettings>(channel.SettingsJson, Json)
                   ?? new NotificationChannelSettings();
        }
        catch (JsonException)
        {
            return new NotificationChannelSettings();
        }
    }

    /// <summary>Тема листа: заголовок каналу попереду теми події.</summary>
    /// <param name="settings">Параметри каналу.</param>
    /// <param name="message">Повідомлення.</param>
    /// <remarks>
    /// ⚠ Той самий <c>title</c>, що й у картці Teams. Окреме поле «префікс теми»
    /// означало б два підписи одного каналу в різних транспортах — і рано чи
    /// пізно вони розійшлися б.
    /// </remarks>
    private static string SubjectOf(NotificationChannelSettings settings, NotificationMessage message)
        => string.IsNullOrWhiteSpace(settings.Title)
            ? message.Subject
            : $"{settings.Title!.Trim()}: {message.Subject}";
}
