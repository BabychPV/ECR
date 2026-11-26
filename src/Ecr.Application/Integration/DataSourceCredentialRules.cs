// src/Ecr.Application/Integration/DataSourceCredentialRules.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration;

/// <summary>Облікові дані з тіла POST/PUT джерела (P-1126-SRCCRED). <c>null</c> — «не змінювати» (R-08).</summary>
/// <param name="AuthMode">Режим входу; <c>null</c> — лишити.</param>
/// <param name="UserName">Логін; <c>null</c> — лишити.</param>
/// <param name="Password">Новий пароль; <c>null</c> — лишити.</param>
/// <param name="ClearPassword">Стерти пароль (лише разом із переходом на Windows).</param>
public sealed record SourceCredentialsInput(
    SourceAuthMode? AuthMode, string? UserName, SecretString? Password, bool ClearPassword)
{
    /// <summary>Клієнт, що не знає нових полів: облікові дані не змінюються.</summary>
    public static SourceCredentialsInput Unchanged { get; } = new(null, null, null, false);
}

/// <summary>Що зробити з обліковими даними джерела після перевірок.</summary>
/// <param name="Mode">Режим після збереження.</param>
/// <param name="UserName">Логін після збереження.</param>
/// <param name="NewPassword">Новий пароль; <c>null</c> — не замінюється.</param>
/// <param name="Changed">Чи облікові дані замінюються (ReplaceCredentials + подія безпеки).</param>
public sealed record SourceCredentialPlan(
    SourceAuthMode Mode, string? UserName, SecretString? NewPassword, bool Changed);

/// <summary>
/// Правила облікових даних джерела (P-1126-SRCCRED: R-05, R-06, R-08, R-09, R-11, R-13).
/// </summary>
/// <remarks>
/// ⛔ Кожна відмова — <c>422 ECR-REQ-0422</c> з ПОЛЕМ, без значення: ні пароль, ні логін у текст відмови не потрапляють.
/// </remarks>
public static class DataSourceCredentialRules
{
    /// <summary>Найбільша довжина пароля (R-13).</summary>
    public const int PasswordMaxLength = 256;

    /// <summary>Ключ: пароль треба ввести заново (нова ціль або логін).</summary>
    public const string PasswordReentryRequiredKey = "err.ECR-REQ-0422.dataSourcePasswordReentryRequired";

    /// <summary>Ключ: логін чи пароль відсутні або недопустимі.</summary>
    public const string CredentialsInvalidKey = "err.ECR-REQ-0422.dataSourceCredentialsInvalid";

    /// <summary>Ключ: транспорт не вміє логіна й пароля (PiWebApi).</summary>
    public const string CredentialsTransportKey = "err.ECR-REQ-0422.dataSourceCredentialsTransport";

