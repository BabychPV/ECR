// src/Ecr.Infrastructure/Jobs/ApprovedDependentsFallback.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// B80 N5 / перф-аудит 20.11: «залежні з затвердженими» (<see cref="CalculationTrigger.DependentsApprovedTargetOf"/>)
/// ПОКРИВАЄ звичайний перерахунок залежних того самого документа й періоду — звичайна не ставиться чи скасовується
/// (<see cref="CalculationTrigger"/>). Коли покривна задача закінчилась НЕ успіхом (<c>Failed</c> чи <c>Cancelled</c> —
/// людиною чи системою), покрита робота загубилась би разом із нею; тут вона повертається.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Повернення — ЗВИЧАЙНИМ шляхом тригера (<see cref="ICalculationTrigger.RequestDependentsAsync(long, PeriodKey, int, CancellationToken)"/>):
/// діють ті самі правила — злиття з тією, що чекає, покриття іншою «з затвердженими», що вже чекає (наприклад,
/// перепоставленою після вичерпаної стелі відкладень), відмова для закритого періоду чи повністю затвердженого документа.
/// Тому зайвий виклик безпечний: щонайбільше одна звичайна задача.
/// </para>
/// <para>
/// ⚠ Хто кличе: виконавець черги після фіксації результату задачі й після закриття прострочених оренд
/// (<c>JobWorker</c>), і планувальник після скасування людиною задачі, що чекала (<c>DbBackgroundJobScheduler.CancelAsync</c>).
/// Quartz у пам'яті покриття не має (<c>FindQueuedAsync</c> → <c>null</c>), тож там і повертати нічого.
/// </para>
/// <para>
/// ⛔ Не кидає (крім скасування): задачу вже закрито, і збій повернення не має підміняти її результат чи відповідь
/// людині на скасування — причина йде в журнал.
/// </para>
/// </remarks>
/// <param name="db">Контекст (читання рядків черги).</param>
/// <param name="trigger">Тригер перерахунку залежних.</param>
/// <param name="logger">Журнал.</param>
public sealed partial class ApprovedDependentsFallback(
    EcrDbContext db,
    ICalculationTrigger trigger,
    ILogger<ApprovedDependentsFallback>? logger = null)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Дешевий відбір без звернення до бази: перерахунок документа з ціллю «з затвердженими».</summary>
    /// <param name="jobCode">Код задачі.</param>
    /// <param name="targetKey">Ціль черги.</param>
    /// <param name="payloadJson">
    /// ✎ B80-N5f: payload задачі — ціль <c>NULL</c> (черга знімає її, коли рядок не зливається чи позаду вже чекає інша,
    /// або задачу поставлено без цілі) не означає «не з затвердженими»: тоді вирішує <c>IncludeApproved</c> payload
    /// (його ставить лише постановка «з затвердженими», <c>CalculationTrigger</c>).
    /// </param>
    /// <returns><c>true</c> — варто перевірити, чим задача закінчилась.</returns>
    public static bool IsCandidate(string? jobCode, string? targetKey, string? payloadJson = null)
        => string.Equals(jobCode, JobListKinds.RecalculationJobCode, StringComparison.Ordinal)
           && (targetKey is not null
               ? targetKey.EndsWith(CalculationTrigger.DependentsApprovedSuffix, StringComparison.Ordinal)
               : Parse(payloadJson) is { IncludeApproved: true });

    /// <summary>
    /// Для кожної з <paramref name="jobIds"/>, що є «залежні з затвердженими» і закінчилась <c>Failed</c> чи
    /// <c>Cancelled</c>, — звичайний перерахунок залежних того самого документа й періоду.
    /// </summary>
    /// <param name="jobIds">Задачі, що щойно завершились (решту відсіює стан і ціль).</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Задачі, що виконають повернуту роботу (нова, злита чи покривна).</returns>
    public async Task<IReadOnlyList<string>> OnEndedAsync(IReadOnlyCollection<string> jobIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        if (jobIds.Count == 0)
        {
            return [];
        }

        var ids = jobIds.Distinct(StringComparer.Ordinal).ToList();
        var code = JobListKinds.RecalculationJobCode;
        var returned = new List<string>();

        try
        {
            var rows = await db.JobProgresses
                .AsNoTracking()
                .Where(p => ids.Contains(p.JobId)
                            && p.JobCode == code
                            && (p.State == "Failed" || p.State == "Cancelled")
                            && (p.TargetKey == null || p.TargetKey.EndsWith(CalculationTrigger.DependentsApprovedSuffix)))
                .Select(p => new { p.JobId, p.State, p.TargetKey, p.Payload })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                // ✎ B80-N5f: ціль NULL — документ і період відновлюються з payload (IncludeApproved ставить лише постановка
                // «з затвердженими»); ціль є — вона має бути саме ціллю «з затвердженими» цього документа й періоду.
                if (Parse(row.Payload) is not { DocumentId: > 0, PeriodKey: { } period, IncludeApproved: true } request
                    || (row.TargetKey is not null
                        && !string.Equals(
                            row.TargetKey,
                            DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>(
                                CalculationTrigger.DependentsApprovedTargetOf(request.DocumentId, new PeriodKey(period))),
                            StringComparison.Ordinal)))
                {
                    continue;
                }

                try
                {
                    var regular = await trigger
                        .RequestDependentsAsync(request.DocumentId, new PeriodKey(period), request.SourceHop, ct)
                        .ConfigureAwait(false);
                    LogReturned(_logger, row.JobId, row.State, request.DocumentId, period, regular ?? "—");
                    if (regular is not null)
                    {
                        returned.Add(regular);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogReturnFailed(_logger, row.JobId, request.DocumentId, period, ex);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReadFailed(_logger, string.Join(", ", ids), ex);
        }

        return returned;
    }

    private static RecalculationRequest? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RecalculationRequest>(payload, PayloadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "B80 N5: задача «залежні з затвердженими» {JobId} закінчилась {State} — покритий нею звичайний перерахунок "
                  + "залежних документа {DocumentId} за {PeriodKey} повернуто (задача {RegularJobId}; «—» — тригер відмовив).")]
    private static partial void LogReturned(
        ILogger logger, string jobId, string state, long documentId, int periodKey, string regularJobId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "B80 N5: не вдалось повернути звичайний перерахунок залежних документа {DocumentId} за {PeriodKey} після "
                  + "завершення задачі «з затвердженими» {JobId}.")]
    private static partial void LogReturnFailed(ILogger logger, string jobId, long documentId, int periodKey, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "B80 N5: не вдалось перевірити, чим закінчились задачі {JobIds} (повернення покритого перерахунку залежних).")]
    private static partial void LogReadFailed(ILogger logger, string jobIds, Exception exception);
}
