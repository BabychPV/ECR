using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Налаштування сеансу в <c>sys_ecr.SystemSetting</c> (<c>D-471</c>).
/// </summary>
/// <remarks>
/// ⚠ Таблиця живе поза моделлю EF (її створює <c>08-system-tables.sql</c>), тож міграції не потрібно.
/// Запис іде через <c>db.Database</c>, а не окремим з'єднанням: так він бере участь у транзакції обробника
/// разом із подією журналу безпеки (відкат зміни не лишає події, і навпаки).
/// </remarks>
public sealed class SessionSettingsStore(EcrDbContext db) : ISessionSettingsStore
{
    /// <inheritdoc />
    public async Task<int?> ReadIdleTimeoutMinutesAsync(CancellationToken ct)
    {
        var raw = await db.Database
            .SqlQuery<string?>($"SELECT Value AS [Value] FROM sys_ecr.SystemSetting WHERE [Key] = {SessionSettings.IdleTimeoutKey}")
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ? minutes : null;
    }

    /// <inheritdoc />
    public Task WriteIdleTimeoutMinutesAsync(int minutes, int byUserId, DateTime at, CancellationToken ct)
        => UpsertAsync(
            SessionSettings.IdleTimeoutKey, minutes, "Sign out after N minutes without activity (D-471)",
            byUserId, at, ct);

    /// <inheritdoc />
    public Task<string?> ReadTechnicalLogRetentionDaysAsync(CancellationToken ct)
        => db.Database
            .SqlQuery<string?>($"SELECT Value AS [Value] FROM sys_ecr.SystemSetting WHERE [Key] = {SessionSettings.TechnicalLogRetentionKey}")
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public Task WriteTechnicalLogRetentionDaysAsync(int days, int byUserId, DateTime at, CancellationToken ct)
        => UpsertAsync(
            SessionSettings.TechnicalLogRetentionKey, days, "Retention of sec.LoginAttempt, days (PSS-24)",
            byUserId, at, ct);

    /// <inheritdoc />
    public Task<string?> ReadAuditRetentionMonthsAsync(CancellationToken ct)
        => db.Database
            .SqlQuery<string?>($"SELECT Value AS [Value] FROM sys_ecr.SystemSetting WHERE [Key] = {SessionSettings.AuditRetentionKey}")
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public Task WriteAuditRetentionMonthsAsync(int months, int byUserId, DateTime at, CancellationToken ct)
        => UpsertAsync(
            SessionSettings.AuditRetentionKey, months, "Minimum audit depth in aud.*, months (PSS-27)",
            byUserId, at, ct);

    private async Task UpsertAsync(string key, int number, string description, int byUserId, DateTime at, CancellationToken ct)
    {
        var value = number.ToString(CultureInfo.InvariantCulture);

        // ⚠ UPDATE, а за відсутності рядка — INSERT, під UPDLOCK/HOLDLOCK: два одночасні збереження
        // не дають порушення первинного ключа.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sys_ecr.SystemSetting WITH (UPDLOCK, HOLDLOCK)
               SET Value = {value}, ModifiedAt = {at}, ModifiedByUserId = {byUserId}
             WHERE [Key] = {key};
            IF @@ROWCOUNT = 0
                INSERT INTO sys_ecr.SystemSetting ([Key], Value, Description, ModifiedAt, ModifiedByUserId)
                VALUES ({key}, {value}, {description}, {at}, {byUserId});
            """,
            ct).ConfigureAwait(false);
    }
}
