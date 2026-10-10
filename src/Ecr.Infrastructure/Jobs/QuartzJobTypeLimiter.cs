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
/// ⚠ Лічильник — у процесі: режим Quartz — сховище в пам'яті, і задачу виконує той процес, що її
/// поставив. Між інстансами межа не узгоджується (як і решта черги Quartz).
/// </para>
/// </remarks>
/// <param name="excelMaxConcurrency">Межа задач Excel (<see cref="JobLaneMap.ExcelMaxConcurrencyKey"/>); менше за 1 — 1.</param>
public sealed class QuartzJobTypeLimiter(int excelMaxConcurrency)
{
    /// <summary>Ключ кількості потоків пулу Quartz.</summary>
    public const string ThreadCountKey = "Jobs:Quartz:ThreadCount";

    /// <summary>Потоків пулу Quartz, коли ключ не задано (вбудоване значення Quartz — 10).</summary>
    public const int DefaultThreadCount = 16;

    /// <summary>Через скільки повторити задачу, що не вмістилася в межу.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);

    private int excelRunning;

    /// <summary>Межа одночасних задач Excel.</summary>
    public int ExcelMaxConcurrency { get; } = Math.Max(1, excelMaxConcurrency);

    /// <summary>Затримка повтору задачі понад межу; лише тести підміняють.</summary>
    public TimeSpan RetryDelay { get; init; } = DefaultRetryDelay;

    /// <summary>Скільки задач Excel виконується зараз.</summary>
    public int ExcelRunning => Volatile.Read(ref excelRunning);

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
    /// Займає місце для задачі типу <paramref name="jobType"/>: тип без межі — завжди (порожня оренда),
    /// Excel — якщо межу не вичерпано. <c>null</c> — місця немає, задачу слід відкласти.
    /// </summary>
    /// <param name="jobType">Тип реалізації задачі.</param>
    /// <returns>Оренда місця (звільняється <see cref="IDisposable.Dispose"/>) або <c>null</c>.</returns>
    public IDisposable? TryEnter(Type jobType)
    {
        ArgumentNullException.ThrowIfNull(jobType);

        if (!JobLaneMap.IsExcel(jobType))
        {
            return new Slot(null);
        }

        while (true)
        {
            var current = Volatile.Read(ref excelRunning);
            if (current >= ExcelMaxConcurrency)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref excelRunning, current + 1, current) == current)
            {
                return new Slot(this);
            }
        }
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
