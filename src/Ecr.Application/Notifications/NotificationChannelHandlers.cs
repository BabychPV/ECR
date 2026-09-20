// src/Ecr.Application/Notifications/NotificationChannelHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Errors;

namespace Ecr.Application.Notifications;

/// <summary>Несекретні параметри каналу — рівно те, що лягає в <c>SettingsJson</c>.</summary>
/// <remarks>
/// ⛔ Типізований запис, а не довільний JSON: поля «password» чи «webhookUrl»
/// тут немає, тож секрет не може приїхати несекретним шляхом і повернутися в
/// <c>GET</c>.
/// </remarks>
/// <param name="Host">SMTP: сервер.</param>
/// <param name="Port">SMTP: порт.</param>
/// <param name="UseTls">SMTP: чи вимагати TLS.</param>
/// <param name="From">SMTP: адреса відправника.</param>
/// <param name="Recipients">SMTP: адресати.</param>
/// <param name="Title">Teams: заголовок картки.</param>
public sealed record NotificationChannelSettings(
    string? Host = null, int? Port = null, bool? UseTls = null, string? From = null,
    IReadOnlyList<string>? Recipients = null, string? Title = null);

/// <summary>Канал сповіщень — рядок екрана. Секрету тут немає й не буде.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Kind">Транспорт.</param>
/// <param name="Name">Назва.</param>
/// <param name="IsEnabled">Чи ввімкнений.</param>
/// <param name="Settings">Несекретні параметри.</param>
/// <param name="HasSecret">Чи задано секрет — єдине, що про нього відомо клієнтові.</param>
/// <param name="ModifiedAt">Остання зміна, UTC.</param>
public sealed record NotificationChannelView(
    int Id, NotificationChannelKind Kind, string Name, bool IsEnabled,
    NotificationChannelSettings Settings, bool HasSecret, DateTime ModifiedAt);

/// <summary>Наслідок пробного повідомлення.</summary>
/// <param name="Ok">Чи прийняв канал повідомлення.</param>
/// <param name="Error">Причина відмови — без секрету; <c>null</c> за успіху.</param>
/// <param name="MessageKey">Ключ каталогу для причини, якщо вона відома наперед.</param>
public sealed record NotificationTestResult(bool Ok, string? Error, string? MessageKey = null);

/// <summary>Перелік каналів. Право <c>System.ManageNotifications</c>.</summary>
public sealed class ListNotificationChannelsHandler(
    INotificationStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право на все керування сповіщеннями.</summary>
    public const string Permission = "System.ManageNotifications";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Повертає канали за назвою.</summary>
    public async Task<IReadOnlyList<NotificationChannelView>> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var channels = await store.ListChannelsAsync(ct).ConfigureAwait(false);

        return [.. channels.Select(ToView)];
    }

    internal static NotificationChannelView ToView(NotificationChannel channel)
        => new(
            channel.Id, channel.Kind, channel.Name, channel.IsEnabled,
            JsonSerializer.Deserialize<NotificationChannelSettings>(channel.SettingsJson, Json) ?? new(),
            channel.HasSecret, channel.ModifiedAt);

    internal static async Task<NotificationChannel> FindAsync(INotificationStore store, int id, CancellationToken ct)
        => await store.FindChannelAsync(id, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               ErrorCodes.SourceEntityNotFound, $"Каналу сповіщень {id} не існує.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-INT-0404.notificationChannel",
                   ["id"] = id.ToString(CultureInfo.InvariantCulture),
               });

    internal static BusinessRuleException Invalid(string messageKey, string message, string? name = null)
        => new(
            ErrorCodes.RequestInvalid, message,
            new Dictionary<string, object?> { ["messageKey"] = messageKey, ["name"] = name });

    internal static Task AuditAsync(
        IAuditWriter audit, IClock clock, ICurrentUser currentUser, int byUserId, string eventType, object details,
        CancellationToken ct)
        => audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow, eventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(details, Json), byUserId, currentUser.CorrelationId),
            ct);
}

