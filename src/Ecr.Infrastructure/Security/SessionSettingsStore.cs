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
    {
        var value = minutes.ToString(CultureInfo.InvariantCulture);

        // ⚠ UPDATE, а за відсутності рядка — INSERT, під UPDLOCK/HOLDLOCK: два одночасні збереження
        // не дають порушення первинного ключа.
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sys_ecr.SystemSetting WITH (UPDLOCK, HOLDLOCK)
               SET Value = {value}, ModifiedAt = {at}, ModifiedByUserId = {byUserId}
             WHERE [Key] = {SessionSettings.IdleTimeoutKey};
            IF @@ROWCOUNT = 0
                INSERT INTO sys_ecr.SystemSetting ([Key], Value, Description, ModifiedAt, ModifiedByUserId)
                VALUES ({SessionSettings.IdleTimeoutKey}, {value}, N'Sign out after N minutes without activity (D-471)', {at}, {byUserId});
            """,
            ct);
    }
}
