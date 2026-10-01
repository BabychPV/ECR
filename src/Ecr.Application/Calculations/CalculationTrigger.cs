// src/Ecr.Application/Calculations/CalculationTrigger.cs
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>
/// Ставить автоматичний перерахунок документа за період (HSE301 §4.5, V-5 → <c>D-174</c>).
/// </summary>
/// <remarks>
/// ⛔ Без прапорця: нові дані без перерахунку однаково блокують подання
/// (<c>SubmitSheetHandler</c>), тобто прапорець лише відкладав би ту саму дію.
/// Кнопка «Перерахувати» лишається для «застаріло» з інших причин.
/// <para>
/// ⚠ Дедуплікація — НЕ тут. Задача ставиться тим самим маркером
/// <see cref="IRecalculationJob"/> і з тією самою ціллю
/// <see cref="RecalculateDocumentHandler.TargetOf"/>, що й кнопка на документі
/// (<c>RecalculateDocumentHandler</c>) і правка шапки
/// (<c>DocumentHeaderHandlers.EnqueueRecalculationAsync</c>). Тож черга сама
/// лишає одну живу задачу на пару «документ × період» — і між автоперерахунком
/// та ручним теж. Друга, власна дедуплікація тут розійшлася б із чергою на
/// першій же зміні її ключа (узгоджено з «Аналізом», власницею черги).
/// </para>
/// <para>
/// ⚠ Постановка — ОДНИМ викликом у <see cref="EnqueueAsync"/>, і це
/// <c>EnqueueCoalescedAsync</c> (без витіснення), а не <c>EnqueueExclusiveAsync</c>:
/// автоперерахунок іде сплесками (три записи в один документ — три виклики), і
/// витіснення переривало б виконуваний перерахунок «гарячого» документа знову й
/// знову. Злиття ставить нову задачу ПІСЛЯ виконуваної (черга в базі) або
/// повертає наявну (Quartz). Лок документа (<c>ecr:recalc:doc:{id}</c>) бере сама
/// задача <see cref="IRecalculationJob"/> — тригер його не чіпає.
/// </para>
/// </remarks>
public sealed class CalculationTrigger(
    IBackgroundJobScheduler jobs,
    IPeriodStore periods,
    IWorkflowStore workflow) : ICalculationTrigger
{
    /// <inheritdoc />
    public async Task<string?> RequestAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        var state = await periods.FindPeriodStateAsync(documentId, periodKey.Value, ct).ConfigureAwait(false);

        // ⚠ `Scheduled` — даних ще немає, перерахунок нічого не дасть; так само
        // пропускає його правка шапки. Невідомий період — нема чого рахувати.
        if (state is not { } periodState || periodState == PeriodState.Scheduled)
        {
            return null;
        }

        // ⛔ Закритий період і поданий аркуш — ТЕ САМЕ правило, що й у задачі
        // та на кнопці (`RecalculationWritePolicy`, ФВ-9.7, ФВ-9.17). Тут воно
        // не гейт (гейт — у `RecalculationJob`), а відмова ставити задачу, яка
        // однаково впала б: інакше кожен пізній збір за закритий період
        // лишав би в журналі задач провалений перерахунок.
        var sheets = await workflow.GetSheetsAsync(documentId, periodKey, ct).ConfigureAwait(false);
        var submitted = sheets.Any(s => s.Status is DocumentStatus.Submitted or DocumentStatus.Approved);

        if (RecalculationWritePolicy.Check(periodState, submitted, hasClosedPeriodApproval: false)
            != RecalculationWriteDenial.None)
        {
            return null;
        }

        return await EnqueueAsync(documentId, periodKey, ct).ConfigureAwait(false);
    }

    /// <summary>Єдина точка постановки автоперерахунку.</summary>
    /// <remarks>
    /// ⚠ Payload — та сама форма, що в <c>RecalculateDocumentHandler</c>;
    /// <c>TriggeredByUserId</c> і автор задачі — <c>null</c>: це системна задача,
    /// людини, що натиснула кнопку, немає (Q-156).
    /// </remarks>
    private Task<string> EnqueueAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
        => jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(documentId, periodKey),
            new
            {
                DocumentId = documentId,
                PeriodKey = periodKey.Value,
                TriggeredByUserId = (int?)null,
                SheetDefId = (int?)null,
            },
            ct,
            createdByUserId: null);
}