/// <summary>Створення і зміна каналу. Право <c>System.ManageNotifications</c>.</summary>
public sealed class SaveNotificationChannelHandler(
    INotificationStore store, IAccessDecisionService access, IUnitOfWork uow, IAuditWriter audit,
    ICurrentUser currentUser, IClock clock)
{
    /// <summary>Створює ввімкнений канал без секрету.</summary>
    public async Task<NotificationChannelView> CreateAsync(
        NotificationChannelKind kind, string name, NotificationChannelSettings? settings, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct).ConfigureAwait(false);

        if (!Enum.IsDefined(kind))
        {
            throw ListNotificationChannelsHandler.Invalid(
                "err.ECR-REQ-0422.notificationChannelInvalid", $"Транспорту {kind} не існує.", name);
        }

        var json = await ValidateAsync(kind, name, settings, exceptId: null, ct).ConfigureAwait(false);
        var channel = new NotificationChannel(kind, name.Trim(), json, clock.UtcNow, profile.UserId);
        store.AddChannel(channel);

        await ListNotificationChannelsHandler.AuditAsync(
            audit, clock, currentUser, profile.UserId, "NotificationChannelCreated",
            new { name = channel.Name, kind = kind.ToString(), settings }, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return ListNotificationChannelsHandler.ToView(channel);
    }

    /// <summary>Змінює назву, стан і несекретні параметри; транспорт і секрет не чіпає.</summary>
    public async Task<NotificationChannelView> UpdateAsync(
        int id, string name, bool isEnabled, NotificationChannelSettings? settings, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct).ConfigureAwait(false);

        var channel = await ListNotificationChannelsHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        var json = await ValidateAsync(channel.Kind, name, settings, id, ct).ConfigureAwait(false);
        channel.Update(name.Trim(), json, isEnabled, clock.UtcNow, profile.UserId);

        await ListNotificationChannelsHandler.AuditAsync(
            audit, clock, currentUser, profile.UserId, "NotificationChannelUpdated",
            new { id, name = channel.Name, isEnabled, settings }, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return ListNotificationChannelsHandler.ToView(channel);
    }

    private async Task<string> ValidateAsync(
        NotificationChannelKind kind, string name, NotificationChannelSettings? settings, int? exceptId,
        CancellationToken ct)
    {
        var trimmed = (name ?? string.Empty).Trim();
        settings ??= new NotificationChannelSettings();

        var smtpIncomplete = kind == NotificationChannelKind.Smtp
            && (string.IsNullOrWhiteSpace(settings.Host)
                || settings.Port is < 1 or > 65535
                || settings.Recipients is not { Count: > 0 }
                || settings.Recipients.Any(string.IsNullOrWhiteSpace));

        if (trimmed.Length is 0 or > NotificationChannel.NameMaxLength || smtpIncomplete)
        {
            throw ListNotificationChannelsHandler.Invalid(
                "err.ECR-REQ-0422.notificationChannelInvalid",
                "Канал потребує назви до 100 символів; SMTP — ще й сервера, порту 1–65535 і адресатів.", trimmed);
        }

        if (await store.IsChannelNameTakenAsync(trimmed, exceptId, ct).ConfigureAwait(false))
        {
            throw ListNotificationChannelsHandler.Invalid(
                "err.ECR-REQ-0422.notificationChannelNameTaken", $"Канал «{trimmed}» уже є.", trimmed);
        }

        return JsonSerializer.Serialize(settings, ListNotificationChannelsHandler.Json);
    }
}

