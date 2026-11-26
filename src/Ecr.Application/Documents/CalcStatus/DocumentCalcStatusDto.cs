// src/Ecr.Application/Documents/CalcStatus/DocumentCalcStatusDto.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Documents.CalcStatus;

/// <summary>
/// ✎ D-577 (UX-огляд екрана документа, L3): стан розрахунку документа за період — один об'єкт для чипа «Calculated / Out of
/// date / Queued / Recalculating / Failed» (<c>GET /documents/{id}/calc-status</c>). Стан береться із сервера, тож
/// переживає перезавантаження сторінки й показує чужі, нічні та автоматичні перерахунки.
/// </summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період запиту (місяць на екрані).</param>
/// <param name="Year">
/// Рік, якщо документ річної розкладки (<c>YearWithMonthColumns</c>, місячний проєкт): <see cref="StaleMonths"/> охоплює всі
/// місяці року з актуальним прогоном; <c>null</c> — лише <see cref="PeriodKey"/>.
/// </param>
/// <param name="ResultsStale">
/// Застаріло хоч в одному місяці, який МОЖНА перерахувати (Q3, рішення людини 26.11): застарілість закритих, ще не
/// відкритих, затверджених місяців і архівованого проєкту — не попередження, а довідка (<see cref="StaleMonths"/> з
/// <c>recalculable = false</c>). <c>null</c> — не визначено (<see cref="ResultsStaleUndetermined"/>).
/// </param>
/// <param name="ResultsStaleSince">Найраніший момент застарілості серед місяців, які можна перерахувати; <c>null</c> — не застаріло.</param>
/// <param name="ResultsStaleUndetermined">Чому <see cref="ResultsStale"/> = <c>null</c> (D-446: частину документа читачеві не відкрито).</param>
/// <param name="StaleMonths">Застарілі місяці (за зростанням); порожньо — застарілих немає чи не визначено.</param>
/// <param name="ActiveJobs">Перерахунки документа в черзі й ті, що виконуються (спершу виконувані); порожньо — немає.</param>
/// <param name="CalculatedAt">Кінець останнього актуального прогону документа за періоди статусу; <c>null</c> — прогону немає.</param>
/// <param name="LastFailure">
/// Останній провалений перерахунок документа за добу, новіший за <see cref="CalculatedAt"/>; <c>null</c> — немає (або вже
/// є новіший успішний прогін).
/// </param>
public sealed record DocumentCalcStatusDto(
    long DocumentId,
    int PeriodKey,
    int? Year,
    bool? ResultsStale,
    DateTime? ResultsStaleSince,
    ResultsStaleUndeterminedReason? ResultsStaleUndetermined,
    IReadOnlyList<CalcStaleMonthDto> StaleMonths,
    IReadOnlyList<CalcActiveJobDto> ActiveJobs,
    DateTime? CalculatedAt,
    CalcFailureDto? LastFailure);

/// <summary>Застарілий місяць документа.</summary>
/// <param name="PeriodKey">Місяць (<c>yyyyMM</c>).</param>
/// <param name="Since">Відколи застаріло (перша правка входу чи зміна довідника після прогону), UTC.</param>
/// <param name="Recalculable">Перерахунок цей місяць перерахує (ручний чи автоматичний).</param>
/// <param name="LockedReason">
/// Чому не перерахує (<see cref="CalcLockedReasons"/>): <c>closed</c>, <c>notOpen</c>, <c>approved</c>, <c>archived</c>;
/// <c>null</c> — <see cref="Recalculable"/>.
/// </param>
public sealed record CalcStaleMonthDto(int PeriodKey, DateTime? Since, bool Recalculable, string? LockedReason);