    /// <summary>Вирішує, що робити з обліковими даними, або відмовляє.</summary>
    /// <param name="existing">Джерело до зміни; <c>null</c> — створення.</param>
    /// <param name="transport">Транспорт після збереження.</param>
    /// <param name="targetChanged">Чи змінилася ціль (транспорт, основна чи запасна адреса).</param>
    /// <param name="input">Облікові дані з тіла.</param>
    /// <param name="endpoint">Основна адреса після збереження.</param>
    /// <param name="secondaryEndpoint">Запасна адреса після збереження.</param>
    public static SourceCredentialPlan Decide(
        DataSource? existing,
        ExternalTransport transport,
        bool targetChanged,
        SourceCredentialsInput input,
        string endpoint,
        string? secondaryEndpoint)
    {
        ArgumentNullException.ThrowIfNull(input);

        var current = existing?.AuthMode ?? SourceAuthMode.Windows;
        var mode = input.AuthMode ?? current;

        if (!Enum.IsDefined(mode))
        {
            throw Invalid(CredentialsInvalidKey, "authMode", "Невідомий режим входу джерела.");
        }

        if (mode == SourceAuthMode.Windows)
        {
            // R-08: перехід на Windows стирає логін і пароль; Windows → Windows нічого не змінює.
            var changed = existing is not null
                          && (existing.AuthMode != SourceAuthMode.Windows || existing.UserName is not null
                              || existing.HasPassword);

            return new SourceCredentialPlan(SourceAuthMode.Windows, null, null, changed);
        }

        // ⛔ R-11: логін і пароль — лише рядок з'єднання (PiSqlClient, Sql). PiWebApi з UserPassword — і на
        // створенні, і на зміні транспорту.
        if (!DataSource.SupportsUserPassword(transport))
        {
            throw Invalid(CredentialsTransportKey, "authMode", "Логін і пароль підтримуються лише для PiSqlClient і Sql.");
        }

        // R-08: стерти пароль, лишаючись у UserPassword, — неможливий стан (логін без пароля).
        if (input.ClearPassword)
        {
            throw Invalid(CredentialsInvalidKey, "clearPassword", "Пароль стирається лише разом із переходом на Windows.");
        }

        RequireNoWindowsSignIn(transport, endpoint, "endpoint");
        RequireNoWindowsSignIn(transport, secondaryEndpoint, "secondaryEndpoint");

        var wasUserPassword = current == SourceAuthMode.UserPassword;
        var userName = input.UserName is not null ? input.UserName.Trim() : wasUserPassword ? existing!.UserName : null;

        if (!IsValidUserName(userName))
        {
            throw Invalid(CredentialsInvalidKey, "userName", "Логін джерела: 1..256 символів без керівних.");
        }

        if (input.Password is { } password)
        {
            if (!IsValidPassword(password))
            {
                throw Invalid(CredentialsInvalidKey, "password", "Пароль джерела: 1..256 символів без NUL.");
            }

            return new SourceCredentialPlan(SourceAuthMode.UserPassword, userName, password, Changed: true);
        }

        // Пароля в тілі немає: лишається збережений — але лише там, куди його вводили.
        if (!wasUserPassword || existing is not { HasPassword: true })
        {
            throw Invalid(CredentialsInvalidKey, "password", "Вхід логіном і паролем потребує пароля в тому самому запиті.");
        }

        // ⛔ R-09: нова ціль чи інший логін — збережений пароль туди не їде. Інакше право Integration.Manage означало б
        // «надішли збережений пароль на мій хост».
        if (targetChanged || !string.Equals(userName, existing.UserName, StringComparison.Ordinal))
        {
            throw Invalid(
                PasswordReentryRequiredKey, "password",
                $"Ціль або логін джерела «{existing.Code}» змінилися: пароль треба ввести заново.");
        }

        return new SourceCredentialPlan(SourceAuthMode.UserPassword, userName, null, Changed: false);
    }

    /// <summary>Логін: 1..256, без керівних символів (&lt; 0x20, 0x7F) — R-13.</summary>
    /// <param name="userName">Логін.</param>
    public static bool IsValidUserName(string? userName)
        => userName is { Length: > 0 and <= DataSource.UserNameMaxLength }
           && !userName.Any(c => c < 0x20 || c == 0x7F);

    /// <summary>Пароль: 1..256, без NUL; БЕЗ <c>Trim</c> (пробіли — частина пароля) — R-13.</summary>
    /// <param name="password">Пароль.</param>
    public static bool IsValidPassword(SecretString password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var value = password.Reveal();
        return value.Length is > 0 and <= PasswordMaxLength && !value.Contains('\0', StringComparison.Ordinal);
    }

    /// <summary>
    /// ⛔ R-06: у режимі UserPassword рядок з'єднання не несе Windows-входу (<c>Integrated Security</c>,
    /// <c>Trusted_Connection</c>, <c>Persist Security Info</c>) — інакше драйвер може проігнорувати логін і піти службовим
    /// записом, а людина бачила б «працює під логіном».
    /// </summary>
    private static void RequireNoWindowsSignIn(ExternalTransport transport, string? address, string field)
    {
        if (SourceConnectionString.Parse(transport, address) is { } builder
            && SourceConnectionString.HasWindowsSignInKey(builder))
        {
            throw Invalid(
                CredentialsInvalidKey, field,
                "У режимі логіна й пароля рядок з'єднання не має нести Integrated Security / Trusted_Connection.");
        }
    }

    private static BusinessRuleException Invalid(string messageKey, string field, string message)
        => new(
            ErrorCodes.RequestInvalid, message,
            new Dictionary<string, object?> { ["messageKey"] = messageKey, ["field"] = field });
}
