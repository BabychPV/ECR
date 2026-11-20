// src/Ecr.Infrastructure/Jobs/RetentionSettings.cs
using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Строки зберігання (PSS-13, PSS-27; рішення людини 20.11 №7): значення в <c>sys_ecr.SystemSetting</c>, які
/// адміністратор змінює будь-коли без перевидання — задачі читають їх на КОЖНОМУ проході, без кешу.
/// </summary>
/// <remarks>
/// ⚠ Той самий механізм, що й межа бездіяльності сеансу (<c>D-471</c>, <c>SessionSettingsStore</c>): таблиця поза
/// моделлю EF (<c>08-system-tables.sql</c>), рядок «ключ → значення». Рядка немає або значення не число —
/// типове значення. Поза межами — притискається до межі (опечатка не має ні стерти журнали за добу, ні
/// перенести аудит молодший за підлогу).
/// <para>
/// Змінити (DBA або адміністратор із правом запису в базу):
/// <c>UPDATE sys_ecr.SystemSetting SET Value = N'365', ModifiedAt = SYSUTCDATETIME() WHERE [Key] = N'Retention.TechnicalLogDays';</c>
/// (рядка немає — <c>INSERT</c> з тими самими колонками). Інструкція — <c>docs/admin/admin-guide.md</c>.
/// </para>
/// <para>
/// ⛔ Цей тип — спільна інфраструктура строків зберігання: ретенція <c>sec.LoginAttempt</c> (PSS-24, інший
/// лан) бере <see cref="RetentionPolicy.TechnicalLogDays"/> звідси ж, а не власний ключ.
/// </para>
/// </remarks>
public static class RetentionSettings
{
    /// <summary>Ключ: скільки днів зберігати технічні журнали (черга й доставки сповіщень, прогони обслуговування).</summary>
    public const string TechnicalLogDaysKey = "Retention.TechnicalLogDays";

    /// <summary>Типово — 400 днів (рішення людини 20.11 №7).</summary>
    public const int DefaultTechnicalLogDays = 400;

    /// <summary>Нижня межа, днів: менше — опечатка, а не політика (зведення й перевірки читають журнали за тижні).</summary>
    public const int MinTechnicalLogDays = 30;

    /// <summary>Верхня межа, днів (100 років): далі — «не видаляти» по суті.</summary>
    public const int MaxTechnicalLogDays = 36_500;

    /// <summary>Ключ: мінімальна глибина аудиту в <c>aud.*</c>, місяців (поріг архівації <c>arc.usp_ArchiveAudit</c>).</summary>
    public const string AuditMonthsKey = "Retention.AuditMonths";

    /// <summary>
    /// Підлога глибини аудиту, місяців (рішення людини 20.11 №7). Ту саму підлогу тримає й сама процедура
    /// <c>arc.usp_ArchiveAudit</c> (PSS-27): менше значення будь-звідки стає 120.
    /// </summary>
    public const int MinAuditMonths = 120;

    /// <summary>Типово — рівно підлога.</summary>
    public const int DefaultAuditMonths = MinAuditMonths;

    /// <summary>Верхня межа, місяців (100 років).</summary>
    public const int MaxAuditMonths = 1_200;

    /// <summary>Читає чинні строки (без кешу).</summary>
    /// <param name="db">Контекст бази.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Строки після притискання до меж.</returns>
    public static async Task<RetentionPolicy> ReadAsync(EcrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var logDays = await ReadRawAsync(db, TechnicalLogDaysKey, ct).ConfigureAwait(false);
        var auditMonths = await ReadRawAsync(db, AuditMonthsKey, ct).ConfigureAwait(false);

        return new RetentionPolicy(
            Clamp(logDays, DefaultTechnicalLogDays, MinTechnicalLogDays, MaxTechnicalLogDays),
            Clamp(auditMonths, DefaultAuditMonths, MinAuditMonths, MaxAuditMonths));
    }

    /// <summary>Значення рядка → число в межах; не число (чи рядка немає) — типове.</summary>
    /// <param name="raw">Сире значення з таблиці.</param>
    /// <param name="fallback">Типове.</param>
    /// <param name="min">Нижня межа.</param>
    /// <param name="max">Верхня межа.</param>
    /// <returns>Чинне значення.</returns>
    public static int Clamp(string? raw, int fallback, int min, int max)
        => int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static Task<string?> ReadRawAsync(EcrDbContext db, string key, CancellationToken ct)
        => db.Database
            .SqlQuery<string?>($"SELECT Value AS [Value] FROM sys_ecr.SystemSetting WHERE [Key] = {key}")
            .FirstOrDefaultAsync(ct);
}

/// <summary>Чинні строки зберігання.</summary>
/// <param name="TechnicalLogDays">Днів для технічних журналів.</param>
/// <param name="AuditMonths">Місяців аудиту в <c>aud.*</c> (не менше <see cref="RetentionSettings.MinAuditMonths"/>).</param>
public sealed record RetentionPolicy(int TechnicalLogDays, int AuditMonths);
