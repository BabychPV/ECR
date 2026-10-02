// src/Ecr.Application/Notifications/SmtpSettingsHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Errors;

namespace Ecr.Application.Notifications;

/// <summary>Налаштування SMTP для екрана. ⛔ Пароля тут немає й бути не може — лише <see cref="HasPassword"/>.</summary>
/// <param name="Host">Сервер.</param>
/// <param name="Port">Порт.</param>
/// <param name="EncryptionMode">Шифрування.</param>
/// <param name="FromAddress">Адреса відправника.</param>
/// <param name="FromName">Ім'я відправника.</param>
/// <param name="AuthMode">Автентифікація.</param>
/// <param name="UserName">Логін.</param>
/// <param name="HasPassword">Чи заданий пароль.</param>
/// <param name="IsEnabled">Чи діють ці налаштування.</param>
/// <param name="Source">Звідки береться транспорт зараз: <c>database</c>, <c>configuration</c> або <c>none</c>.</param>
/// <param name="Configured">Чи є чим слати пошту.</param>
/// <param name="UpdatedAt">Коли змінено (UTC); <c>null</c> — рядка ще немає.</param>
public sealed record SmtpSettingsView(
    string Host, int Port, SmtpEncryptionMode EncryptionMode, string FromAddress, string? FromName,
    SmtpAuthMode AuthMode, string? UserName, bool HasPassword, bool IsEnabled, string Source, bool Configured,
    DateTime? UpdatedAt);

/// <summary>Запит на зміну налаштувань SMTP.</summary>
/// <param name="Host">Сервер.</param>
/// <param name="Port">Порт 1–65535.</param>
/// <param name="EncryptionMode">Шифрування.</param>
/// <param name="FromAddress">Адреса відправника.</param>
/// <param name="FromName">Ім'я відправника.</param>
/// <param name="AuthMode">Автентифікація.</param>
/// <param name="UserName">Логін (для автентифікації за паролем).</param>
/// <param name="Password">Новий пароль; <c>null</c> або порожньо — не змінювати.</param>
/// <param name="ClearPassword">Прибрати збережений пароль.</param>
/// <param name="IsEnabled">Чи діють налаштування.</param>
public sealed record SmtpSettingsInput(
    string? Host, int Port, SmtpEncryptionMode EncryptionMode, string? FromAddress, string? FromName,
    SmtpAuthMode AuthMode, string? UserName, string? Password, bool ClearPassword, bool IsEnabled);

/// <summary>Читання налаштувань SMTP. Право <c>System.ManageNotifications</c>.</summary>
public sealed class GetSmtpSettingsHandler(
    ISmtpSettingsStore store, INotificationSender sender, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає налаштування без пароля.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SmtpSettingsView> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct)
            .ConfigureAwait(false);

        return ToView(await store.FindAsync(ct).ConfigureAwait(false), sender.IsConfigured);
    }

    internal static SmtpSettingsView ToView(SmtpSettings? row, bool transportConfigured)
    {
        if (row is null)
        {
            return new SmtpSettingsView(
                string.Empty, 587, SmtpEncryptionMode.StartTls, string.Empty, null, SmtpAuthMode.None, null,
                HasPassword: false, IsEnabled: false, transportConfigured ? "configuration" : "none",
                transportConfigured, UpdatedAt: null);
        }

        var source = row.IsComplete ? "database" : transportConfigured ? "configuration" : "none";

        return new SmtpSettingsView(
            row.Host, row.Port, row.EncryptionMode, row.FromAddress, row.FromName, row.AuthMode, row.UserName,
            row.HasPassword, row.IsEnabled, source, source != "none",
            DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc));
    }
}

