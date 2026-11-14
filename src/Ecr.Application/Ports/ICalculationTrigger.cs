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
}
