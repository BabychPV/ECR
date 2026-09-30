using System.Globalization;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Книжковий шлях запису (P8, застосування імпорту книги): зміни кількох
/// екземплярів ОДНОГО документа за ОДИН період — за сталу кількість звернень
/// до бази на кожен крок, а не за кількість таблиць.
/// </summary>
/// <remarks>
/// ⛔ Правила — ТІ САМІ методи, що й у поштучного <see cref="HandleAsync"/>
/// (<c>EnforceRowCreationRules</c>, <c>EnsureNoVersionConflictsAsync</c>,
/// <c>CheckAccess</c>, <c>BuildCellChanges</c>, <c>EvaluateRequiredInputs</c>,
/// <c>EnsureValidationPassesAsync</c>, <c>CheckLookups*</c>, <c>CheckUnitsExist</c>,
/// <c>CheckSheetStatus</c>, <c>CheckRowLimit</c>, <c>BuildAuditRecords</c>).
/// Відрізняється лише ТЕ, ЯК дістаються дані: пакетом на книгу замість запиту
/// на таблицю. Друга копія правила колись розійшлася б із першою.
///
/// ⚠ Відмова на екземплярі несе <c>tableInstanceId</c> (той самий ключ і тип
/// винятку, що й <c>ExcelImporter.Blame</c>); код і <c>messageKey</c> — ті
/// самі, що в поштучного. Коли в книзі кілька різних відмов, перша — за
/// ЕТАПОМ (усі екземпляри проходять етап, перш ніж почнеться наступний), а в
/// межах етапу — за порядком екземплярів; поштучна послідовність назвала б
/// першу за порядком екземплярів. Стан бази від цього не залежить: відмова
/// відкочує все. ✎ 2026-09-29: такий порядок прийнято рішенням координатора
/// (P8, порція 3) — не «виправляти» на поштучний.
///
/// ⚠ Транзакцію «усе або нічого» (DAT-05) тримає викликач; без неї — одна
/// власна на всю книгу (<see cref="IUnitOfWork.ExecuteInTransactionAsync"/>).
/// </remarks>
public sealed partial class PatchCellsHandler
{
    /// <summary>
    /// Застосовує зміни кількох екземплярів одного документа за один період.
    /// </summary>
    /// <param name="requests">Батчі по екземплярах; екземпляри не повторюються, період один.</param>
    /// <param name="recalculationSeeds">
    /// Насіння перерахунку всієї книги. Задачі цей метод НЕ ставить: одну на книгу
    /// ставить викликач, що тримає транзакцію (<c>ExcelImporter</c>), — останнім
    /// оператором своєї транзакції або після коміту залежно від
    /// <see cref="IBackgroundJobScheduler.EnlistsInCallerTransaction"/> (MI-02 (в)).
    /// Колекція, а не прапорець: викликач не відновлює перелік змінених комірок сам.
    /// </param>
    /// <param name="ct">Скасування.</param>
    /// <param name="heldSheetStatuses">
    /// Стани аркушів (<c>SheetDefId</c> → стан), які викликач УЖЕ прочитав під
    /// спільним блокуванням <see cref="ISheetEditGate.EnterEditAsync"/> у ЦІЙ САМІЙ
    /// транзакції (<c>ExcelImporter</c>); <c>null</c> — взяти й прочитати тут.
    /// </param>
    /// <returns>Відповідь на кожен батч — у порядку <paramref name="requests"/>.</returns>
    /// <remarks>
    /// ⚠ Стан, прочитаний під спільним блокуванням, лишається правдою до кінця
    /// транзакції: подання бере виняткове блокування того самого ключа й не
    /// зафіксується, поки воно тримається. Друге читання нічого не додало б, крім
    /// двох звернень на аркуш.
    /// </remarks>
    public async Task<IReadOnlyList<PatchCellsResponse>> HandleWorkbookAsync(
        IReadOnlyList<PatchCellsRequest> requests,
        ICollection<RecalculationSeed> recalculationSeeds,
        CancellationToken ct,
        IReadOnlyDictionary<int, Domain.Enums.DocumentStatus>? heldSheetStatuses = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(recalculationSeeds);

        if (requests.Count == 0)
        {
            return [];
        }

        var ids = requests.Select(r => r.TableInstanceId).ToList();
        if (ids.Distinct().Count() != ids.Count || requests.Any(r => r.PeriodKey != requests[0].PeriodKey))
        {
            throw new ArgumentException("Книга: екземпляри мусять бути різними, а період — одним.", nameof(requests));
        }

        foreach (var request in requests)
        {
            Blamed(request.TableInstanceId, request.EnsureWithinCellLimit);
        }

        var userId = ResolveUserId();
        var period = new PeriodKey(requests[0].PeriodKey);

        var instances = await rowStore.ResolveTableInstancesAsync(ids, ct).ConfigureAwait(false);
        var documentId = instances[ids[0]].DocumentId;
        if (instances.Values.Any(i => i.DocumentId != documentId))
        {
            throw new ArgumentException("Книга: усі екземпляри мусять належати одному документу.", nameof(requests));
        }

        var profile = await BlamedAsync(ids[0], () => EnsureDocumentReadableAsync(userId, documentId, ct))
            .ConfigureAwait(false);

        foreach (var request in requests)
        {
            Blamed(request.TableInstanceId, () => EnsurePeriodMatches(request, instances[request.TableInstanceId]));
        }

        var snapshots = new Dictionary<int, TemplateVersionSnapshot>();
        foreach (var versionId in instances.Values.Select(i => i.TemplateVersionId).Distinct())
        {
            snapshots[versionId] = await metadata.GetAsync(versionId, ct).ConfigureAwait(false);
        }

        var rows = await rowStore.GetRowsBatchAsync(ids, period, ct).ConfigureAwait(false);

        var items = requests
            .Select(request =>
            {
                var instance = instances[request.TableInstanceId];
                return new WorkbookItem(request, Blamed(request.TableInstanceId, () => BuildContext(
                    request, userId, profile, instance, snapshots[instance.TemplateVersionId],
                    rows.GetValueOrDefault(request.TableInstanceId, Array.Empty<RowState>()))));
            })
            .ToList();

        // ⛔ Порожній батч — no-op, як і в поштучного (V-02).
        var active = items.Where(x => x.Context.Creations.Count > 0 || x.Context.Updates.Count > 0).ToList();

        if (active.Count > 0)
        {
            await ApplyWorkbookAsync(active, documentId, period, userId, heldSheetStatuses, ct).ConfigureAwait(false);
        }

        var responses = new List<PatchCellsResponse>(items.Count);
        foreach (var item in items)
        {
            if (item.Applied is not { } applied)
            {
                responses.Add(new PatchCellsResponse(
                    AppliedCells: 0,
                    RowVersions: new Dictionary<string, string>(StringComparer.Ordinal),
                    Validation: [],
                    RecalculationJobId: null));
                continue;
            }

            foreach (var seed in BuildRecalculationSeeds(applied))
            {
                recalculationSeeds.Add(seed);
            }

            var versions = (IReadOnlyDictionary<string, string>?)MergedRowVersions(item.Context, applied)
                           ?? await rowStore.GetRowVersionsAsync(item.Id, period, ct).ConfigureAwait(false);
            responses.Add(ToResponse(applied, item.Messages, recalculationJobId: null, versions));
        }

        return responses;
    }

