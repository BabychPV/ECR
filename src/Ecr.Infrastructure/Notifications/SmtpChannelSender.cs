// src/Ecr.Infrastructure/Notifications/SmtpChannelSender.cs
using System.Text.Json;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
public sealed class SmtpChannelSender(
    INotificationSender transport, EcrDbContext? db = null, IUiStringCatalog? catalog = null)
    : INotificationChannelSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Конверт причини відмови для журналу доставок (ключ каталогу + назва каналу).</summary>
    private static string DeliveryError(string key, string channelName)
        => JobProgressMessageCodec.EncodeWithinLimit(
            new JobProgressMessageEnvelope(
                key, new Dictionary<string, string>(StringComparer.Ordinal) { ["channel"] = channelName }),
            "channel");

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
            // ⛔ T2-09: причина йде в журнал доставок один раз, без мови читача, — тож конверт «ключ + параметри»
            // (читач резолвить його своєю мовою в `ListNotificationDeliveriesHandler`), а не зашитий український текст.
            throw new InvalidOperationException(DeliveryError("notifications.delivery.smtpNotConfigured", channel.Name));
        }

        var settings = SettingsOf(channel);

        var recipients = (settings.Recipients ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList();

        // ⛔ Ліміт (проба) — на ВСІХ адресатів разом: спершу явні, решта — ролям (рев'ю ent6 S3).
        var limit = message.RecipientLimit ?? int.MaxValue;

        if (recipients.Count > limit)
        {
            recipients = recipients.Take(limit).ToList();
        }

        var byRole = await RoleRecipientsAsync(channel.Id, ct).ConfigureAwait(false);

        // ⚠ Адреса, що вже є в явному переліку, у групу мови не потрапляє вдруге: людина отримує один лист.
        var explicitSet = recipients.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = byRole
            .Where(r => explicitSet.Add(r.Email))
            .Take(Math.Max(0, limit - recipients.Count))
            .GroupBy(r => r.Language, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (explicitSet.Count == 0)
        {
            throw new NotificationNoRecipientsException(
                DeliveryError("notifications.delivery.smtpNoRecipients", channel.Name));
        }

        // Явні адреси — мовою каталогу за замовчуванням (мови одержувача система не знає).
        if (recipients.Count > 0)
        {
            await transport
                .SendAsync(recipients, SubjectOf(settings, message), message.Body, ct)
                .ConfigureAwait(false);
        }

        foreach (var group in groups)
        {
            var (subject, body) = await RenderAsync(message, group.Key, ct).ConfigureAwait(false);

            await transport
                .SendAsync([.. group.Select(r => r.Email)], SubjectOf(settings, message with { Subject = subject }), body, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Тема й тіло мовою одержувача; без локалізованого тексту чи каталогу — як є.</summary>
    private async Task<(string Subject, string Body)> RenderAsync(
        NotificationMessage message, string language, CancellationToken ct)
    {
        if (message.Text is not { } text || catalog is null
            || string.Equals(language, Ecr.Application.Localization.UiStringResolver.DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return (message.Subject, message.Body);
        }

        var strings = (await catalog.GetAsync(language, ct).ConfigureAwait(false)).Strings;

        string Fill(string key)
            => text.Args.Aggregate(
                strings.GetValueOrDefault(key, key),
                (acc, arg) => acc.Replace("{" + arg.Key + "}", arg.Value, StringComparison.Ordinal));

        return (Fill(text.SubjectKey), Fill(text.BodyKey));
    }

    /// <summary>
    /// Адресати за ролями каналу: активні користувачі з поштою, мова — з налаштування <c>language</c>
    /// (інакше — каталог за замовчуванням). ⚠ <c>User.ReceivesAlerts</c> НЕ вимагається: роль обрав
    /// адміністратор у каналі, прапорець — це вибір для старої черги <c>NotificationOutbox</c>.
    /// </summary>
    private async Task<List<(string Email, string Language)>> RoleRecipientsAsync(int channelId, CancellationToken ct)
    {
        if (db is null)
        {
            return [];
        }

        var users = await (
                from link in db.NotificationChannelRoles
                join a in db.RoleAssignments on link.RoleId equals a.RoleId
                join u in db.Users on a.UserId equals u.Id
                where link.ChannelId == channelId && u.IsActive && u.Email != null
                select new { u.Id, Email = u.Email! })
            .Distinct().OrderBy(u => u.Id).ToListAsync(ct).ConfigureAwait(false);

        var ids = users.Select(u => u.Id).ToList();
        var prefs = await db.UserPreferences.AsNoTracking()
            .Where(p => p.Key == "language" && ids.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, p => p.ValueJson, ct).ConfigureAwait(false);

        return [.. users.Select(u => (u.Email.Trim(), LanguageOf(prefs.GetValueOrDefault(u.Id))))];
    }

    private static string LanguageOf(string? valueJson)
    {
        try
        {
            return valueJson is null ? Ecr.Application.Localization.UiStringResolver.DefaultLanguage
                : JsonSerializer.Deserialize<string>(valueJson) is { Length: > 0 } l ? l
                : Ecr.Application.Localization.UiStringResolver.DefaultLanguage;
        }
        catch (JsonException)
        {
            return Ecr.Application.Localization.UiStringResolver.DefaultLanguage;
        }
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
