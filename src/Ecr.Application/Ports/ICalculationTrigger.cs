// src/Ecr.Application/Ports/ICalculationTrigger.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Автоматичний перерахунок методологій документа за період після того, як
/// у його комірки лягли нові дані без участі людини (HSE301 §4.5, V-5 → <c>D-174</c>).
/// </summary>
/// <remarks>
/// ⛔ Порт існує, щоб постановка автоперерахунку жила в ОДНОМУ місці. Викликачі —
/// <c>MaterializeCollectedDataJob</c> (A4), далі <c>RowWindowFetchJob</c> (A1) і
/// синхронізація подій (A5b).
/// <para>
/// ⚠ Власної дедуплікації тригер НЕ має: задача ставиться маркером
/// (<see cref="IRecalculationJob"/>) зі сталою ціллю на «документ × період»
/// (✎ B83: своєю, <c>CalculationTrigger.AutoTargetOf</c>, а не ціллю кнопки — щоб злиття
/// не губило позначку поданих), а дублікати зливає сама черга без витіснення
/// (<see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/>): виконувана
/// задача не переривається, нова стає ПІСЛЯ неї.
/// </para>
/// <para>
/// ⛔ Кликати ОДИН раз на прогін задачі-викликача, а не на сутність, поле чи
/// комірку: навіть зі злиттям виклик у циклі — зайві звернення до бази, а
/// <see cref="IBackgroundJobScheduler.EnqueueExclusiveAsync{TJob}"/> тут узагалі
/// заборонено — він витісняє виконуваний перерахунок гарячого документа.
/// </para>
/// </remarks>
public interface ICalculationTrigger
{
    /// <summary>
    /// Чи постановка йде в ПОТОЧНУ транзакцію контексту викликача (черга в базі,
    /// <see cref="IBackgroundJobScheduler.EnlistsInCallerTransaction"/>, MI-02 (в)).
    /// </summary>
    /// <remarks>
    /// ⛔ F2-04 (аудит R11): <c>true</c> — постановку роблять ВСЕРЕДИНІ транзакції, що змінила дані (видалення
    /// рядків подій), останнім оператором: збій відкочує й дані, а закомічене завжди має задачу. <c>false</c>
    /// (Quartz у пам'яті) — задача стартує раніше за коміт і прочитала б старі дані, тож ставити треба ПІСЛЯ
    /// коміту. Типово <c>false</c> — поведінка, що була до F2-04.
    /// </remarks>
    public bool EnlistsInCallerTransaction => false;

