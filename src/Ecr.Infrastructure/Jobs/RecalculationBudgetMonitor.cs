// src/Ecr.Infrastructure/Jobs/RecalculationBudgetMonitor.cs
using System.Diagnostics.Metrics;
using System.Globalization;
using Ecr.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Налаштування бюджету річного перерахунку (ПРД-13): поріг сигналу і режим
/// виконавця для тега метрики.
/// </summary>
/// <param name="WarnAfter">Після скількох секунд одна задача перерахунку вважається такою, що вийшла за бюджет.</param>
/// <param name="Mode">Режим черги — <c>Database</c> або <c>Quartz</c> (<c>Jobs:Queue:Mode</c>).</param>
/// <remarks>
/// ⚠ Читається з конфігурації процесу, а не з <c>Ecr.Calculations.CalculationLimits</c>:
/// <c>Ecr.Infrastructure</c> на ту збірку не посилається. Секція та сама —
/// <c>Calculations</c>. Недійсне значення на старті Api зупиняє
/// <c>EcrConfigurationValidation</c>; воркер-процес (без цієї перевірки) бере
/// дефолт, а не падає посеред задачі.
/// </remarks>
public sealed record RecalculationBudgetOptions(TimeSpan WarnAfter, string Mode)
{
    /// <summary>Ключ порога, секунди.</summary>
    public const string WarnSecondsKey = "Calculations:FullYearWarnSeconds";

    /// <summary>Бюджет ПРД-13: повний річний перерахунок ≤ 10 хвилин.</summary>
    public const int DefaultWarnSeconds = 600;

    /// <summary>Читає налаштування з конфігурації процесу.</summary>
    /// <param name="configuration">Конфігурація.</param>
    public static RecalculationBudgetOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var seconds = int.TryParse(
                          configuration[WarnSecondsKey]?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                      && parsed >= 1
            ? parsed
            : DefaultWarnSeconds;

        return new RecalculationBudgetOptions(
            TimeSpan.FromSeconds(seconds), DbBackgroundJobScheduler.ReadMode(configuration).ToString());
    }
}

/// <summary>
/// Вимір бюджету перерахунку (ПРД-13, НФ-8.6.4): гістограма
/// <c>ecr.calc.full_year</c> і сигнал про перевищення порога.
/// </summary>
/// <remarks>
/// ⛔ ЧОМУ ТУТ, а не в <c>Ecr.Api.Observability.EcrMetrics</c>. Перерахунок виконує
/// і Api (режими Quartz та InProcess), і окремий <c>Ecr.Worker</c> (лейн черги в
/// базі), а той на <c>Ecr.Api</c> не посилається. До цього гістограма жила в
/// <c>EcrMetrics</c>, не мала жодного викликача, і бюджет, заради якого її
/// оголосили, ніде не вимірювався (аудит вимог, ПРД-13). Тепер інструмент один —
/// тут; <c>EcrMetrics.CalcFullYear</c> лише посилається на його ім'я.
///
/// ⚠ Meter <c>Ecr</c> створюється завжди (через <see cref="IMeterFactory"/>, а без
/// нього — власний): вимірювання не залежить від того, чи ввімкнено експорт OTLP
/// (<c>Telemetry:Enabled</c>) — лише його ВИДНО без експорту не буде. Сигнал, що
/// працює й без нього, — рядок Warning у журналі та конверт у
/// <c>itg.JobProgress.Message</c>, який рахує перевірка <c>jobs</c>
/// (<c>/health/ready</c>).
///
/// ⚠ Вимір НЕ змінює логіки перерахунку: клас лише читає різницю годинника, яку
/// дає задача, і нічого не кидає (збій спостерігача не має валити перерахунок).
/// </remarks>
public sealed partial class RecalculationBudgetMonitor : IDisposable
{
    /// <summary>Назва Meter — та сама, що в <c>EcrMetrics.MeterName</c>.</summary>
    public const string MeterName = "Ecr";