    /// <summary>Перевірки й запис непорожніх батчів книги — етап за етапом.</summary>
    private async Task ApplyWorkbookAsync(
        List<WorkbookItem> active,
        long documentId,
        PeriodKey period,
        int userId,
        IReadOnlyDictionary<int, Domain.Enums.DocumentStatus>? heldSheetStatuses,
        CancellationToken ct)
    {
        foreach (var item in active)
        {
            await BlamedAsync(item.Id, async () =>
            {
                EnforceRowCreationRules(item.Context);
                await EnsureNoVersionConflictsAsync(item.Context, ct).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }

        await EnsureWorkbookAccessAsync(active, ct).ConfigureAwait(false);

        var numberCulture = Localization.NumberCulture.ForLanguage(currentUser.Language);

        foreach (var item in active)
        {
            item.Planned = Blamed(item.Id, () => BuildCellChanges(item.Context, numberCulture));
        }

        // ⚠ Межі періоду — одні на книгу (документ × період) і лише тоді, коли
        // їх справді питає методологія чи дата чинності Lookup.
        PeriodBounds? bounds = null;
        var boundsRead = false;
        async Task<PeriodBounds?> Bounds()
        {
            if (!boundsRead)
            {
                bounds = await periods.FindPeriodBoundsAsync(documentId, period.Value, ct).ConfigureAwait(false);
                boundsRead = true;
            }

            return bounds;
        }

        await EnforceWorkbookRequiredInputsAsync(active, Bounds, ct).ConfigureAwait(false);

        var headerValues = await headers.GetExpressionValuesAsync(documentId, ct).ConfigureAwait(false);
        foreach (var item in active)
        {
            // ⚠ Нуль звернень, якщо правила таблиці не читають `REGFIELD`.
            var registryFields = await Validation.TableValidation
                .LoadRegistryFieldsAsync(
                    validation, registries, item.Context.Snapshot, item.Context.Table, item.Planned.Upserts, ct)
                .ConfigureAwait(false);
            // ⚠ S6: перевірка асинхронна — фільтр видимих повідомлень питає межі
            // читання (лише коли повідомлення називає колонку поза батчем).
            item.Messages = await BlamedAsync(item.Id, () => EnsureValidationPassesAsync(
                item.Context, item.Request, item.Planned, item.RequiredInputMessages, headerValues, registryFields, ct))
                .ConfigureAwait(false);
        }

        var toWrite = active.SelectMany(x => AddressesToWrite(x.Planned)).ToList();
        var previous = toWrite.Count == 0
            ? new Dictionary<CellAddress, CellValueData>()
            : await cellStore.ReadCellsAsync(toWrite, ct).ConfigureAwait(false);

        await EnsureWorkbookReferencesAsync(active, previous, Bounds, ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        var isLateEdit = await DetermineIsLateEditAsync(
                documentId, [.. active.Select(x => x.Context.Table.SheetDefId).Distinct()], period.Value, ct)
            .ConfigureAwait(false);

        await uow.ExecuteInTransactionAsync(
            innerCt => PersistWorkbookAsync(
                active, documentId, period, userId, now, isLateEdit, previous, heldSheetStatuses, innerCt),
            ct).ConfigureAwait(false);
    }

    /// <summary>Права на всю книгу — двома пакетними рішеннями.</summary>
    private async Task EnsureWorkbookAccessAsync(List<WorkbookItem> active, CancellationToken ct)
    {
        var profile = active[0].Context.Profile;

        foreach (var item in active)
        {
            item.AccessAddresses = Blamed(item.Id, () => AccessAddresses(item.Context));
        }

        var cellRequests = active
            .Where(x => x.AccessAddresses.Count > 0)
            .Select(x => new CellsAccessRequest(x.Id, x.Context.PeriodKey, x.AccessAddresses))
            .ToList();
        var decisions = cellRequests.Count == 0
            ? new Dictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>()
            : await access.CanEditCellsBatchAsync(profile, cellRequests, ct).ConfigureAwait(false);

        var rowKeys = active
            .Where(x => x.Context.Creations.Count > 0)
            .ToDictionary(
                x => x.Id,
                IReadOnlyCollection<string> (x) => [.. x.Context.Creations.Select(r => r.RowKey)]);
        var newRows = rowKeys.Count == 0
            ? new Dictionary<long, IReadOnlyDictionary<string, NewRowAccess>>()
            : await access.CanCreateRowsBatchAsync(profile, rowKeys, ct).ConfigureAwait(false);

        // ⛔ Рішення, якого служба не повернула, — відмова (`CheckAccess`), як і в
        // поштучного: `null` тут означає «жодного рішення на цей екземпляр».
        foreach (var item in active)
        {
            Blamed(item.Id, () => CheckAccess(
                item.Request, item.Context, item.AccessAddresses,
                decisions.GetValueOrDefault(item.Id), newRows.GetValueOrDefault(item.Id)));
        }
    }

    /// <summary>
    /// Обов'язкові входи методологій: прив'язки всіх таблиць — одним читанням,
    /// методологія — один раз на книгу, збережені входи — одним читанням.
    /// </summary>
    /// <remarks>
    /// ⚠ Прив'язки — через <see cref="IMethodologyStore.GetColumnResultBindingsAsync"/>,
    /// а не <c>GetMethodologyIdsBoundToTablesAsync</c>: останній віддає ОБ'ЄДНАННЯ
    /// методологій, а тут потрібні методології КОЖНОЇ таблиці. Предикат той самий,
    /// що в <c>GetMethodologyIdsBoundToTableAsync</c> (<c>IsActive</c> і таблиця).
    /// </remarks>
    private async Task EnforceWorkbookRequiredInputsAsync(
        List<WorkbookItem> active, Func<Task<PeriodBounds?>> bounds, CancellationToken ct)
    {
        var touched = active.Where(x => x.Planned.Touched.Count > 0).ToList();
        if (touched.Count == 0)
        {
            return;
        }

        var bindings = await methodologies
            .GetColumnResultBindingsAsync([.. touched.Select(x => x.Context.Instance.TableDefId).Distinct()], ct)
            .ConfigureAwait(false);

        if (bindings.Count == 0 || await bounds().ConfigureAwait(false) is not { } periodBounds)
        {
            return;
        }

        var resolved = new Dictionary<int, ApplicableMethodology?>();
        foreach (var item in touched)
        {
            var methodologyIds = bindings
                .Where(b => b.TableDefId == item.Context.Instance.TableDefId)
                .Select(b => b.MethodologyId)
                .Distinct()
                .Order();

            foreach (var methodologyId in methodologyIds)
            {
                if (!resolved.TryGetValue(methodologyId, out var applied))
                {
                    applied = await ResolveApplicableAsync(methodologyId, periodBounds.PeriodEnd, ct).ConfigureAwait(false);
                    resolved[methodologyId] = applied;
                }

                if (applied is not null)
                {
                    item.Applicable.Add(applied);
                }
            }
        }

        var gated = touched.Where(x => x.Applicable.Count > 0).ToList();
        var addresses = gated.SelectMany(x => RequiredInputAddresses(x.Context, x.Planned, x.Applicable)).ToList();
        var baseline = addresses.Count == 0
            ? new Dictionary<CellAddress, CellValueData>()
            : await cellStore.ReadCellsAsync(addresses, ct).ConfigureAwait(false);

        foreach (var item in gated)
        {
            item.RequiredInputMessages = EvaluateRequiredInputs(item.Context, item.Planned, item.Applicable, baseline);
        }
    }

    /// <summary>Посилання <c>Lookup</c> (<c>C7</c>) і <c>Unit</c> — одним читанням довідника на книгу.</summary>
    private async Task EnsureWorkbookReferencesAsync(
        List<WorkbookItem> active,
        IReadOnlyDictionary<CellAddress, CellValueData> previous,
        Func<Task<PeriodBounds?>> bounds,
        CancellationToken ct)
    {
        var lookups = active.Select(x => LookupCells(x.Context, x.Planned)).ToList();
        var entryIds = lookups.SelectMany(l => l.Select(cell => cell.EntryId!.Value)).Distinct().ToList();

        if (entryIds.Count > 0)
        {
            var standings = (await registries.FindEntryStandingsAsync(entryIds, ct).ConfigureAwait(false))
                .ToDictionary(s => s.Id);
            var changed = lookups.Select(l => ChangedLookups(l, previous)).ToList();

            // ⚠ Межі — лише якщо є що перевіряти на дату, як і в поштучного.
            var periodBounds = changed.Any(c => c.Count > 0) ? await bounds().ConfigureAwait(false) : null;

            for (var i = 0; i < active.Count; i++)
            {
                if (lookups[i].Count == 0)
                {
                    continue;
                }

                var (item, own, ownChanged) = (active[i], lookups[i], changed[i]);
                Blamed(item.Id, () =>
                {
                    CheckLookupsExist(item.Context, own, standings);
                    if (ownChanged.Count > 0)
                    {
                        CheckLookupStandings(item.Context, ownChanged, standings, periodBounds);
                    }
                });
            }
        }

        var unitCells = active.Select(x => UnitCells(x.Context, x.Planned)).ToList();
        if (unitCells.TrueForAll(u => u.Count == 0))
        {
            return;
        }

        var catalog = await units.GetAsync(ct).ConfigureAwait(false);
        var known = catalog.Units.Values.Select(u => u.Id).ToHashSet();
        for (var i = 0; i < active.Count; i++)
        {
            var (item, own) = (active[i], unitCells[i]);
            if (own.Count > 0)
            {
                Blamed(item.Id, () => CheckUnitsExist(item.Context, own, known));
            }
        }
    }

    /// <summary>
    /// Тіло транзакції книги: блокування аркушів, стеля рядків, нові рядки,
    /// значення, «дотик» документа й аудит — кожен крок один на книгу.
    /// </summary>
    /// <remarks>
    /// ⚠ Замки аркушів — у порядку <c>SheetDefId</c>, по одному на аркуш.
    /// Спільний замок, уже взятий викликачем (<c>ExcelImporter</c>) у тій самій
    /// транзакції, — той самий власник: видається одразу (<c>SheetEditGate</c>).
    /// Винятковий (<c>C3b</c>) — лише для аркушів, де таблиця зі стелею створює
    /// рядки, як і в поштучного.
    ///
    /// ⚠ Стратегія повторів може виконати тіло вдруге: заплановані зміни
    /// (<c>Planned</c>) не змінюються, <c>Applied</c> перезаписується.
    /// </remarks>
    private async Task PersistWorkbookAsync(
        List<WorkbookItem> active,
        long documentId,
        PeriodKey period,
        int userId,
        DateTime now,
        bool isLateEdit,
        IReadOnlyDictionary<CellAddress, CellValueData> previous,
        IReadOnlyDictionary<int, Domain.Enums.DocumentStatus>? heldSheetStatuses,
        CancellationToken ct)
    {
        var statuses = new Dictionary<int, Domain.Enums.DocumentStatus>();
        foreach (var sheet in active.GroupBy(x => x.Context.Table.SheetDefId).OrderBy(g => g.Key))
        {
            statuses[sheet.Key] = await BlamedAsync(sheet.First().Id, async () =>
            {
                if (sheet.Any(x => CreatesRowsUnderCeiling(x.Context)))
                {
                    await sheetGate.EnterSubmitAsync(documentId, sheet.Key, period, ct).ConfigureAwait(false);
                }

                // ⚠ Стан, уже прочитаний викликачем під спільним блокуванням цієї
                // транзакції, — без другого звернення (див. `heldSheetStatuses`).
                if (heldSheetStatuses is not null && heldSheetStatuses.TryGetValue(sheet.Key, out var held))
                {
                    return held;
                }

                return await sheetGate.EnterEditAsync(documentId, sheet.Key, period, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        foreach (var item in active)
        {
            Blamed(item.Id, () => CheckSheetStatus(statuses[item.Context.Table.SheetDefId], item.Planned));
        }

        var ceiling = active.Where(x => CreatesRowsUnderCeiling(x.Context)).ToList();
        if (ceiling.Count > 0)
        {
            var existing = await rowStore
                .GetRowIdsBatchAsync([.. ceiling.Select(x => x.Id)], period, ct)
                .ConfigureAwait(false);
            foreach (var item in ceiling)
            {
                Blamed(item.Id, () => CheckRowLimit(item.Context, existing.GetValueOrDefault(item.Id)?.Count ?? 0));
            }
        }

        foreach (var item in active)
        {
            item.Applied = item.Planned;
        }

        var creating = active.Where(x => x.Context.Creations.Count > 0).ToList();
        if (creating.Count > 0)
        {
            IReadOnlyList<IReadOnlyList<long>> newIds;
            try
            {
                newIds = await rowStore
                    .CreateRowsBatchAsync(
                        [.. creating.Select(x => new RowCreationBatch(
                            x.Id, period, [.. x.Context.Creations.Select(r => RowKey.Create(r.RowKey))], Ordinal: 0))],
                        ct)
                    .ConfigureAwait(false);
            }
            catch (EcrException error) when (creating.Count == 1 && Blame(error, creating[0].Id) is { } named)
            {
                // Кілька наборів — сховище вже назвало винний; один — називаємо тут.
                throw named;
            }

            for (var i = 0; i < creating.Count; i++)
            {
                creating[i].Applied = WithRealRowIds(creating[i].Context, creating[i].Planned, newIds[i]);
            }
        }

        IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>> written;
        try
        {
            written = await cellStore
                .ApplyBatchAsync(
                    [.. active.Select(x => new CellChangeSet(
                        x.Id, x.Applied!.Upserts, x.Applied.Deletes, x.Applied.Touched,
                        userId, isLateEdit, x.Applied.ExpectedRowVersions))],
                    ct)
                .ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException error) when (StaleRowIds(error) is { Count: > 0 } staleRowIds)
        {
            // ⚠ Винний — перший набір із застарілими рядками: сховище називає
            // його в `tableInstanceId` (на одному наборі — він сам).
            var guilty = active.Count == 1
                ? active[0]
                : active.First(x => string.Equals(
                    x.Id.ToString(CultureInfo.InvariantCulture),
                    error.Details!.GetValueOrDefault("tableInstanceId") as string,
                    StringComparison.Ordinal));
            throw Blame(StaleRowsConflict(guilty.Planned, staleRowIds), guilty.Id)!;
        }
        catch (EcrException error) when (active.Count == 1 && Blame(error, active[0].Id) is { } named)
        {
            // Кілька наборів — сховище вже назвало винний; один — називаємо тут.
            throw named;
        }

        foreach (var item in active)
        {
            item.Applied = item.Applied! with { NewRowVersions = written.GetValueOrDefault(item.Id) };
        }

        await documents.TouchAsync(documentId, userId, now, ct).ConfigureAwait(false);

        await audit.WriteCellChangesAsync(
            [.. active.SelectMany(x => BuildAuditRecords(
                x.Request, x.Applied!.Upserts, x.Applied.Deletes, userId, now, documentId,
                x.Applied.RowKeyById, previous, isLateEdit))],
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Та сама відмова з номером екземпляра, на якому книга спинилася;
    /// <c>null</c> — невідомий тип відмови лишається як є.
    /// </summary>
    /// <remarks>
    /// ⚠ Ключ і форма — ті самі, що в <c>ExcelImporter.Blame</c>: додається рівно
    /// <c>tableInstanceId</c> рядком; тип винятку (а з ним HTTP-статус), код і
    /// <c>messageKey</c> не змінюються.
    /// </remarks>
    private static EcrException? Blame(EcrException error, long tableInstanceId)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (error.Details is not null)
        {
            foreach (var (key, value) in error.Details)
            {
                details[key] = value;
            }
        }

        details["tableInstanceId"] = tableInstanceId.ToString(CultureInfo.InvariantCulture);

        return error switch
        {
            ConcurrencyConflictException => new ConcurrencyConflictException(error.ErrorCode, error.Message, details),
            AccessDeniedException => new AccessDeniedException(error.ErrorCode, error.Message, details),
            NotFoundException => new NotFoundException(error.ErrorCode, error.Message, details),
            BusinessRuleException => new BusinessRuleException(error.ErrorCode, error.Message, details),
            _ => null,
        };
    }

    private static void Blamed(long tableInstanceId, Action check)
        => Blamed(tableInstanceId, () =>
        {
            check();
            return true;
        });

    private static T Blamed<T>(long tableInstanceId, Func<T> step)
    {
        try
        {
            return step();
        }
        catch (EcrException error) when (Blame(error, tableInstanceId) is { } named)
        {
            throw named;
        }
    }

    private static async Task<T> BlamedAsync<T>(long tableInstanceId, Func<Task<T>> step)
    {
        try
        {
            return await step().ConfigureAwait(false);
        }
        catch (EcrException error) when (Blame(error, tableInstanceId) is { } named)
        {
            throw named;
        }
    }

    /// <summary>Батч одного екземпляра книги і те, що про нього вже з'ясовано.</summary>
    private sealed class WorkbookItem(PatchCellsRequest request, RequestContext context)
    {
        public PatchCellsRequest Request { get; } = request;

        public RequestContext Context { get; } = context;

        public long Id => Request.TableInstanceId;

        public List<CellAddress> AccessAddresses { get; set; } = [];

        public CellChangeLists Planned { get; set; } = null!;

        public CellChangeLists? Applied { get; set; }

        public List<ApplicableMethodology> Applicable { get; } = [];

        public List<Validation.ValidationMessage> RequiredInputMessages { get; set; } = [];

        public List<Validation.ValidationMessage> Messages { get; set; } = [];
    }
}
