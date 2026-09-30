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
/// ⚠ Власної дедуплікації тригер НЕ має: задача ставиться тим самим маркером
/// (<see cref="IRecalculationJob"/>) і з тією самою ціллю
/// (<c>RecalculateDocumentHandler.TargetOf</c>), що й кнопка «Перерахувати» та
/// правка шапки, а дублікати зливає сама черга без витіснення
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
    /// <summary>Ставить перерахунок документа за період, якщо в період можна писати.</summary>
    /// <param name="documentId">Документ, у комірки якого записано нові дані.</param>
    /// <param name="periodKey">Період запису.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Ідентифікатор поставленої задачі; <c>null</c> — задачу не поставлено: період
    /// не знайдено, він ще <c>Scheduled</c>, закритий або має поданий аркуш
    /// (<c>RecalculationWritePolicy</c>).
    /// </returns>
    public Task<string?> RequestAsync(long documentId, PeriodKey periodKey, CancellationToken ct);
}
