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
/// ⚠ Дедуплікація — НЕ тут. Задача ставиться маркером <see cref="IRecalculationJob"/> зі
/// сталою ціллю на пару «документ × період» (<see cref="AutoTargetOf"/>, <see cref="DependentsTargetOf"/>),
/// і черга сама лишає одну живу задачу на ціль. Друга, власна дедуплікація тут розійшлася б
/// із чергою на першій же зміні її ключа (узгоджено з «Аналізом», власницею черги).
/// ✎ B83: ціль автоперерахунку більше НЕ спільна з кнопкою (<see cref="RecalculateDocumentHandler.TargetOf"/>) —
/// інакше злиття губило б позначку поданих; два прогони серіалізує лок документа.
/// </para>
/// <para>
/// ⚠ Постановка — ОДНИМ викликом у <see cref="EnqueueSubmittedAsync"/>, і це
/// <c>EnqueueCoalescedAsync</c> (без витіснення), а не <c>EnqueueExclusiveAsync</c>:
/// автоперерахунок іде сплесками (три записи в один документ — три виклики), і
/// витіснення переривало б виконуваний перерахунок «гарячого» документа знову й
/// знову. Злиття ставить нову задачу ПІСЛЯ виконуваної (черга в базі) або
/// повертає наявну (Quartz). Лок документа (<c>ecr:recalc:doc:{id}</c>) бере сама
/// задача <see cref="IRecalculationJob"/> — тригер його не чіпає.
/// </para>
/// <para>
/// ⛔ B97 (F13 batch-5): сховища складу документа й копій аркушів — ОБОВ'ЯЗКОВІ. Доти вони були необов'язкові, і
/// без реєстрації склад документа «невідомий»: повністю затверджений документ із копіями аркушів ставився в чергу й
/// відмовлявся вже в задачі. Обов'язковий параметр ламає збірку чи резолюцію контейнера, а не мовчить.
/// </para>
/// </remarks>
public sealed class CalculationTrigger(
    IBackgroundJobScheduler jobs,
    IPeriodStore periods,
    IWorkflowStore workflow,
    IDocumentStore documents,
    IDocumentSheetCopyStore sheetCopies) : ICalculationTrigger
{
    /// <inheritdoc />
    public bool EnlistsInCallerTransaction => jobs.EnlistsInCallerTransaction;

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ B83 (слово людини 07.11: «тільки погоджені не мають перераховуватися»): автоперерахунок після збору PI і
    /// синхронізації подій — те саме правило, що в залежних (B63): пропускається лише ЗАТВЕРДЖЕНИЙ аркуш, чернетка
    /// поруч із поданим перераховується, поданий — із журналом і позначкою «перераховано після подання»
    /// (<c>RecalculateSubmitted</c>). Доти один поданий чи затверджений аркуш знімав із перерахунку ВЕСЬ документ.
    /// <para>
    /// ⚠ Нічний розклад сюди НЕ ходить (дочірні задачі <c>RecalculationJob.FanOutAsync</c>, ціль <c>TargetOf</c>,
    /// без позначки) і поданих не чіпає: інакше кожна ніч позначала б усі подані аркуші, і позначка нічого б не значила.
    /// </para>
    /// <para>
    /// ⚠ Ціль — своя (<see cref="AutoTargetOf"/>), а не <c>TargetOf</c>: черга зливає постановку з тією, що чекає, і
    /// позначка поданих загубилась би в нічній дочірній задачі чи правці шапки з тією самою ціллю (урок B63). Два
    /// прогони одного документа однаково не йдуть разом — лок документа бере сама задача.
    /// </para>
    /// </remarks>
    public async Task<string?> RequestAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        if (!await WritableAsync(documentId, periodKey, ct).ConfigureAwait(false))
        {
            return null;
        }

        return await EnqueueSubmittedAsync(AutoTargetOf(documentId, periodKey), documentId, periodKey, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ B63 (слово людини 07.11: «тільки погоджені не мають перераховуватися»): залежні від довідника чи
    /// документа-джерела (RT-25, HSE400 3b, FLERT F6) перераховуються ПО АРКУШУ — пропускається лише затверджений;
    /// чернетка, повернутий і поданий перераховуються, поданий — із записом журналу й позначкою
    /// «перераховано після подання» (той самий механізм, що в ручного перерахунку: <c>RecalculationJob</c>
    /// з <c>RecalculateSubmitted</c>). Доти один поданий чи затверджений аркуш знімав із перерахунку ВЕСЬ документ.
    /// <para>
    /// ⚠ «Рахувати нічого» — затверджено КОЖЕН аркуш складу (оригінал і кожна копія, RS), те саме правило, що в
    /// <c>RecalculateDocumentHandler</c>; інакше задача відмовила б 4221 і лишила проваленого перерахунку в журналі.
    /// Склад невідомий — «нічого», лише коли всі наявні стани аркушів затверджені.
    /// </para>
    /// <para>
    /// ⚠ Ціль — своя (<see cref="DependentsTargetOf"/>), а не <c>TargetOf</c>: черга зливає постановку з тією, що
    /// чекає, і payload залежних без позначки поданих загубився б в автоперерахунку збору. Два прогони одного
    /// документа однаково не йдуть разом — лок документа бере сама задача.
    /// </para>
    /// </remarks>
    public async Task<string?> RequestDependentsAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        if (!await WritableAsync(documentId, periodKey, ct).ConfigureAwait(false))
        {
            return null;
        }

        return await EnqueueSubmittedAsync(DependentsTargetOf(documentId, periodKey), documentId, periodKey, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Ціль перерахунку залежних: окрема від автоперерахунку збору й кнопки (див. <see cref="RequestDependentsAsync"/>).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <returns><c>doc{id}-p{період}-dependents</c>.</returns>
    public static string DependentsTargetOf(long documentId, PeriodKey periodKey)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{RecalculateDocumentHandler.TargetOf(documentId, periodKey)}-dependents");

    /// <summary>
    /// B83: ціль автоперерахунку після збору PI і синхронізації подій — окрема від кнопки, правки шапки й нічних
    /// дочірніх задач (<c>TargetOf</c>), щоб злиття не губило позначку поданих (див. <see cref="RequestAsync"/>).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <returns><c>doc{id}-p{період}-auto</c>.</returns>
    public static string AutoTargetOf(long documentId, PeriodKey periodKey)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{RecalculateDocumentHandler.TargetOf(documentId, periodKey)}-auto");

    /// <summary>
    /// Чи є що перераховувати без людини: період відомий, не <c>Scheduled</c> і не закритий, і хоч один аркуш складу
    /// (оригінал чи копія) ще не затверджений.
    /// </summary>
    /// <remarks>
    /// ⛔ Тут правило не гейт (гейт — у <c>RecalculationJob</c>), а відмова ставити задачу, яка однаково відмовила б:
    /// інакше кожен пізній збір за закритий період лишав би в журналі задач провалений перерахунок.
    /// </remarks>
    private async Task<bool> WritableAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        var state = await periods.FindPeriodStateAsync(documentId, periodKey.Value, ct).ConfigureAwait(false);
        if (state is not { } periodState || periodState == PeriodState.Scheduled
            || RecalculationWritePolicy.Check(periodState, hasSubmittedSheets: false) != RecalculationWriteDenial.None)
        {
            return false;
        }

        var sheets = await workflow.GetSheetsAsync(documentId, periodKey, ct).ConfigureAwait(false);
        return !await AllApprovedAsync(documentId, sheets, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// «Рахувати нічого»: затверджено КОЖЕН аркуш складу (оригінал і кожна копія, RS) — те саме правило, що в
    /// <c>RecalculateDocumentHandler</c>. Склад невідомий — лише коли всі наявні стани аркушів затверджені.
    /// </summary>
    private async Task<bool> AllApprovedAsync(
        long documentId, IReadOnlyList<Domain.Entities.Workflow.ApprovalState> sheets, CancellationToken ct)
    {
        bool Approved(int sheetId, long? copyId) => sheets.Any(s =>
            s.SheetDefId == sheetId && s.SheetCopyId == copyId && s.Status == DocumentStatus.Approved);

        var included = await documents.GetIncludedSheetIdsAsync(documentId, ct).ConfigureAwait(false);

        if (included.Count == 0)
        {
            return sheets.Count > 0 && sheets.All(s => s.Status == DocumentStatus.Approved);
        }

        var copiesBySheet = (await sheetCopies.ListGroupsAsync(documentId, ct).ConfigureAwait(false))
            .ToDictionary(g => g.SheetDefId, g => g.Copies);

        return included.All(sheetId => Approved(sheetId, null)
            && (copiesBySheet.GetValueOrDefault(sheetId) ?? []).All(c => Approved(sheetId, c.SheetCopyId)));
    }

    /// <summary>Єдина точка постановки системного перерахунку, що пише й подані аркуші.</summary>
    /// <remarks>
    /// ⚠ Payload — форма <c>RecalculationRequest</c>; <c>TriggeredByUserId</c> і автор задачі — <c>null</c>: це
    /// системна задача, людини, що натиснула кнопку, немає (Q-156). <c>RecalculateSubmitted</c> — подані
    /// перераховуються з журналом і позначкою, затверджені пропускає сама задача.
    /// </remarks>
    private Task<string> EnqueueSubmittedAsync(string target, long documentId, PeriodKey periodKey, CancellationToken ct)
        => jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            target,
            new
            {
                DocumentId = documentId,
                PeriodKey = periodKey.Value,
                TriggeredByUserId = (int?)null,
                SheetDefId = (int?)null,
                RecalculateSubmitted = true,
            },
            ct,
            createdByUserId: null);
}
