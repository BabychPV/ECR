// src/Ecr.Application/Integration/SourceConnectionString.cs
using System.Data.Common;
using Ecr.Domain.Enums;

namespace Ecr.Application.Integration;

/// <summary>
/// Рядок з'єднання джерела, розібраний ТИМИ Ж правилами, якими його читає адаптер (P-1126-SRCCRED, R-04, R-06, R-10):
/// ODBC для <c>PiSqlClient</c>, SqlClient для <c>Sql</c>.
/// </summary>
/// <remarks>
/// ⛔ Підрядковий пошук (<c>password=</c>) і розбір за чужими правилами розходяться з драйвером саме там, де
/// цікаво: <c>userid=</c>, табуляція перед <c>=</c>, фігурні дужки ODBC з <c>;</c> усередині. Тому відмова «адреса несе
/// облікові дані» і «та сама ціль» дивляться на ІМЕНА КЛЮЧІВ так, як їх побачить драйвер.
/// </remarks>
public static class SourceConnectionString
{
    /// <summary>Ключі, значення яких — облікові дані (ім'я ключа без пробілів, у нижньому регістрі).</summary>
    /// <remarks>⚠ Точні імена, не підрядки: <c>guid=</c> і <c>superuser=</c> — не облікові дані.</remarks>
    private static readonly HashSet<string> CredentialKeys = new(StringComparer.Ordinal)
    {
        "uid", "user", "userid", "username", "pwd", "password",
        "secret", "token", "apikey", "api-key", "accesskey", "sig",
    };

    /// <summary>Ключі Windows-входу, що конфліктують із логіном і паролем (R-06).</summary>
    private static readonly HashSet<string> WindowsSignInKeys = new(StringComparer.Ordinal)
    {
        "integratedsecurity", "trustedconnection", "trusted_connection", "persistsecurityinfo",
    };

    /// <summary>Чи транспорт читає адресу як рядок з'єднання (а не URL).</summary>
    /// <param name="transport">Транспорт.</param>
    public static bool IsConnectionString(ExternalTransport transport)
        => transport is ExternalTransport.PiSqlClient or ExternalTransport.Sql;

    /// <summary>Розбирає рядок правилами адаптера; <c>null</c> — не розбирається або транспорт не рядковий.</summary>
    /// <param name="transport">Транспорт.</param>
    /// <param name="text">Рядок з'єднання.</param>
    public static DbConnectionStringBuilder? Parse(ExternalTransport transport, string? text)
    {
        if (!IsConnectionString(transport) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new DbConnectionStringBuilder(useOdbcRules: transport == ExternalTransport.PiSqlClient);

        try
        {
            builder.ConnectionString = text;
        }
        catch (ArgumentException)
        {
            return null;
        }

        return builder;
    }

    /// <summary>Ім'я ключа для порівняння: без пробільних символів, у нижньому регістрі.</summary>
    /// <param name="key">Ім'я ключа з рядка.</param>
    public static string NormalizeKey(string key)
        => string.Concat((key ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

    /// <summary>Чи рядок має ключ облікових даних (логін, пароль, токен).</summary>
    /// <param name="builder">Розібраний рядок.</param>
    public static bool HasCredentialKey(DbConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Keys.Cast<string>().Any(key => CredentialKeys.Contains(NormalizeKey(key)));
    }

    /// <summary>Чи рядок має ключ Windows-входу (<c>Integrated Security</c>, <c>Trusted_Connection</c>, <c>Persist Security Info</c>).</summary>
    /// <param name="builder">Розібраний рядок.</param>
    public static bool HasWindowsSignInKey(DbConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Keys.Cast<string>().Any(key => WindowsSignInKeys.Contains(NormalizeKey(key)));
    }

    /// <summary>
    /// Канонічна форма рядка з'єднання: пари «ключ=значення» за правилами транспорту, ключі без регістру, у сталому
    /// порядку (ключі не зливаються за синонімами: сумнів — це зміна). <c>null</c> — рядок не розбирається.
    /// </summary>
    /// <param name="transport">Транспорт.</param>
    /// <param name="text">Рядок з'єднання.</param>
    public static string? Canonical(ExternalTransport transport, string text)
    {
        var builder = Parse(transport, text);

        if (builder is not { Count: > 0 })
        {
            return null;
        }

        return string.Join(
            ";",
            builder.Keys.Cast<string>()
                .Select(key => (Key: key.Trim().ToUpperInvariant(), Value: Convert.ToString(builder[key], System.Globalization.CultureInfo.InvariantCulture)))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }
}