    /// <summary>Назва гістограми повного річного перерахунку, секунди.</summary>
    public const string InstrumentName = "ecr.calc.full_year";

    /// <summary>Ключ каталогу конверта про перевищення бюджету в <c>itg.JobProgress.Message</c>.</summary>
    public const string OverBudgetKey = "jobs.recalcOverBudget";

    private readonly RecalculationBudgetOptions options;
    private readonly ILogger<RecalculationBudgetMonitor> logger;
    private readonly Meter? ownedMeter;
    private readonly Histogram<double> fullYearHistogram;

    /// <summary>Створює вимірювач.</summary>
    /// <param name="options">Поріг і режим.</param>
    /// <param name="logger">Журнал.</param>
    /// <param name="meterFactory">Фабрика Meter; <c>null</c> — власний Meter.</param>
    public RecalculationBudgetMonitor(
        RecalculationBudgetOptions options,
        ILogger<RecalculationBudgetMonitor> logger,
        IMeterFactory? meterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options;
        this.logger = logger;

        Meter meter;
        if (meterFactory is null)
        {
            ownedMeter = new Meter(MeterName);
            meter = ownedMeter;
        }
        else
        {
            meter = meterFactory.Create(MeterName);
        }

        fullYearHistogram = meter.CreateHistogram<double>(
            InstrumentName, "s", "Повний річний перерахунок одного документа (бюджет ПРД-13 — 600 с)");
    }

    /// <summary>Поріг, після якого задача вважається такою, що вийшла за бюджет.</summary>
    public TimeSpan WarnAfter => options.WarnAfter;

    /// <summary>
    /// Фіксує завершену задачу перерахунку: метрика (лише для повного року) і сигнал
    /// перевищення (для будь-якої задачі довшої за поріг).
    /// </summary>
    /// <param name="projectId">Проєкт — тег метрики.</param>
    /// <param name="documentId">Документ; нуль чи менше — увесь проєкт однією задачею.</param>
    /// <param name="fullYear">Задача рахувала увесь рік (<c>PeriodKey = null</c>).</param>
    /// <param name="elapsed">Скільки тривала задача.</param>
    /// <param name="progress">Канал прогресу задачі: у нього йде конверт перевищення.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>true</c> — задача перевищила поріг.</returns>
    public async Task<bool> ObserveAsync(
        int projectId, long documentId, bool fullYear, TimeSpan elapsed, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (fullYear)
        {
            fullYearHistogram.Record(
                elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("project", projectId),
                new KeyValuePair<string, object?>("mode", options.Mode));
        }

        if (elapsed <= options.WarnAfter)
        {
            return false;
        }

        LogOverBudget(logger, OverBudgetKey, documentId, projectId, elapsed.TotalSeconds, options.WarnAfter.TotalSeconds, options.Mode);

        // ⚠ Конверт — слід для перевірки `jobs`: успішна задача не лишає в базі
        // нічого, за чим можна було б дізнатися, що вона вийшла за бюджет.
        // Розповідь про задачу не сміє її зруйнувати — збій запису йде в журнал.
        try
        {
            await progress
                .ReportAsync(
                    100,
                    JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
                        OverBudgetKey,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["seconds"] = Math.Round(elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                            ["limit"] = Math.Round(options.WarnAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                        })),
                    ct)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Прогрес — розповідь про задачу, а не сама задача: перерахунок уже завершено.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogReportFailed(logger, documentId, projectId, ex);
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose() => ownedMeter?.Dispose();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "[{MessageKey}] Перерахунок документа {DocumentId} проєкту {ProjectId} тривав {Seconds:0} с — понад бюджет {Budget:0} с (ПРД-13, режим {Mode}).")]
    private static partial void LogOverBudget(
        ILogger logger, string messageKey, long documentId, int projectId, double seconds, double budget, string mode);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Не вдалося записати в прогрес перевищення бюджету перерахунку документа {DocumentId} проєкту {ProjectId}; сам перерахунок завершено.")]
    private static partial void LogReportFailed(ILogger logger, long documentId, int projectId, Exception exception);
}