    /// <summary>
    /// Ставить перерахунок документа за період після збору PI чи синхронізації подій, якщо в період можна писати.
    /// ✎ B83: як і залежні (B63) — пропускаються лише затверджені аркуші, подані перераховуються з позначкою.
    /// </summary>
    /// <param name="documentId">Документ, у комірки якого записано нові дані.</param>
    /// <param name="periodKey">Період запису.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Ідентифікатор поставленої задачі; <c>null</c> — задачу не поставлено: період
    /// не знайдено, він ще <c>Scheduled</c>, закритий, проєкт архівований (B97) або затверджено всі аркуші.
    /// </returns>
    public Task<string?> RequestAsync(long documentId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// B63: ставить перерахунок документа, ЗАЛЕЖНОГО від зміненого довідника чи документа-джерела (RT-25, HSE400 3b,
    /// FLERT F6) — пропускаються лише затверджені аркуші, подані перераховуються з позначкою для погоджувача.
    /// </summary>
    /// <param name="documentId">Залежний документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Ідентифікатор задачі; <c>null</c> — період не знайдено, <c>Scheduled</c>, закритий, проєкт архівований (B97)
    /// або затверджено всі аркуші.
    /// </returns>
    /// <remarks>Типова реалізація — як <see cref="RequestAsync"/> (для фейків тестів); справжня — <c>CalculationTrigger</c>.</remarks>
    public Task<string?> RequestDependentsAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
        => RequestAsync(documentId, periodKey, ct);

    /// <summary>
    /// B179 L2: те саме, що <see cref="RequestDependentsAsync(long, PeriodKey, CancellationToken)"/>, але з ланкою
    /// ланцюжка джерел: перерахунок споживача, що сам є джерелом, понесе її далі (стеля
    /// <see cref="RegistryImpactRecalculationRequest.MaxHops"/>).
    /// </summary>
    /// <param name="documentId">Залежний документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sourceHop">Ланка (≥ 1 для залежного від зміненого джерела).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Як у <see cref="RequestDependentsAsync(long, PeriodKey, CancellationToken)"/>.</returns>
    /// <remarks>Типова реалізація ланку відкидає (фейки тестів); справжня — <c>CalculationTrigger</c>.</remarks>
    public Task<string?> RequestDependentsAsync(long documentId, PeriodKey periodKey, int sourceHop, CancellationToken ct)
        => RequestDependentsAsync(documentId, periodKey, ct);

    /// <summary>
    /// B80 N5 (D-554, рішення людини 19.11): те саме, що
    /// <see cref="RequestDependentsAsync(long, PeriodKey, int, CancellationToken)"/>, але з ЯВНИМ перекриттям
    /// адміністратора: перераховуються й ЗАТВЕРДЖЕНІ аркуші. Право на перекриття перевіряє ендпоінт, що його ввімкнув.
    /// </summary>
    /// <param name="documentId">Залежний документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sourceHop">Ланка ланцюжка джерел.</param>
    /// <param name="approvedOverride">Перекриття: хто й чому (журнал записів аркушів); <c>null</c> — як без нього.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Як у <see cref="RequestDependentsAsync(long, PeriodKey, CancellationToken)"/>.</returns>
    /// <remarks>Типова реалізація перекриття відкидає (фейки тестів); справжня — <c>CalculationTrigger</c>.</remarks>
    public Task<string?> RequestDependentsAsync(
        long documentId, PeriodKey periodKey, int sourceHop, ApprovedRecalculationOverride? approvedOverride, CancellationToken ct)
        => RequestDependentsAsync(documentId, periodKey, sourceHop, ct);

    /// <summary>
    /// ✎ PERF-06 (<c>D-566</c>): чи тригер ставить каскад від правки документа-джерела ОДНІЄЮ задачею на документ-споживач і
    /// рік з об'єднанням періодів (<see cref="RequestSourceCascadeAsync"/>); <c>false</c> — поперіодно
    /// (<see cref="RequestDependentsAsync(long, PeriodKey, int, CancellationToken)"/>), як до D-566.
    /// </summary>
    /// <remarks>Типово <c>false</c> (фейки тестів); справжній тригер — за чергою (<see cref="IBackgroundJobScheduler.MergesCascadePeriods"/>).</remarks>
    public bool CoalescesCascadePeriods => false;

    /// <summary>
    /// ✎ PERF-06 (<c>D-566</c>, рішення людини 22.11): каскад від правки документа-джерела B179 — ОДНА задача на документ-споживач
    /// і рік, що рахує лише <paramref name="periodKeys"/> (періоди, які читають змінені місяці джерела) і пізніші місяці, що
    /// читають їхні обчислені числа. Постановки за вікно каскаду (<c>CalculationTrigger.CascadeDebounce</c>) зливаються з
    /// ОБ'ЄДНАННЯМ періодів; ланцюжок, що вже йде, дописує нові періоди, не повертаючись на перший місяць.
    /// </summary>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="year">Календарний рік періодів.</param>
    /// <param name="periodKeys">Періоди споживача (ключі <c>yyyyMM</c> року <paramref name="year"/>).</param>
    /// <param name="sourceHop">Ланка ланцюжка джерел (≥ 1).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Задача і періоди, які вона рахуватиме; без задачі — жоден період не дозволяє запису.</returns>
    /// <remarks>Типова реалізація — поперіодно (фейки тестів).</remarks>
    public async Task<SourceCascadeEnqueued> RequestSourceCascadeAsync(
        long documentId, int year, IReadOnlyCollection<int> periodKeys, int sourceHop, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);
        string? last = null;
        var queued = new List<int>();
        foreach (var period in periodKeys.Distinct().Order())
        {
            if (await RequestDependentsAsync(documentId, new PeriodKey(period), sourceHop, ct).ConfigureAwait(false) is { } jobId)
            {
                last = jobId;
                queued.Add(period);
            }
        }

        return new SourceCascadeEnqueued(last, queued);
    }
}

/// <summary>✎ PERF-06 (<c>D-566</c>): результат постановки каскаду від правки документа-джерела.</summary>
/// <param name="JobId">Задача; <c>null</c> — не поставлено (жоден період не дозволяє запису).</param>
/// <param name="Periods">Періоди, які задача рахуватиме (дозволені до запису, без покритих «з затвердженими»).</param>
public sealed record SourceCascadeEnqueued(string? JobId, IReadOnlyList<int> Periods);

/// <summary>
/// B80 N5 (D-554): явне перекриття «перерахувати також затверджені залежні» — лише адміністратор з правом
/// <c>Calculation.RecalculateApprovedDependents</c>; причина обов'язкова й іде в журнал записів аркушів.
/// </summary>
/// <param name="UserId">Хто ввімкнув перекриття.</param>
/// <param name="Reason">Причина (не порожня).</param>
public sealed record ApprovedRecalculationOverride(int UserId, string Reason);
