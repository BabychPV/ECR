using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class DocumentStore(
    EcrDbContext db,
    Microsoft.Extensions.Caching.Memory.IMemoryCache? staleCache = null,
    Ecr.Domain.Abstractions.IClock? staleClock = null,
    StaleCountsEpoch? staleEpoch = null,
    Microsoft.Extensions.Logging.ILogger<DocumentStore>? staleLog = null,
    Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? staleScopeFactory = null) : IDocumentStore
{
    /// <summary>Стеля карти періоду; у тестах зменшується, щоб довести деградацію без 200 тис. документів.</summary>
    internal int StaleMapMaxDocuments { get; init; } = StaleResultsMapCache.DefaultMaxDocuments;

    /// <summary>Карта застарілості періоду (LP-45, етап 2): кеш - якщо його передано.</summary>
    private StaleResultsMapCache StaleMap => new(db, staleCache, staleClock, staleEpoch, staleLog, StaleMapMaxDocuments, staleScopeFactory);

    /// <summary>Правило «усі аркуші групи обов'язкові».</summary>
    private const byte RequiresAll = 0;

    /// <summary>Правило «хоча б один аркуш групи».</summary>
    private const byte RequiresOne = 1;

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ `DAT-09`. Перед додаванням відчіплюється ПОПЕРЕДНІЙ документ, що
    /// лишився в трекері як <c>Added</c>. Це рівно шлях повтору після
    /// програної гонитви за <c>UQ_Document</c>: невдалий <c>SaveChanges</c>
    /// НЕ прибирає сутність із трекера, тож наступний <c>SaveChanges</c> у
    /// тому самому запиті повторив би ТОЙ САМИЙ конфліктний <c>INSERT</c> із
    /// тим самим ключем — скільки б ключів обробник не підбирав.
    ///
    /// ⚠ Аркуші складу відчіплюються ЯВНО: EF не відчіплює залежних разом із
    /// принципалом, і вони лишилися б <c>Added</c> із посиланням на сутність,
    /// якої в трекері вже немає (помилка зовнішнього ключа замість
    /// повторної вставки).
    ///
    /// ⚠ Область — один HTTP-запит (сховище <c>Scoped</c>), і в ньому
    /// створюється РІВНО один документ. Тому «попередній доданий документ»
    /// не може бути чиєюсь чужою незбереженою роботою.
    /// </remarks>
    public Task AddAsync(Document document, CancellationToken ct)
    {
        foreach (var stale in db.ChangeTracker.Entries<Document>()
                     .Where(e => e.State == EntityState.Added)
                     .ToList())
        {
            // ⚠ Знімок списку, а не сам список: відчеплення аркуша тягне
            // fixup EF, який ВИЛУЧАЄ його з навігації принципала — обхід
            // по живій колекції падає «Collection was modified».
            foreach (var sheet in stale.Entity.Sheets.ToList())
            {
                db.Entry(sheet).State = EntityState.Detached;
            }

            stale.State = EntityState.Detached;
        }

        db.Documents.Add(document);
        return Task.CompletedTask;
    }

    /// <summary>
    /// RPT-STABLE Part B: рідка карта «період → версія» для проєктів зі шкалою з кількох сегментів: лише періоди, версія яких
    /// відрізняється від чинної. Проєкт з одним сегментом (або без шкали) у результат не потрапляє.
    /// </summary>
    private async Task<Dictionary<int, IReadOnlyDictionary<string, string>>> VersionByPeriodAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct)
    {
        var result = new Dictionary<int, IReadOnlyDictionary<string, string>>();
        var ids = projectIds.Distinct().ToList();
        var entries = await db.ProjectTemplateVersions
            .AsNoTracking()
            .Where(t => ids.Contains(t.ProjectId))
            .Select(t => new { t.ProjectId, t.FromPeriodKey, t.TemplateVersionId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var group in entries.GroupBy(e => e.ProjectId).Where(g => g.Count() > 1))
        {
            var line = group.OrderBy(e => e.FromPeriodKey).Select(e => new TimelineEntry(e.FromPeriodKey, e.TemplateVersionId)).ToList();
            var current = await db.Projects.AsNoTracking()
                .Where(p => p.Id == group.Key).Select(p => p.TemplateVersionId).FirstAsync(ct).ConfigureAwait(false);
            var periodKeys = await db.Periods.AsNoTracking()
                .Where(p => p.ProjectId == group.Key).OrderBy(p => p.PeriodKeyValue).Select(p => p.PeriodKeyValue)
                .ToListAsync(ct).ConfigureAwait(false);
            var differing = periodKeys
                .Select(k => (Key: k, Version: ProjectVersionTimeline.VersionOf(line, k, current)))
                .Where(x => x.Version != current)
                .ToList();
            if (differing.Count == 0)
            {
                continue;
            }

            var versionIds = differing.Select(x => x.Version).Distinct().ToList();
            var labels = await db.TemplateVersions.AsNoTracking()
                .Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Version, ct).ConfigureAwait(false);
            result[group.Key] = differing.ToDictionary(
                x => x.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                x => labels.GetValueOrDefault(x.Version, x.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                StringComparer.Ordinal);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<DocumentSummary?> FindAsync(
        long documentId, PeriodKeyFilter period, CancellationToken ct)
    {
        var document = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new
            {
                d.Id, d.ProjectId, d.BusinessKey, d.CreatedAt, d.NameL10n, d.CreatedByUserId,
                // EA-C: стан проєкту для canManageEditAccess — у тому самому запиті (без додаткового роундтрипу).
                ProjectReadOnly = db.Projects.Where(p => p.Id == d.ProjectId)
                    .Select(p => p.Status == ProjectStatus.Archived || p.IsArchiving).FirstOrDefault(),
                // D-12: картка читає «коли й ким змінено» так само, як перелік (`ListAsync`); раніше ці поля тут
                // не вибирались, і картка віддавала `null` усім. Звуженому читачеві їх знімає R-7 (`DocumentSheetVisibility.For`).
                d.ModifiedAt,
                // RC15: версія шаблону проєкту - у тому самому запиті (без додаткового роундтрипу).
                TemplateVersion = db.Projects.Where(p => p.Id == d.ProjectId)
                    .Join(db.TemplateVersions, p => p.TemplateVersionId, v => v.Id, (p, v) => v.Version)
                    .FirstOrDefault(),
                // ✎ RPT-STABLE Part B: ознака «кілька версій за періодами» - у тому самому запиті (без зайвого звернення).
                Multi = db.ProjectTemplateVersions.Any(t => t.ProjectId == d.ProjectId
                    && t.TemplateVersionId != db.Projects.Where(p => p.Id == d.ProjectId).Select(p => p.TemplateVersionId).FirstOrDefault()),
                // AN-63: періодичність проєкту - підзапит у тому самому запиті (без додаткового роундтрипу); (PeriodKind?) - null, якщо проєкту немає.
                ProjectPeriodKind = db.Projects.Where(p => p.Id == d.ProjectId).Select(p => (PeriodKind?)p.PeriodKind).FirstOrDefault(),
                ModifiedBy = db.Users.Where(u => u.Id == d.ModifiedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                Owner =db.Users.Where(u => u.Id == d.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                // К6: «хто затвердив» - у тому самому запиті (без додаткового роундтрипу).
                Approver = db.Users.Where(u => u.Id ==
                    (db.ApprovalEvents
                        .Where(e => e.DocumentId == d.Id && e.ByUserId != null
                                    && (e.Action == Domain.Entities.Workflow.ApprovalAction.Approve
                                        || e.Action == Domain.Entities.Workflow.ApprovalAction.ApproveStep)
                                    && (!db.ApprovalEvents.Any(r => r.DocumentId == d.Id && r.At >= e.At
                                        && (r.Action == Domain.Entities.Workflow.ApprovalAction.Reject
                                            || r.Action == Domain.Entities.Workflow.ApprovalAction.Reopen
                                            || r.Action == Domain.Entities.Workflow.ApprovalAction.Recall))))
                        .OrderByDescending(e => e.At)
                        .Select(e => e.ByUserId)
                        .FirstOrDefault()
                    ?? db.ApprovalStates
                        .Where(a => a.DocumentId == d.Id && a.Status == DocumentStatus.Approved && a.ApprovedByUserId != null)
                        .OrderByDescending(a => a.ApprovedAt)
                        .Select(a => a.ApprovedByUserId)
                        .FirstOrDefault()))
                .Select(u => u.DisplayName).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        var sheetCount = await db.DocumentSheets
            .CountAsync(s => s.DocumentId == documentId && s.IsIncluded, ct)
            .ConfigureAwait(false);

        var sheets = await StatesAsync(documentId, period, ct).ConfigureAwait(false);
        sheets = await MarkRecalculatedAfterSubmissionAsync(documentId, period, sheets, ct).ConfigureAwait(false);
        var late = await LateEditsBatchAsync([documentId], period, ct).ConfigureAwait(false);
        var included = await IncludedCodesBatchAsync([documentId], period, ct).ConfigureAwait(false);
        // ⛔ Картка одного документа - ВУЗЬКИЙ запит по одному id тією самою плоскою формою, а НЕ карта періоду:
        // після кожного PATCH кеш карти холодний, і повний запит періоду заради одного документа - зайвий.
        var stale = await StaleResultsDirectAsync([documentId], period, ct).ConfigureAwait(false);

        return new DocumentSummary(
            document.Id, document.ProjectId, document.BusinessKey, document.CreatedAt, sheetCount,
            ToStateMap(sheets), document.NameL10n,
            ModifiedAt: document.ModifiedAt, ModifiedByDisplayName: document.ModifiedBy,
            HasLateEdits: late.Contains(documentId),
            Sheets: sheets, IncludedSheetCodes: included.GetValueOrDefault(documentId),
            OwnerDisplayName: document.Owner,
            ApproverDisplayName: document.Approver,
            ResultsStale: stale is null ? null : stale.ContainsKey(documentId),
            ResultsStaleSince: stale?.GetValueOrDefault(documentId),
            TemplateVersion: document.TemplateVersion,
            TemplateVersionByPeriod: document.Multi
                ? (await VersionByPeriodAsync([document.ProjectId], ct).ConfigureAwait(false)).GetValueOrDefault(document.ProjectId)
                : null,
            OwnerUserId: document.CreatedByUserId,
            ProjectIsReadOnly: document.ProjectReadOnly,
            PeriodKind: document.ProjectPeriodKind?.ToString());
    }


    /// <summary>
    /// ✎ UAT-recalc-ux: позначка «перераховано після подання» — поданий аркуш-оригінал, для якого в журналі змін є
    /// запис <c>SheetRecalc/RecalculatedAfterSubmission</c> ПІЗНІШИЙ за останнє подання (<c>SubmittedAt</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Окремого стовпця немає навмисно: факт уже лежить у журналі (його пише <c>RecalculationJob</c>), а правило
    /// зникнення — саме порівняння з <c>SubmittedAt</c>: повторне подання зсуває <c>SubmittedAt</c>, затвердження
    /// знімає стан <c>Submitted</c>. Запит лише тоді, коли є поданий аркуш (один на картку).
    /// </remarks>
    private async Task<IReadOnlyList<DocumentSheetState>> MarkRecalculatedAfterSubmissionAsync(
        long documentId, PeriodKeyFilter period, IReadOnlyList<DocumentSheetState> sheets, CancellationToken ct)
    {
        if (period.Value is not { } periodKey
            || !sheets.Any(s => string.Equals(s.State, nameof(DocumentStatus.Submitted), StringComparison.Ordinal)))
        {
            return sheets;
        }

        var entityType = Application.Calculations.RecalculationSkips.AuditEntityType;
        var operation = Application.Calculations.RecalculationSkips.AuditRecalculatedAfterSubmission;
        var prefix = string.Create(CultureInfo.InvariantCulture, $"sheet-recalc:{documentId}:");
        var suffix = string.Create(CultureInfo.InvariantCulture, $":{periodKey}");

        var codes = await db.Database
            .SqlQuery<string>($"""
                SELECT sd.Code AS Value
                  FROM wf.ApprovalState a
                  JOIN cfg.SheetDef sd ON sd.Id = a.SheetDefId
                 WHERE a.DocumentId = {documentId} AND a.PeriodKey = {periodKey} AND a.SheetCopyId IS NULL
                   AND a.Status = 1 AND a.SubmittedAt IS NOT NULL
                   AND EXISTS (SELECT 1
                                 FROM aud.StructureChange c
                                WHERE c.EntityType = {entityType} AND c.Operation = {operation}
                                  AND c.ChangedAt > a.SubmittedAt
                                  AND c.CorrelationId = CONCAT({prefix}, CAST(a.SheetDefId AS nvarchar(12)), {suffix}))
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (codes.Count == 0)
        {
            return sheets;
        }

        var marked = codes.ToHashSet(StringComparer.Ordinal);
        return [.. sheets.Select(s => marked.Contains(s.Code) ? s with { RecalculatedAfterSubmission = true } : s)];
    }

    /// <summary>
    /// Коди аркушів складу документів ОДНИМ запитом — для обробника, що відсікає аркуші поза
    /// межами читача (<c>DocumentSheetVisibility</c>). З періодом склад уже є в
    /// <c>Sheets</c> (той самий запит, <c>SheetStatesQuery</c>) — другого запиту нема (Q-167);
    /// без періоду стану аркушів немає, а кількість видимих аркушів усе одно потрібна.
    /// </summary>
    private async Task<Dictionary<long, IReadOnlyList<string>>> IncludedCodesBatchAsync(
        long[] documentIds, PeriodKeyFilter period, CancellationToken ct)
    {
        if (period.Value is not null)
        {
            return [];
        }

        var rows = await db.DocumentSheets
            .AsNoTracking()
            .Where(s => documentIds.Contains(s.DocumentId) && s.IsIncluded)
            .Join(db.SheetDefs, s => s.SheetDefId, d => d.Id, (s, d) => new { s.DocumentId, d.Code, d.Ordinal })
            .OrderBy(r => r.DocumentId).ThenBy(r => r.Ordinal).ThenBy(r => r.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.DocumentId)
            .ToDictionary(g => g.Key, IReadOnlyList<string> (g) => [.. g.Select(r => r.Code).Distinct()]);   // RS: копії діляться кодом з оригіналом
    }

    /// <inheritdoc />
    public async Task<PagedResult<DocumentSummary>> ListAsync(
        int? projectId,
        PeriodKeyFilter period,
        DocumentListFilter filter,
        CursorRequest page,
        IReadOnlyCollection<int>? visibleProjectIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        // Масив, а не `IReadOnlyCollection`: `Contains` над масивом EF
        // перекладає в `IN (...)`, над інтерфейсом — не гарантовано.
        var allowedProjects = visibleProjectIds?.ToArray();

        // ✎ Версія шаблону (фільтр і сортування): версія — властивість ПРОЄКТУ за період, тож групи проєктів за версією
        // рахуються один раз (3 легкі запити, лише коли параметр задано), а сам перелік лишається запитом по Id в межах
        // групи: видимість (`VisibleDocuments`) і решта фільтрів діють ПЕРШИМИ, групи лише звужують проєкти.
        var versionPlan = filter.TemplateVersions is not null || filter.VersionSort is not null
            ? await PlanVersionsAsync(period, filter, ct).ConfigureAwait(false)
            : null;

        // LP-45: карта «*» береться ОДИН раз на запит (також для позначки в рядках нижче).
        var starFetched = false;
        IReadOnlyDictionary<long, DateTime?>? starMap = null;
        IReadOnlyDictionary<long, DateTime?>? staleMap = null;
        if (filter.ResultsStale is not null && period.Value is { } staleByPeriod)
        {
            // LP-45 (етап 2): ідентифікатори беруться з карти періоду (кеш), а не підзапитом по журналу правок.
            staleMap = (await StaleMap.GetAsync(staleByPeriod, [filter.StaleByUserId], ct).ConfigureAwait(false))[0];
            if (filter.StaleByUserId is null)
            {
                starFetched = true;
                starMap = staleMap;
            }
        }

        IQueryable<Document> Build(long after, int[]? onlyProjects)
        {
            var documents = VisibleDocuments(projectId, after, filter, allowedProjects);

            if (onlyProjects is not null)
            {
                documents = documents.Where(d => onlyProjects.Contains(d.ProjectId));
            }

            // ✎ g2 (макет 08.11, людина: «Year 2024 — No documents for 2024»): перелік за РІК — лише документи
            // проєктів, що мають період у цьому році. Раніше рік звужував лише смугу й фільтр стану, тож рік без
            // проєктів показував документи 2026 із порожньою смугою, а лічильники рахували їх як Draft.
            if (period.Value is null && filter.Year is { } listYear)
            {
                documents = WhereProjectYear(documents, listYear);
            }

            // BE-09b: фільтри — у ЗАПИТІ, до `Take`; межа грантів вище лишається.
            if (filter.MineUserId is { } me)
            {
                documents = documents.Where(d => d.CreatedByUserId == me
                                                 || db.ApprovalStates.Any(a => a.DocumentId == d.Id && a.SubmittedByUserId == me));
            }

            // ✎ D-450: власник — теж ЗАПИТОМ до `Take`, і лише серед документів, де ім'я власника читачеві віддається
            // (`WhereOwnerDisclosed`): інакше фільтр підтверджував би власника документа, у рядку якого стоїть «—».
            if (filter.OwnerUserId is { } owner)
            {
                documents = WhereOwnerDisclosed(documents, filter).Where(d => d.CreatedByUserId == owner);
            }

            if (filter.State is { } state && period.Value is { } periodKey)
            {
                documents = WhereState(documents, state, periodKey, filter.HiddenSheetDefIds, filter.Participation?.HiddenSheets);
            }
            else if (filter.PartlySubmitted && period.Value is { } partlyPeriod)
            {
                documents = WhereState(
                    documents, state: null, partlyPeriod, filter.HiddenSheetDefIds, filter.Participation?.HiddenSheets);
            }
            else if ((filter.State is not null || filter.PartlySubmitted) && period.Value is null && filter.Year is { } year)
            {
                // ✎ 2026-11-07 Period clarity: стан за РІК — те саме правило над клітинками «аркуш × розпочатий період».
                documents = WhereYearState(
                    documents, filter.PartlySubmitted ? null : filter.State, year,
                    filter.HiddenSheetDefIds, filter.Participation?.HiddenSheets);
            }

            // BE-09b: та сама умова, що дає позначку в рядку (`LateEditDocumentIds`),
            // застосована ЗАПИТОМ до стелі сторінки — не друге визначення «пізньої
            // правки». Без періоду діє за БУДЬ-ЯКИЙ, як і сама позначка.
            if (filter.HasLateEdits is { } wantLate)
            {
                var lateIds = LateEditDocumentIds(period, filter);
                documents = wantLate
                    ? documents.Where(d => lateIds.Contains(d.Id))
                    : documents.Where(d => !lateIds.Contains(d.Id));
            }

            // resultsStale: той самий предикат, що дає позначку в рядку (`StaleResultsQuery`), - підзапитом ДО `Take`.
            // ⛔ Проєкти зі звуженням виключаються в обох напрямках (`false` теж): інакше «не застарілі» = решта,
            // тобто те саме знання навиворіт. Без періоду фільтр не діє (застарілість належить періоду).
            // ✎ D-446: документ-надання, видимий учасникові ЦІЛКОМ, — у фільтрі як звичайний; частково схований — поза ним.
            // LP-45 / D-432: карта «*» береться ОДИН раз на запит переліку - фільтр і позначка рядків бачать одну й ту саму
            // карту (без другого читання відбитка й без гонки з фоновим оновленням між двома зверненнями).
            if (filter.ResultsStale is { } wantStale && period.Value is { } staleBy)
            {
                var narrowed = filter.NarrowedProjectIds?.ToArray() ?? [];

                // ✎ D-446: так само документи-надання, де учасник не бачить хоч щось: застарілість — по ВСЬОМУ документу,
                // тож для нього вона не визначена (рядок — `null`), і «не застарілі» без них не стають знанням навиворіт.
                var narrowedDocuments = filter.Participation?.NarrowedDocumentIds?.ToArray() ?? [];
                documents = documents.Where(d => !narrowed.Contains(d.ProjectId));
                if (narrowedDocuments.Length > 0)
                {
                    documents = documents.Where(d => !narrowedDocuments.Contains(d.Id));
                }

                if (staleMap is null)
                {
                    // Карта переповнена: підзапитом по журналу, як у 1885c2c77 (без кешу).
                    var staleSubquery = StaleResultsQuery.Documents(db, staleBy, filter.StaleByUserId).Select(r => r.DocumentId);
                    documents = wantStale
                        ? documents.Where(d => staleSubquery.Contains(d.Id))
                        : documents.Where(d => !staleSubquery.Contains(d.Id));
                }
                else
                {
                    var staleIds = staleMap.Keys.ToArray();
                    documents = wantStale
                        ? documents.Where(d => staleIds.Contains(d.Id))
                        : documents.Where(d => !staleIds.Contains(d.Id));
                }
            }

            // UI-18: пошук — теж у ЗАПИТІ до `Take`, ПОРУЧ із межею проєктів вище (а не
            // постфільтр: той дав би хибні `NextCursor`/`TotalCount`). Лише поля самого
            // документа — аркуші й таблиці не чіпаються, тож приховане не підтверджується.
            if (filter.Query is { Length: > 0 } term)
            {
                var matched = DocumentIdsMatching(term);
                documents = documents.Where(d => matched.Contains(d.Id));
            }

            return documents;
        }

        List<DocumentRow> rows;
        if (versionPlan is { Sorted: true } plan)
        {
            // Сортування за версією: групи проєктів у порядку версій, усередині групи - за Id (детермінований другий ключ).
            // Курсор несе групу (її версію) і Id останнього рядка; сторінка добирається з наступних груп.
            var (groupIndex, after) = plan.Resume(page.Cursor);
            rows = [];
            for (var g = groupIndex; g < plan.Groups.Count && rows.Count < page.Limit + 1; g++)
            {
                var fromId = g == groupIndex ? after : 0;
                rows.AddRange(await ProjectRows(Build(fromId, plan.Groups[g].ProjectIds).OrderBy(d => d.Id).Take(page.Limit + 1 - rows.Count))
                    .ToListAsync(ct).ConfigureAwait(false));
            }
        }
        else
        {
            var documents = Build(Cursor.Decode(page.Cursor), versionPlan?.WantedProjects);
            rows = await ProjectRows(documents.OrderBy(d => d.Id).Take(page.Limit + 1)).ToListAsync(ct).ConfigureAwait(false);        }

        IQueryable<DocumentRow> ProjectRows(IQueryable<Document> documents)
            => documents
            .Select(d => new DocumentRow(
                d.Id,
                d.ProjectId,
                d.BusinessKey,
                d.CreatedAt,
                db.DocumentSheets.Count(s => s.DocumentId == d.Id && s.IsIncluded),
                d.NameL10n,
                d.ModifiedAt,
                db.Users.Where(u => u.Id == d.ModifiedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                db.Users.Where(u => u.Id == d.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                db.Users.Where(u => u.Id ==
                    (db.ApprovalEvents
                        .Where(e => e.DocumentId == d.Id && e.ByUserId != null
                                    && (e.Action == Domain.Entities.Workflow.ApprovalAction.Approve
                                        || e.Action == Domain.Entities.Workflow.ApprovalAction.ApproveStep)
                                    && (!db.ApprovalEvents.Any(r => r.DocumentId == d.Id && r.At >= e.At
                                        && (r.Action == Domain.Entities.Workflow.ApprovalAction.Reject
                                            || r.Action == Domain.Entities.Workflow.ApprovalAction.Reopen
                                            || r.Action == Domain.Entities.Workflow.ApprovalAction.Recall))))
                        .OrderByDescending(e => e.At)
                        .Select(e => e.ByUserId)
                        .FirstOrDefault()
                    ?? db.ApprovalStates
                        .Where(a => a.DocumentId == d.Id && a.Status == DocumentStatus.Approved && a.ApprovedByUserId != null)
                        .OrderByDescending(a => a.ApprovedAt)
                        .Select(a => a.ApprovedByUserId)
                        .FirstOrDefault()))
                .Select(u => u.DisplayName).FirstOrDefault(),
                // AN-63: періодичність проєкту - скалярний підзапит у тому самому запиті сторінки (запитів не додається).
                db.Projects.Where(p => p.Id == d.ProjectId).Select(p => (PeriodKind?)p.PeriodKind).FirstOrDefault(),
                // ✎ D-450: ідентифікатор власника поруч з іменем, тим самим розкриттям (звуженому читачеві `For` знімає обидва).
                d.CreatedByUserId,
                // ✎ RPT-STABLE: типова версія шаблону — скалярний підзапит у тому самому запиті сторінки (запитів не додається).
                db.Projects.Where(p => p.Id == d.ProjectId)
                    .Join(db.TemplateVersions, p => p.TemplateVersionId, v => v.Id, (p, v) => v.Version)
                    .FirstOrDefault(),
                // ✎ RPT-STABLE Part B: ознака «у проєкті кілька версій за періодами» - у тому самому запиті сторінки.
                db.ProjectTemplateVersions.Any(t => t.ProjectId == d.ProjectId
                    && t.TemplateVersionId != db.Projects.Where(p => p.Id == d.ProjectId).Select(p => p.TemplateVersionId).FirstOrDefault())));

        var hasMore = rows.Count > page.Limit;
        var page1 = rows.Take(page.Limit).ToList();

        // ⛔ Q-167 (аудит фази 2, продуктивність): ОДИН пакетний запит станів
        // на ВСЮ сторінку замість запиту на кожен документ — «перелік
        // документів» найчастіше відвідуваний екран системи, і зайвий похід
        // у базу на кожен рядок сторінки платить кожен, хто його відкриває.
        var statesByDocument = await StatesBatchAsync(
            [.. page1.Select(d => d.Id)], period, ct).ConfigureAwait(false);

        // BE-09: так само ОДИН запит на сторінку — лічильники останньої перевірки.
        var findingsByDocument = await LatestFindingsBatchAsync(
            [.. page1.Select(d => d.Id)], period, ct).ConfigureAwait(false);

        // BE-09b: і позначка пізніх правок — теж ОДИН запит на сторінку.
        var late = await LateEditsBatchAsync([.. page1.Select(d => d.Id)], period, ct, filter).ConfigureAwait(false);

        var includedByDocument = await IncludedCodesBatchAsync([.. page1.Select(d => d.Id)], period, ct).ConfigureAwait(false);

        // І застарілість результатів - теж ОДИН запит на сторінку.
        var staleByDocument = await StaleResultsBatchAsync(
            [.. page1.Select(d => d.Id)], period, starFetched, starMap, ct).ConfigureAwait(false);

        // ✎ 2026-11-07 Period clarity: смуга станів року — теж ОДИН запит на сторінку.
        var periodStates = filter.Year is { } stripYear
            ? await PeriodStatesBatchAsync([.. page1.Select(d => d.Id)], stripYear, filter, ct).ConfigureAwait(false)
            : null;

        var multiProjects = page1.Where(d => d.MultiVersion).Select(d => d.ProjectId).ToList();
        var versionByPeriod = multiProjects.Count == 0
            ? []
            : await VersionByPeriodAsync(multiProjects, ct).ConfigureAwait(false);

        var items = new List<DocumentSummary>(page1.Count);
        foreach (var d in page1)
        {
            IReadOnlyList<DocumentSheetState> sheets = statesByDocument.TryGetValue(d.Id, out var found)
                ? found
                : [];

            // ⛔ Немає підсумку — `null`, а не нуль: документ не перевіряли.
            var findings = findingsByDocument.GetValueOrDefault(d.Id);

            items.Add(new DocumentSummary(
                d.Id, d.ProjectId, d.BusinessKey, d.CreatedAt, d.SheetCount, ToStateMap(sheets), d.NameL10n,
                d.ModifiedAt, d.ModifiedByDisplayName, findings?.ErrorCount, findings?.WarningCount,
                late.Contains(d.Id), sheets, includedByDocument.GetValueOrDefault(d.Id), d.OwnerDisplayName,
                d.ApproverDisplayName,
                staleByDocument is null ? null : staleByDocument.ContainsKey(d.Id),
                staleByDocument?.GetValueOrDefault(d.Id),
                PeriodKind: d.ProjectPeriodKind?.ToString(),
                OwnerUserId: d.OwnerUserId,
                TemplateVersion: d.TemplateVersion,
                TemplateVersionByPeriod: versionByPeriod.GetValueOrDefault(d.ProjectId),
                PeriodStates: periodStates is null ? null : periodStates.GetValueOrDefault(d.Id) ?? []));
        }

        var next = !hasMore
            ? null
            : versionPlan is { Sorted: true } sortedPlan
                ? sortedPlan.Encode(items[^1].ProjectId, items[^1].Id)
                : Cursor.Encode(items[^1].Id);
        return new PagedResult<DocumentSummary>(items, next, TotalCount: null);
    }

    /// <summary>
    /// Групи проєктів за версією шаблону ДЛЯ ПЕРІОДУ (та сама логіка, що в колонці «Version»: версія періоду або типова
    /// версія проєкту) — основа фільтра <see cref="DocumentListFilter.TemplateVersions"/> і сортування
    /// <see cref="DocumentListFilter.VersionSort"/>. Лише метадані шаблону (не секрет); перелік документів з них не береться.
    /// </summary>
    private async Task<VersionPlan> PlanVersionsAsync(PeriodKeyFilter period, DocumentListFilter filter, CancellationToken ct)
    {
        var projects = await ProjectVersionLabelsAsync(period, onlyProjects: null, ct).ConfigureAwait(false);

        var byVersion = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var unversioned = new List<int>();
        foreach (var project in projects)
        {
            if (project.Value is { } version)
            {
                if (!byVersion.TryGetValue(version, out var ids))
                {
                    byVersion[version] = ids = [];
                }

                ids.Add(project.Key);
            }
            else
            {
                unversioned.Add(project.Key);
            }
        }

        var wanted = filter.TemplateVersions is null ? null : filter.TemplateVersions.ToHashSet(StringComparer.Ordinal);
        var kept = byVersion.Where(e => wanted is null || wanted.Contains(e.Key)).ToList();

        var descending = filter.VersionSort == DocumentVersionSort.Descending;
        var ordered = kept
            .OrderBy(e => e.Key, Comparer<string>.Create((a, b) => descending ? VersionOrder.Compare(b, a) : VersionOrder.Compare(a, b)))
            .Select(e => new VersionGroup(e.Key, [.. e.Value]))
            .ToList();

        // Без версії - в кінці в обох напрямках (як у колонці); фільтр за версією їх виключає.
        if (wanted is null && unversioned.Count > 0)
        {
            ordered.Add(new VersionGroup(null, [.. unversioned]));
        }

        return new VersionPlan(
            filter.VersionSort is not null, descending, ordered,
            [.. ordered.SelectMany(g => g.ProjectIds)]);
    }

    /// <summary>
    /// Версія шаблону кожного проєкту ДЛЯ ПЕРІОДУ (<c>null</c> — без версії). Єдине місце, що дає версію переліку:
    /// фільтр, сортування і варіанти фільтра (<see cref="ListTemplateVersionsAsync"/>) не розходяться.
    /// </summary>
    /// <param name="period">Період; без нього — чинна версія проєкту.</param>
    /// <param name="onlyProjects">Лише ці проєкти; <c>null</c> — усі проєкти, що мають документи.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task<Dictionary<int, string?>> ProjectVersionLabelsAsync(
        PeriodKeyFilter period, int[]? onlyProjects, CancellationToken ct)
    {
        // ⛔ Версію за період дає ЛИШЕ `doc.fn_VersionOf` (`ProjectTemplateVersionReadGuardTests`): без періоду - за
        // найпізніший можливий період (YYYY12 далекого року), тобто чинна версія проєкту. Проєкти без документів у групи
        // не потрапляють (їм нічого сортувати).
        var key = period.Value ?? MaxPeriodKey;
        var candidates = db.Projects.AsNoTracking().Where(p => db.Documents.Any(d => d.ProjectId == p.Id));
        if (onlyProjects is not null)
        {
            candidates = candidates.Where(p => onlyProjects.Contains(p.Id));
        }

        var projects = await candidates
            .Select(p => new { p.Id, VersionId = EcrDbContext.VersionOf(p.Id, key) })
            .ToListAsync(ct).ConfigureAwait(false);
        var versionIds = projects.Select(p => p.VersionId).Distinct().ToArray();
        var labels = await db.TemplateVersions.AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Version })
            .ToDictionaryAsync(v => v.Id, v => v.Version, ct).ConfigureAwait(false);

        return projects.ToDictionary(
            p => p.Id,
            p => labels.TryGetValue(p.VersionId, out var version) && !string.IsNullOrWhiteSpace(version) ? version : null);
    }

    /// <inheritdoc />
    public async Task<DocumentTemplateVersionOptions> ListTemplateVersionsAsync(
        int? projectId,
        PeriodKeyFilter period,
        DocumentListFilter filter,
        IReadOnlyCollection<int> visibleProjectIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(visibleProjectIds);

        // Видимість — тим самим базовим запитом, що й перелік: версії, які є лише в невидимих документах, не називаються.
        var perProject = await VisibleDocuments(projectId, 0, filter, visibleProjectIds.ToArray())
            .GroupBy(d => d.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .OrderBy(g => g.ProjectId)
            .Take(MaxVersionProjects)
            .ToListAsync(ct).ConfigureAwait(false);
        if (perProject.Count == 0)
        {
            return new DocumentTemplateVersionOptions([], 0);
        }

        var labels = await ProjectVersionLabelsAsync(period, [.. perProject.Select(p => p.ProjectId)], ct).ConfigureAwait(false);

        var byVersion = new Dictionary<string, int>(StringComparer.Ordinal);
        var without = 0;
        foreach (var project in perProject)
        {
            if (labels.TryGetValue(project.ProjectId, out var version) && version is not null)
            {
                byVersion[version] = byVersion.GetValueOrDefault(version) + project.Count;
            }
            else
            {
                without += project.Count;
            }
        }

        var items = byVersion
            .OrderBy(e => e.Key, Comparer<string>.Create(VersionOrder.Compare))
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Take(MaxVersionOptions)
            .Select(e => new DocumentTemplateVersionOption(e.Key, e.Value))
            .ToList();
        return new DocumentTemplateVersionOptions(items, without);
    }

    /// <summary>Стеля проєктів, з яких збираються варіанти версій (проти необмеженого читання).</summary>
    private const int MaxVersionProjects = 5000;

    /// <summary>Стеля варіантів версій у відповіді.</summary>
    private const int MaxVersionOptions = 200;

    /// <summary>Найпізніший можливий ключ періоду (YYYYMM): версія за нього = чинна версія проєкту.</summary>
    private const int MaxPeriodKey = 999912;

    /// <summary>Група проєктів з однією версією (<c>null</c> — без версії).</summary>
    private sealed record VersionGroup(string? Label, int[] ProjectIds);

    /// <summary>План переліку за версією: групи у порядку сортування і курсор «група + Id».</summary>
    private sealed record VersionPlan(bool Sorted, bool Descending, List<VersionGroup> Groups, int[] WantedProjects)
    {
        /// <summary>Курсор: <c>V{id}|{версія}</c> або <c>N{id}</c> (група без версії); непрозорий для клієнта.</summary>
        public string Encode(int projectId, long id)
        {
            var group = Groups.FirstOrDefault(g => g.ProjectIds.Contains(projectId));
            var number = id.ToString(CultureInfo.InvariantCulture);
            var text = group?.Label is { } version ? string.Concat("V", number, "|", version) : string.Concat("N", number);
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        }

        /// <summary>Група і Id, з яких продовжувати; зіпсований курсор - початок. Версія, якої вже немає, - наступна за нею група.</summary>
        public (int GroupIndex, long AfterId) Resume(string? cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor))
            {
                return (0, 0);
            }

            Span<byte> buffer = stackalloc byte[256];
            if (!Convert.TryFromBase64String(cursor, buffer, out var written) || written < 2)
            {
                return (0, 0);
            }

            var text = System.Text.Encoding.UTF8.GetString(buffer[..written]);
            var bar = text.IndexOf('|', StringComparison.Ordinal);
            var digits = text[0] switch
            {
                'V' when bar > 1 => text[1..bar],
                'N' => text[1..],
                _ => null,
            };
            if (digits is null || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return (0, 0);
            }

            if (text[0] == 'N')
            {
                var tail = Groups.FindIndex(g => g.Label is null);
                return tail < 0 ? (Groups.Count, 0) : (tail, id);
            }

            var label = text[(bar + 1)..];
            var exact = Groups.FindIndex(g => string.Equals(g.Label, label, StringComparison.Ordinal));
            if (exact >= 0)
            {
                return (exact, id);
            }

            // Версії вже немає серед груп (змінились дані між сторінками): перша група, що йде ПІСЛЯ неї в порядку сортування.
            var next = Groups.FindIndex(g => g.Label is null
                || (Descending ? VersionOrder.Compare(g.Label, label) < 0 : VersionOrder.Compare(g.Label, label) > 0));
            return (next < 0 ? Groups.Count : next, 0);
        }
    }

    /// <summary>
    /// Базовий запит видимості переліку: курсор, проєкт, проєкти з грантом і (D-443) документи-надання учасника з
    /// порахованими межами. ✎ D-450: винесено з <see cref="ListAsync"/>, щоб вибір власників
    /// (<see cref="ListOwnersAsync"/>) бачив РІВНО ті самі документи, а не власну, ширшу межу.
    /// </summary>
    private IQueryable<Document> VisibleDocuments(int? projectId, long after, DocumentListFilter filter, int[]? allowedProjects)
    {
        IQueryable<Document> documents;
        if (filter.Participation is { } participation && allowedProjects is not null)
        {
            // ✎ D-443: документи, відкриті наданням учасника, — ПІДЗАПИТОМ у тому самому запиті (0 нових звернень),
            // поза проєктами з забороною ролі. Без учасника — рівно попередній запит (гілка нижче).
            var participated = EditAssignmentFactsReader.ParticipatedDocuments(db, participation.UserId, participation.AsOfUtc);
            var denied = participation.DeniedProjectIds.ToArray();
            documents = db.Documents
                .AsNoTracking()
                .Where(d => d.Id > after
                            && (projectId == null || d.ProjectId == projectId)
                            && (allowedProjects.Contains(d.ProjectId)
                                || (!denied.Contains(d.ProjectId) && participated.Contains(d.Id))));

            // ✎ D-446: поза проєктами з грантом — лише документи, для яких межі учасника пораховані (закрито за
            // замовчуванням: надання, що з'явилось посеред запиту, видно з наступним запитом, а не без меж).
            if (participation.DocumentIds is { } boundedIds)
            {
                var bounded = boundedIds.ToArray();
                documents = documents.Where(d => allowedProjects.Contains(d.ProjectId) || bounded.Contains(d.Id));
            }
        }
        else
        {
            documents = db.Documents
                .AsNoTracking()
                .Where(d => d.Id > after
                            && (projectId == null || d.ProjectId == projectId)
                            && (allowedProjects == null || allowedProjects.Contains(d.ProjectId)));
        }

        return documents;
    }

    /// <summary>
    /// ✎ D-450: лише документи, де ім'я власника читачеві ВІДДАЄТЬСЯ — дзеркало умови <c>narrowed</c> у
    /// <c>DocumentSheetVisibility.For</c>, що знімає <see cref="DocumentSummary.OwnerDisplayName"/>: схована таблиця чи
    /// колонка проєкту (у документі-наданні — самого документа) звужує КОЖЕН документ, схований аркуш — лише той, у
    /// складі якого він є. Межі — ті самі пари, що вже несе фільтр (роль — «проєкт, Id», надання — «документ, Id»).
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього фільтр і вибір власників були б оракулом «чий документ», якого рядок переліку навмисно не каже.
    /// ⚠ Документ проєкту з роллю, де читач має ще й надання, звужується обома наборами пар — свідомо, як фільтр стану
    /// D-444: лише недолік (документ випадає з фільтра), не витік.
    /// </remarks>
    private IQueryable<Document> WhereOwnerDisclosed(IQueryable<Document> documents, DocumentListFilter filter)
    {
        var projectWide = (filter.HiddenTableDefIds ?? []).Select(p => p.ProjectId)
            .Concat((filter.HiddenColumnDefIds ?? []).Select(p => p.ProjectId))
            .Distinct()
            .Order()
            .ToArray();
        if (projectWide.Length > 0)
        {
            documents = documents.Where(d => !projectWide.Contains(d.ProjectId));
        }

        if (filter.HiddenSheetDefIds is { Count: > 0 } hiddenSheets)
        {
            var hiddenRows = HiddenSheetRows(hiddenSheets);
            documents = documents.Where(d => !db.DocumentSheets.Any(s => s.DocumentId == d.Id && s.IsIncluded && hiddenRows.Contains(s.Id)));
        }

        if (filter.Participation is { } participation)
        {
            var documentWide = (participation.HiddenTables ?? []).Select(p => p.DocumentId)
                .Concat((participation.HiddenColumns ?? []).Select(p => p.DocumentId))
                .Distinct()
                .Order()
                .ToArray();
            if (documentWide.Length > 0)
            {
                documents = documents.Where(d => !documentWide.Contains(d.Id));
            }

            if (participation.HiddenSheets is { Count: > 0 } participantSheets)
            {
                var participantRows = HiddenParticipantSheetRows(participantSheets);
                documents = documents.Where(d => !db.DocumentSheets.Any(s => s.DocumentId == d.Id && s.IsIncluded && participantRows.Contains(s.Id)));
            }
        }

        return documents;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentOwnerOption>> ListOwnersAsync(
        int? projectId,
        DocumentListFilter filter,
        IReadOnlyCollection<int> visibleProjectIds,
        string? nameQuery,
        int limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(visibleProjectIds);

        var allowedProjects = visibleProjectIds.ToArray();
        var documents = WhereOwnerDisclosed(VisibleDocuments(projectId, 0, filter, allowedProjects), filter);

        var owners = documents
            .GroupBy(d => d.CreatedByUserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            // ⚠ Анонімний тип, а не позиційний record: EF не перекладає доступ до властивості, заданої лише
            // конструктором (`OrderByDescending(o => o.Count)` над `new Row(a, b, c)`) — так вибір власників давав 500.
            .Join(db.Users, g => g.UserId, u => u.Id, (g, u) => new { g.UserId, u.DisplayName, g.Count });

        if (nameQuery is { Length: > 0 } term)
        {
            // Параметр, не склеєний рядок; регістр — за зіставленням бази (CI), метасимволи екрануються — як у пошуку документів.
            var pattern = "%" + term
                .Replace(@"\", @"\\", StringComparison.Ordinal)
                .Replace("%", @"\%", StringComparison.Ordinal)
                .Replace("_", @"\_", StringComparison.Ordinal)
                .Replace("[", @"\[", StringComparison.Ordinal) + "%";
            owners = owners.Where(o => EF.Functions.Like(o.DisplayName, pattern, @"\"));
        }

        var rows = await owners
            .OrderByDescending(o => o.Count)
            .ThenBy(o => o.DisplayName)
            .ThenBy(o => o.UserId)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(o => new DocumentOwnerOption(o.UserId, o.DisplayName, o.Count))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CompositionViolation>> ValidateCompositionAsync(
        int templateVersionId, IReadOnlyList<int> sheetDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sheetDefIds);

        var rules = await db.SheetGroupRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId)
            .OrderBy(r => r.Id)
            .Take(MaxRules)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rules.Count == 0)
        {
            return [];
        }

        var groups = await db.SheetDefs
            .AsNoTracking()
            .Where(s => s.TemplateVersionId == templateVersionId && s.SheetGroup != null)
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.SheetGroup })
            .Take(MaxSheets)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var chosen = sheetDefIds.ToHashSet();
        var violations = new List<CompositionViolation>();

        foreach (var rule in rules)
        {
            var inGroup = groups.Where(g => g.SheetGroup == rule.SheetGroup).Select(g => g.Id).ToList();
            if (inGroup.Count == 0)
            {
                continue;
            }

            var picked = inGroup.Count(chosen.Contains);

            // RequiresAll: група або входить цілком, або не входить зовсім.
            // Половина групи — це звіт, у якому частина форм просто відсутня,
            // і виявиться це вже в регуляторі.
            if (rule.RuleKind == RequiresAll && picked > 0 && picked < inGroup.Count)
            {
                violations.Add(new CompositionViolation(
                    rule.SheetGroup, rule.RuleKind,
                    $"обрано {picked} з {inGroup.Count}: група вимагає всі аркуші"));
            }

            if (rule.RuleKind == RequiresOne && picked == 0)
            {
                violations.Add(new CompositionViolation(
                    rule.SheetGroup, rule.RuleKind, "група вимагає хоча б один аркуш"));
            }
        }

        return violations;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SheetGroupRuleSummary>> GetGroupRulesAsync(
        int templateVersionId, CancellationToken ct)
    {
        var rules = await db.SheetGroupRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId)
            .OrderBy(r => r.Id)
            .Take(MaxRules)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rules.Select(r => new SheetGroupRuleSummary(r.SheetGroup, r.RuleKind, r.TargetGroup))];
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ `DAT-09`. <c>COUNT</c> + перевірка «чи вільний» — це TOCTOU: два
    /// одночасні <c>POST</c> у той самий проєкт бачать той самий стан бази і
    /// повертаються з ОДНАКОВИМ ключем. Унікальність тримає індекс, тож один
    /// із них програє на <c>UQ_Document</c>; переможеному
    /// <c>CreateDocumentHandler</c> дає ще кілька спроб.
    ///
    /// ⛔ Але самого повтору мало: сховище живе один HTTP-запит, тож ДРУГИЙ
    /// виклик цього методу в межах одного запиту — це за визначенням повтор
    /// після програшу. Усі програвші читають той самий (уже новий) <c>COUNT</c>
    /// і без розкиду зійшлися б на тому самому номері ще раз — і так щоразу,
    /// доки не скінчаться спроби. Тому з другого виклику початок пошуку
    /// зсувається випадково, і розкид росте з кожною спробою: десять
    /// одночасних створень розходяться за один-два повтори, а не
    /// вишиковуються в чергу довжиною в десять.
    ///
    /// ⚠ Перший виклик лишається строго послідовним (<c>COUNT + 1</c>):
    /// звичайне, неконкурентне створення документа отримує той самий
    /// впізнаваний номер, що й до цієї правки. Дірки в нумерації з'являються
    /// лише там, де без них був би <c>500</c> або відмова.
    ///
    /// ⛔ `R-17`. Номер рахувався як <c>COUNT + 1</c> — і ключ ВИДАЛЕНОГО
    /// документа видавався знову: видалили <c>P5-V3-0005</c>, наступний
    /// створений — знову <c>P5-V3-0005</c>. Той самий ключ в аудиті, у назвах
    /// експортів і в листуванні означав тепер два різні документи. Тепер
    /// номер лише зростає: від НАЙБІЛЬШОГО виданого (<see cref="HighestIssuedNumberAsync"/>),
    /// а не від кількості живих.
    /// </remarks>
    public async Task<string> NextBusinessKeyAsync(
        int projectId, int templateVersionId, CancellationToken ct)
    {
        // ⚠ Ключ будується з проєкту і порядкового номера, а не з GUID:
        // BusinessKey потрапляє в аудит і в назви експортів, і людина мусить
        // упізнавати його з першого погляду.
        var count = await db.Documents
            .AsNoTracking()
            .CountAsync(d => d.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        // ⚠ `Max`, а не саме лише найбільший номер: ключі, задані людиною
        // (`ChangeDocumentKeyHandler`), шаблону не мають і в номер не
        // рахуються, але й не мають зсунути нумерацію НАЗАД — кількість
        // документів лишається нижньою межею, як і було.
        var used = Math.Max(
            count,
            await HighestIssuedNumberAsync(projectId, ct).ConfigureAwait(false));

        // ⚠ Розкид на повторі — від ОДИНИЦІ, не від нуля: нульовий зсув
        // повернув би рівно той номер, на якому запит щойно програв, тобто
        // повтор без зсуву. Верхня межа росте з кожним повтором, щоб
        // розійшлися й ті, хто програв двічі.
        var retry = _keyRequests++;
        var spread = retry == 0
            ? 0
            : System.Random.Shared.Next(1, Math.Min(MaxKeySpread, KeySpreadStep << (retry - 1)) + 1);

        for (var attempt = used + 1 + spread; attempt < used + spread + MaxKeyAttempts; attempt++)
        {
            var candidate = string.Create(
                CultureInfo.InvariantCulture, $"P{projectId}-V{templateVersionId}-{attempt:D4}");

            if (!await db.Documents
                    .AnyAsync(d => d.ProjectId == projectId && d.BusinessKey == candidate, ct)
                    .ConfigureAwait(false))
            {
                return candidate;
            }
        }

        // Унікальність тримає індекс; сюди можна дійти лише якщо хтось створює
        // документи швидше, ніж ми перебираємо номери.
        throw new Application.Errors.BusinessRuleException(
            "ECR-DOC-0409", "Не вдалося підібрати вільний бізнес-ключ документа.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-DOC-0409.businessKeyExhausted" });
    }

    /// <summary>
    /// Найбільший порядковий номер, який проєкт УЖЕ видавав документу, —
    /// живому, видаленому чи перейменованому (`R-17`).
    /// </summary>
    /// <remarks>
    /// ⚠ Без міграції схеми, і це свідомо: окремий лічильник означав би нову
    /// таблицю чи колонку, а слід кожного виданого номера вже є. Живі
    /// документи несуть його в <c>BusinessKey</c>; видалені й перейменовані —
    /// у журналі безпеки (<c>aud.SecurityEvent</c>, події
    /// <c>DocumentDeleted</c> з <c>businessKey</c> і <c>DocumentKeyChanged</c>
    /// з <c>oldKey</c> — `DeleteDocumentHandler`, `ChangeDocumentKeyHandler`).
    ///
    /// ⚠ Межа, названа вголос: журнал пишеться ПІСЛЯ коміту видалення, і
    /// архівація (`ArchiveJob`) з часом переносить старі події з
    /// <c>aud.SecurityEvent</c>. Номер, чий слід уже заархівовано або не
    /// записався через збій журналу, знову стає вільним. Повністю закрити це
    /// може лише власний лічильник — тобто міграція, якої ця правка не додає.
    ///
    /// ⚠ Подій такого роду — одиниці (видалення чернетки й зміна ключа —
    /// рідкісні ручні дії), тож читання без предиката <c>ChangedAt</c> по всіх
    /// партиціях журналу коштує мало, а обмежити його вікном означало б знову
    /// видавати номер, видалений давніше за вікно.
    /// </remarks>
    private async Task<int> HighestIssuedNumberAsync(int projectId, CancellationToken ct)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"P{projectId}-V");

        var live = await db.Documents
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.BusinessKey.StartsWith(prefix))
            .Select(d => d.BusinessKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var project = projectId.ToString(CultureInfo.InvariantCulture);

        var released = await db.Database
            .SqlQuery<string>($"""
                SELECT JSON_VALUE(e.DetailsJson, '$.businessKey') AS [Value]
                  FROM aud.SecurityEvent AS e
                 WHERE e.EventType = {DeletedEventType}
                   AND JSON_VALUE(e.DetailsJson, '$.projectId') = {project}
                UNION ALL
                SELECT JSON_VALUE(e.DetailsJson, '$.oldKey') AS [Value]
                  FROM aud.SecurityEvent AS e
                 WHERE e.EventType = {KeyChangedEventType}
                   AND JSON_VALUE(e.DetailsJson, '$.projectId') = {project}
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var highest = 0;

        foreach (var key in live.Concat(released))
        {
            if (NumberOf(key, prefix) is { } number && number > highest)
            {
                highest = number;
            }
        }

        return highest;
    }

    /// <summary>
    /// Порядковий номер із ключа <c>P{проєкт}-V{версія}-{номер}</c>; <c>null</c> —
    /// ключ іншого вигляду (заданий людиною) або чужого проєкту.
    /// </summary>
    public static int? NumberOf(string? businessKey, string prefix)
    {
        if (businessKey is null || !businessKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var dash = businessKey.IndexOf('-', prefix.Length);
        if (dash <= prefix.Length || dash == businessKey.Length - 1)
        {
            return null;
        }

        // ⚠ Між `V` і дефісом — лише цифри версії: `P5-V3x-0007` — не наш ключ.
        for (var i = prefix.Length; i < dash; i++)
        {
            if (!char.IsAsciiDigit(businessKey[i]))
            {
                return null;
            }
        }

        return int.TryParse(
            businessKey.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>Подія журналу про видалення документа — `DeleteDocumentHandler.DeletedEventType`.</summary>
    private const string DeletedEventType = Application.Documents.DeleteDocumentHandler.DeletedEventType;

    /// <summary>Подія журналу про зміну ключа — `ChangeDocumentKeyHandler.EventType`.</summary>
    private const string KeyChangedEventType = Application.Documents.ChangeDocumentKeyHandler.EventType;

    /// <inheritdoc />
    /// <remarks>
    /// ✎ RPT-STABLE B2: склад документа тримає аркуші ЧИННОЇ версії, а період до X після переносу «з місяця» - аркуші
    /// старої версії. Тому аркуш «у складі», якщо в складі є аркуш з ТИМ САМИМ кодом (той самий один запит). Чи належить
    /// аркуш версії саме цього періоду, перевіряє вже подання під блокуванням (знімок версії періоду).
    /// </remarks>
    public async Task<bool> HasSheetAsync(long documentId, int sheetDefId, CancellationToken ct)
        => await db.DocumentSheets
            .AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.IsIncluded)
            .Join(db.SheetDefs, s => s.SheetDefId, d => d.Id, (s, d) => new { s.SheetDefId, d.Code })
            .AnyAsync(
                x => x.SheetDefId == sheetDefId
                     || x.Code == db.SheetDefs.Where(r => r.Id == sheetDefId).Select(r => r.Code).FirstOrDefault(),
                ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>Той самий збіг за кодом, що й <see cref="HasSheetAsync"/> (перенос версії міняє <c>SheetDefId</c>).</remarks>
    public async Task<bool> HasSheetCopyAsync(long documentId, int sheetDefId, long sheetCopyId, CancellationToken ct)
        => await db.DocumentSheets
            .AsNoTracking()
            .Where(s => s.Id == sheetCopyId && s.DocumentId == documentId && s.CopyOfId != null && s.IsIncluded)
            .Join(db.SheetDefs, s => s.SheetDefId, d => d.Id, (s, d) => new { s.SheetDefId, d.Code })
            .AnyAsync(
                x => x.SheetDefId == sheetDefId
                     || x.Code == db.SheetDefs.Where(r => r.Id == sheetDefId).Select(r => r.Code).FirstOrDefault(),
                ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetIncludedSheetIdsAsync(long documentId, CancellationToken ct)
        => await db.DocumentSheets
            .AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.IsIncluded)
            .Select(s => s.SheetDefId)
            .Distinct()   // RS: копії діляться SheetDefId з оригіналом
            .OrderBy(id => id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int?> FindProjectIdAsync(long documentId, CancellationToken ct)
        => await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => (int?)d.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int?> FindProjectTemplateVersionIdAsync(int projectId, CancellationToken ct)
        => await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => (int?)p.TemplateVersionId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Той самий блок, що й <c>DocumentVersionMigrationStore.LockProjectVersionAsync</c>:
    /// <c>UPDLOCK, HOLDLOCK, ROWLOCK</c> на рядку <c>doc.Project</c>. Версія читається цим самим
    /// оператором — під RCSI хінт блокування змушує читати зафіксоване, а не знімок.
    /// </remarks>
    public async Task<int?> LockProjectTemplateVersionAsync(int projectId, CancellationToken ct)
    {
        _ = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "LockProjectTemplateVersionAsync викликано поза транзакцією: блок рядка проєкту звільнився б одразу.");

        var versions = await db.Database
            .SqlQuery<int>($"""
                SELECT TemplateVersionId AS Value
                FROM   doc.Project WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                WHERE  Id = {projectId}
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return versions.Count == 0 ? null : versions[0];
    }

    /// <inheritdoc />
    public async Task<Domain.Enums.ProjectStatus?> FindProjectStatusAsync(int projectId, CancellationToken ct)
        => await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => (Domain.Enums.ProjectStatus?)p.Status)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> GetTemplateVersionIdAsync(long documentId, CancellationToken ct)
    {
        // Один запит через увесь ланцюг: документ → проєкт. Версія живе на
        // проєкті — той самий ланцюг, що в `RowStore.ResolveTableInstanceAsync`.
        var found = await (
            from document in db.Documents.AsNoTracking()
            where document.Id == documentId
            join project in db.Projects.AsNoTracking() on document.ProjectId equals project.Id
            select (int?)project.TemplateVersionId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return found ?? throw new Ecr.Application.Errors.NotFoundException(
            "ECR-DOC-0404",
            $"Документ {documentId} не знайдено.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0404.document",
                ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <inheritdoc />
    public async Task<int> GetTemplateVersionIdForPeriodAsync(long documentId, int periodKey, CancellationToken ct)
    {
        // ✎ RPT-STABLE B2: той самий один запит, але версія - ПЕРІОДУ (doc.fn_VersionOf), а не чинна версія проєкту.
        var found = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => (int?)EcrDbContext.VersionOf(d.ProjectId, periodKey))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return found ?? throw new Ecr.Application.Errors.NotFoundException(
            "ECR-DOC-0404",
            $"Документ {documentId} не знайдено.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0404.document",
                ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ `DAT-01`. Раніше документ брався ВІДСТЕЖУВАНИМ, і «дотик» їхав у
    /// базу звичайним <c>SaveChanges</c> — тобто
    /// <c>UPDATE doc.Document … WHERE RowVersion = @rv</c>, бо
    /// <c>RowVersion</c> оголошено <c>IsRowVersion()</c>
    /// (<c>DocumentConfiguration.cs:165</c>). Наслідок: два оператори, що
    /// пишуть у РІЗНІ таблиці одного документа, конфліктували на порожньому
    /// місці. Другий <c>PATCH</c> чекав X-замка на рядку документа, після
    /// коміту першого отримував 0 оновлених рядків →
    /// <c>DbUpdateConcurrencyException</c> → <c>ECR-CELL-0409</c>, і його
    /// транзакція відкочувалася ЦІЛКОМ. Рядок документа був точкою
    /// серіалізації всіх операторів документа.
    ///
    /// ⛔ Тому тут <c>ExecuteUpdateAsync</c> БЕЗ читання і БЕЗ предиката
    /// версії: «дотик» — це не правка документа, за яку хтось змагається, а
    /// відмітка часу. <c>RowVersion</c> документа лишається чинним для
    /// СПРАВЖНІХ правок документа (склад аркушів, ім'я) — його не прибрано.
    ///
    /// ⚠ Вікно <see cref="TouchWindowSeconds"/>: якщо документ уже позначено
    /// щойно, оператор не бере X-замок на його рядок узагалі — 0 оновлених
    /// рядків тут НЕ помилка, а саме те, чого ми хочемо. Дата зміни при цьому
    /// не бреше більш ніж на це вікно.
    ///
    /// ⚠ Документа немає — тихо нічого (0 рядків). Зміна комірок неіснуючого
    /// документа відхиляється раніше, зовнішнім ключем; кидати ще й тут
    /// означало б повідомляти про ту саму помилку двічі й різними словами.
    ///
    /// ⚠ <c>ExecuteUpdateAsync</c> приєднується до вже відкритої
    /// ambient-транзакції того самого <c>DbContext</c> (`Q-243`), тобто на
    /// гарячому шляху (<c>PatchCellsHandler</c>) «дотик» і далі комітиться
    /// разом із даними. Поза транзакцією (<c>CreateRowHandler</c>) він
    /// автокомітний — рівно як сусідній <c>RowStore.TouchRowsAsync</c>, який
    /// у тому самому обробнику вже виконався до цього рядка.
    /// </remarks>
    public Task TouchAsync(long documentId, int userId, DateTime utcNow, CancellationToken ct)
    {
        // Поріг рахується ДО виразу: `utcNow.AddSeconds(-5)` усередині дерева
        // виразів EF довелося б перекладати в SQL, а тут це просто константа.
        var threshold = utcNow.AddSeconds(-TouchWindowSeconds);

        return db.Documents
            .Where(d => d.Id == documentId && d.ModifiedAt < threshold)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(d => d.ModifiedAt, utcNow)
                    .SetProperty(d => d.ModifiedByUserId, userId),
                ct);
    }

    /// <summary>
    /// Стан КОЖНОГО аркуша складу за період: рядок <c>wf.ApprovalState</c>, а
    /// де його немає — <c>Draft</c>. ЄДИНЕ місце, звідки стан аркуша беруть і
    /// картка документа (<see cref="StatesAsync"/>), і сторінка переліку
    /// (<see cref="StatesBatchAsync"/>).
    /// </summary>
    /// <remarks>
    /// ⛔ U-03. Перелік показував «—» на документі, який смуга над ним рахувала
    /// як «1 Draft», а фільтр <c>State = Draft</c> — повертав: три відповіді про
    /// стан одного документа на одному екрані. Причина — джерелом рядків тут був
    /// <c>wf.ApprovalState</c>, тобто аркуш БЕЗ рядка стану просто не потрапляв у
    /// словник, тоді як <see cref="WhereState"/> і
    /// <c>DocumentListSummaryStore</c> обидва рахують такий аркуш чернеткою
    /// (рядок стану з'являється лише з першим поданням, <c>S-17</c>).
    ///
    /// ⛔ Тому джерело рядків — СКЛАД документа (<c>doc.DocumentSheet</c>,
    /// <c>IsIncluded</c>), рівно як у тих двох; рядок стану лише ДОповнює його.
    /// Це не четверте правило, а те саме, записане на шляху читання: щоб два
    /// місця не розійшлися знову, обидва методи читають цей один запит.
    ///
    /// ⚠ Наслідок, який називаю прямо: рядок <c>ApprovalState</c> для аркуша
    /// ПОЗА складом (аркуш вилучили після подання) у словник більше не
    /// потрапляє. Так і має бути — і смуга, і фільтр його теж не бачать, а
    /// показувати стан аркуша, якого в документі немає, означало б четверту
    /// відповідь замість третьої.
    ///
    /// ⚠ Корельований підзапит, а не <c>LEFT JOIN</c> у LINQ: він перекладається
    /// в один <c>OUTER APPLY</c>, тобто запит лишається ОДИН на всю сторінку
    /// (<c>Q-167</c>) — жодного циклу по документах.
    ///
    /// ⚠ <c>PeriodKey</c> стоїть у предикаті партиційованої <c>wf.ApprovalState</c>
    /// (урок <c>WR-05</c>).
    ///
    /// ⚠ Назва й порядок аркуша (<c>SheetDef.NameL10n</c>, <c>Ordinal</c>) —
    /// з ТОГО Ж з'єднання з <c>cfg.SheetDef</c>, що вже дає код: перелік
    /// отримує назви без жодного додаткового запиту (<c>Q-167</c>). Сортування
    /// — у SQL, до <c>Take</c>: стеля обрізає хвіст, а не випадкові аркуші.
    /// </remarks>
    private IQueryable<SheetStateRow> SheetStatesQuery(long[] documentIds, int periodKey)
        => db.DocumentSheets
            .AsNoTracking()
            .Where(s => documentIds.Contains(s.DocumentId) && s.IsIncluded)
            .Join(
                db.SheetDefs,
                s => s.SheetDefId,
                d => d.Id,
                (s, d) => new
                {
                    RowId = s.Id, s.DocumentId, s.SheetDefId, s.CopyOfId, s.CopyOrdinal, s.NameOverrideL10n,
                    d.Code, d.NameL10n, d.Ordinal,
                })
            .OrderBy(s => s.DocumentId)
            .ThenBy(s => s.Ordinal)
            .ThenBy(s => s.Code)
            .ThenBy(s => s.CopyOrdinal)
            .Select(s => new SheetStateRow(
                s.DocumentId,
                s.Code,
                s.NameL10n,
                // ⛔ RS: стан СВОГО рядка складу — копія має власний `wf.ApprovalState` (SheetCopyId = Id рядка), оригінал — без копії.
                db.ApprovalStates
                    .Where(a => a.DocumentId == s.DocumentId
                                && a.SheetDefId == s.SheetDefId
                                && a.PeriodKey == periodKey
                                && (a.SheetCopyId == s.RowId || (a.SheetCopyId == null && s.CopyOfId == null)))
                    .Select(a => (DocumentStatus?)a.Status)
                    .FirstOrDefault(),
                s.RowId, s.SheetDefId, s.CopyOfId, s.CopyOrdinal, s.NameOverrideL10n));

    /// <summary>Аркуш складу, його назва і стан; <c>null</c> — рядка стану ще немає. Копія — окремий рядок (RS).</summary>
    private sealed record SheetStateRow(
        long DocumentId, string Code, LocalizedText NameL10n, DocumentStatus? Status,
        long RowId, int SheetDefId, long? CopyOfId, short CopyOrdinal, LocalizedText? NameOverride);

    /// <summary>Рядки запиту → аркуші контракту; аркуш без рядка стану — <c>Draft</c> (<c>U-03</c>).</summary>
    /// <remarks>
    /// ⛔ RS: оригінал має ключ <c>КОД</c>, копія — <c>КОД#sheetCopyId</c> (<c>SheetCopyNaming.StateKey</c>), назва —
    /// «&lt;Назва&gt; N» (оригінал групи з копіями — «&lt;Назва&gt; 1»; назва групи — з оригінала, інакше з шаблону).
    /// </remarks>
    private static List<DocumentSheetState> ToSheets(IReadOnlyList<SheetStateRow> rows)
    {
        var overrides = rows.Where(r => r.CopyOfId is null)
            .GroupBy(r => (r.DocumentId, r.SheetDefId))
            .ToDictionary(g => g.Key, g => g.First().NameOverride);
        var withCopies = rows.Where(r => r.CopyOfId is not null).Select(r => (r.DocumentId, r.SheetDefId)).ToHashSet();

        return
        [
            .. rows.Select(r => new DocumentSheetState(
                SheetCopyNaming.StateKey(r.Code, r.CopyOfId is null ? null : r.RowId),
                SheetCopyNaming.Display(
                    SheetCopyNaming.Effective(r.NameL10n, overrides.GetValueOrDefault((r.DocumentId, r.SheetDefId))),
                    r.CopyOrdinal, withCopies.Contains((r.DocumentId, r.SheetDefId))),
                (r.Status ?? DocumentStatus.Draft).ToString())),
        ];
    }

    /// <summary>
    /// Словник «код → стан» із тих самих аркушів — щоб <c>SheetStates</c> і
    /// <c>Sheets</c> не могли розійтися: друге поле не має власного джерела.
    /// </summary>
    private static Dictionary<string, string> ToStateMap(IReadOnlyList<DocumentSheetState> sheets)
        => sheets.ToDictionary(s => s.Code, s => s.State, StringComparer.Ordinal);

    /// <summary>Стан аркушів за період; порожньо, якщо період не вказано.</summary>
    /// <remarks>
    /// ⛔ Q-271. Ключ словника — <c>SheetDef.Code</c>, а НЕ числовий
    /// <c>SheetDefId</c>: саме так задокументовано контракт у
    /// <c>GetDocumentTablesHandler.DocumentTableDto.SheetCode</c>
    /// ("він же ключ у DocumentSummary.SheetStates"), і саме за кодом аркуша
    /// читає словник фронтенд (`DocumentPage.tsx`: `sheetStates[s.code]`).
    /// Ключ за `SheetDefId.ToString()` (число-рядок) НІКОЛИ не збігається з
    /// кодом аркуша — пошук у фронтенді завжди промахувався, і бейдж
    /// статусу подання/затвердження не оновлювався НІКОЛИ, попри те що сам
    /// запит `/submit`/`/approve` спрацьовував і стан у базі мінявся.
    /// </remarks>
    private async Task<IReadOnlyList<DocumentSheetState>> StatesAsync(
        long documentId, PeriodKeyFilter period, CancellationToken ct)
    {
        if (period.Value is not { } periodKey)
        {
            return [];
        }

        var states = await SheetStatesQuery([documentId], periodKey)
            .Take(MaxSheets)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return ToSheets(states);
    }

    /// <summary>Стан аркушів кількох документів ОДНИМ запитом; порожньо, якщо період не вказано.</summary>
    /// <remarks>
    /// ⛔ Q-167 (аудит фази 2, продуктивність). Той самий стан, що й
    /// <see cref="StatesAsync"/> — буквально той самий запит
    /// (<see cref="SheetStatesQuery"/>), лише для сторінки документів разом:
    /// `WHERE DocumentId IN (...)`, згруповано на клієнті, а не запит на
    /// кожен документ сторінки.
    ///
    /// ⚠ Документ БЕЗ жодного аркуша складу у словнику відсутній — як і
    /// раніше. Рядка для нього тут узяти нізвідки: словник — «аркуш → стан»,
    /// а аркушів немає.
    /// </remarks>
    private async Task<IReadOnlyDictionary<long, IReadOnlyList<DocumentSheetState>>> StatesBatchAsync(
        IReadOnlyList<long> documentIds, PeriodKeyFilter period, CancellationToken ct)
    {
        if (period.Value is not { } periodKey || documentIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<DocumentSheetState>>();
        }

        // Масив, а не `IReadOnlyList`: `Contains` над масивом EF перекладає в
        // `IN (...)`, над інтерфейсом — не гарантовано (та сама причина, що в
        // `ListAsync` для переліку проєктів).
        var states = await SheetStatesQuery([.. documentIds], periodKey)
            .Take(documentIds.Count * MaxSheets)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⚠ `GroupBy` на клієнті зберігає порядок рядків усередині групи —
        // тобто порядок аркушів із SQL (`Ordinal`).
        return states
            .GroupBy(s => s.DocumentId)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<DocumentSheetState> (g) => ToSheets([.. g]));
    }

    /// <summary>Лічильники ОСТАННЬОГО підсумку перевірки для сторінки документів — одним запитом.</summary>
    /// <remarks>
    /// ⚠ Читання збереженого підсумку, не повторний прогін (<c>BE-09</c>).
    /// Документа без підсумку у словнику НЕМАЄ — і саме це дає <c>null</c>.
    /// Два підсумки з однаковим <c>RunAt</c> — рідкість; береться пізніше записаний (більший <c>Id</c>).
    /// </remarks>
    private async Task<IReadOnlyDictionary<long, LatestFindings>> LatestFindingsBatchAsync(
        IReadOnlyList<long> documentIds, PeriodKeyFilter period, CancellationToken ct)
    {
        if (period.Value is not { } periodKey || documentIds.Count == 0)
        {
            return new Dictionary<long, LatestFindings>();
        }

        var latest = await db.ValidationResults
            .AsNoTracking()
            .Where(v => documentIds.Contains(v.DocumentId)
                        && v.PeriodKey == periodKey
                        && v.RunAt == db.ValidationResults
                            .Where(x => x.DocumentId == v.DocumentId && x.PeriodKey == periodKey)
                            .Max(x => x.RunAt))
            // ⚠ Порядок визначає, КОТРИЙ із двох підсумків з однаковим `RunAt`
            // бере `g.First()` нижче: пізніше записаний (більший `Id`), а не
            // той, що план запиту віддав першим (EF 10102).
            .OrderBy(v => v.DocumentId)
            .ThenByDescending(v => v.Id)
            .Select(v => new LatestFindings(v.DocumentId, v.ErrorCount, v.WarningCount))
            .Take(documentIds.Count * MaxRunTies)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return latest
            .GroupBy(f => f.DocumentId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>Фільтр зведеного стану документа за період (<c>BE-09b</c>).</summary>
    /// <remarks>
    /// ⛔ Правило — ТЕ САМЕ, що в смузі (<see cref="DocumentListSummaryStore"/>):
    /// аркуш складу без рядка стану — чернетка; хоч один відхилений — Rejected;
    /// Approved — усі аркуші. Інше правило дало б фільтр, що розходиться з цифрою
    /// над таблицею.
    /// <para>
    /// ✎ UAT-recalc-ux: <c>state = null</c> — «частково подано»: є поданий/затверджений і є неподаний видимий аркуш,
    /// жодного відхиленого. <c>Draft</c> — жоден видимий аркуш не поданий і не затверджений.
    /// </para>
    /// </remarks>
    private IQueryable<Document> WhereState(
        IQueryable<Document> documents, DocumentStatus? state, int periodKey,
        IReadOnlyCollection<(int ProjectId, int Id)>? hiddenSheetDefIds,
        IReadOnlyCollection<(long DocumentId, int SheetDefId)>? hiddenParticipantSheets)
    {
        // ⛔ Схований від читача аркуш не бере участі в зведеному стані (ту саму межу накладає смуга):
        // інакше `state=Rejected` знаходив би документ, відхилений лише схованим аркушем.
        // ⛔ N1-01: схований — ПАРА «проєкт, аркуш»: версія шаблону спільна для кількох проєктів, а `Deny Sheet`
        // може стояти лише в A. Плаский `SheetDefId IN (...)` ховав би цей аркуш і в документах B.
        var sheets = db.DocumentSheets.Where(s => s.IsIncluded);
        if (hiddenSheetDefIds is { Count: > 0 })
        {
            var hiddenRows = HiddenSheetRows(hiddenSheetDefIds);
            sheets = sheets.Where(s => !hiddenRows.Contains(s.Id));
        }

        // ✎ D-444 (AN-131): документ-надання учасника має ВЛАСНІ межі — пари «документ, аркуш» (та сама межа, що
        // `ParticipantSummaryScope.HiddenSheets` смуги). Для документа проєкту з роллю вони не ширші за межі проєкту.
        if (hiddenParticipantSheets is { Count: > 0 })
        {
            var participantRows = HiddenParticipantSheetRows(hiddenParticipantSheets);
            sheets = sheets.Where(s => !participantRows.Contains(s.Id));
        }

        var states = db.ApprovalStates.Where(a => a.PeriodKey == periodKey);

        Expression<Func<Document, bool>> rejected = d => sheets.Any(s => s.DocumentId == d.Id
            && states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null)) && a.Status == DocumentStatus.Rejected));
        Expression<Func<Document, bool>> draftSheet = d => sheets.Any(s => s.DocumentId == d.Id
            && !states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null)) && a.Status != DocumentStatus.Draft));

        Expression<Func<Document, bool>> sentSheet = d => sheets.Any(s => s.DocumentId == d.Id
            && states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null))
                && (a.Status == DocumentStatus.Submitted || a.Status == DocumentStatus.Approved)));

        return state switch
        {
            DocumentStatus.Rejected => documents.Where(rejected),

            // ✎ UAT-recalc-ux: Draft — ЖОДЕН видимий аркуш не поданий і не затверджений (частково подані — окремо).
            DocumentStatus.Draft => documents.Where(Not(rejected)).Where(Not(sentSheet)),
            null => documents.Where(Not(rejected)).Where(draftSheet).Where(sentSheet),
            DocumentStatus.Submitted => documents.Where(Not(rejected)).Where(Not(draftSheet))
                .Where(d => sheets.Any(s => s.DocumentId == d.Id
                    && states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null)) && a.Status == DocumentStatus.Submitted))),
            DocumentStatus.Approved => documents
                .Where(d => sheets.Any(s => s.DocumentId == d.Id))
                .Where(d => !sheets.Any(s => s.DocumentId == d.Id
                    && !states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null)) && a.Status == DocumentStatus.Approved))),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Невідомий стан документа."),
        };
    }

    /// <summary>Рядки складу, видимі читачеві: та сама межа, що в <see cref="WhereState"/> і смузі (S18, N1-01, D-444).</summary>
    private IQueryable<Domain.Entities.Documents.DocumentSheet> VisibleSheets(
        IReadOnlyCollection<(int ProjectId, int Id)>? hiddenSheetDefIds,
        IReadOnlyCollection<(long DocumentId, int SheetDefId)>? hiddenParticipantSheets)
    {
        var sheets = db.DocumentSheets.Where(s => s.IsIncluded);
        if (hiddenSheetDefIds is { Count: > 0 })
        {
            var hiddenRows = HiddenSheetRows(hiddenSheetDefIds);
            sheets = sheets.Where(s => !hiddenRows.Contains(s.Id));
        }

        if (hiddenParticipantSheets is { Count: > 0 })
        {
            var participantRows = HiddenParticipantSheetRows(hiddenParticipantSheets);
            sheets = sheets.Where(s => !participantRows.Contains(s.Id));
        }

        return sheets;
    }

    /// <summary>
    /// ✎ g2 (макет 08.11): документи проєктів, що мають хоч один період у році <paramref name="year"/> — межа переліку
    /// за рік (той самий предикат, що в <c>DocumentListSummaryStore.SummarizeYearAsync</c>).
    /// </summary>
    private IQueryable<Document> WhereProjectYear(IQueryable<Document> documents, int year)
    {
        var from = year * 100;
        var to = (year + 1) * 100;

        return documents.Where(d => db.Periods.Any(p => p.ProjectId == d.ProjectId
                                                        && p.PeriodKeyValue > from && p.PeriodKeyValue < to));
    }

    /// <summary>
    /// ✎ 2026-11-07 Period clarity (п. e2): фільтр стану документа за РІК без періоду.
    /// </summary>
    /// <remarks>
    /// ⛔ Правило року — ТЕ САМЕ п'ятистанове правило, що й за місяць (<see cref="WhereState"/>), лише над
    /// клітинками «видимий аркуш складу × РОЗПОЧАТИЙ період проєкту в році» (стан періоду не <c>Scheduled</c>):
    /// <c>Rejected</c> — хоч одна клітинка повернута; <c>Draft</c> — жодна не подана/затверджена; <c>PartlySubmitted</c> —
    /// є подана/затверджена і є неподана; <c>Submitted</c> — неподаних немає, хоч одна лише подана; <c>Approved</c> — усі
    /// затверджені. Майбутні (<c>Scheduled</c>) місяці не тягнуть рік у «частково» — у них ще нема чого подавати.
    /// </remarks>
    private IQueryable<Document> WhereYearState(
        IQueryable<Document> documents, DocumentStatus? state, int year,
        IReadOnlyCollection<(int ProjectId, int Id)>? hiddenSheetDefIds,
        IReadOnlyCollection<(long DocumentId, int SheetDefId)>? hiddenParticipantSheets)
    {
        var sheets = VisibleSheets(hiddenSheetDefIds, hiddenParticipantSheets);
        var from = year * 100;
        var to = (year + 1) * 100;
        var periods = db.Periods.Where(p => p.PeriodKeyValue > from && p.PeriodKeyValue < to && p.State != PeriodState.Scheduled);
        var states = db.ApprovalStates.Where(a => a.PeriodKey > from && a.PeriodKey < to);

        // Клітинка стану — рядок `wf.ApprovalState` аркуша складу в розпочатому періоді проєкту документа.
        Expression<Func<Document, bool>> anyCell = d => sheets.Any(s => s.DocumentId == d.Id)
            && periods.Any(p => p.ProjectId == d.ProjectId);
        Expression<Func<Document, bool>> rejected = d => sheets.Any(s => s.DocumentId == d.Id
            && states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null))
                && a.Status == DocumentStatus.Rejected && periods.Any(p => p.ProjectId == d.ProjectId && p.PeriodKeyValue == a.PeriodKey)));
        Expression<Func<Document, bool>> sentCell = d => sheets.Any(s => s.DocumentId == d.Id
            && states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null))
                && (a.Status == DocumentStatus.Submitted || a.Status == DocumentStatus.Approved)
                && periods.Any(p => p.ProjectId == d.ProjectId && p.PeriodKeyValue == a.PeriodKey)));
        Expression<Func<Document, bool>> submittedCell = d => sheets.Any(s => s.DocumentId == d.Id
            && states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null))
                && a.Status == DocumentStatus.Submitted && periods.Any(p => p.ProjectId == d.ProjectId && p.PeriodKeyValue == a.PeriodKey)));
        Expression<Func<Document, bool>> draftCell = d => sheets.Any(s => s.DocumentId == d.Id
            && periods.Any(p => p.ProjectId == d.ProjectId
                && !states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && a.PeriodKey == p.PeriodKeyValue
                    && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null)) && a.Status != DocumentStatus.Draft)));
        Expression<Func<Document, bool>> notApprovedCell = d => sheets.Any(s => s.DocumentId == d.Id
            && periods.Any(p => p.ProjectId == d.ProjectId
                && !states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && a.PeriodKey == p.PeriodKeyValue
                    && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null)) && a.Status == DocumentStatus.Approved)));

        return state switch
        {
            DocumentStatus.Rejected => documents.Where(rejected),
            DocumentStatus.Draft => documents.Where(Not(rejected)).Where(Not(sentCell)),
            null => documents.Where(Not(rejected)).Where(draftCell).Where(sentCell),
            DocumentStatus.Submitted => documents.Where(Not(rejected)).Where(Not(draftCell)).Where(submittedCell),
            DocumentStatus.Approved => documents.Where(anyCell).Where(Not(notApprovedCell)),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Невідомий стан документа."),
        };
    }

    /// <summary>
    /// ✎ 2026-11-07 Period clarity (п. e2): стан погодження кожного документа сторінки за КОЖЕН період проєкту в році —
    /// ОДНИМ запитом (без N+1).
    /// </summary>
    /// <remarks>
    /// Визначення стану клітинки «документ × період» — те саме, що у фільтра <c>state</c> і смуги (лише видимі аркуші
    /// складу, S18): повернутий → <c>Rejected</c>; жодного поданого/затвердженого → <c>Draft</c>; є і поданий, і неподаний →
    /// <c>PartlySubmitted</c>; усі затверджені → <c>Approved</c>; інакше <c>Submitted</c>. Смуга показує всі періоди року
    /// (і <c>Scheduled</c> — як <c>Draft</c>), фільтр року бере лише розпочаті.
    /// </remarks>
    private async Task<IReadOnlyDictionary<long, IReadOnlyList<DocumentPeriodState>>> PeriodStatesBatchAsync(
        long[] documentIds, int year, DocumentListFilter filter, CancellationToken ct)
    {
        if (documentIds.Length == 0)
        {
            return new Dictionary<long, IReadOnlyList<DocumentPeriodState>>();
        }

        var sheets = VisibleSheets(filter.HiddenSheetDefIds, filter.Participation?.HiddenSheets);
        var from = year * 100;
        var to = (year + 1) * 100;
        var states = db.ApprovalStates.Where(a => a.PeriodKey > from && a.PeriodKey < to);

        var rows = await db.Documents
            .AsNoTracking()
            .Where(d => documentIds.Contains(d.Id))
            .Join(
                db.Periods.Where(p => p.PeriodKeyValue > from && p.PeriodKeyValue < to),
                d => d.ProjectId,
                p => p.ProjectId,
                (d, p) => new { DocumentId = d.Id, PeriodKey = p.PeriodKeyValue })
            .Select(c => new
            {
                c.DocumentId,
                c.PeriodKey,
                Total = sheets.Count(s => s.DocumentId == c.DocumentId),
                Rejected = sheets.Count(s => s.DocumentId == c.DocumentId
                    && states.Any(a => a.DocumentId == c.DocumentId && a.SheetDefId == s.SheetDefId && a.PeriodKey == c.PeriodKey
                        && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null)) && a.Status == DocumentStatus.Rejected)),
                Submitted = sheets.Count(s => s.DocumentId == c.DocumentId
                    && states.Any(a => a.DocumentId == c.DocumentId && a.SheetDefId == s.SheetDefId && a.PeriodKey == c.PeriodKey
                        && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null)) && a.Status == DocumentStatus.Submitted)),
                Approved = sheets.Count(s => s.DocumentId == c.DocumentId
                    && states.Any(a => a.DocumentId == c.DocumentId && a.SheetDefId == s.SheetDefId && a.PeriodKey == c.PeriodKey
                        && (a.SheetCopyId == s.Id || (a.SheetCopyId == null && s.CopyOfId == null)) && a.Status == DocumentStatus.Approved)),
            })
            .OrderBy(r => r.DocumentId)
            .ThenBy(r => r.PeriodKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.DocumentId)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<DocumentPeriodState> (g) => [.. g.Select(r => new DocumentPeriodState(
                    r.PeriodKey, CellState(r.Total, r.Rejected, r.Submitted, r.Approved)))]);
    }

    /// <summary>П'ять станів документа за лічильниками аркушів (те саме правило, що й <see cref="WhereState"/>).</summary>
    internal static string CellState(int total, int rejected, int submitted, int approved)
    {
        if (rejected > 0)
        {
            return nameof(DocumentStatus.Rejected);
        }

        if (submitted + approved == 0)
        {
            return nameof(DocumentStatus.Draft);
        }

        if (total - submitted - approved > 0)
        {
            return Application.Documents.ListDocumentsHandler.StatePartlySubmitted;
        }

        return approved == total ? nameof(DocumentStatus.Approved) : nameof(DocumentStatus.Submitted);
    }

    /// <summary>
    /// Рядки складу (<c>doc.DocumentSheet.Id</c>), де аркуш схований від читача В ПРОЄКТІ документа (N1-01).
    /// </summary>
    /// <remarks>
    /// ⚠ Сирий SQL із парами JSON-ом (<c>OPENJSON … WITH (p, i)</c>): пару «проєкт, аркуш» LINQ у `IN`
    /// не перекладає, а предикат — той самий <c>h.p = d.ProjectId</c>, що в смузі
    /// (<c>DocumentListSummaryStore</c>). Лишається <c>IQueryable</c> — компонується підзапитом.
    /// </remarks>
    private IQueryable<long> HiddenSheetRows(IReadOnlyCollection<(int ProjectId, int Id)> hidden)
    {
        var pairs = PairsJson(hidden);

        return db.Database
            .SqlQuery<long>($"""
                SELECT ds.Id AS Value
                  FROM doc.DocumentSheet AS ds
                  JOIN doc.Document AS d ON d.Id = ds.DocumentId
                  JOIN OPENJSON({pairs}) WITH (p int '$.p', i int '$.i') AS h
                    ON h.p = d.ProjectId AND h.i = ds.SheetDefId
                """);
    }

    /// <summary>D-444: рядки складу (<c>doc.DocumentSheet.Id</c>) за парами «документ, аркуш», схованими від учасника.</summary>
    private IQueryable<long> HiddenParticipantSheetRows(IReadOnlyCollection<(long DocumentId, int SheetDefId)> hidden)
    {
        var pairs = JsonSerializer.Serialize(hidden.Select(x => new { d = x.DocumentId, i = x.SheetDefId }));

        return db.Database
            .SqlQuery<long>($"""
                SELECT ds.Id AS Value
                  FROM doc.DocumentSheet AS ds
                  JOIN OPENJSON({pairs}) WITH (d bigint '$.d', i int '$.i') AS h
                    ON h.d = ds.DocumentId AND h.i = ds.SheetDefId
                """);
    }

    /// <summary>Пари «проєкт, Id» як JSON-масив <c>[{"p":1,"i":2}]</c> для <c>OPENJSON</c>; <c>null</c> — порожній.</summary>
    private static string PairsJson(IReadOnlyCollection<(int ProjectId, int Id)>? pairs)
        => JsonSerializer.Serialize((pairs ?? []).Select(x => new { p = x.ProjectId, i = x.Id }));

    /// <summary>✎ D-446: пари «документ, Id» як JSON-масив <c>[{"d":1,"i":2}]</c> для <c>OPENJSON</c>; <c>null</c> — порожній.</summary>
    private static string DocumentPairsJson(IReadOnlyCollection<(long DocumentId, int Id)>? pairs)
        => JsonSerializer.Serialize((pairs ?? []).Select(x => new { d = x.DocumentId, i = x.Id }));

    private static Expression<Func<T, bool>> Not<T>(Expression<Func<T, bool>> predicate)
        => Expression.Lambda<Func<T, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    /// <summary>
    /// Документи, код чи назва яких містить <paramref name="term"/> (без регістру).
    /// </summary>
    /// <remarks>
    /// ⚠ Сирий SQL з параметром, не склеєний рядок. Назви — JSON у <c>nvarchar(max)</c>, а
    /// <c>ToJson</c> екранує кирилицю в <c>\uXXXX</c>, тож <c>LIKE</c> по сирому
    /// стовпцю її не знайде: <c>OPENJSON</c> розкодовує (той самий прийом, що в
    /// <c>SearchStore</c>). Регістр — за зіставленням бази (CI), метасимволи
    /// екрануються. Індексу під <c>LIKE '%q%'</c> немає й не потрібен: це відбір
    /// серед документів видимих проєктів.
    /// </remarks>
    private IQueryable<long> DocumentIdsMatching(string term)
    {
        var pattern = "%" + term
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal)
            .Replace("[", @"\[", StringComparison.Ordinal) + "%";

        return db.Database
            .SqlQuery<long>($"""
                SELECT d.Id AS Value
                  FROM doc.Document AS d
                 WHERE d.BusinessKey LIKE {pattern} ESCAPE N'\'
                    OR EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(d.NameL10n) = 1 THEN d.NameL10n END) AS n
                                WHERE n.value COLLATE DATABASE_DEFAULT LIKE {pattern} ESCAPE N'\')
                """);
    }

    /// <summary>Документи з хоч однією пізньою правкою (<c>D-70</c>) — не обмежено сторінкою.</summary>
    /// <remarks>
    /// ⚠ Сирий SQL: <c>aud.CellChange</c> навмисно поза моделлю EF (незмінний журнал).
    /// Пошук іде індексом <c>IX_CellChange_Cell</c> (провідна колонка — <c>DocumentId</c>).
    /// Без періоду — пізня правка за будь-який період.
    ///
    /// ⛔ ЄДИНЕ місце, де живе предикат «пізня правка» (<c>BE-09b</c>): і
    /// позначка в рядку (<see cref="LateEditsBatchAsync"/>), і фільтр
    /// <c>hasLateEdits</c> у <see cref="ListAsync"/> компонують САМЕ цей
    /// запит — другого визначення немає. Результат лишається
    /// <c>IQueryable</c>, а не матеріалізується тут: виклик з фільтра
    /// компонується в один запит із <c>documents</c> (підзапит <c>IN</c>),
    /// виклик з позначки — звужується до сторінки нижче.
    /// </remarks>
    private IQueryable<long> LateEditDocumentIds(PeriodKeyFilter period, DocumentListFilter hidden = default)
    {
        // ⛔ R-7: пізня правка колонки, схованої від читача (аркуш/таблиця/колонка), не дає позначки —
        // інакше вона розкриває, що у схованому аркуші щось правили після прогону. Ідентифікатори йдуть
        // JSON-масивом (`OPENJSON`), бо перелік змінної довжини в сирий SQL параметрами не розгорнути.
        // ⛔ N1-01: це ПАРИ «проєкт, Id» (`h.p = dd.ProjectId`): версія шаблону спільна для кількох проєктів,
        // а схований аркуш/таблиця/колонка — лише в тому проєкті, де стоїть заборона чи звуження.
        // ✎ D-446: документ-надання учасника має ВЛАСНІ межі — пари «документ, Id» (`h.d = c.DocumentId`), ті самі, що
        // фільтр стану (D-444) і картка (R-7). Це ТОЙ САМИЙ предикат, доповнений ще трьома парами, а не друге визначення.
        var participation = hidden.Participation;
        object[] args =
        [
            PairsJson(hidden.HiddenColumnDefIds),
            PairsJson(hidden.HiddenTableDefIds),
            PairsJson(hidden.HiddenSheetDefIds),
            DocumentPairsJson(participation?.HiddenColumns),
            DocumentPairsJson(participation?.HiddenTables),
            DocumentPairsJson(participation?.HiddenSheets),
        ];

        // ⚠ P1-05 (AN-109): два тексти запиту замість «catch-all» `(@any = 1 OR c.PeriodKey = @p)`. Один кешований
        // план мусив годитися для обох значень `@any`, тож `PeriodKey` у ньому був лише залишковим фільтром, а
        // позначка за період ділила план із позначкою «за будь-який період». Тепер запит за період несе
        // `c.PeriodKey = @p` як звичайний sargable-предикат (придатний для індексу з хвоста нижче).
        // ⛔ Межі `ChangedAt` тут НЕМАЄ свідомо: з даних її не вивести без втрат. Правка періоду можлива задовго
        // до його початку (`OpenOffsetDays` може бути від'ємним, правка «поза вікном» за Warn-політикою, D-239),
        // а пізньою вона стає і в стані Open — після Reopen аркуша (D-70 б). Будь-яка вигадана межа мовчки
        // знімала б позначку. Справжнє прискорення — фільтрований індекс `WHERE IsLateEdit = 1` (зміна схеми,
        // хвіст P1-05), а не межа в запиті.
        var periodPredicate = string.Empty;
        if (period.Value is { } periodKey)
        {
            periodPredicate = "AND c.PeriodKey = {6}";
            args = [.. args, periodKey];
        }

        var sql = System.Runtime.CompilerServices.FormattableStringFactory.Create(
            $$"""
            SELECT DISTINCT c.DocumentId AS Value
              FROM aud.CellChange AS c
             WHERE c.IsLateEdit = 1
               {{periodPredicate}}
               AND NOT EXISTS (
                   SELECT 1
                     FROM cfg.ColumnDef AS cd
                     JOIN cfg.TableDef AS td ON td.Id = cd.TableDefId
                     JOIN doc.Document AS dd ON dd.Id = c.DocumentId
                    WHERE cd.Id = c.ColumnDefId
                      AND (EXISTS (SELECT 1 FROM OPENJSON({0}) WITH (p int '$.p', i int '$.i') AS h
                                    WHERE h.p = dd.ProjectId AND h.i = cd.Id)
                           OR EXISTS (SELECT 1 FROM OPENJSON({1}) WITH (p int '$.p', i int '$.i') AS h
                                       WHERE h.p = dd.ProjectId AND h.i = td.Id)
                           OR EXISTS (SELECT 1 FROM OPENJSON({2}) WITH (p int '$.p', i int '$.i') AS h
                                       WHERE h.p = dd.ProjectId AND h.i = td.SheetDefId)
                           OR EXISTS (SELECT 1 FROM OPENJSON({3}) WITH (d bigint '$.d', i int '$.i') AS h
                                       WHERE h.d = c.DocumentId AND h.i = cd.Id)
                           OR EXISTS (SELECT 1 FROM OPENJSON({4}) WITH (d bigint '$.d', i int '$.i') AS h
                                       WHERE h.d = c.DocumentId AND h.i = td.Id)
                           OR EXISTS (SELECT 1 FROM OPENJSON({5}) WITH (d bigint '$.d', i int '$.i') AS h
                                       WHERE h.d = c.DocumentId AND h.i = td.SheetDefId)))
            """,
            args);

        return db.Database.SqlQuery<long>(sql);
    }

    /// <inheritdoc />
    public async Task<bool> HasVisibleLateEditsAsync(
        long documentId, PeriodKeyFilter period, DocumentListFilter hidden, CancellationToken ct)
    {
        var late = await LateEditsBatchAsync([documentId], period, ct, hidden).ConfigureAwait(false);
        return late.Contains(documentId);
    }

    /// <summary>Документи сторінки з хоч однією пізньою правкою — одним запитом.</summary>
    private async Task<HashSet<long>> LateEditsBatchAsync(
        IReadOnlyList<long> documentIds, PeriodKeyFilter period, CancellationToken ct,
        DocumentListFilter hidden = default)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        // Масив, а не `IReadOnlyList`: та сама причина, що й `allowedProjects`
        // вище — `Contains` над масивом EF перекладає в параметризований `IN`.
        var ids = documentIds.ToArray();

        var late = await LateEditDocumentIds(period, hidden)
            .Where(id => ids.Contains(id))
            .OrderBy(id => id)
            .Take(documentIds.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. late];
    }

    /// <summary>
    /// Документи сторінки із застарілими результатами (значення - відколи) - одним запитом;
    /// <c>null</c> - періоду немає, застарілість не визначена.
    /// </summary>
    private async Task<Dictionary<long, DateTime?>?> StaleResultsBatchAsync(
        IReadOnlyList<long> documentIds, PeriodKeyFilter period,
        bool mapFetched, IReadOnlyDictionary<long, DateTime?>? fetchedMap, CancellationToken ct)
    {
        if (period.Value is not { } periodKey)
        {
            return null;
        }

        if (documentIds.Count == 0)
        {
            return [];
        }

        // LP-45 (етап 2): карта ВСЬОГО періоду (кеш, один запит) - сторінка лише відбирає з неї свої документи в
        // пам'яті. Видимість уже застосована: `documentIds` прийшли з запиту документів із межею грантів.
        var map = mapFetched ? fetchedMap : (await StaleMap.GetAsync(periodKey, [null], ct).ConfigureAwait(false))[0];
        if (map is null)
        {
            // Карта переповнена (понад стелю): прямий запит по ids сторінки, як у 1885c2c77.
            return await StaleResultsDirectAsync(documentIds, period, ct).ConfigureAwait(false);
        }

        var found = new Dictionary<long, DateTime?>();
        foreach (var id in documentIds)
        {
            if (map.TryGetValue(id, out var since))
            {
                found[id] = since;
            }
        }

        return found;
    }

    /// <summary>
    /// Те саме, що <see cref="StaleResultsBatchAsync"/>, але вузьким запитом по ids БЕЗ карти й кешу
    /// (плоска форма <see cref="StaleResultsQuery"/>): картка документа і деградація при переповненні карти.
    /// </summary>
    private async Task<Dictionary<long, DateTime?>?> StaleResultsDirectAsync(
        IReadOnlyList<long> documentIds, PeriodKeyFilter period, CancellationToken ct)
    {
        if (period.Value is not { } periodKey)
        {
            return null;
        }

        if (documentIds.Count == 0)
        {
            return [];
        }

        var ids = documentIds.ToArray();

        var rows = await StaleResultsQuery.Documents(db, periodKey)
            .Where(r => ids.Contains(r.DocumentId))
            .OrderBy(r => r.DocumentId)
            .Take(documentIds.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Kind::Unspecified з datetime2 - у JSON без `Z` читався б як локальний час; журнал пише UTC (L3-01).
        return rows.ToDictionary(
            r => r.DocumentId,
            r => r.Since is { } since ? (DateTime?)DateTime.SpecifyKind(since, DateTimeKind.Utc) : null);
    }

    /// <summary>Лічильники останньої перевірки одного документа.</summary>
    private sealed record LatestFindings(long DocumentId, int ErrorCount, int WarningCount);

    /// <summary>Скільки підсумків з однаковим <c>RunAt</c> допускаємо на документ.</summary>
    private const int MaxRunTies = 4;

    /// <summary>
    /// Рядок переліку документів.
    /// </summary>
    /// <remarks>
    /// Названий тип, а не анонімний, і це не стиль. Анонімний тип містить
    /// фігурну дужку, яка розриває вираз на два «речення» — і архітектурне
    /// правило «немає <c>ToListAsync</c> без <c>Take</c>» бачить другу
    /// половину без межі. Правило право: читати його код має бути так само
    /// легко, як людині.
    /// </remarks>
    private sealed record DocumentRow(
        long Id, int ProjectId, string BusinessKey, DateTime CreatedAt, int SheetCount,
        Domain.ValueObjects.LocalizedText? NameL10n, DateTime ModifiedAt, string? ModifiedByDisplayName,
        string? OwnerDisplayName, string? ApproverDisplayName, PeriodKind? ProjectPeriodKind, int OwnerUserId,
        string? TemplateVersion, bool MultiVersion = false);

    /// <summary>Стеля кількості правил складу в одній версії.</summary>
    private const int MaxRules = 500;

    /// <summary>Стеля кількості аркушів у версії; у чинному шаблоні їх 24.</summary>
    private const int MaxSheets = 500;

    /// <summary>Скільки номерів перебирати, шукаючи вільний ключ.</summary>
    private const int MaxKeyAttempts = 1000;

    /// <summary>Базовий розкид номера на першому повторі (`DAT-09`).</summary>
    private const int KeySpreadStep = 32;

    /// <summary>Стеля розкиду: далі номер уже нічого не каже людині.</summary>
    private const int MaxKeySpread = 256;

    /// <summary>
    /// Скільки разів у цьому запиті вже просили ключ (`DAT-09`).
    /// </summary>
    /// <remarks>
    /// ⚠ Поле екземпляра, а не статичне: сховище <c>Scoped</c>, тобто один
    /// екземпляр на HTTP-запит. Статичний лічильник розкидував би номери й
    /// там, де жодної гонитви немає.
    /// </remarks>
    private int _keyRequests;

    /// <summary>
    /// Вікно «дотику» документа в секундах (`DAT-01`).
    /// </summary>
    /// <remarks>
    /// ⚠ Компроміс названий прямо: дата зміни документа може відставати від
    /// правди щонайбільше на ці секунди. Платня за це — відсутність X-замка
    /// на рядку документа в переважній більшості <c>PATCH</c>, тобто
    /// відсутність точки, де всі оператори документа стають у чергу один за
    /// одним.
    /// </remarks>
    private const int TouchWindowSeconds = 5;
}
