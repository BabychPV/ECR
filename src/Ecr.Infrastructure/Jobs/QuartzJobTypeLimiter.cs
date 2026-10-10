// src/Ecr.Infrastructure/Jobs/QuartzJobTypeLimiter.cs
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Межа одночасних задач за типом у режимі Quartz (AN-116, P1-06): експорт і імпорт Excel
/// (<see cref="JobLaneMap.IsExcel"/>) — не більше <see cref="ExcelMaxConcurrency"/> на процес.
/// </summary>
/// <remarks>
/// ⛔ Чому не <c>WithPriority</c> і не <c>[DisallowConcurrentExecution]</c>. Пріоритет триґера в
/// <c>RAMJobStore</c> поступається часу спрацювання, а <c>DisallowConcurrentExecution</c> діє на
/// <c>JobKey</c> — у кожної разової задачі він свій. Лишається межа за ТИПОМ у самому адаптері.
/// <para>
/// ⛔ Задача понад межу НЕ чекає на потоці пулу: <see cref="QuartzJobAdapter"/> ставить їй одноразовий
/// триґер через <see cref="RetryDelay"/> і звільняє потік одразу (той самий прийом, що
/// <c>JobDeferredException</c>, O1). Очікування всередині задачі тримало б потік — рівно те, від чого
/// межа й рятує перерахунок формул.
/// </para>
/// <para>
/// ⛔ Z5-04 / R2-03: черга ЧЕКАЮЧИХ — за порядком надходження (FIFO). Кожна відкладена задача повертається на
/// власному триґері зі своєю фазою, і без порядку вільне місце діставалося тій, чий триґер спрацював першим, а не
/// тій, що чекала найдовше: книга, що стала в чергу першою, могла програвати наступним знову й знову. Тепер
/// відкладена задача записується в чергу при першій відмові (<see cref="TryEnter(Type, string?)"/> за ідентифікатором
/// задачі), а місце дається лише тим, хто в голові черги в межах вільних місць. Очікувач, якого не бачили довше за
/// <see cref="WaiterExpiry"/> (задачу скасовано чи видалено), з черги випадає - інакше мертва голова блокувала б усіх.
/// </para>
/// <para>
/// ⚠ Лічильник — у процесі: режим Quartz — сховище в пам'яті, і задачу виконує той процес, що її
/// поставив. Між інстансами межа не узгоджується (як і решта черги Quartz).
/// </para>
/// </remarks>
/// <param name="excelMaxConcurrency">Межа задач Excel (<see cref="JobLaneMap.ExcelMaxConcurrencyKey"/>); менше за 1 — 1.</param>
/// <param name="time">Годинник для спливу очікувачів; <c>null</c> — системний (лише тести підміняють).</param>
public sealed class QuartzJobTypeLimiter(int excelMaxConcurrency, TimeProvider? time = null)
{
    /// <summary>Ключ кількості потоків пулу Quartz.</summary>
    public const string ThreadCountKey = "Jobs:Quartz:ThreadCount";

    /// <summary>Потоків пулу Quartz, коли ключ не задано (вбудоване значення Quartz — 10).</summary>
    public const int DefaultThreadCount = 16;

    /// <summary>Через скільки повторити задачу, що не вмістилася в межу.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly LinkedList<Waiter> waiters = new();
    private int excelRunning;

    /// <summary>Межа одночасних задач Excel.</summary>
    public int ExcelMaxConcurrency { get; } = Math.Max(1, excelMaxConcurrency);

    /// <summary>Затримка повтору задачі понад межу; лише тести підміняють.</summary>
    public TimeSpan RetryDelay { get; init; } = DefaultRetryDelay;

    /// <summary>Скільки задач Excel виконується зараз.</summary>
    public int ExcelRunning => Volatile.Read(ref excelRunning);

    /// <summary>Скільки задач Excel чекає місця в черзі зараз.</summary>
    public int ExcelWaiting
    {
        get
        {
            lock (gate)
            {
                return waiters.Count;
            }
        }
    }

    /// <summary>
    /// Скільки очікувач може не з'являтись, лишаючись у черзі: кілька затримок повтору (<see cref="RetryDelay"/>),
    /// не менше 30 с - запас на зайнятий пул потоків.
    /// </summary>
    public TimeSpan WaiterExpiry => TimeSpan.FromTicks(Math.Max(RetryDelay.Ticks * 4, TimeSpan.FromSeconds(30).Ticks));

