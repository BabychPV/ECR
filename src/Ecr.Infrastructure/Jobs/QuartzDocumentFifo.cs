// src/Ecr.Infrastructure/Jobs/QuartzDocumentFifo.cs
using Ecr.Application.Ports;
using Quartz;
using Quartz.Impl.Matchers;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ M11 (репетиція W15z, JQ-FIFO у режимі Quartz): перерахунки документа (<see cref="IRecalculationJob"/>)
/// стартують у порядку ПОСТАНОВКИ — задача документа не стартує, поки в цьому планувальнику чекає чи виконується
/// раніше поставлена задача перерахунку того самого документа.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Чому окремо від лока документа (<see cref="RecalculationDocumentLock"/>). Лок серіалізує прогони, але не
/// впорядковує: задача, що не дочекалась лока, відкладається новим триґером (<see cref="RecalculationDocumentLock.DeferDelay"/>),
/// а постановка, що прийшла у вільну мить, бере лок одразу. W15z: правка HSE400 у жовтні поставила залежні Land
/// <c>p202610</c> (EnqueueSeq 39) і <c>p202611</c> (40); 39 чекав лок і відклався, 40 прийшов у мить звільнення й
/// стартував першим (21:46:34 проти 21:46:44). Дві постановки однієї мілісекунди (29/30) — порядок вирішував GUID у
/// ключі триґера (<c>RAMJobStore</c> сортує однаковий час спрацювання за ключем). Черга в базі цього не має
/// (<c>ORDER BY AvailableAt, EnqueueSeq, JobId</c> і <c>DocumentExclusion</c>), Quartz — тепер теж.
/// </para>
/// <para>
/// ⚠ Порядок — лічильник процесу (<see cref="NextSequence"/>) у даних задачі, а не <c>itg.JobProgress.EnqueueSeq</c>:
/// сховище прогресу для адаптера необов'язкове, а черга Quartz однаково в пам'яті процесу. Злиття в задачу, що чекає
/// (<c>EnqueueCoalesced</c>), нового номера не дає — вона лишається на своєму місці, як злиття в базі.
/// </para>
/// <para>
/// ⚠ «Чекає» — є триґер (зокрема відкладення чи пауза ретраю) або виконується зараз. Задача, що впала остаточно
/// (дурабельна деталь без триґера), і знята з черги нікого не тримають — мертвої голови немає. Перерахунок, який
/// натиснула людина (<see cref="RecalculationJob.IsManualPress"/>), не стоїть за системними — як прохід «спершу
/// ручні» черги в базі; за ним — стоять.
/// </para>
/// </remarks>
public static class QuartzDocumentFifo
{
    /// <summary>Ключ документа задачі в <c>JobDataMap</c>.</summary>
    public const string DocumentKey = "ecr.fifoDocumentId";

    /// <summary>Ключ номера постановки в <c>JobDataMap</c>.</summary>
    public const string SequenceKey = "ecr.fifoSequence";

    /// <summary>Через скільки повторити задачу, що стоїть за ранішою задачею документа.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    private static long last;

    /// <summary>Наступний номер постановки (строго зростає в процесі).</summary>
    public static long NextSequence() => Interlocked.Increment(ref last);

    /// <summary>Чи діє порядок на задачу з цим кодом (<c>JobCodeKey</c>).</summary>
    /// <param name="jobCode">Повне ім'я типу-маркера задачі.</param>
    public static bool AppliesTo(string? jobCode)
        => string.Equals(jobCode, JobListKinds.RecalculationJobCode, StringComparison.Ordinal);

    /// <summary>
    /// Раніше поставлена задача перерахунку того самого документа, що ще чекає чи виконується; <c>null</c> — немає
    /// (задача може стартувати).
    /// </summary>
    /// <param name="scheduler">Планувальник задачі.</param>
    /// <param name="detail">Деталь задачі, що стартує.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<string?> EarlierPendingAsync(IScheduler scheduler, IJobDetail detail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(detail);

        if (!TryRead(detail.JobDataMap, out var documentId, out var sequence))
        {
            return null;
        }

        HashSet<JobKey>? executing = null;

        foreach (var key in await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct).ConfigureAwait(false))
        {
            // Усі ключі разових перерахунків починаються з імені маркера (`EnqueueAsync`, ціль `~`, злиття).
            if (key.Equals(detail.Key) || !key.Name.StartsWith(nameof(IRecalculationJob), StringComparison.Ordinal))
            {
                continue;
            }

            var other = await scheduler.GetJobDetail(key, ct).ConfigureAwait(false);
            if (other is null
                || !TryRead(other.JobDataMap, out var otherDocument, out var otherSequence)
                || otherDocument != documentId
                || otherSequence >= sequence)
            {
                continue;
            }

            executing ??= [.. (await scheduler.GetCurrentlyExecutingJobs(ct).ConfigureAwait(false)).Select(c => c.JobDetail.Key)];

            if (executing.Contains(key) || (await scheduler.GetTriggersOfJob(key, ct).ConfigureAwait(false)).Count > 0)
            {
                return key.Name;
            }
        }

        return null;
    }

    private static bool TryRead(JobDataMap map, out long documentId, out long sequence)
    {
        documentId = 0;
        sequence = 0;

        if (map.TryGetValue(DocumentKey, out var document) && document is long d
            && map.TryGetValue(SequenceKey, out var number) && number is long s)
        {
            documentId = d;
            sequence = s;
            return true;
        }

        return false;
    }
}

/// <summary>Перевизначення паузи <see cref="QuartzDocumentFifo"/>; лише тести (у застосунку не реєструється).</summary>
/// <param name="RetryDelay">Пауза перед повтором задачі, що стоїть за ранішою.</param>
public sealed record QuartzDocumentFifoSettings(TimeSpan RetryDelay);
