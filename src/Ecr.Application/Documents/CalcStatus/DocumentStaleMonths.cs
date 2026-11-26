// src/Ecr.Application/Documents/CalcStatus/DocumentStaleMonths.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents.CalcStatus;

/// <summary>
/// ✎ D-577 (Q3, рішення людини 26.11): які з застарілих місяців документа перерахунок справді перерахує.
/// </summary>
/// <remarks>
/// ⛔ BUG-2 (UX-огляд екрана документа): картка річного документа вважала його застарілим, якщо застарів хоч ОДИН місяць
/// року — і закритий, і затверджений, — а перерахунок такі місяці пропускає (<c>RecalculationJob.RefusedPeriodsAsync</c>).
/// Після успішного «Перерахувати» банер лишався назавжди. Тепер «застаріло» — лише місяці, які перерахунок перерахує;
/// решта — довідка з причиною.
/// <para>
/// Правило — те саме, що в автоперерахунку (<c>CalculationTrigger.WritableAsync</c>) і в задачі: ефективний стан періоду з
/// винятком «місяць відкрито для документа» (<see cref="IPeriodStore.FindPeriodStatesAsync"/>), <c>Scheduled</c> (D-575),
/// архів (B69), затверджено КОЖЕН аркуш складу — оригінал і кожна копія (F13 p218). Поданий аркуш перераховується
/// (рішення 07.11), тож «подано» причиною не є.
/// </para>
/// <para>
/// ⚠ Межа: закритий ЯКІР річної розкладки у відкритому році задача рахує лише не-місячними цілями (B24) — тут він «закритий»
/// (місячних чисел якоря перерахунок не оновить).
/// </para>
/// </remarks>
public sealed class DocumentStaleMonths(
    IDocumentCalcStatusStore store,
    IPeriodStore periods,
    IWorkflowStore workflow,
    IDocumentStore documents,
    IDocumentSheetCopyStore sheetCopies)
{
    /// <summary>Застарілі місяці документа з ознакою «перерахується».</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="periodKey">Період запиту.</param>
    /// <param name="year">Рік річної розкладки; <c>null</c> — лише <paramref name="periodKey"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Місяці за зростанням; порожньо — застарілих немає (жодного звернення понад запит застарілості).</returns>
    public async Task<IReadOnlyList<CalcStaleMonthDto>> ClassifyAsync(
        long documentId, int projectId, int periodKey, int? year, CancellationToken ct)
    {
        var stale = await store.StaleMonthsAsync(documentId, projectId, periodKey, year, ct).ConfigureAwait(false);
        if (stale.Count == 0)
        {
            return [];
        }

        var keys = stale.Select(m => m.PeriodKey).Distinct().Order().ToList();
        var states = await periods.FindPeriodStatesAsync(documentId, keys, ct).ConfigureAwait(false);
        var archived = await periods.IsDocumentProjectArchivedOrArchivingAsync(documentId, ct).ConfigureAwait(false);

        // Склад і стани аркушів — лише коли є місяць, відкритий для запису (інакше причина вже відома).
        IReadOnlyDictionary<int, IReadOnlyList<Domain.Entities.Workflow.ApprovalState>>? sheets = null;
        IReadOnlyList<int> included = [];
        Dictionary<int, IReadOnlyList<SheetCopyOrdinal>> copies = [];
        if (!archived && keys.Exists(k => Writable(states.GetValueOrDefault(k))))
        {
            sheets = await workflow.GetSheetsForPeriodsAsync(documentId, keys, ct).ConfigureAwait(false);
            included = await documents.GetIncludedSheetIdsAsync(documentId, ct).ConfigureAwait(false);
            copies = (await sheetCopies.ListGroupsAsync(documentId, ct).ConfigureAwait(false))
                .ToDictionary(g => g.SheetDefId, g => g.Copies);
        }

        return
        [
            .. stale.OrderBy(m => m.PeriodKey).Select(m =>
            {
                var reason = LockedReason(
                    states.GetValueOrDefault(m.PeriodKey),
                    archived,
                    sheets is not null && AllApproved(sheets.GetValueOrDefault(m.PeriodKey) ?? [], included, copies));
                return new CalcStaleMonthDto(m.PeriodKey, m.Since, reason is null, reason);
            }),
        ];
    }

    /// <summary>Причина, з якої перерахунок місяць не перерахує; <c>null</c> — перерахує.</summary>
    /// <param name="state">Ефективний стан періоду документа; <c>null</c> — періоду немає в календарі.</param>
    /// <param name="archived">Проєкт архівований чи архівується.</param>
    /// <param name="allApproved">Затверджено кожен аркуш складу.</param>
    public static string? LockedReason(PeriodState? state, bool archived, bool allApproved)
    {
        if (archived)
        {
            return CalcLockedReasons.Archived;
        }

        if (state is null or PeriodState.Scheduled)
        {
            return CalcLockedReasons.NotOpen;
        }

        if (RecalculationWritePolicy.Check(state.Value, hasSubmittedSheets: false) != RecalculationWriteDenial.None)
        {
            return CalcLockedReasons.Closed;
        }

        return allApproved ? CalcLockedReasons.Approved : null;
    }

    private static bool Writable(PeriodState? state)
        => state is { } known && known != PeriodState.Scheduled
           && RecalculationWritePolicy.Check(known, hasSubmittedSheets: false) == RecalculationWriteDenial.None;

    /// <summary>
    /// «Рахувати нічого»: затверджено КОЖЕН аркуш складу (оригінал і кожна копія) — правило <c>CalculationTrigger</c>.
    /// Склад невідомий — лише коли всі наявні стани аркушів затверджені.
    /// </summary>
    private static bool AllApproved(
        IReadOnlyList<Domain.Entities.Workflow.ApprovalState> sheets,
        IReadOnlyList<int> included,
        Dictionary<int, IReadOnlyList<SheetCopyOrdinal>> copies)
    {
        bool Approved(int sheetId, long? copyId) => sheets.Any(s =>
            s.SheetDefId == sheetId && s.SheetCopyId == copyId && s.Status == DocumentStatus.Approved);

        if (included.Count == 0)
        {
            return sheets.Count > 0 && sheets.All(s => s.Status == DocumentStatus.Approved);
        }

        return included.All(sheetId => Approved(sheetId, null)
            && (copies.GetValueOrDefault(sheetId) ?? []).All(c => Approved(sheetId, c.SheetCopyId)));
    }
}