/// <summary>Зміна налаштувань SMTP. Право <c>System.ManageNotifications</c>.</summary>
public sealed class SaveSmtpSettingsHandler(
    ISmtpSettingsStore store, ISmtpPasswordProtector protector, ISmtpSettingsCache cache,
    INotificationSender sender, IAccessDecisionService access, IUnitOfWork uow, IAuditWriter audit,
    ICurrentUser currentUser, IClock clock)
{
    /// <summary>Записує налаштування й скидає кеш транспорту.</summary>
    /// <param name="input">Нові значення.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SmtpSettingsView> HandleAsync(SmtpSettingsInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct).ConfigureAwait(false);

        var row = await store.FindAsync(ct).ConfigureAwait(false);
        var created = row is null;

        if (row is null)
        {
            row = new SmtpSettings(clock.UtcNow, profile.UserId);
            store.Add(row);
        }

        var host = (input.Host ?? string.Empty).Trim();
        var from = (input.FromAddress ?? string.Empty).Trim();
        var user = string.IsNullOrWhiteSpace(input.UserName) ? null : input.UserName.Trim();
        var newPassword = string.IsNullOrEmpty(input.Password) ? null : input.Password;
        var clears = input.ClearPassword || input.AuthMode == SmtpAuthMode.None;
        var hadPassword = row.HasPassword;
        var willHavePassword = !clears && (newPassword is not null || hadPassword);

        Validate(input, host, from, user, willHavePassword);

        // ⛔ S1 (ent6): збережений пароль іде лише туди, куди його ввели. Змінився хост, порт, шифрування чи
        // логін, а пароль не введено заново, — відмова: інакше адміністратор із правом на налаштування
        // перенаправив би збережений секрет на чужий хост (порожній Password = «лишити» цього не бачить).
        if (!clears && newPassword is null && hadPassword && EndpointChanged(row, host, input, user))
        {
            throw Invalid(
                PasswordReentryRequiredKey,
                "Адресу, порт, шифрування чи логін змінено: збережений пароль не переноситься — введіть пароль заново.",
                "password");
        }

        row.Update(
            host, input.Port, input.EncryptionMode, from, input.FromName, input.AuthMode, user, input.IsEnabled,
            clock.UtcNow, profile.UserId);

        if (clears)
        {
            row.ReplacePassword(null, clock.UtcNow, profile.UserId);
        }
        else if (newPassword is not null)
        {
            row.ReplacePassword(protector.Protect(newPassword), clock.UtcNow, profile.UserId);
        }

        // ⛔ У журнал — лише факт зміни пароля, не він сам і не його довжина.
        await ListNotificationChannelsHandler.AuditAsync(
            audit, clock, currentUser, profile.UserId, created ? "SmtpSettingsCreated" : "SmtpSettingsUpdated",
            new
            {
                host,
                port = input.Port,
                encryption = input.EncryptionMode.ToString(),
                from,
                fromName = row.FromName,
                auth = input.AuthMode.ToString(),
                userName = row.UserName,
                isEnabled = input.IsEnabled,
                // Стирання збереженого секрету (явне чи перехід на режим без автентифікації) — теж зміна секрету.
                passwordChanged = newPassword is not null || (clears && hadPassword),
            },
            ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        cache.Invalidate();

        return GetSmtpSettingsHandler.ToView(row, sender.IsConfigured);
    }

    /// <summary>Ключ відмови: адресу змінено, а збережений пароль не підтверджено введенням.</summary>
    public const string PasswordReentryRequiredKey = "err.ECR-REQ-0422.smtpPasswordReentryRequired";

    /// <summary>Ключ відмови: пароль не можна слати без шифрування (AUTH LOGIN відкритим текстом).</summary>
    public const string PasswordNeedsTlsKey = "err.ECR-REQ-0422.smtpPasswordNeedsTls";

    private static bool EndpointChanged(SmtpSettings row, string host, SmtpSettingsInput input, string? user)
        => !string.Equals(row.Host, host, StringComparison.OrdinalIgnoreCase)
           || row.Port != input.Port
           || row.EncryptionMode != input.EncryptionMode
           || !string.Equals(row.UserName ?? string.Empty, user ?? string.Empty, StringComparison.Ordinal);

    private static void Validate(SmtpSettingsInput input, string host, string from, string? user, bool willHavePassword)
    {
        if (!Enum.IsDefined(input.EncryptionMode) || !Enum.IsDefined(input.AuthMode))
        {
            throw Invalid("err.ECR-REQ-0422.smtpSettingsInvalid", "Невідомий режим шифрування чи автентифікації.", "mode");
        }

        if (input.Port is < 1 or > 65535)
        {
            throw Invalid("err.ECR-REQ-0422.smtpSettingsInvalid", "Порт має бути від 1 до 65535.", "port");
        }

        // ⚠ Порожній хост дозволено лише для ВИМКНЕНИХ налаштувань: так адміністратор може зберегти
        // чернетку, не змусивши транспорт слати пошту з половини значень.
        if ((input.IsEnabled || host.Length > 0)
            && (host.Length is 0 or > SmtpSettings.HostMaxLength || !IsHostAllowed(host)))
        {
            throw Invalid(
                "err.ECR-REQ-0422.smtpSettingsInvalid",
                "Сервер: ім'я хоста або IP без схеми й шляху; адреси link-local і метаданих хмари заборонені.",
                "host");
        }

        if ((input.IsEnabled || from.Length > 0) && !SmtpSettings.IsValidAddress(from))
        {
            throw Invalid("err.ECR-REQ-0422.smtpSettingsInvalid", "Адреса відправника не є поштовою адресою.", "from");
        }

        if (input.FromName is { Length: > SmtpSettings.NameMaxLength })
        {
            throw Invalid("err.ECR-REQ-0422.smtpSettingsInvalid", "Ім'я відправника задовге.", "fromName");
        }

        // ⛔ S1: автентифікація за паролем без шифрування віддала б пароль відкритим текстом (AUTH LOGIN).
        // Без винятків і прапорів: внутрішній relay без TLS — AuthMode.None.
        if (input.AuthMode == SmtpAuthMode.Password && input.EncryptionMode == SmtpEncryptionMode.None)
        {
            throw Invalid(PasswordNeedsTlsKey, "Автентифікація за паролем вимагає шифрування (STARTTLS).", "encryption");
        }

        if (input.AuthMode == SmtpAuthMode.Password
            && (user is null or { Length: > SmtpSettings.AddressMaxLength } || (input.IsEnabled && !willHavePassword)))
        {
            throw Invalid(
                "err.ECR-REQ-0422.smtpSettingsInvalid", "Автентифікація за паролем потребує логіна й пароля.", "auth");
        }
    }

    /// <summary>
    /// Хост SMTP: ім'я чи IP без схеми, шляху, пробілів і порту. ⚠ Приватні й loopback-адреси ДОЗВОЛЕНІ
    /// (внутрішній релей — звичайна справа, а налаштування міняє лише адміністратор); блокуються
    /// link-local та імена метаданих хмари — за тим самим правилом, що й адреса SQL-джерела.
    /// </summary>
    internal static bool IsHostAllowed(string host)
        => host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']')
           && DataSourceEndpointPolicy.CheckSqlServerAddress(host) == EndpointVerdict.Allowed;

    private static BusinessRuleException Invalid(string key, string message, string field)
        => ListNotificationChannelsHandler.Invalid(key, message, field);
}

