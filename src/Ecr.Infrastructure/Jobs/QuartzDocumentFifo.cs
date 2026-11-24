// src/Ecr.Infrastructure/Jobs/QuartzDocumentFifo.cs
using Ecr.Application.Ports;
using Quartz;

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
    /// ✎ Огляд b11 (MEDIUM-3): індекс задач, що чекають, — планувальник × документ → номери постановки за зростанням. Доти
    /// кожен старт (і кожна повторна перевірка за 2 с) перебирав УСІ ключі планувальника з <c>GetJobDetail</c> на кожен —
    /// O(N²) під замком <c>RAMJobStore</c> на 200 споживачах × 12 періодах.
    /// </summary>
    private static readonly Lock IndexGate = new();

    private static readonly Dictionary<(string Scheduler, long Document), SortedDictionary<long, string>> Index = [];

    private static readonly Dictionary<(string Scheduler, string JobId), (long Document, long Sequence)> ByJob = [];

    /// <summary>Скільки задач у індексі (тести).</summary>
    internal static int IndexedCount
    {
        get
        {
            lock (IndexGate)
            {
                return ByJob.Count;
            }
        }
    }

    /// <summary>Задача стала в чергу (постановка чи ручний перезапуск).</summary>
    /// <param name="scheduler">Ім'я планувальника.</param>
    /// <param name="detail">Деталь задачі з документом і номером (<see cref="DocumentKey"/>, <see cref="SequenceKey"/>).</param>
    public static void Track(string scheduler, IJobDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (!TryRead(detail.JobDataMap, out var document, out var sequence))
        {
            return;
        }

        lock (IndexGate)
        {
            if (!Index.TryGetValue((scheduler, document), out var queue))
            {
                Index[(scheduler, document)] = queue = [];
            }

            queue[sequence] = detail.Key.Name;
            ByJob[(scheduler, detail.Key.Name)] = (document, sequence);
        }
    }

    /// <summary>Задача більше не чекає й не виконується (успіх, скасування, остаточний провал, зняття).</summary>
    /// <param name="scheduler">Ім'я планувальника.</param>
    /// <param name="jobId">Задача.</param>
    public static void Forget(string scheduler, string jobId)
    {
        lock (IndexGate)
        {
            if (!ByJob.Remove((scheduler, jobId), out var entry))
            {
                return;
            }

            if (Index.TryGetValue((scheduler, entry.Document), out var queue)
                && queue.TryGetValue(entry.Sequence, out var id) && string.Equals(id, jobId, StringComparison.Ordinal))
            {
                queue.Remove(entry.Sequence);
                if (queue.Count == 0)
                {
                    Index.Remove((scheduler, entry.Document));
                }
            }
        }
    }

    /// <summary>
    /// Раніше поставлена задача перерахунку того самого документа, що ще чекає чи виконується; <c>null</c> — немає
    /// (задача може стартувати).
    /// </summary>
    /// <param name="scheduler">Планувальник задачі.</param>
    /// <param name="detail">Деталь задачі, що стартує.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// Голова індексу документа — O(log n); перевіряється ЛИШЕ вона (є триґер чи виконується). Голова, що вже не чекає (зникла,
    /// впала остаточно без триґера, зняття пройшло повз <see cref="Forget"/>), з індексу випадає, і береться наступна — мертва
    /// голова нікого не тримає.
    /// </remarks>
    public static async Task<string?> EarlierPendingAsync(IScheduler scheduler, IJobDetail detail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(detail);

        if (!TryRead(detail.JobDataMap, out var documentId, out var sequence))
        {
            return null;
        }

        var name = scheduler.SchedulerName;
        while (true)
        {
            string? head = null;
            lock (IndexGate)
            {
                if (Index.TryGetValue((name, documentId), out var queue))
                {
                    foreach (var (number, jobId) in queue)
                    {
                        if (number >= sequence)
                        {
                            break;
                        }

                        if (!string.Equals(jobId, detail.Key.Name, StringComparison.Ordinal))
                        {
                            head = jobId;
                            break;
                        }
                    }
                }
            }

            if (head is null)
            {
                return null;
            }

            var key = new JobKey(head);
            if ((await scheduler.GetTriggersOfJob(key, ct).ConfigureAwait(false)).Count > 0
                || (await scheduler.GetCurrentlyExecutingJobs(ct).ConfigureAwait(false)).Any(c => c.JobDetail.Key.Equals(key)))
            {
                return head;
            }

            Forget(name, head);
        }
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