    /// <summary>Потоків пулу Quartz з конфігурації; не задано чи недійсне — <see cref="DefaultThreadCount"/>.</summary>
    /// <remarks>Недійсне значення старт зупиняє раніше (<c>EcrConfigurationValidation.Integers</c>).</remarks>
    public static int ReadThreadCount(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return int.TryParse(configuration[ThreadCountKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
               && value >= 1
            ? value
            : DefaultThreadCount;
    }

    /// <summary>
    /// Займає місце для задачі типу <paramref name="jobType"/> без черги (задача без ідентифікатора): див.
    /// <see cref="TryEnter(Type, string?)"/>.
    /// </summary>
    /// <param name="jobType">Тип реалізації задачі.</param>
    /// <returns>Оренда місця або <c>null</c>.</returns>
    public IDisposable? TryEnter(Type jobType) => TryEnter(jobType, jobId: null);

    /// <summary>
    /// Займає місце для задачі типу <paramref name="jobType"/>: тип без межі - завжди (порожня оренда), Excel - якщо
    /// межу не вичерпано І задача в голові черги чекаючих в межах вільних місць. <c>null</c> - місця немає, задачу
    /// слід відкласти (вона записана в чергу за порядком першої відмови).
    /// </summary>
    /// <param name="jobType">Тип реалізації задачі.</param>
    /// <param name="jobId">
    /// Ідентифікатор задачі (<c>JobKey.Name</c>): за ним черга впізнає ту саму задачу при кожному поверненні;
    /// <c>null</c> - задача в чергу не стає (новоприбула, що не вмістилася, просто відкладається).
    /// </param>
    /// <returns>Оренда місця (звільняється <see cref="IDisposable.Dispose"/>) або <c>null</c>.</returns>
    public IDisposable? TryEnter(Type jobType, string? jobId)
    {
        ArgumentNullException.ThrowIfNull(jobType);

        if (!JobLaneMap.IsExcel(jobType))
        {
            return new Slot(null);
        }

        lock (gate)
        {
            DropExpired();

            var free = ExcelMaxConcurrency - Volatile.Read(ref excelRunning);
            var node = jobId is null ? null : Find(jobId);

            // Позиція в черзі: скільки чекаючих СТОЇТЬ ПЕРЕД задачею (новоприбула - усі наявні).
            var ahead = 0;
            for (var current = waiters.First; current is not null && current != node; current = current.Next)
            {
                ahead++;
            }

            if (ahead < free)
            {
                if (node is not null)
                {
                    waiters.Remove(node);
                }

                Interlocked.Increment(ref excelRunning);
                return new Slot(this);
            }

            if (node is not null)
            {
                node.Value.SeenAt = clock.GetTimestamp();
            }
            else if (jobId is not null)
            {
                waiters.AddLast(new Waiter(jobId, clock.GetTimestamp()));
            }

            return null;
        }
    }

    /// <summary>Знімає задачу з черги чекаючих (скасована чи видалена); <c>false</c> - її там не було.</summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    public bool Forget(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        lock (gate)
        {
            var node = Find(jobId);
            if (node is null)
            {
                return false;
            }

            waiters.Remove(node);
            return true;
        }
    }

    private LinkedListNode<Waiter>? Find(string jobId)
    {
        for (var current = waiters.First; current is not null; current = current.Next)
        {
            if (string.Equals(current.Value.JobId, jobId, StringComparison.Ordinal))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>Прибирає очікувачів, яких не бачили довше за <see cref="WaiterExpiry"/>.</summary>
    private void DropExpired()
    {
        var expiry = WaiterExpiry;
        var now = clock.GetTimestamp();

        for (var current = waiters.First; current is not null;)
        {
            var next = current.Next;
            if (clock.GetElapsedTime(current.Value.SeenAt, now) > expiry)
            {
                waiters.Remove(current);
            }

            current = next;
        }
    }

    /// <summary>Чекаюча задача: ідентифікатор і момент, коли її востаннє бачили.</summary>
    private sealed class Waiter(string jobId, long seenAt)
    {
        public string JobId { get; } = jobId;

        public long SeenAt { get; set; } = seenAt;
    }

    /// <summary>Оренда місця; звільнення ідемпотентне.</summary>
    private sealed class Slot(QuartzJobTypeLimiter? owner) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (owner is not null && Interlocked.Exchange(ref released, 1) == 0)
            {
                Interlocked.Decrement(ref owner.excelRunning);
            }
        }
    }
}