/// <summary>Перерахунок документа в черзі чи той, що виконується.</summary>
/// <param name="JobId">Задача (опитування — <c>GET /jobs/{id}</c> лише для свого; чип опитує цей ресурс).</param>
/// <param name="State"><c>Queued</c> або <c>Running</c>.</param>
/// <param name="Kind">
/// Звідки задача (<see cref="CalcJobKinds"/>): <c>manual</c> — кнопка людини, <c>auto</c> — автоперерахунок після правки чи
/// збору даних, <c>cascade</c> — залежні від джерела чи довідника, <c>scheduled</c> — нічний розклад, перерахунок проєкту,
/// правка шапки.
/// </param>
/// <param name="Percent">Прогрес 0..100.</param>
/// <param name="QueuePosition">Місце в черзі перерахунку (D-564c); <c>null</c> — виконується чи черга без позицій (Quartz).</param>
/// <param name="EstimatedStartAt">ОРІЄНТОВНИЙ старт (D-564c); <c>null</c> — оцінити нема з чого.</param>
/// <param name="StepDone">Ланцюжок року: скільки місяців уже зроблено («місяць k з N»: k = StepDone + 1); <c>null</c> — не ланцюжок.</param>
/// <param name="StepTotal">Ланцюжок року: скільки місяців разом; <c>null</c> — невідомо.</param>
/// <param name="CurrentPeriodKey">Місяць, який рахується зараз (або найближчий у плані); <c>null</c> — невідомо.</param>
/// <param name="QueuedAt">Перша постановка, UTC.</param>
/// <param name="StartedAt">Старт виконання, UTC; <c>null</c> — ще в черзі.</param>
/// <param name="Own">Задачу поставив сам читач.</param>
/// <param name="StartedByDisplayName">Хто поставив; <c>null</c> — системна задача або читачеві з обмеженнями не показується.</param>
public sealed record CalcActiveJobDto(
    string JobId,
    string State,
    string Kind,
    int Percent,
    int? QueuePosition,
    DateTime? EstimatedStartAt,
    int? StepDone,
    int? StepTotal,
    int? CurrentPeriodKey,
    DateTime? QueuedAt,
    DateTime? StartedAt,
    bool Own,
    string? StartedByDisplayName);

/// <summary>Провалений перерахунок документа.</summary>
/// <param name="JobId">Задача.</param>
/// <param name="FailedAt">Коли провалився, UTC.</param>
/// <param name="ErrorCode">Код каталогу помилок (<c>err.{code}…</c> у клієнта); <c>null</c> — невідомий.</param>
/// <param name="CorrelationId">Кореляція для підтримки.</param>
/// <param name="Kind">Як у <see cref="CalcActiveJobDto.Kind"/>.</param>
/// <param name="Own">Задачу поставив сам читач.</param>
public sealed record CalcFailureDto(string JobId, DateTime FailedAt, string? ErrorCode, string? CorrelationId, string Kind, bool Own);

/// <summary>Причини, з яких застарілий місяць не перераховується (<see cref="CalcStaleMonthDto.LockedReason"/>).</summary>
public static class CalcLockedReasons
{
    /// <summary>Місяць закритий (і не відкритий для документа винятком).</summary>
    public const string Closed = "closed";

    /// <summary>Місяць ще не відкрито (<c>Scheduled</c>, D-575) або його немає в календарі проєкту.</summary>
    public const string NotOpen = "notOpen";

    /// <summary>Затверджено кожен аркуш складу (оригінал і кожна копія): затверджені числа не перераховуються.</summary>
    public const string Approved = "approved";

    /// <summary>Проєкт архівований або архівується (B69).</summary>
    public const string Archived = "archived";
}

/// <summary>Звідки задача перерахунку (<see cref="CalcActiveJobDto.Kind"/>).</summary>
public static class CalcJobKinds
{
    /// <summary>Кнопка «Перерахувати» людини.</summary>
    public const string Manual = "manual";

    /// <summary>Автоперерахунок після правки (D-577) чи збору даних (D-174).</summary>
    public const string Auto = "auto";

    /// <summary>Залежні від документа-джерела чи довідника.</summary>
    public const string Cascade = "cascade";

    /// <summary>Нічний розклад, перерахунок проєкту, правка шапки, перенос версії — без людини-автора.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Вид за ключем цілі черги й автором задачі.</summary>
    /// <param name="targetKey">
    /// Ключ цілі черги в базі (<c>IRecalculationJob~doc…-auto</c>) або ідентифікатор задачі Quartz (<c>IRecalculationJob~ціль~guid</c>
    /// — ціль усередині); <c>null</c> — невідомо.
    /// </param>
    /// <param name="createdByUserId">Автор; <c>null</c> — системна.</param>
    public static string Of(string? targetKey, int? createdByUserId)
    {
        if (targetKey is not null)
        {
            if (targetKey.Contains("-auto", StringComparison.Ordinal))
            {
                return Auto;
            }

            if (targetKey.Contains("-dependents", StringComparison.Ordinal))
            {
                return Cascade;
            }
        }

        return createdByUserId is null ? Scheduled : Manual;
    }
}
