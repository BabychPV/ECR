using System.Globalization;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Строк зберігання <c>sec.LoginAttempt</c> (PSS-24 / PSS-13, рішення людини 20.11 п. 7): 400 днів, але це
/// НАЛАШТУВАННЯ, а не константа.
/// </summary>
/// <remarks>
/// ⛔ Де міняється. Той самий механізм і той самий екран, що й <c>Security.IdleTimeoutMinutes</c> (D-471): рядок
/// <c>sys_ecr.SystemSetting</c> із ключем <see cref="SettingKey"/>, поле «Security → Session» (право
/// <c>Security.ManageUsers</c>, подія <c>SessionSettingsUpdated</c>). Значення читається ПРИ КОЖНОМУ проході прибирання
/// (раз на <see cref="DefaultIntervalMinutes"/> хв), тож нове число діє без перевидання і без перезапуску — не пізніше
/// ніж за цей інтервал. Немає рядка (чи він не число) — <see cref="DefaultDays"/>. Межі й розбір — в одному місці:
/// <see cref="Ecr.Application.Security.SessionSettings"/>.
///
/// ⛔ Стеля й підлога: значення з бази обмежується межами <see cref="MinDays"/>–<see cref="MaxDays"/> (рядок, змінений
/// руками в обхід, — 0, −5, 99999 — не вимикає прибирання й не стирає свіжі спроби).
///
/// ⛔ Ключ <c>Retention.TechnicalLogDays</c> і межі 30–36500 (типово 400) — ТІ САМІ, що в <c>RetentionSettings</c> (лан L6,
/// PSS-13): один рядок налаштувань на всі технічні журнали. Читання й розбір — ОДНЕ правило: ключ, межі й <c>Resolve</c> живуть
/// у <c>SessionSettings</c> (Application), <c>RetentionSettings</c> (L6) делегує йому, а <see cref="LoginAttemptPurger.ReadRetentionDaysAsync"/>
/// читає через <c>RetentionSettings.ReadTechnicalLogDaysAsync</c>.
///
/// ⚠ Що це НЕ чіпає. Прибирається лише оперативний <c>sec.LoginAttempt</c>. Незмінний слід входів — у
/// <c>aud.SecurityEvent</c> (<c>LoginSucceeded</c> / <c>LoginFailed</c> / <c>AccountLocked</c>): його не чистить ніщо в цьому
/// файлі, а архівація тримає його не менше 120 місяців (PSS-27, <c>arc.usp_ArchiveAudit</c>).
/// </remarks>
public static class LoginAttemptRetention
{
    /// <summary>Ключ у <c>sys_ecr.SystemSetting</c> (число днів).</summary>
    public const string SettingKey = Ecr.Application.Security.SessionSettings.TechnicalLogRetentionKey;

    /// <summary>Ключ конфігурації: інтервал між проходами, хв.</summary>
    public const string IntervalMinutesKey = "Security:LoginAttemptRetention:IntervalMinutes";

    /// <summary>Ключ конфігурації: вимикач прибирання (лише для стендів і розслідувань).</summary>
    public const string EnabledKey = "Security:LoginAttemptRetention:Enabled";

    /// <summary>400 днів — слово людини 20.11.</summary>
    public const int DefaultDays = Ecr.Application.Security.SessionSettings.DefaultTechnicalLogRetentionDays;

    /// <summary>Підлога: менше 30 днів — опечатка, а не політика (рядок із меншим значенням дає типове 400 з попередженням).</summary>
    public const int MinDays = Ecr.Application.Security.SessionSettings.MinTechnicalLogRetentionDays;

    /// <summary>Стеля: 100 років (36 500 днів).</summary>
    public const int MaxDays = Ecr.Application.Security.SessionSettings.MaxTechnicalLogRetentionDays;

    /// <summary>Інтервал між проходами за замовчуванням, хв.</summary>
    public const int DefaultIntervalMinutes = 60;

    /// <summary>Чинний строк у днях: правило L6 — немає рядка 400; не число чи менше 30 — 400 з попередженням; більше 36500 — 36500 з попередженням.</summary>
    /// <param name="stored">Значення з <c>SystemSetting</c>; <c>null</c> — рядка немає.</param>
    /// <param name="logger">Куди йде попередження; <c>null</c> — нікуди.</param>
    /// <returns>Дні.</returns>
    public static int Resolve(string? stored, ILogger? logger = null)
        => Ecr.Application.Security.SessionSettings.ResolveTechnicalLogRetentionDays(stored, logger);
}

