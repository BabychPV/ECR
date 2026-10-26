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
    /// <param name="rowWindowChanges">
    /// Змінені колонки кожного записаного екземпляра для хука вікон рядків (HSE301 A1).
    /// Хук цей метод НЕ кличе: транзакцію тримає викликач, і задача, поставлена до
    /// коміту, підтягнула б вікна за даними, яких після відкату не було (L6-11).
    /// Викликач передає перелік у <see cref="NotifyRowWindowsAsync"/> ПІСЛЯ коміту.
    /// </param>
    /// <param name="ct">Скасування.</param>
    /// <param name="heldSheetStatuses">
    /// Стани аркушів (<c>SheetDefId</c> → стан), які викликач УЖЕ прочитав під
    /// спільним блокуванням <see cref="ISheetEditGate.EnterEditAsync"/> у ЦІЙ САМІЙ
    /// транзакції (<c>ExcelImporter</c>); <c>null</c> — взяти й прочитати тут.
    /// ⚠ Не <c>null</c> означає й те, що викликач уже тримає спільне блокування
    /// структури документа (<see cref="ISheetEditGate.EnterStructureAsync"/>, L6-02)
    /// і звірив під ним версію шаблону.
    /// </param>
    /// <param name="overwrittenRows">
    /// ✎ AN-114 (D-338). Рядки (<c>TableInstanceId</c> → <c>RowKey</c>), які людина
    /// свідомо перезаписала поверх чужих правок, зроблених після експорту книги:
    /// їхні комірки йдуть у журнал із походженням
    /// <see cref="CellChangeOrigins.ImportOverwrite"/> замість <c>Origin</c> батчу.
    /// Лише журнал — правила запису ті самі. <c>null</c> — таких рядків немає.
    /// </param>
    /// <param name="ownerRecalcs">
    /// Q-17: куди скласти насіння екземплярів ВЛАСНИКІВ, записаних книгою (колонки з іншим власником, за прапором
    /// <c>Collection:MonthColumnRouting</c>); задачі на їхні періоди ставить викликач. <c>null</c> — не збирати.
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
        ICollection<RowWindowChange> rowWindowChanges,
        CancellationToken ct,
        IReadOnlyDictionary<int, Domain.Enums.DocumentStatus>? heldSheetStatuses = null,
        IReadOnlyDictionary<long, IReadOnlySet<string>>? overwrittenRows = null,
        ICollection<WorkbookOwnerRecalc>? ownerRecalcs = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(recalculationSeeds);
        ArgumentNullException.ThrowIfNull(rowWindowChanges);

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
            Blamed(request.TableInstanceId, request.EnsureNoDuplicates);
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

        // ⛔ D-6: те саме, що в поштучному `LoadContextAsync` — схована від читача таблиця для нього НЕ ІСНУЄ
        // (404 `ECR-DOC-0404 tableInstance`) ДО будь-якої відмови, що її називає (409 RowMode, період, версії).
        // Книга й поштучний запис дають однакову відповідь.
        foreach (var request in requests)
        {
            var instance = instances[request.TableInstanceId];
            await BlamedAsync(request.TableInstanceId, async () =>
            {
                await DocumentVisibility.RequireTableVisibleAsync(
                    access, profile, instance.DocumentId, instance.TableInstanceId, instance.TableDefId,
                    instance.PeriodKey, ct).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }

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
                    rows.GetValueOrDefault(request.TableInstanceId, Array.Empty<RowState>()))))
                {
                    OverwrittenRowKeys = overwrittenRows?.GetValueOrDefault(request.TableInstanceId),
                };
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

            // ✎ D-445: запис погоджувача поточного кроку в поданий аркуш (позначка під блокуванням) несе аркуш у
            // насінні — каскад перерахує формули саме цього поданого аркуша, як після правки в сітці (D-442).
            if (applied.Routed is { } groups)
            {
                // ✎ Q-17: насіння по ФІЗИЧНИХ екземплярах: власні комірки - у задачу періоду книги, комірки власників -
                // у задачі їхніх екземплярів (`ownerRecalcs`; ставить викликач разом із задачею книги).
                foreach (var group in groups)
                {
                    if (group.TableInstanceId == item.Id)
                    {
                        foreach (var seed in group.Seeds)
                        {
                            recalculationSeeds.Add(seed);
                        }
                    }
                    else
                    {
                        ownerRecalcs?.Add(new WorkbookOwnerRecalc(group.TableInstanceId, group.PeriodKey, group.Seeds));
                    }
                }
            }
            else
            {
                foreach (var seed in BuildRecalculationSeeds(
                             applied, item.Context.ApproverEdit ? item.Context.Table.SheetDefId : null))
                {
                    recalculationSeeds.Add(seed);
                }
            }

            // HSE301 A1: одна зміна на таблицю; хук кличе викликач після коміту (L6-11).
            rowWindowChanges.Add(ChangedColumns(item.Context.Instance, applied));

            var versions = (IReadOnlyDictionary<string, string>?)MergedRowVersions(item.Context, applied)
                           ?? await rowStore.GetRowVersionsAsync(item.Id, period, ct).ConfigureAwait(false);
            responses.Add(ToResponse(
                applied, item.Messages, recalculationJobId: null, versions, OutOfWindowKeys(item.Context, applied)));
        }

        return responses;
    }

    /// <summary>
    /// Передає хукові вікон рядків зміни, зібрані <see cref="HandleWorkbookAsync"/>, — після коміту.
    /// </summary>
    /// <param name="changes">Зміни з <c>rowWindowChanges</c> книжкового шляху.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Завершення.</returns>
    /// <remarks>
    /// ⛔ L6-11: до цього книжковий шлях кликав хук сам — усередині транзакції
    /// <c>ExcelImporter</c>, тобто ДО коміту: Quartz у пам'яті стартував
    /// підтягування раніше, ніж дані ставали видимими, а відкат книги лишав
    /// поставлену задачу.
    /// </remarks>
    public async Task NotifyRowWindowsAsync(IEnumerable<RowWindowChange> changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (rowWindows is null)
        {
            return;
        }

        foreach (var change in changes)
        {
            await rowWindows.RowsChangedAsync(change, ct).ConfigureAwait(false);
        }
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
                documentId, [.. active.Select(x => x.Context.Table.SheetDefId).Distinct()], period.Value, ct,
                [.. active.SelectMany(x => x.Context.ColumnPeriods).Distinct()],
                active.Exists(x => x.Context.InstancePeriodGoverns),
                [.. active.SelectMany(x => x.Context.AnnualPeriods).Distinct()])
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

        // C1: ключ — (методологія, версія шаблону): локалізація ключів залежить від версії шаблону екземпляра.
        // D-425: колонки статусу залежать і від таблиці — вона частина ключа.
        var resolved = new Dictionary<(int MethodologyId, int TemplateVersionId, int TableDefId), ApplicableMethodology?>();
        foreach (var item in touched)
        {
            var methodologyIds = bindings
                .Where(b => b.TableDefId == item.Context.Instance.TableDefId)
                .Select(b => b.MethodologyId)
                .Distinct()
                .Order();

            foreach (var methodologyId in methodologyIds)
            {
                var templateVersionId = item.Context.Instance.TemplateVersionId;
                var tableDefId = item.Context.Instance.TableDefId;
                if (!resolved.TryGetValue((methodologyId, templateVersionId, tableDefId), out var applied))
                {
                    applied = await ResolveApplicableAsync(
                        methodologyId, periodBounds.PeriodEnd, templateVersionId, tableDefId, ct).ConfigureAwait(false);
                    resolved[(methodologyId, templateVersionId, tableDefId)] = applied;
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
            item.RequiredInputMessages = EvaluateRequiredInputs(
                item.Context, item.Planned, item.Applicable, baseline, item.Request.Origin);
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

            // PRMG: знімок груп оновлюється лише при незбігу реєстру (Land - гілка недосяжна, 0 звернень).
            await Permits.Groups.LookupTargetMatcher.EnsureFreshOnMismatchAsync(
                groups,
                active.SelectMany((x, i) => LookupPairs(x.Context, changed[i], standings)),
                ct).ConfigureAwait(false);

            // Рішення людини 25.10: проєкт документа для Registry.View scoped-ролі - лише для таких профілів, раз на документ.
            var viewProjects = new Dictionary<long, int?>();
            for (var i = 0; i < active.Count; i++)
            {
                var documentId = active[i].Context.Instance.DocumentId;
                if (lookups[i].Count > 0 && !viewProjects.ContainsKey(documentId))
                {
                    viewProjects[documentId] = await LookupViewProjectAsync(active[i].Context, ct).ConfigureAwait(false);
                }
            }

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
                        CheckLookupsBelong(
                            item.Context, ownChanged, standings, groups,
                            viewProjects[item.Context.Instance.DocumentId]);
                    }
                });
            }

            // ⛔ S18 (час, P3-7): межі періоду читаються ЛИШЕ після того, як усі відмови "запису немає / чужий довідник / Deny /
            // без Registry.View" пройшли, - як у поштучного (`EnsureRegistryReferencesExistAsync`): відмова за закритий
            // довідник не відрізняється від "запису немає" ще й одним запитом у БД. Порядок відмов у книзі - за етапами.
            var periodBounds = changed.Any(c => c.Count > 0) ? await bounds().ConfigureAwait(false) : null;
            for (var i = 0; i < active.Count; i++)
            {
                if (changed[i].Count == 0)
                {
                    continue;
                }

                var (item, ownChanged) = (active[i], changed[i]);
                Blamed(item.Id, () => CheckLookupStandings(item.Context, ownChanged, standings, periodBounds));
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
        // ⛔ L6-02: структура документа — до блокувань аркушів, той самий порядок,
        // що в поштучного. Викликач, що передав `heldSheetStatuses`
        // (`ExcelImporter`), уже взяв її першою дією своєї транзакції й звірив
        // версію — тоді другого звернення немає.
        if (heldSheetStatuses is null)
        {
            var locked = await sheetGate.EnterStructureAsync(documentId, exclusive: false, ct).ConfigureAwait(false);
            foreach (var item in active)
            {
                Blamed(item.Id, () => DocumentStructure.EnsureUnchanged(
                    locked, item.Context.Instance.TemplateVersionId, documentId));
            }
        }

        var statuses = new Dictionary<int, Domain.Enums.DocumentStatus>();
        foreach (var sheet in active.GroupBy(x => x.Context.Table.SheetDefId).OrderBy(g => g.Key))
        {
            // ⛔ Q-17: періоди колонок/річні ЦЬОГО аркуша блокуються РАЗОМ з власним, за зростанням ключа
            // (інакше книга з перегляду 5 у колонку місяця 3 тримала 5 і чекала 3, а правка з перегляду 3 — навпаки).
            // Нижчі від власного — ДО нього, вищі — ПІСЛЯ; стани перечитує `EnsureColumnPeriodsEditableAsync`
            // (повторне спільне блокування того ж ключа тією ж транзакцією видається одразу).
            var otherPeriods = new SortedSet<int>(sheet.SelectMany(x => x.Context.ColumnPeriods));
            otherPeriods.UnionWith(sheet.SelectMany(x => x.Context.AnnualPeriods));
            otherPeriods.Remove(period.Value);

            statuses[sheet.Key] = await BlamedAsync(sheet.First().Id, async () =>
            {
                foreach (var lower in otherPeriods.Where(p => p < period.Value))
                {
                    await sheetGate.EnterEditAsync(documentId, sheet.Key, new PeriodKey(lower), ct).ConfigureAwait(false);
                }

                var ownStatus = await LockOwnWorkbookSheetAsync().ConfigureAwait(false);

                foreach (var higher in otherPeriods.Where(p => p > period.Value))
                {
                    await sheetGate.EnterEditAsync(documentId, sheet.Key, new PeriodKey(higher), ct).ConfigureAwait(false);
                }

                return ownStatus;

                async Task<Domain.Enums.DocumentStatus> LockOwnWorkbookSheetAsync()
                {
                if (sheet.Any(x => CreatesRowsUnderCeiling(x.Context)))
                {
                    // ⛔ L6-08: викликач уже тримає СПІЛЬНЕ блокування цього аркуша
                    // (`heldSheetStatuses`). Виняткове поверх нього — перетворення
                    // блокування, і два такі власники чекали б один на одного
                    // (прихований дедлок). Такий виклик — помилка викликача, а не стан.
                    if (heldSheetStatuses is not null)
                    {
                        throw new InvalidOperationException(
                            $"Книга створює рядки в таблиці зі стелею на аркуші {sheet.Key}, а викликач уже тримає " +
                            "спільне блокування аркуша: виняткове поверх нього — прихований дедлок.");
                    }

                    // ⛔ Y7-03: відмова «зайнято» — про рядки, а не про подання (див. `RowWriteLock`).
                    await RowWriteLock.EnterAsync(sheetGate, documentId, sheet.Key, period, ct).ConfigureAwait(false);
                }

                // ⚠ Стан, уже прочитаний викликачем під спільним блокуванням цієї
                // транзакції, — без другого звернення (див. `heldSheetStatuses`).
                if (heldSheetStatuses is not null && heldSheetStatuses.TryGetValue(sheet.Key, out var held))
                {
                    return held;
                }

                return await sheetGate.EnterEditAsync(documentId, sheet.Key, period, ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        await EnsureWorkbookSheetsEditableAsync(active, statuses, ct).ConfigureAwait(false);

        // ⛔ AN-106 (P1-01): ліміт очікування блокувань — до стелі й вставки нових рядків, як у
        // поштучного (`PersistCoreAsync`); `ApplyBatchAsync` нижче його вже не повторює.
        await cellStore.LimitLockWaitAsync(ct).ConfigureAwait(false);

        // ⛔ EA-B (G-11): передача зрізу повторно під блокуваннями аркушів — один раз на книгу,
        // ПІСЛЯ ліміту очікування блокувань. Відкликання між переглядом і застосуванням імпорту →
        // відмова, нічого не записано.
        var assignmentsNeeded = active.Exists(x => x.Context.AssignmentsPresent || x.Context.LiftedColumns.Count > 0);
        var assignmentGate = await ReadAssignmentGateAsync(
                active[0].Context.Profile, documentId, assignmentsNeeded, ct)
            .ConfigureAwait(false);
        if (assignmentGate is not null)
        {
            foreach (var item in active)
            {
                Blamed(item.Id, () =>
                {
                    if (AssignmentDenial(item.Context, assignmentGate) is { } reason)
                    {
                        var denied = item.Planned.Upserts.Count + item.Planned.Deletes.Count;
                        throw new AccessDeniedException(
                            "ECR-ACCS-0403",
                            $"Заборонених комірок у батчі: {denied}. Причина першої: {reason}.",
                            new Dictionary<string, object?>
                            {
                                ["messageKey"] = "err.ECR-ACCS-0403.deniedCells",
                                ["deniedCount"] = denied.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["reason"] = reason.ToString(),
                            });
                    }
                });
            }
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

        // ✎ Q-17: комірки колонок з іншим власником (за прапором) йдуть в екземпляри власників. Набори ОДНОГО
        // екземпляра зливаються (ApplyBatchAsync забороняє дублі), набір екземпляра таблиці несе claim версій її рядків.
        var routedItems = new Dictionary<long, RoutedWrite>();
        var batchSets = new List<CellChangeSet>(active.Count);
        var ownerSetsById = new SortedDictionary<long, CellChangeSet>();
        foreach (var item in active)
        {
            var routed = await BlamedAsync(
                    item.Id, () => RouteForeignOwnersAsync(item.Context, item.Applied!, isLateEdit, previous, ct))
                .ConfigureAwait(false);
            if (routed is null)
            {
                batchSets.Add(new CellChangeSet(
                    item.Id, item.Applied!.Upserts, item.Applied.Deletes, item.Applied.Touched,
                    userId, isLateEdit, item.Applied.ExpectedRowVersions));
                continue;
            }

            routedItems[item.Id] = routed;
            item.Context.OutOfWindow.UnionWith(routed.OutOfWindowPhysical);
            batchSets.Add(routed.ViewSet);
            foreach (var owner in routed.OwnerSets)
            {
                ownerSetsById[owner.TableInstanceId] = ownerSetsById.TryGetValue(owner.TableInstanceId, out var already)
                    ? new CellChangeSet(
                        owner.TableInstanceId,
                        [.. already.Upserts, .. owner.Upserts],
                        [.. already.Deletes, .. owner.Deletes],
                        [.. already.TouchedRowIds.Union(owner.TouchedRowIds)],
                        userId, isLateEdit, ExpectedRowVersions: null)
                    : owner;
            }
        }

        batchSets.AddRange(ownerSetsById.Values);

        IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>> written;
        try
        {
            written = await cellStore.ApplyBatchAsync(batchSets, ct).ConfigureAwait(false);
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

            // ⛔ AN-106 (хвіст D1-01 / AN-104): та сама повна відмова, що й у поштучного
            // (`PersistChangesAsync`) — справжня колонка, `currentVersion`, «ваше / чинне», автор,
            // а не `*` з порожньою версією («рядка більше немає»).
            // ⚠ На відміну від поштучного, транзакція книги ще ВІДКРИТА (її відкочує викликач), а
            // захоплення вже підняло версії НЕзастарілих рядків цієї ж транзакції. Тому чинна версія
            // береться лише для застарілих рядків (їх ця транзакція не чіпала — читання під RCSI бачить
            // закомічене чуже), решта лишається прочитаною на вході: інакше власні захоплення
            // виглядали б чужими конфліктами.
            var fresh = await rowStore.GetRowsAsync(guilty.Id, period, ct).ConfigureAwait(false);
            try
            {
                await EnsureNoVersionConflictsAsync(WithFreshVersionsOf(guilty.Context, fresh, staleRowIds), ct)
                    .ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException conflict)
            {
                throw Blame(conflict, guilty.Id)!;
            }

            // Запасний: версії знову збіглися — лишається адресна відмова сховища.
            throw Blame(StaleRowsConflict(guilty.Planned, staleRowIds), guilty.Id)!;
        }
        catch (EcrException error) when (active.Count == 1 && Blame(error, active[0].Id) is { } named)
        {
            // Кілька наборів — сховище вже назвало винний; один — називаємо тут.
            throw named;
        }

        // Попередні значення для аудиту: фізичні адреси власників додаються до прочитаних на вході.
        var auditPrevious = previous;
        if (routedItems.Count > 0)
        {
            var merged = new Dictionary<CellAddress, CellValueData>(previous);
            foreach (var routed in routedItems.Values)
            {
                foreach (var (address, value) in routed.PreviousPhysical)
                {
                    merged[address] = value;
                }
            }

            auditPrevious = merged;
        }

        foreach (var item in active)
        {
            item.Applied = item.Applied! with
            {
                NewRowVersions = written.GetValueOrDefault(item.Id),
                Routed = routedItems.TryGetValue(item.Id, out var routed) ? routed.Groups : null,
            };
        }

        await documents.TouchAsync(documentId, userId, now, ct).ConfigureAwait(false);

        // ✎ Q-17: аудит маршрутизованих елементів - на ФІЗИЧНИХ адресах (період і рядок власника).
        await audit.WriteCellChangesAsync(
            [.. active.SelectMany(x =>
            {
                var changes = routedItems.TryGetValue(x.Id, out var routed) ? routed.Physical : x.Applied!;
                return BuildAuditRecords(
                    x.Request, changes.Upserts, changes.Deletes, userId, now, documentId,
                    changes.RowKeyById, auditPrevious, isLateEdit, AuditCorrelationId(), x.Context.OutOfWindow,
                    x.OverwrittenRowKeys, approverEdit: x.Context.ApproverEdit);
            })],
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Стан аркушів під блокуванням: поданий аркуш пише лише погоджувач поточного кроку (D-445), решта —
    /// <c>CheckSheetStatus</c>, як і доти.
    /// </summary>
    /// <remarks>
    /// ✎ D-445 (пряме слово людини 24.10): імпорт Excel для поданого аркуша відкрито погоджувачу поточного кроку —
    /// ТИМ САМИМ правилом, що й правка в сітці (<c>EnsureSheetStillEditableAsync</c>, D-442): рішення дозволило
    /// запис саме роллю погоджувача (<see cref="RequestContext.ByApprover"/>), і під блокуванням аркуша «чия
    /// черга» перечитано заново (<c>CanApproveAsync</c>) — крок погоджено між переглядом і застосуванням або
    /// аркуш затверджено → відмова <c>DocumentSubmitted</c>/<c>DocumentApproved</c>. Позначка
    /// <see cref="RequestContext.ApproverEdit"/> дає журнал <c>ApproverEdit</c> і насіння з аркушем погоджувача.
    /// <para>
    /// ⛔ Нові рядки на поданому аркуші — ні: роль кроку знімає лише заборону «подано» для ЗНАЧЕНЬ наявних рядків.
    /// Імпорт рядків і так не створює (перегляд — <c>ECR-ROW-0404</c>, застосування — нульова версія рядка) і не
    /// видаляє; батч із новими рядками на поданому аркуші падає в <c>CheckSheetStatus</c> тією самою відмовою.
    /// </para>
    /// <para>
    /// ⚠ «Чия черга» — один раз на аркуш, а не на таблицю: таблиці одного аркуша мають ту саму чергу.
    /// Позначка скидається на кожній спробі: стратегія повторів виконує тіло транзакції вдруге.
    /// </para>
    /// </remarks>
    private async Task EnsureWorkbookSheetsEditableAsync(
        List<WorkbookItem> active,
        Dictionary<int, Domain.Enums.DocumentStatus> statuses,
        CancellationToken ct)
    {
        var stillApprover = new Dictionary<int, bool>();

        foreach (var item in active)
        {
            var context = item.Context;
            var sheetDefId = context.Table.SheetDefId;
            var status = statuses[sheetDefId];

            context.ApproverEdit = false;

            // ✎ P2-A: усі комірки набору — місячні колонки інших місяців: період екземпляра ними не керує.
            if (!context.InstancePeriodGoverns)
            {
                continue;
            }

            if (status == Domain.Enums.DocumentStatus.Submitted
                && context.ByApprover
                && context.Creations.Count == 0)
            {
                if (!stillApprover.TryGetValue(sheetDefId, out var approver))
                {
                    approver = await StillCurrentApproverAsync(context, ct).ConfigureAwait(false);
                    stillApprover[sheetDefId] = approver;
                }

                if (approver)
                {
                    context.ApproverEdit = true;
                    continue;
                }
            }

            Blamed(item.Id, () => CheckSheetStatus(status, item.Planned));
        }

        // ✎ P2-A: стани аркушів періодів місячних колонок — під їхніми блокуваннями, за зростанням (див.
        // `EnsureColumnPeriodsEditableAsync`).
        foreach (var item in active.Where(x => x.Context.ColumnPeriods.Count > 0 || x.Context.AnnualPeriods.Count > 0))
        {
            await BlamedAsync(item.Id, async () =>
            {
                await EnsureColumnPeriodsEditableAsync(
                        item.Context.Instance.DocumentId, item.Context.Table.SheetDefId, item.Context.ColumnPeriods,
                        item.Planned, ct, item.Context.AnnualPeriods)
                    .ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
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
    /// <summary>
    /// Контекст, у якому версії ЗАСТАРІЛИХ рядків (<paramref name="staleRowIds"/>) — чинні з
    /// <paramref name="fresh"/>, а решта — прочитані на вході (AN-106).
    /// </summary>
    /// <remarks>
    /// ⚠ Застарілого рядка, якого вже немає, у версіях немає й тут — <see cref="EnsureNoVersionConflictsAsync"/>
    /// назве його <c>*</c> з порожньою версією, як і має бути для справді видаленого рядка.
    /// </remarks>
    private static RequestContext WithFreshVersionsOf(
        RequestContext context, IReadOnlyList<RowState> fresh, IReadOnlyList<long> staleRowIds)
    {
        var stale = staleRowIds.ToHashSet();
        var versions = new Dictionary<string, string>(context.Versions, StringComparer.Ordinal);
        foreach (var (rowKey, rowId) in context.RowIds)
        {
            if (stale.Contains(rowId))
            {
                versions.Remove(rowKey);
            }
        }

        foreach (var row in fresh)
        {
            if (stale.Contains(row.Id))
            {
                versions[row.RowKey] = row.RowVersion;
            }
        }

        // ⚠ Через інтерфейс: властивість `Versions` — `IReadOnlyDictionary` (CA1859 інакше просить конкретний тип).
        IReadOnlyDictionary<string, string> merged = versions;
        return context with { Versions = merged };
    }

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

        /// <summary>Рядки, свідомо перезаписані поверх чужих правок (AN-114); <c>null</c> — немає.</summary>
        public IReadOnlySet<string>? OverwrittenRowKeys { get; init; }

        public List<CellAddress> AccessAddresses { get; set; } = [];

        public CellChangeLists Planned { get; set; } = null!;

        public CellChangeLists? Applied { get; set; }

        public List<ApplicableMethodology> Applicable { get; } = [];

        public List<Validation.ValidationMessage> RequiredInputMessages { get; set; } = [];

        public List<Validation.ValidationMessage> Messages { get; set; } = [];
    }
}
