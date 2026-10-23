using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// D-442: блокування аркуша під затвердження і НОВИЙ зріз даних, якщо погоджувач правив поданий аркуш.
/// </summary>
/// <remarks>
/// ⛔ Навіщо. Після <c>Submit</c> поданий аркуш править лише погоджувач поточного кроку (аркуш лишається
/// <c>Submitted</c>), а зріз подання (<c>calc.SubmissionSnapshot</c>) знято ще ДО його правок. Без
/// повторного зрізу затверджені дані розходилися б із «доказом того, що погоджено»: жива комірка й журнал
/// (<c>ApproverEdit</c>) — одне, зріз — інше.
///
/// ⚠ Зріз знімається ЛИШЕ коли комірки САМЕ цього аркуша змінилися від останнього зрізу. Поданий аркуш не
/// змінює ніхто, крім погоджувача (перерахунок і збір його пропускають, ФВ-9.17), тож різниця = правка
/// погоджувача. Затвердження без правок нового зрізу не дає (історія версій не засмічується, а зріз
/// подання лишається тим, що затверджено). Сусідні аркуші документа, що змінилися після подання, приводом
/// не є — вони входять у новий зріз лише разом із правкою погоджувача, як і в зріз подання.
///
/// ⛔ Під ВИНЯТКОВИМ блокуванням аркуша (<see cref="EnterAsync"/>) — тим самим, що в подання: правки
/// погоджувача, що вже пишуть, зафіксуються ДО зрізу, нові чекатимуть коміту затвердження й побачать новий
/// стан (крок N+1 чи <c>Approved</c>) — і відмову. Формули аркуша перераховуються тут же, ДО зрізу:
/// каскадна задача після правки поданий аркуш пропускає, і без цього зріз мав би свіжий вхід і застаріле
/// обчислене число (той самий дефект, що закрито в поданні, <c>SubmitRecalculationRaceTests</c>).
/// </remarks>
public sealed class ApprovalSnapshotRefresh(
    ISheetEditGate sheetGate,
    ICellStore cellStore,
    IRowStore rowStore,
    IDocumentStore documents,
    IMetadataCache metadata,
    IDocumentHeaderStore headers,
    IWorkflowStore workflow,
    Recalculation.ISubmitRecalculation recalculation)
{
    /// <summary>
    /// Блокування під затвердження: структура документа (спільно) → шапка (спільно) → аркуш (ВИНЯТКОВО) —
    /// той самий порядок, що в подання (<see cref="ISheetEditGate"/>). Лише всередині транзакції, першою дією.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task EnterAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        await sheetGate.EnterStructureAsync(documentId, exclusive: false, ct).ConfigureAwait(false);
        await sheetGate.EnterHeaderAsync(documentId, exclusive: false, ct).ConfigureAwait(false);
        await sheetGate.EnterSubmitAsync(documentId, sheetDefId, periodKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Знімає новий зріз, якщо комірки аркуша змінилися від останнього зрізу (правка погоджувача).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш, що затверджується.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="userId">Погоджувач — автор нового зрізу.</param>
    /// <param name="now">Момент затвердження.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>true</c> — зріз знято.</returns>
    public async Task<bool> RefreshAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, int userId, DateTime now, CancellationToken ct)
    {
        var instances = await rowStore.GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);
        var templateVersionId = instances.Count > 0
            ? instances[0].TemplateVersionId
            : await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        var sheetTables = snapshot.Sheets
            .Where(s => s.Id == sheetDefId)
            .SelectMany(s => s.Tables)
            .Select(t => t.Id)
            .ToHashSet();
        var sheetColumns = snapshot.ColumnsById.Values
            .Where(c => sheetTables.Contains(c.TableDefId))
            .Select(c => c.Id)
            .ToHashSet();

        var headerFieldCodes = snapshot.HeaderFields
            .Where(f => !f.IsDeleted)
            .ToDictionary(f => f.Id, f => f.Code);

        var live = await SubmissionPayloadSource
            .ReadAsync(cellStore, headers, documentId, instances, periodKey, headerFieldCodes, ct)
            .ConfigureAwait(false);
        // ⚠ Від найновішого (`GetSnapshotsAsync` сортує за `SubmittedAt` спадно).
        var snapshots = await workflow.GetSnapshotsAsync(documentId, sheetDefId, periodKey, ct).ConfigureAwait(false);
        var latest = snapshots.Count > 0 ? snapshots[0].PayloadJson : null;

        if (!SheetChanged(latest, live, sheetColumns))
        {
            return false;
        }

        await recalculation
            .RecalculateSheetUnderSubmitLockAsync(documentId, sheetDefId, periodKey, ct)
            .ConfigureAwait(false);

        var payload = await SubmissionPayloadSource
            .ReadAsync(cellStore, headers, documentId, instances, periodKey, headerFieldCodes, ct)
            .ConfigureAwait(false);

        // ⚠ Режими й версії методологій — так само `null`, як у зрізі подання (Q-155): чесне «невідомо».
        await workflow.SaveSnapshotAsync(
            new SubmissionSnapshotRecord(
                documentId, sheetDefId, periodKey.Value,
                templateVersionId,
                MethodologyVersionsJson: null,
                NumericMode: null,
                CalendarMode: null,
                PayloadJson: payload,
                ContentHash: SubmissionPayloadSource.Hash(payload),
                SubmittedAt: now,
                SubmittedByUserId: userId),
            ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Чи відрізняються комірки аркуша (колонки <paramref name="sheetColumns"/>) у живому payload від
    /// останнього зрізу; зрізу немає — так.
    /// </summary>
    /// <param name="latestPayload">Payload останнього зрізу; <c>null</c> — зрізу немає.</param>
    /// <param name="livePayload">Payload живих даних.</param>
    /// <param name="sheetColumns">Колонки таблиць аркуша, що затверджується.</param>
    public static bool SheetChanged(string? latestPayload, string livePayload, IReadOnlySet<int> sheetColumns)
    {
        ArgumentNullException.ThrowIfNull(livePayload);
        ArgumentNullException.ThrowIfNull(sheetColumns);

        if (latestPayload is null)
        {
            return true;
        }

        var before = SubmissionPayload.Read(latestPayload).Where(c => sheetColumns.Contains(c.Column)).ToHashSet();
        var after = SubmissionPayload.Read(livePayload).Where(c => sheetColumns.Contains(c.Column)).ToHashSet();

        return !before.SetEquals(after);
    }
}