/// <summary>Один прохід прибирання <c>sec.LoginAttempt</c> за чинним строком.</summary>
/// <remarks>
/// ⚠ Пакетами (<see cref="Batch"/> рядків), у межах <see cref="TimeBudget"/>. Індексу за <c>AttemptedAt</c> немає, тож
/// проти повного сканування — межа за <c>Id</c> (як у L6, PSS-13): один раз на прохід береться <c>Id</c> першого рядка, що
/// молодший за строк, і пакети видаляють лише нижче нього (рядки пишуться в порядку часу). Старий рядок над межею
/// (зсув годинників вузлів) дочищається наступним проходом. Безпечний для кількох вузлів: <c>DELETE</c> умовний.
/// </remarks>
public sealed class LoginAttemptPurger(EcrDbContext db, IClock clock, ILogger? logger = null)
{
    /// <summary>Рядків за один <c>DELETE</c>: 4 000 — нижче порогу ескалації блокувань (≈5 000), як у L6.</summary>
    public const int Batch = 4000;

    /// <summary>Скільки часу один прохід має право працювати.</summary>
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(60);

    /// <summary>Чинний строк у днях (читає базу при КОЖНОМУ виклику).</summary>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Збій читання НЕ підмінюється типовим значенням: людина могла поставити 1000, і мовчазні 400 стерли б дані між
    /// 400 і 1000 днями. Виняток іде нагору — прохід пропускається (службa пише Warning), наступний повторить.
    /// </remarks>
    public async Task<int> ReadRetentionDaysAsync(CancellationToken ct)
        => await Jobs.RetentionSettings.ReadTechnicalLogDaysAsync(db, ct, logger).ConfigureAwait(false);

    /// <summary>Видаляє спроби старші за чинний строк.</summary>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки рядків видалено.</returns>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var days = await ReadRetentionDaysAsync(ct).ConfigureAwait(false);
        var cutoff = clock.UtcNow.AddDays(-days);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var total = 0;
        int batch;

        // Межа за Id: перший рядок, молодший за строк (немає такого — усі рядки старі, межі немає).
        var newer = await db.Database
            .SqlQuery<long?>($"SELECT (SELECT TOP (1) Id FROM sec.LoginAttempt WHERE AttemptedAt >= {cutoff} ORDER BY Id) AS [Value]")
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var upper = newer ?? long.MaxValue;

        do
        {
            batch = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({Batch}) FROM sec.LoginAttempt WHERE Id < {upper} AND AttemptedAt < {cutoff}", ct)
                .ConfigureAwait(false);
            total += batch;
        }
        while (batch >= Batch && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeBudget);

        return total;
    }
}

/// <summary>Фонове прибирання <c>sec.LoginAttempt</c>: перший прохід — за 5 хвилин після старту, далі — за інтервалом.</summary>
/// <remarks>
/// ⚠ Усе береться з контейнера ЛІНИВО, у циклі, і будь-який збій лише пишеться в лог: служба не має права ні
/// зупинити хост на старті (мінімальні хости тестів без бази), ні впасти посеред роботи.
/// </remarks>
public sealed partial class LoginAttemptRetentionService(IServiceProvider services, ILogger<LoginAttemptRetentionService> logger)
    : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = TimeSpan.FromMinutes(DefaultIntervalMinutesOf(services));
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // зупинка хоста
        }
    }

    private static int DefaultIntervalMinutesOf(IServiceProvider services)
    {
        var configuration = services.GetService<IConfiguration>();
        return configuration is not null
               && int.TryParse(
                   configuration[LoginAttemptRetention.IntervalMinutesKey], NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out var minutes) && minutes >= 1
            ? minutes
            : LoginAttemptRetention.DefaultIntervalMinutes;
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var configuration = services.GetService<IConfiguration>();
            if (configuration is not null
                && bool.TryParse(configuration[LoginAttemptRetention.EnabledKey], out var enabled) && !enabled)
            {
                return;
            }

            await using var scope = services.CreateAsyncScope();
            var purged = await ActivatorUtilities.CreateInstance<LoginAttemptPurger>(scope.ServiceProvider, logger)
                .PurgeAsync(ct).ConfigureAwait(false);

            if (purged > 0)
            {
                LogPurged(purged);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogFailed(failure);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Прибрано спроб входу за строком зберігання: {Count}.")]
    private partial void LogPurged(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Прибирання sec.LoginAttempt не вдалося; наступна спроба за інтервалом.")]
    private partial void LogFailed(Exception exception);
}
