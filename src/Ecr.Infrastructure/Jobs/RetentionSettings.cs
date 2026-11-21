// src/Ecr.Infrastructure/Jobs/RetentionSettings.cs
using Ecr.Application.Security;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Строки зберігання (PSS-13, PSS-27; рішення людини 20.11 №7): значення в <c>sys_ecr.SystemSetting</c>, які
/// адміністратор змінює будь-коли без перевидання — задачі читають їх на КОЖНОМУ проході, без кешу.
/// </summary>
/// <remarks>
/// ⚠ Той самий механізм, що й межа бездіяльності сеансу (<c>D-471</c>, <c>SessionSettingsStore</c>): таблиця поза
/// моделлю EF (<c>08-system-tables.sql</c>), рядок «ключ → значення». Рядка немає — типове значення.
/// ✎ рев'ю M2: не число, нижче мінімуму (0, -1, «40» замість «400») — ТИПОВЕ значення й попередження в журналі, а не
/// притискання до мінімуму: притискання опечатки до 30 днів стерло б журнали за місяць. 0 НЕ означає «зберігати
/// вічно». Вище максимуму — максимум і попередження.
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
    public const string TechnicalLogDaysKey = SessionSettings.TechnicalLogRetentionKey;

    /// <summary>Типово — 400 днів (рішення людини 20.11 №7).</summary>
    public const int DefaultTechnicalLogDays = SessionSettings.DefaultTechnicalLogRetentionDays;

    /// <summary>Нижня межа, днів: менше — опечатка, а не політика (зведення й перевірки читають журнали за тижні).</summary>
    public const int MinTechnicalLogDays = SessionSettings.MinTechnicalLogRetentionDays;

    /// <summary>Верхня межа, днів (100 років): далі — «не видаляти» по суті.</summary>
    public const int MaxTechnicalLogDays = SessionSettings.MaxTechnicalLogRetentionDays;

    /// <summary>Ключ: мінімальна глибина аудиту в <c>aud.*</c>, місяців (поріг архівації <c>arc.usp_ArchiveAudit</c>).</summary>
    public const string AuditMonthsKey = SessionSettings.AuditRetentionKey;

    /// <summary>
    /// Підлога глибини аудиту, місяців (рішення людини 20.11 №7). Ту саму підлогу тримає й сама процедура
    /// <c>arc.usp_ArchiveAudit</c> (PSS-27): менше значення будь-звідки стає 120.
    /// </summary>
    public const int MinAuditMonths = SessionSettings.MinAuditRetentionMonths;

    /// <summary>Типово — рівно підлога.</summary>
    public const int DefaultAuditMonths = SessionSettings.DefaultAuditRetentionMonths;

    /// <summary>Верхня межа, місяців (100 років).</summary>
    public const int MaxAuditMonths = SessionSettings.MaxAuditRetentionMonths;

    /// <summary>Читає чинні строки (без кешу).</summary>
    /// <param name="db">Контекст бази.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="logger">Журнал для попереджень про неприйнятні значення; <c>null</c> — без запису.</param>
    /// <returns>Чинні строки.</returns>
    public static async Task<RetentionPolicy> ReadAsync(EcrDbContext db, CancellationToken ct, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var logDays = await ReadRawAsync(db, TechnicalLogDaysKey, ct).ConfigureAwait(false);
        var auditMonths = await ReadRawAsync(db, AuditMonthsKey, ct).ConfigureAwait(false);

        return new RetentionPolicy(
            Resolve(TechnicalLogDaysKey, logDays, DefaultTechnicalLogDays, MinTechnicalLogDays, MaxTechnicalLogDays, logger),
            Resolve(AuditMonthsKey, auditMonths, DefaultAuditMonths, MinAuditMonths, MaxAuditMonths, logger));
    }

    /// <summary>
    /// Значення рядка → чинне число: рядка немає — типове; не число чи нижче мінімуму — типове (і причина);
    /// вище максимуму — максимум (і причина).
    /// </summary>
    /// <param name="raw">Сире значення з таблиці.</param>
    /// <param name="fallback">Типове.</param>
    /// <param name="min">Нижня межа.</param>
    /// <param name="max">Верхня межа.</param>
    /// <param name="problem">Чому значення не взято як є; <c>null</c> — взято (або рядка немає).</param>
    /// <returns>Чинне значення.</returns>
    public static int Resolve(string? raw, int fallback, int min, int max, out string? problem)
    {
        return SessionSettings.ResolveRetention(raw, fallback, min, max, out problem);
    }

    private static int Resolve(string key, string? raw, int fallback, int min, int max, ILogger? logger)
        => SessionSettings.ResolveRetention(key, raw, fallback, min, max, logger);

    /// <summary>Читає лише строк технічних журналів (без кешу); збій читання йде нагору, а не підміняється типовим.</summary>
    /// <param name="db">Контекст бази.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="logger">Журнал для попереджень; <c>null</c> — без запису.</param>
    /// <returns>Чинний строк, днів.</returns>
    public static async Task<int> ReadTechnicalLogDaysAsync(EcrDbContext db, CancellationToken ct, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var raw = await ReadRawAsync(db, TechnicalLogDaysKey, ct).ConfigureAwait(false);
        return Resolve(TechnicalLogDaysKey, raw, DefaultTechnicalLogDays, MinTechnicalLogDays, MaxTechnicalLogDays, logger);
    }

    private static Task<string?> ReadRawAsync(EcrDbContext db, string key, CancellationToken ct)
        => db.Database
            .SqlQuery<string?>($"SELECT Value AS [Value] FROM sys_ecr.SystemSetting WHERE [Key] = {key}")
            .FirstOrDefaultAsync(ct);
}

/// <summary>Чинні строки зберігання.</summary>
/// <param name="TechnicalLogDays">Днів для технічних журналів.</param>
/// <param name="AuditMonths">Місяців аудиту в <c>aud.*</c> (не менше <see cref="RetentionSettings.MinAuditMonths"/>).</param>
public sealed record RetentionPolicy(int TechnicalLogDays, int AuditMonths);