/// <summary>Результат проби SMTP.</summary>
/// <param name="To">Адреса, на яку слати пробний лист.</param>
// ✎ D-263: <c>To</c> — РІВНО одна адреса (список, роздільники, ім'я-відображення, CRLF → 422). Порожня — пошта
// поточного користувача. Лист фіксованого тексту, лише адмін із правом; журнал — лише домен. XML-опис не чіпаємо
// (він потрапляє в знімок контракту).
public sealed record SmtpTestRequest(string? To);

/// <summary>Пробний лист через ефективні налаштування (БД, інакше конфігурація). Право <c>System.ManageNotifications</c>.</summary>
public sealed class TestSmtpSettingsHandler(
    INotificationSender sender, IAccessDecisionService access, IAuditWriter audit, ICurrentUser currentUser,
    IClock clock, IUserStore users)
{
    /// <summary>Шле пробний лист на одну введену адресу (інакше — поточному користувачу); відмова транспорту — відповідь із категорією.</summary>
    /// <param name="request">Запит; <c>To</c> — одна адреса або порожньо.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<NotificationTestResult> HandleAsync(SmtpTestRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, ListNotificationChannelsHandler.Permission, ct).ConfigureAwait(false);

        // ✎ Зміна рішення D-263: введена адреса ВИКОРИСТОВУЄТЬСЯ, але рівно ОДНА (без відкритого релея);
        // порожня — як і раніше, пошта поточного користувача.
        var entered = (request.To ?? string.Empty).Trim();
        var useEntered = entered.Length > 0;
        var to = entered;

        if (!useEntered)
        {
            var me = await users.FindByIdAsync(profile.UserId, ct).ConfigureAwait(false);
            to = (me?.Email ?? string.Empty).Trim();
        }

        if (!IsSingleAddress(to))
        {
            throw ListNotificationChannelsHandler.Invalid(
                "err.ECR-REQ-0422.smtpTestRecipientInvalid", "Адресат проби не є поштовою адресою.", "to");
        }

        var result = !sender.IsConfigured
            ? new NotificationTestResult(
                false, "The SMTP transport is not configured.", "notifications.test.smtpNotConfigured")
            : await TestNotificationChannelHandler.TryAsync(
                () => sender.SendAsync([to], "ECR test notification", "SMTP settings test message.", ct),
                classify: true).ConfigureAwait(false);

        // ⛔ Текст відмови транспорту (e.Message) назовні не віддаємо: лише ключ категорії; невідома — загальний ключ клієнта.
        if (!result.Ok)
        {
            result = result with { Error = null };
        }

        await ListNotificationChannelsHandler.AuditAsync(
            audit, clock, currentUser, profile.UserId, "SmtpSettingsTested",
            new { ok = result.Ok, recipient = useEntered ? "entered" : "own", domain = to[(to.LastIndexOf('@') + 1)..] }, ct)
            .ConfigureAwait(false);

        return result;
    }

    /// <summary>Рівно одна «чиста» адреса: без списків, роздільників, імен-відображення, пробілів і керівних символів.</summary>
    internal static bool IsSingleAddress(string address)
        => address.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not (',' or ';' or '<' or '>' or '"' or '(' or ')'))
           && SmtpSettings.IsValidAddress(address);
}
