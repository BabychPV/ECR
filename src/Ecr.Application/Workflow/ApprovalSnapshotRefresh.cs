using System.Runtime.ExceptionServices;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Security;
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
///
/// ✎ D-442 (борг AN-129): каскадна задача тепер перераховує поданий аркуш, коли насіння — правка його
/// погоджувача (<c>RecalculationSeed.ApproverSheetDefId</c>), тож погоджувач бачить свіжі формули ще до
/// затвердження. Перерахунок тут лишається: задача могла ще не виконатися до затвердження, а після нього
/// <c>Approved</c> вона вже не пише. Інші подані аркуші каскад, як і доти, пропускає.
/// </remarks>
public sealed class ApprovalSnapshotRefresh(
    ISheetEditGate sheetGate,
    ICellStore cellStore,
    IRowStore rowStore,
    IDocumentStore documents,
    IMetadataCache metadata,
    IDocumentHeaderStore headers,
    IWorkflowStore workflow,
    Recalculation.ISubmitRecalculation recalculation,

    // ✎ C-1 (D-475, PO 12.11 «якщо є помилки то затвердити теж не можна»): стан помилок формул аркуша для блоку
    // затвердження. `null` — тестові фабрики без сховища (блоку немає).
    IFormulaErrorStore? formulaErrors = null)
{
    /// <summary>✎ C-1: екземпляри й знімок, прочитані <see cref="RefreshAsync"/> (нуль повторних звернень).</summary>
    private IReadOnlyList<TableInstanceRef>? _sheetInstances;

    private Domain.Entities.Configuration.TemplateVersionSnapshot? _snapshot;

    /// <summary>✎ C-1: комірки з помилкою з перерахунку, якщо <see cref="RefreshAsync"/> перерахував аркуш.</summary>
    private IReadOnlyList<Recalculation.FormulaErrorCell>? _recalculatedErrors;

    /// <summary>
    /// ✎ C-1 (D-475, PO 12.11): повідомлення <c>FORMULA-ERROR</c> для комірок аркуша з помилкою формули чи «не
    /// оновлених через помилку» — вони блокують ЗАТВЕРДЖЕННЯ так само, як подання. Кличеться ПІСЛЯ
    /// <see cref="RefreshAsync"/> у тій самій транзакції: якщо аркуш перераховано, береться результат прогону, інакше —
    /// збережений стан, і лише коли екземпляр аркуша має прапорець (без помилок — нуль звернень).
    /// </summary>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="language">Мова запасного тексту.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<Validation.ValidationMessage>> FormulaErrorMessagesAsync(
        int sheetDefId, PeriodKey periodKey, string language, CancellationToken ct)
    {
        if (_snapshot is null || _sheetInstances is null)
        {
            return [];
        }

        var tables = _snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);
        var cells = _recalculatedErrors;
        if (cells is null)
        {
            var sheetTables = _snapshot.Sheets.Where(s => s.Id == sheetDefId).SelectMany(s => s.Tables).Select(t => t.Id).ToHashSet();
            var flagged = _sheetInstances
                .Where(i => i.HasFormulaErrors && sheetTables.Contains(i.TableDefId))
                .ToList();
            if (formulaErrors is null || flagged.Count == 0)
            {
                return [];
            }

            var rows = await formulaErrors
                .ReadInstancesAsync([.. flagged.Select(i => i.TableInstanceId)], periodKey, ct)
                .ConfigureAwait(false);
            cells = [.. rows.Select(r => new Recalculation.FormulaErrorCell(
                r.TableDefId, r.SheetCopyId, r.RowKey, r.ColumnDefId, r.Kind, r.ErrorCode,
                r.SourcePeriodKey, r.SourceTableDefId, r.SourceRowKey, r.SourceColumnDefId))];
        }

        return Validation.FormulaErrorIssues.Messages(cells, tables, language);
    }

    /// <summary>
    /// Блокування під затвердження: структура документа (спільно) → шапка (спільно) → аркуш (ВИНЯТКОВО) —
    /// той самий порядок, що в подання (<see cref="ISheetEditGate"/>). Лише всередині транзакції, першою дією.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="sheetCopyId">Копія аркуша (RS); <c>null</c> — оригінал.</param>
    public async Task EnterAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct, long? sheetCopyId = null)
    {
        await sheetGate.EnterStructureAsync(documentId, exclusive: false, ct).ConfigureAwait(false);
        await sheetGate.EnterHeaderAsync(documentId, exclusive: false, ct).ConfigureAwait(false);
        await sheetGate.EnterSubmitOfAsync(documentId, sheetDefId, periodKey, sheetCopyId, ct).ConfigureAwait(false);
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
    /// <param name="sheetCopyId">Копія аркуша (RS); <c>null</c> — оригінал. Зрізи копії окремі.</param>
    /// <returns><c>true</c> — зріз знято.</returns>
    public async Task<bool> RefreshAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, int userId, DateTime now, CancellationToken ct,
        long? sheetCopyId = null)
    {
        var allInstances = await rowStore.GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);
        var templateVersionId = allInstances.Count > 0
            ? allInstances[0].TemplateVersionId
            : await Documents.DocumentPeriodVersion.GetAsync(documents, documentId, periodKey.Value, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        // RS: таблиці цього аркуша — лише своєї копії (див. SubmissionPayloadSource.ForSheetCopy).
        var instances = SubmissionPayloadSource.ForSheetCopy(
            allInstances, snapshot.Sheets.FirstOrDefault(s => s.Id == sheetDefId), sheetCopyId);
        _sheetInstances = instances;
        _snapshot = snapshot;
        _recalculatedErrors = null;

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
        // ⛔ PERF: лише НАЙНОВІШИЙ зріз, і спершу без payload. Раніше `GetSnapshotsAsync` тягнув payload УСІХ
        // зрізів аркуша за період (~10 МБ кожен на документі з 91 таблицею; кожне подання додає зріз) —
        // затвердження 268→493 с і росло з числом подань. Хеш живих даних збігся з хешем найновішого зрізу
        // (той самий `SubmissionPayloadSource`) — дані байт-у-байт ті самі, зміни немає, payload не читаємо.
        var latestKey = await workflow.GetLatestSnapshotKeyOfAsync(documentId, sheetDefId, periodKey, sheetCopyId, ct)
            .ConfigureAwait(false);
        if (latestKey is { } key
            && string.Equals(key.ContentHash, SubmissionPayloadSource.Hash(live), StringComparison.Ordinal))
        {
            return false;
        }

        var latest = latestKey is { } found
            ? await workflow.GetSnapshotPayloadAsync(found.Id, ct).ConfigureAwait(false)
            : await MigratedSheetPayloadAsync(documentId, sheetDefId, periodKey, sheetCopyId, snapshot, ct).ConfigureAwait(false);

        if (!SheetChanged(latest, live, sheetColumns))
        {
            return false;
        }

        // ⛔ RS: рахується «вид» своєї копії (оригінал для sheetCopyId = null).
        // ✎ C-1: прогін повертає й комірки аркуша з помилкою — вони блокують затвердження (FormulaErrorMessagesAsync).
        _recalculatedErrors = await recalculation
            .RecalculateSheetForSubmitAsync(documentId, sheetDefId, periodKey, ct, sheetCopyId)
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
                SubmittedByUserId: userId,
                SheetCopyId: sheetCopyId),
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

        // ⛔ PERF (AN-135): кожен payload розбирається ОДИН раз (SubmissionPayload.Read), а множини будуються лише
        // тоді, коли відфільтровані послідовності не збіглися поелементно. Обидва payload пише
        // SubmissionPayload.Write у тому самому порядку (рядок, колонка), тож поелементна рівність — звичайний
        // випадок «аркуш не змінено»; вона ж достатня для рівності множин. Інакше — рівно старе порівняння
        // множин (дублікати й порядок не мають значення).
        // Два незалежні розбори по ~10 МБ — паралельно (чисті функції над незмінними рядками). Помилка розбору
        // виходить та сама й у тому самому порядку, що й доти (спершу зрізу, потім живих), — без AggregateException.
        List<SubmissionPayloadCell> before = [], after = [];
        ExceptionDispatchInfo? beforeError = null, afterError = null;
        Parallel.Invoke(
            () =>
            {
                try
                {
                    before = Filter(SubmissionPayload.Read(latestPayload), sheetColumns);
                }
                catch (JsonException ex)
                {
                    beforeError = ExceptionDispatchInfo.Capture(ex);
                }
            },
            () =>
            {
                try
                {
                    after = Filter(SubmissionPayload.Read(livePayload), sheetColumns);
                }
                catch (JsonException ex)
                {
                    afterError = ExceptionDispatchInfo.Capture(ex);
                }
            });
        beforeError?.Throw();
        afterError?.Throw();

        if (before.Count == after.Count && SequenceEqual(before, after))
        {
            return false;
        }

        return !before.ToHashSet().SetEquals(after);
    }

    private static List<SubmissionPayloadCell> Filter(IReadOnlyList<SubmissionPayloadCell> cells, IReadOnlySet<int> columns)
    {
        var result = new List<SubmissionPayloadCell>(cells.Count);
        foreach (var cell in cells)
        {
            if (columns.Contains(cell.Column))
            {
                result.Add(cell);
            }
        }

        return result;
    }

    private static bool SequenceEqual(List<SubmissionPayloadCell> left, List<SubmissionPayloadCell> right)
    {
        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// ⛔ V-3 (аудит 2026-11): зрізу на поточному <c>SheetDefId</c> немає, бо перенос проєкту (LP-32) перемапив стан
    /// аркуша на нову версію, а зріз лишив на старій (доказ). Шукаємо зріз за КОДОМ аркуша й перекладаємо його колонки
    /// на версію документа — інакше перше затвердження після переносу знімало б «правку погоджувача», якої не було.
    /// </summary>
    /// <returns>Payload (лише клітинки) у колонках поточної версії; <c>null</c> — зрізу немає (як і доти).</returns>
    private async Task<string?> MigratedSheetPayloadAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId,
        Domain.Entities.Configuration.TemplateVersionSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Sheets.FirstOrDefault(s => s.Id == sheetDefId) is not { } sheetDef
            || await workflow.FindLatestSnapshotOfSheetCodeAsync(documentId, sheetDef.Code, periodKey, sheetCopyId, ct)
                .ConfigureAwait(false) is not { } foreign
            || foreign.TemplateVersionId == snapshot.TemplateVersionId
            || await workflow.GetSnapshotPayloadAsync(foreign.Id, ct).ConfigureAwait(false) is not { } foreignPayload)
        {
            return null;
        }

        var source = await metadata.GetAsync(foreign.TemplateVersionId, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(
            SubmissionSnapshotTranslation.Cells(SubmissionPayload.Read(foreignPayload), source, snapshot));
    }
}