/// <summary>Видалення каналу разом із його правилами. Право <c>System.ManageNotifications</c>.</summary>
public sealed class DeleteNotificationChannelHandler(
    INotificationStore store, IAccessDecisionService access, IUnitOfWork uow, IAuditWriter audit,
    ICurrentUser currentUser, IClock clock)
{
    /// <summary>Прибирає канал і його правила; журнал доставок лишається.</summary>
    public async Task HandleAsync(int id, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct).ConfigureAwait(false);

        var channel = await ListNotificationChannelsHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        var rules = await store.RemoveChannelWithRulesAsync(channel, ct).ConfigureAwait(false);

        await ListNotificationChannelsHandler.AuditAsync(
            audit, clock, currentUser, profile.UserId, "NotificationChannelDeleted",
            new { id, name = channel.Name, removedRules = rules }, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Заміна секрету каналу (write-only). Право <c>System.ManageNotifications</c>.</summary>
/// <remarks>
/// ⛔ Для Teams секрет — це сам URL вебхука (хто його знає, той пише в канал),
/// тому SSRF-перевірка стоїть ТУТ, а не в несекретних параметрах.
/// </remarks>
public sealed class ReplaceNotificationChannelSecretHandler(
    INotificationStore store, INotificationSecretProtector protector, WebhookUrlPolicy webhooks,
    IAccessDecisionService access, IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    /// <summary>Замінює секрет; порожній — прибирає.</summary>
    public async Task<NotificationChannelView> HandleAsync(int id, string? secret, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct).ConfigureAwait(false);

        var channel = await ListNotificationChannelsHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        var cleared = string.IsNullOrWhiteSpace(secret);

        if (!cleared && channel.Kind == NotificationChannelKind.TeamsWebhook && !webhooks.IsAllowed(secret))
        {
            // ⛔ Ні URL, ні хост у відмову не йдуть: відмова потрапляє в журнал.
            throw ListNotificationChannelsHandler.Invalid(
                "err.ECR-REQ-0422.webhookUrlNotAllowed",
                "Адреса вебхука має бути https і вести на дозволений хост.", channel.Name);
        }

        channel.ReplaceSecret(cleared ? null : protector.Protect(secret!.Trim()), clock.UtcNow, profile.UserId);

        await ListNotificationChannelsHandler.AuditAsync(
            audit, clock, currentUser, profile.UserId, "NotificationChannelSecretReplaced",
            new { id, name = channel.Name, hasSecret = channel.HasSecret }, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return ListNotificationChannelsHandler.ToView(channel);
    }
}

/// <summary>Пробне повідомлення в канал. Право <c>System.ManageNotifications</c>.</summary>
/// <remarks>
/// ⚠ Два шляхи, і вони РІЗНІ навмисно. SMTP іде через
/// <see cref="INotificationSender"/> — транспорт процесу, у якого є стан «ще не
/// налаштовано»; його треба показати окремим ключем, бо це конфігурація
/// сервера, а не відмова каналу. Решта транспортів іде тим самим
/// <see cref="INotificationChannelSender"/>, яким користується розсилка
/// (<c>BE-34</c>): проба, що ходить іншою дорогою, ніж бойова доставка, зеленіє
/// саме тоді, коли доставка не працює.
///
/// ⛔ Ні адреси вебхука, ні пароля у відповіді немає: текст відмови формує сам
/// відправник і секрету в нього не кладе (`ФВ-6.11`).
/// </remarks>
public sealed class TestNotificationChannelHandler(
    INotificationStore store,
    INotificationSender sender,
    IEnumerable<INotificationChannelSender> channelSenders,
    IAccessDecisionService access,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Шле пробу й повертає підсумок; відмова каналу — не виняток.</summary>
    /// <remarks>
    /// ⚠ <see cref="IUnitOfWork"/> тут немає навмисно: проба не змінює жодної
    /// сутності, а <see cref="IAuditWriter"/> пише власною командою.
    /// </remarks>
    public async Task<NotificationTestResult> HandleAsync(int id, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct).ConfigureAwait(false);

        var channel = await ListNotificationChannelsHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        var result = await ProbeAsync(channel, ct).ConfigureAwait(false);

        // ⛔ Проба — подія БЕЗПЕКИ, а не діагностика: вона шле повідомлення
        // назовні від імені системи, і решта дій над каналом уже в журналі.
        // ⛔ У деталях лише факт і підсумок: ні секрету, ні тіла повідомлення,
        // ні тексту відмови транспорту (той складає не наш код).
        await ListNotificationChannelsHandler.AuditAsync(
            audit, clock, currentUser, profile.UserId, "NotificationChannelTested",
            new { id, name = channel.Name, kind = channel.Kind.ToString(), ok = result.Ok }, ct)
            .ConfigureAwait(false);

        return result;
    }

    /// <summary>Сама проба: вибирає дорогу за транспортом каналу.</summary>
    private async Task<NotificationTestResult> ProbeAsync(NotificationChannel channel, CancellationToken ct)
    {
        if (channel.Kind == NotificationChannelKind.Smtp)
        {
            return !sender.IsConfigured
                ? new NotificationTestResult(
                    false, "The SMTP transport is not configured.", "notifications.test.smtpNotConfigured")
                : await TryAsync(
                    () => sender.SendAsync(
                        ListNotificationChannelsHandler.ToView(channel).Settings.Recipients ?? [],
                        Subject, BodyFor(channel), ct))
                    .ConfigureAwait(false);
        }

        var transport = channelSenders.FirstOrDefault(s => s.Kind == channel.Kind);

        if (transport is null)
        {
            // ⚠ Той самий стан, що й рядок `Failed` у журналі доставок: канал
            // увімкнений, а доставити його нічим. Проба має називати це так
            // само, інакше екран каналів і журнал розповідали б різне.
            return new NotificationTestResult(
                false, $"No sender is registered for the {channel.Kind} transport.",
                "notifications.test.senderNotRegistered");
        }

        return await TryAsync(
            () => transport.SendAsync(channel, new NotificationMessage(Subject, BodyFor(channel)), ct))
            .ConfigureAwait(false);
    }

    private const string Subject = "ECR test notification";

    private static string BodyFor(NotificationChannel channel)
        => $"Test message for channel \"{channel.Name}\".";

    /// <summary>Виконує відправку; відмова транспорту стає відповіддю, не винятком.</summary>
    private static async Task<NotificationTestResult> TryAsync(Func<Task> send)
    {
        try
        {
            await send().ConfigureAwait(false);

            return new NotificationTestResult(true, null);
        }
#pragma warning disable CA1031 // Відмова транспорту — це й є відповідь проби, а не аварія запиту.
        catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return new NotificationTestResult(false, e.Message);
        }
    }
}
