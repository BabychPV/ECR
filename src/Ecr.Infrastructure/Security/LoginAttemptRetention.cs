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
/// PSS-13): один рядок налаштувань на всі технічні журнали. Цей клас має власне читання (<see cref="LoginAttemptPurger.ReadRetentionDaysAsync"/>)
/// лише тому, що файл L6 у цій гілці ще відсутній; при злитті звести до <c>RetentionSettings.ReadAsync(...).TechnicalLogDays</c>.
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

    /// <summary>Підлога: жодні налаштування не стирають спроби молодші за 30 днів.</summary>
    public const int MinDays = Ecr.Application.Security.SessionSettings.MinTechnicalLogRetentionDays;

    /// <summary>Стеля: 10 років.</summary>
    public const int MaxDays = Ecr.Application.Security.SessionSettings.MaxTechnicalLogRetentionDays;

    /// <summary>Інтервал між проходами за замовчуванням, хв.</summary>
    public const int DefaultIntervalMinutes = 60;

    /// <summary>Чинний строк у днях: збережене значення, інакше 400; завжди в межах.</summary>
    /// <param name="stored">Значення з <c>SystemSetting</c>; <c>null</c> — рядка немає.</param>
    /// <returns>Дні в межах <see cref="MinDays"/>–<see cref="MaxDays"/>.</returns>
    public static int Resolve(string? stored)
        => Ecr.Application.Security.SessionSettings.ResolveTechnicalLogRetentionDays(stored);
}

/// <summary>Один прохід прибирання <c>sec.LoginAttempt</c> за чинним строком.</summary>
/// <remarks>
/// ⚠ Пакетами (<see cref="Batch"/> рядків), у межах <see cref="TimeBudget"/>: сканування без індексу за
/// <c>AttemptedAt</c> іде від найстаріших (ключ <c>Id</c> росте з часом), тож пакет знаходить кандидатів одразу, а
/// довгий хвіст дочищається наступним проходом. Безпечний для кількох вузлів одночасно: <c>DELETE</c> умовний.
/// </remarks>
public sealed class LoginAttemptPurger(EcrDbContext db, IClock clock)
{
    /// <summary>Рядків за один <c>DELETE</c>.</summary>
    public const int Batch = 5000;

    /// <summary>Скільки часу один прохід має право працювати.</summary>
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(60);

    /// <summary>Чинний строк у днях (читає базу при КОЖНОМУ виклику).</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<int> ReadRetentionDaysAsync(CancellationToken ct)
    {
        string? stored;
        try
        {
            stored = await db.Database
                .SqlQuery<string?>($"SELECT Value AS [Value] FROM sys_ecr.SystemSetting WHERE [Key] = {LoginAttemptRetention.SettingKey}")
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // ⛔ Збій читання налаштування не має ні вимкнути прибирання, ні скоротити строк: діє значення за
            // замовчуванням (400), тобто СТАРІШЕ, ніж могла поставити людина, а не молодше.
            stored = null;
        }

        return LoginAttemptRetention.Resolve(stored);
    }

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

        do
        {
            batch = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({Batch}) FROM sec.LoginAttempt WHERE AttemptedAt < {cutoff}", ct).ConfigureAwait(false);
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
            var purged = await ActivatorUtilities.CreateInstance<LoginAttemptPurger>(scope.ServiceProvider)
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
