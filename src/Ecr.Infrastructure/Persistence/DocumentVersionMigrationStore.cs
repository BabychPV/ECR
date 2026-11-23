using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Статистика одного зафіксованого діапазону переносу (для тестів і діагностики).</summary>
/// <param name="Stage">Етап (<c>VersionMigrationStages</c>).</param>
/// <param name="Kind">Вид діапазону: <c>cells</c>, <c>index</c>, <c>validation</c>.</param>
/// <param name="Index">Номер діапазону в своєму виді.</param>
/// <param name="Count">Скільки діапазонів у виді (0 — невідомо).</param>
/// <param name="Rows">Скільки рядків змінено.</param>
/// <param name="LogicalReads">Логічні читання сесії за діапазон.</param>
/// <param name="ElapsedMs">Тривалість, мс.</param>
public sealed record MigrationRangeStat(string Stage, string Kind, int Index, int Count, long Rows, long LogicalReads, long ElapsedMs);

/// <summary>Перенос документів проєкту на нову версію шаблону (ФВ-7.5) — набором SQL; важка частина — діапазонами з комітом.</summary>
/// <remarks>
/// ⚠ Версія живе на ПРОЄКТІ (<c>Project.TemplateVersionId</c>), і кожен шлях
/// читання бере її звідти. Тому переносяться всі документи проєкту разом із
/// перемиканням версії, в одній транзакції: документ, чиї комірки вже
/// посилаються на колонки нової версії, а проєкт — ще на стару, відкрився б
/// без жодного значення.
/// ✎ B74 P3 (D-576): з кількома шаблонами в проєкті «весь проєкт» — це всі документи ШАБЛОНУ пари
/// (<see cref="UseTemplate"/>): кожен оператор, що міг би зачепити документи іншого шаблону, обмежено цим набором
/// (<c>@docs</c> прелюдії, <c>PairDocuments</c>); версія — версія пари; <c>Project.TemplateVersionId</c> — лише для основного.
///
/// ⚠ Кожен запит до <c>doc.TableRow</c>/<c>doc.CellValue</c>/<c>doc.TableInstance</c>
/// несе <c>PeriodKey</c> із періодів проєкту — таблиці партиціоновані
/// (<c>WR-05</c>), і без ключа перенос читав би всі партиції бази.
/// </remarks>
public sealed partial class DocumentVersionMigrationStore(
    EcrDbContext db,
    Microsoft.Extensions.Logging.ILogger<DocumentVersionMigrationStore>? logger = null) : IDocumentVersionMigrationStore
{
    /// <summary>N-3: скільки рядків змінює одна пачкова команда (нові рядки, результати валідації).</summary>
    public const int BatchRows = 5_000;

    /// <summary>
    /// Скільки рядків <c>doc.TableRow</c> (≈ ×3 комірок) входить в один діапазон переносу — одну команду й один коміт.
    /// </summary>
    /// <remarks>
    /// Вибір 5000 — компроміс "час переносу / тривалість табличного блокування" за ВИМІРОМ (1 млн комірок, один прогін,
    /// <c>index_lock_promotion_count</c> по <c>doc.CellValue</c>): 20000 рядків — 57,9 с (ескалація 17 з 18 діапазонів);
    /// 5000 — 83,2 с (67/68); 1000 — 173,8 с (325/335); 300 — 400 с (0). Зміна ключової колонки <c>CellValue</c> (delete+insert
    /// у кластерному + 3 NC-індекси + FK) дає &gt; 5000 локів уже на ~400 рядках, тож ескалація до табличного X-lock
    /// неминуча, доки не змінено <c>LOCK_ESCALATION</c> (DDL, беклог: AUTO — ескалація до партиції). Діапазон 5000 тримає її
    /// ~1,2 с; чужі PATCH на цей час чекають (≤ ~1–3 с) або дістають 409 lockTimeout (повтор). Рішення координатора 21.10.
    /// </remarks>
    public const int DefaultRangeRows = 5_000;

    /// <summary>Скільки рядків <c>doc.TableRow</c> у діапазоні (змінюється лише у тестах: малі діапазони на малих обсягах).</summary>
    public int RangeRows { get; init; } = DefaultRangeRows;

    /// <summary>
    /// ✎ B135: скільки мс крок 6 переносу чекає глобальний applock кроку 6 (<see cref="FinishLockSql"/>).
    /// </summary>
    /// <remarks>
    /// Власник замка після нього виконує лише решту кроку 6 (гранти, пари перейменувань, шкала версій, перемикання версії
    /// проєкту) і комітить — секунди. 60 с з запасом покривають це й чергу з кількох одночасних переносів, і
    /// лишаються далеко в межах <see cref="ApplyCommandTimeoutSeconds"/>: довше очікування означає завислий або аномально
    /// великий чужий перенос — тоді відмова з «повторіть перенос» (409), а не безстрокове очікування з відкритою
    /// транзакцією й блокуванням структури документів свого проєкту.
    /// </remarks>
    public const int DefaultFinishLockTimeoutMilliseconds = 60_000;

    /// <summary>Очікування applock кроку 6, мс (змінюється лише у тестах).</summary>
    public int FinishLockTimeoutMilliseconds { get; init; } = DefaultFinishLockTimeoutMilliseconds;

    /// <summary>
    /// Спостерігач завершених діапазонів (лише тести/діагностика): викликається ПІСЛЯ коміту діапазону, поки ззовні
    /// транзакція переносу ще відкрита.
    /// </summary>
    public Action<MigrationRangeStat>? RangeObserver { get; set; }

    /// <summary>
    /// N-3: <c>CommandTimeout</c> кожної команди переносу, с (глобальний — 60). 300 с не вистачило на проєкті
    /// ~4.5 млн комірок (Execution Timeout у фазі комірок) — 3600 с лишає запас на повільний диск/блокування.
    /// </summary>
    public const int ApplyCommandTimeoutSeconds = 3600;


    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Перенос версії: етап {Stage}, пачка {Batch}, перенесено {Rows} рядків, остання пачка {LastMs} мс, найповільніша {MaxMs} мс.")]
    private static partial void LogBatchProgress(
        Microsoft.Extensions.Logging.ILogger logger, string stage, long batch, long rows, long lastMs, long maxMs);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Перенос версії завершено: проєкт {ProjectId}, пачок {Batches}, рядків {Rows}, {Seconds} с, найповільніша пачка {MaxMs} мс.")]
    private static partial void LogApplyDone(
        Microsoft.Extensions.Logging.ILogger logger, int projectId, long batches, long rows, double seconds, long maxMs);

    // ✎ B74 P3 (D-576): шаблон пари (UseTemplate). null — увесь проєкт (основний шаблон), як до B74.
    private int? _templateId;
    private bool _isPrimary = true;

    /// <inheritdoc />
    public void UseTemplate(int templateId, bool isPrimary)
    {
        _templateId = templateId;
        _isPrimary = isPrimary;
    }

    /// <summary>
    /// ✎ B74 P3 (D-576): основний шаблон проєкту й чинна версія пари під блоком рядка проєкту (той самий блок
    /// <c>UPDLOCK, HOLDLOCK</c>, що й створення документа). <c>@templateId</c> NULL — без пари (увесь проєкт, як до B74):
    /// версія проєкту. Пара без рядка <c>doc.ProjectTemplate</c> — лише для основного шаблону: версія проєкту.
    /// </summary>
    /// <remarks>
    /// Блок (<c>/*LOCK*/</c> → <see cref="PairLockSql"/>) — лише під транзакцією переносу; розв'язання пари в обробнику
    /// (сухий прогін, перелік цілей, постановка) читає без блоку: інакше чекало б чужу відкриту транзакцію переносу.
    /// </remarks>
    private const string PairSql = """
        SELECT pv.TemplateId AS PrimaryTemplateId,
               CASE WHEN @templateId IS NULL THEN x.PrimaryVersionId
                    ELSE COALESCE(pt.CurrentTemplateVersionId,
                                  CASE WHEN @templateId = pv.TemplateId THEN x.PrimaryVersionId END) END AS CurrentVersionId
        FROM   (SELECT Id, TemplateVersionId AS PrimaryVersionId
                FROM   doc.Project /*LOCK*/
                WHERE  Id = @projectId) x
        JOIN   cfg.TemplateVersion pv ON pv.Id = x.PrimaryVersionId
        LEFT JOIN doc.ProjectTemplate pt ON pt.ProjectId = x.Id AND pt.TemplateId = @templateId
        """;

    /// <summary><see cref="PairSql"/> під блоком рядка проєкту (той самий текст, статична заміна).</summary>
    private static readonly string PairLockSql =
        PairSql.Replace("/*LOCK*/", "WITH (UPDLOCK, HOLDLOCK, ROWLOCK)", StringComparison.Ordinal);

    /// <summary>Рядок <see cref="PairSql"/>.</summary>
    private sealed record PairRow(int PrimaryTemplateId, int? CurrentVersionId);

    private async Task<PairRow?> ReadPairAsync(int projectId, int? templateId, bool locked, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQueryRaw<PairRow>(
                locked ? PairLockSql : PairSql,
                new SqlParameter("@projectId", projectId),
                new SqlParameter("@templateId", System.Data.SqlDbType.Int) { Value = (object?)templateId ?? DBNull.Value })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<VersionMigrationTemplatePair?> ReadTemplatePairAsync(long documentId, CancellationToken ct)
    {
        var document = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.ProjectId, d.TemplateId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        // Шаблон документа — його TemplateId; NULL — основний шаблон проєкту (його дає той самий запит).
        var primary = await ReadPairAsync(document.ProjectId, null, locked: false, ct).ConfigureAwait(false);
        if (primary is null)
        {
            return null;
        }

        var templateId = document.TemplateId ?? primary.PrimaryTemplateId;
        var pair = await ReadPairAsync(document.ProjectId, templateId, locked: false, ct).ConfigureAwait(false);
        return pair?.CurrentVersionId is { } current
            ? new VersionMigrationTemplatePair(document.ProjectId, templateId, templateId == primary.PrimaryTemplateId, current)
            : null;
    }

    /// <inheritdoc />
    public async Task<int?> LockProjectVersionAsync(int projectId, CancellationToken ct)
    {
        _ = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "LockProjectVersionAsync викликано поза транзакцією: блок рядка проєкту звільнився б одразу.");

        // ✎ B74 P3 (D-576): блок рядка проєкту, «поточна» — версія пари (без пари — версія проєкту, як до B74).
        return (await ReadPairAsync(projectId, _templateId, locked: true, ct).ConfigureAwait(false))?.CurrentVersionId;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ListPeriodKeysAsync(int projectId, CancellationToken ct)
        => await db.Periods
            .AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .OrderBy(p => p.PeriodKeyValue)
            .Select(p => p.PeriodKeyValue)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// ✎ B74 P3 (D-576): документи проєкту, які переносить ця пара: шаблон пари, а для основного — і <c>TemplateId NULL</c>.
    /// Без пари — усі документи проєкту.
    /// </summary>
    private IQueryable<Domain.Entities.Documents.Document> PairDocuments(int projectId)
    {
        var templateId = _templateId;
        var isPrimary = _isPrimary;
        return db.Documents.Where(d => d.ProjectId == projectId
                                       && (templateId == null
                                           || d.TemplateId == templateId
                                           || (isPrimary && d.TemplateId == null)));
    }

    /// <summary>
    /// ✎ B74 P3 (D-576): параметри предиката пари для інтерпольованого SQL —
    /// <c>({all} = 1 OR d.TemplateId = {template} OR (d.TemplateId IS NULL AND {primary} = 1))</c>.
    /// </summary>
    private (int All, int Template, int Primary) PairFilter()
        => (_templateId is null ? 1 : 0, _templateId ?? 0, _templateId is null || _isPrimary ? 1 : 0);

    /// <summary>✎ B74 P3: <c>@templateId</c> прелюдії (<c>NULL</c> — увесь проєкт); свіжий на кожну команду.</summary>
    private SqlParameter TemplateParameter()
        => new("@templateId", System.Data.SqlDbType.Int) { Value = (object?)_templateId ?? DBNull.Value };

    /// <summary>✎ B74 P3: <c>@isPrimary</c> прелюдії і <see cref="FinishSql"/> (без пари — 1, як до B74).</summary>
    private SqlParameter PrimaryParameter()
        => new("@isPrimary", System.Data.SqlDbType.Bit) { Value = _templateId is null || _isPrimary };

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> ListDocumentIdsAsync(int projectId, CancellationToken ct)
        => await PairDocuments(projectId)
            .AsNoTracking()
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<VersionMigrationScope> ReadScopeAsync(int projectId, CancellationToken ct)
        => ReadScopeAsync(projectId, 0, ct);

    /// <inheritdoc />
    public async Task<VersionMigrationScope> ReadScopeAsync(int projectId, int fromPeriodKey, CancellationToken ct)
    {
        // N-3: підрахунок по всіх значеннях проєкту (сотні тисяч) теж довший за глобальний таймаут.
        using var timeout = ExtendTimeout();

        var documentIds = await ListDocumentIdsAsync(projectId, ct).ConfigureAwait(false);
        var (all, template, primary) = PairFilter();

        var cells = await db.Database
            .SqlQuery<CellUsageRow>($"""
                SELECT cv.ColumnDefId, r.RowDefId, COUNT_BIG(*) AS [Values]
                FROM   doc.Period p
                JOIN   doc.TableInstance ti ON ti.PeriodKey = p.PeriodKey
                JOIN   doc.Document d       ON d.Id = ti.DocumentId AND d.ProjectId = p.ProjectId
                JOIN   doc.TableRow r       ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id
                JOIN   doc.CellValue cv     ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id
                WHERE  p.ProjectId = {projectId}
                  AND  p.PeriodKey >= {fromPeriodKey}
                  AND  ({all} = 1 OR d.TemplateId = {template} OR (d.TemplateId IS NULL AND {primary} = 1))
                  AND  r.IsDeleted = 0
                  AND  cv.IsEmpty = 0
                GROUP BY cv.ColumnDefId, r.RowDefId
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var header = await db.Database
            .SqlQuery<HeaderUsageRow>($"""
                SELECT h.HeaderFieldDefId, COUNT_BIG(*) AS [Values]
                FROM   doc.DocumentHeaderValue h
                JOIN   doc.Document d ON d.Id = h.DocumentId
                WHERE  d.ProjectId = {projectId}
                  AND  ({all} = 1 OR d.TemplateId = {template} OR (d.TemplateId IS NULL AND {primary} = 1))
                  AND  h.IsEmpty = 0
                GROUP BY h.HeaderFieldDefId
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var locked = await db.ApprovalStates
            .AsNoTracking()
            .Where(a => (a.Status == DocumentStatus.Submitted || a.Status == DocumentStatus.Approved)
                        && a.PeriodKey >= fromPeriodKey)
            .Join(
                PairDocuments(projectId),
                a => a.DocumentId,
                d => d.Id,
                (a, _) => new { a.DocumentId, a.SheetDefId, a.PeriodKey, a.Status })
            .OrderBy(a => a.DocumentId).ThenBy(a => a.SheetDefId).ThenBy(a => a.PeriodKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new VersionMigrationScope(
            documentIds,
            [.. cells.Select(c => new VersionMigrationCellUsage(c.ColumnDefId, c.RowDefId, c.Values))],
            header.ToDictionary(h => h.HeaderFieldDefId, h => h.Values),
            locked.Count,
            [.. locked.Select(a => new VersionMigrationLockedSheet(a.DocumentId, a.SheetDefId, a.PeriodKey, a.Status.ToString()))]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HeaderLookupValueRow>> ListHeaderLookupValuesAsync(
        int projectId, IReadOnlyCollection<int> headerFieldDefIds, int max, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(headerFieldDefIds);
        if (headerFieldDefIds.Count == 0)
        {
            return [];
        }

        var fieldIds = headerFieldDefIds.ToList();
        var rows = await (
                from h in db.DocumentHeaderValues.AsNoTracking()
                join d in PairDocuments(projectId).AsNoTracking() on h.DocumentId equals d.Id
                where !h.IsEmpty && fieldIds.Contains(h.HeaderFieldDefId)
                orderby h.DocumentId, h.HeaderFieldDefId
                select new HeaderLookupValueRow(
                    h.DocumentId,
                    h.HeaderFieldDefId,
                    h.ValueRegistryEntryId,
                    db.ApprovalStates.Any(a => a.DocumentId == h.DocumentId
                                               && (a.Status == DocumentStatus.Submitted || a.Status == DocumentStatus.Approved))))
            .Take(max + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows;
    }

    /// <inheritdoc />
    public Task ApplyAsync(int projectId, int targetVersionId, VersionMigrationPlan plan, CancellationToken ct)
        => ApplyAsync(projectId, targetVersionId, plan, onStep: null, ct);

    /// <inheritdoc />
    public Task ApplyAsync(
        int projectId, int targetVersionId, VersionMigrationPlan plan, VersionMigrationStepReporter? onStep,
        CancellationToken ct)
        => ApplyAsync(projectId, targetVersionId, plan, onStep, beforeCells: null, ct);

    /// <inheritdoc />
    public Task ApplyAsync(
        int projectId, int targetVersionId, VersionMigrationPlan plan, VersionMigrationStepReporter? onStep,
        Func<CancellationToken, Task>? beforeCells, CancellationToken ct)
        => ApplyCoreAsync(projectId, targetVersionId, plan, onStep, beforeCells, MigrationPhase.All, ct);

    /// <summary>Скільки чекати після фіксації маркера, поки читачі (кеш маркерів, оновлюється раз на 2 с) його побачать.</summary>
    /// <remarks>Змінюється лише у тестах (там маркер перевіряється явно, а не очікуванням).</remarks>
    public static TimeSpan MarkerPropagationDelay { get; set; } = TimeSpan.FromMilliseconds(3_000);

    /// <inheritdoc />
    public async Task<int?> ReadMigrationMarkerAsync(int projectId, CancellationToken ct)
    {
        var marker = await db.Database
            .SqlQueryRaw<int>(
                """
                SELECT TOP (1) TRY_CAST(JSON_VALUE(ExternalSettingsJson, '$.migratingTo') AS int) AS Value
                FROM   doc.Project
                WHERE  Id = {0} AND ISJSON(ExternalSettingsJson) = 1
                  AND  JSON_VALUE(ExternalSettingsJson, '$.migratingTo') IS NOT NULL
                """,
                projectId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return marker.Count == 0 ? null : marker[0];
    }

    /// <inheritdoc />
    public async Task BeginMigrationAsync(
        int projectId, int targetVersionId, VersionMigrationPlan plan, int? startedByUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("BeginMigrationAsync викликано поза транзакцією: маркер ставить перша транзакція.");

        var hash = PlanHash(plan);

        // Відновлення: маркер ЦІЄЇ цілі вже стоїть. Мапа мусить бути тією самою (хеш), інакше 422 - перенос, обірваний з
        // однією мапою колонок/Lookup, не можна «дожати» з іншою: частину комірок уже переселено за старою.
        var existing = await db.Database
            .SqlQueryRaw<string>(
                """
                SELECT TOP (1) CONCAT(ISNULL(JSON_VALUE(ExternalSettingsJson, '$.planHash'), N''), N'|',
                                      CASE WHEN TRY_CAST(JSON_VALUE(ExternalSettingsJson, '$.leaseUntil') AS datetime2) > SYSUTCDATETIME()
                                           THEN 1 ELSE 0 END) AS Value
                FROM   doc.Project
                WHERE  Id = {0} AND ISJSON(ExternalSettingsJson) = 1
                  AND  TRY_CAST(JSON_VALUE(ExternalSettingsJson, '$.migratingTo') AS int) = {1}
                """,
                projectId, targetVersionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (existing.Count > 0)
        {
            var parts = existing[0].Split('|');
            if (parts[0].Length > 0 && !string.Equals(parts[0], hash, StringComparison.Ordinal))
            {
                throw new Application.Errors.BusinessRuleException(
                    Domain.Errors.ErrorCodes.GuardedChangeWithoutStrategy,
                    "Перенос цього проєкту обірвано; продовжити його можна лише з тією самою мапою (колонок, рядків, Lookup шапки): " +
                    "мапу змінено відносно збереженої.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-SCHM-0422.migrateResumeMismatch",
                        ["projectId"] = projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
            }

            // Взаємне виключення: важку частину цього переносу вже виконує інший процес (оренда ще не минула).
            if (parts.Length > 1 && parts[1] == "1")
            {
                throw MigrationRunning(projectId);
            }

            return;
        }

        // ⛔ Маркер «міграція в процесі» — у вже наявній колонці проєкту (без DDL): JSON-властивість `migratingTo` =
        // цільова версія, `planHash` - відбиток мапи, `startedAt`/`startedBy` - коли й ким розпочато, `owner`/`leaseUntil` -
        // оренда виконавця важкої частини, `wm*` - watermark діапазонів (пишуть самі діапазони). Маркер бачать писарі
        // (`SheetEditGate`, в тому самому запиті, що читає версію) і читачі (фільтр API); знімає його лише фінальна
        // транзакція переносу (`FinishSql`) разом з перемиканням версії. Залишки старих запусків стираються.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE doc.Project
            SET    ExternalSettingsJson =
                   JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(
                       CASE WHEN ExternalSettingsJson IS NULL OR ISJSON(ExternalSettingsJson) = 0
                            THEN CONCAT(CHAR(123), '"migratingTo":', {targetVersionId}, CHAR(125))
                            ELSE JSON_MODIFY(ExternalSettingsJson, '$.migratingTo', {targetVersionId}) END,
                       '$.planHash', {hash}),
                       '$.startedAt', CONVERT(varchar(30), SYSUTCDATETIME(), 126)),
                       '$.startedBy', {startedByUserId}),
                       '$.owner', NULL), '$.leaseUntil', NULL),
                       '$.wmCellsPeriod', NULL), '$.wmCellsHi', NULL), '$.wmIndexHi', NULL)
            WHERE  Id = {projectId}
            """, ct).ConfigureAwait(false);
    }

    private static Application.Errors.ConcurrencyConflictException MigrationRunning(int projectId)
        => new(
            Domain.Errors.ErrorCodes.SheetBusy,
            $"Важку частину переносу проєкту {projectId} уже виконує інший процес: повторіть після завершення або закінчення оренди.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-4091.structureChanging",
                ["projectId"] = projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

    /// <summary>
    /// ✎ B135: крок 6 не дочекався глобального applock кроку 6 — його тримає інший перенос, що саме завершується.
    /// Транзакція відкочується; перенос ідемпотентний — повтор безпечний.
    /// </summary>
    private static Application.Errors.ConcurrencyConflictException FinishBusy(int targetVersionId)
        => new(
            Domain.Errors.ErrorCodes.JobStateConflict,
            $"Перенос на версію {targetVersionId}: інший перенос версії шаблону ще завершується. Нічого не змінено; повторіть перенос.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-JOB-0409.migrationFinishBusy",
                ["targetVersionId"] = targetVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

    /// <summary>
    /// D-10: код таблиці <c>X → Y</c> (пункти <c>Renamed</c> плану — мапа перейменування) у чаті таблиці
    /// (<c>doc.TableDiscussion*</c>) і в передачах зрізу (<c>doc.EditAssignment</c>). Без мапи — жодного звернення.
    /// </summary>
    /// <remarks>
    /// Та сама транзакція переносу; повтор (відновлення) нічого не знаходить на <c>X</c> — ідемпотентно. Чат ключується
    /// документом × кодом (без аркуша й періоду) — переїжджає цілим. Передача з ЯВНИМ періодом до «з місяця» лишається
    /// на <c>X</c>: ці періоди лишились на старій версії (та сама межа, що в <c>WorkflowSql</c>). Код, який мапа веде в
    /// РІЗНІ цілі на різних аркушах, у чаті не перейменовується (чат аркуша не знає). Ключ зрізу рахує лише домен
    /// (<see cref="Domain.Entities.Documents.EditAssignment.RenameTable"/>). Колізію з наявною передачею під <c>Y</c>
    /// (2601 на <c>UQ_EditAssignment</c>) план відмовляє заздалегідь (<see cref="CountAssignmentRenameConflictsAsync"/>,
    /// D-10-b); якщо вона все ж виникла між планом і записом — 2601 і відкат усього переносу.
    /// </remarks>
    private async Task RenameTableCodesAsync(int projectId, VersionMigrationPlan plan, DateTime now, CancellationToken ct)
    {
        var renames = TableCodeRenames(plan);
        if (renames.Count == 0)
        {
            return;
        }

        var chatRenames = renames
            .GroupBy(r => r.From, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.To).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .Select(g => new { f = g.Key, t = g.First().To })
            .ToList();
        if (chatRenames.Count > 0)
        {
            var json = JsonSerializer.Serialize(chatRenames);
            var (all, template, primary) = PairFilter();
            await db.Database.ExecuteSqlAsync($"""
                DECLARE @r TABLE (f nvarchar(64) PRIMARY KEY, t nvarchar(64) NOT NULL);
                INSERT @r (f, t) SELECT f, t FROM OPENJSON({json}) WITH (f nvarchar(64), t nvarchar(64));
                DECLARE @d TABLE (Id bigint PRIMARY KEY);
                INSERT @d (Id) SELECT Id FROM doc.Document WHERE ProjectId = {projectId}
                    AND ({all} = 1 OR TemplateId = {template} OR (TemplateId IS NULL AND {primary} = 1));

                UPDATE m SET m.TableDefCode = r.t
                  FROM doc.TableDiscussionMessage m JOIN @r r ON r.f = m.TableDefCode JOIN @d d ON d.Id = m.DocumentId;
                UPDATE x SET x.TableDefCode = r.t
                  FROM doc.TableDiscussionMention x JOIN @r r ON r.f = x.TableDefCode JOIN @d d ON d.Id = x.DocumentId;
                -- Підсумок і «прочитано» мають код у ключі: рядок під Y міг лишитися від старішої версії — тоді рядок X
                -- знімається (повідомлення вже переїхали вище), інакше перейменовується.
                DELETE t FROM doc.TableDiscussion t JOIN @r r ON r.f = t.TableDefCode JOIN @d d ON d.Id = t.DocumentId
                 WHERE EXISTS (SELECT 1 FROM doc.TableDiscussion y WHERE y.DocumentId = t.DocumentId
                                 AND y.TableDefCode = r.t AND y.SheetCopyKey = t.SheetCopyKey);
                UPDATE t SET t.TableDefCode = r.t
                  FROM doc.TableDiscussion t JOIN @r r ON r.f = t.TableDefCode JOIN @d d ON d.Id = t.DocumentId;
                DELETE t FROM doc.TableDiscussionRead t JOIN @r r ON r.f = t.TableDefCode JOIN @d d ON d.Id = t.DocumentId
                 WHERE EXISTS (SELECT 1 FROM doc.TableDiscussionRead y WHERE y.UserId = t.UserId AND y.DocumentId = t.DocumentId
                                 AND y.TableDefCode = r.t AND y.SheetCopyKey = t.SheetCopyKey);
                UPDATE t SET t.TableDefCode = r.t
                  FROM doc.TableDiscussionRead t JOIN @r r ON r.f = t.TableDefCode JOIN @d d ON d.Id = t.DocumentId;
                """, ct).ConfigureAwait(false);
        }

        // Передачі: один запит на всі пари мапи (без N+1), точна пара «аркуш + таблиця» — у пам'яті.
        var candidates = await RenamedAssignmentsQuery(projectId, renames, plan.FromPeriodKey)
            .ToListAsync(ct).ConfigureAwait(false);

        var changed = false;
        foreach (var assignment in candidates)
        {
            if (RenameTargetOf(renames, assignment.SheetCode, assignment.TableCode) is { } to)
            {
                assignment.RenameTable(to, now);
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ті самі кандидати, що перейменує <see cref="RenameTableCodesAsync"/> (аркуш + таблиця з мапи, період від
    /// «з місяця»), новий ключ зрізу — доменом (<see cref="Domain.Entities.Documents.EditAssignment.KeyOf"/>), без
    /// зміни сутностей. Конфлікт — новий ключ уже є в документі під ІНШОЮ передачею (будь-якою, і такою, що теж
    /// переїжджає: обмін X ↔ Y так само впав би на 2601) або два кандидати дають один ключ.
    /// </remarks>
    public async Task<int> CountAssignmentRenameConflictsAsync(int projectId, VersionMigrationPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var renames = TableCodeRenames(plan);
        if (renames.Count == 0)
        {
            return 0;
        }

        var candidates = await RenamedAssignmentsQuery(projectId, renames, plan.FromPeriodKey)
            .AsNoTracking()
            .Select(a => new { a.Id, a.DocumentId, a.SheetCode, a.TableCode, a.MonthNumber, a.PeriodKeyValue, a.Level })
            .ToListAsync(ct).ConfigureAwait(false);
        var moved = candidates
            .Select(a => (a.Id, a.DocumentId, To: RenameTargetOf(renames, a.SheetCode, a.TableCode), a.SheetCode, a.MonthNumber, a.PeriodKeyValue, a.Level))
            .Where(a => a.To is not null)
            .Select(a => (a.Id, a.DocumentId, Key: Domain.Entities.Documents.EditAssignment.KeyOf(
                Domain.Entities.Documents.EditAssignmentSlice.Create(a.SheetCode, a.To, a.MonthNumber, a.PeriodKeyValue), a.Level)))
            .ToList();
        if (moved.Count == 0)
        {
            return 0;
        }

        var documentIds = moved.Select(m => m.DocumentId).Distinct().ToList();
        var newKeys = moved.Select(m => m.Key).Distinct().ToList();
        var occupied = await db.EditAssignments.AsNoTracking()
            .Where(a => documentIds.Contains(a.DocumentId) && newKeys.Contains(a.SliceKey))
            .Select(a => new { a.Id, a.DocumentId, a.SliceKey })
            .ToListAsync(ct).ConfigureAwait(false);

        // ⚠ SliceKey порівнюється без урахування регістру — як і UQ_EditAssignment під типовою колацією бази.
        var taken = occupied
            .Select(o => (o.Id, o.DocumentId, Key: o.SliceKey.ToUpperInvariant()))
            .ToList();
        var collidesWithExisting = moved.Count(m =>
            taken.Any(t => t.DocumentId == m.DocumentId && t.Id != m.Id
                           && string.Equals(t.Key, m.Key.ToUpperInvariant(), StringComparison.Ordinal)));
        var duplicatesAmongMoved = moved
            .GroupBy(m => (m.DocumentId, Key: m.Key.ToUpperInvariant()))
            .Where(g => g.Count() > 1)
            .Sum(g => g.Count() - 1);
        return collidesWithExisting + duplicatesAmongMoved;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Той самий текст <see cref="AnchorFillSql"/>, що виконує T2 (<c>@apply = 0</c> — лише рахує й перелічує). Комірки
    /// зводяться до цільових ідентифікаторів (<c>@columnMap</c>/<c>@tableMap</c>, або вже цільові — відновлення після обриву
    /// важкої частини), тож план до й після важкої частини однаковий.
    /// </remarks>
    public async Task<VersionMigrationAnchorFill> ReadAnchorFillAsync(
        int projectId, VersionMigrationPlan plan, int maxConflicts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.AnchorColumns.Count == 0)
        {
            return VersionMigrationAnchorFill.None;
        }

        using var timeout = ExtendTimeout();
        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            opened = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandTimeout = ApplyCommandTimeoutSeconds;
            command.CommandText = Prelude + AnchorFillSql;
            command.Parameters.AddRange(new SqlParameter[]
            {
                new("@projectId", projectId),
                TemplateParameter(),
                PrimaryParameter(),
                new("@fromPeriod", plan.FromPeriodKey),
                new("@sheets", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(plan.Sheets.Select(p => new { o = p.Key, n = p.Value })) },
                new("@tables", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(plan.Tables.Select(p => new { o = p.Key, n = p.Value })) },
                new("@columns", System.Data.SqlDbType.NVarChar, -1)
                {
                    Value = JsonSerializer.Serialize(plan.Columns.Select(c => new { o = c.SourceColumnDefId, n = c.TargetColumnDefId, t = c.TargetTableDefId })),
                },
                new("@monthColumns", System.Data.SqlDbType.NVarChar, -1)
                {
                    Value = JsonSerializer.Serialize(plan.MonthColumns.Select(c => new
                    {
                        o = c.SourceColumnDefId, m = c.MonthNumber, n = c.TargetColumnDefId, t = c.TargetTableDefId,
                    })),
                },
                new("@rows", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(plan.Rows.Select(p => new { o = p.Key, n = p.Value })) },
                new("@headers", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(plan.HeaderFields.Select(p => new { o = p.Key, n = p.Value })) },
                new("@anchorColumns", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(plan.AnchorColumns) },
                new("@newRows", System.Data.SqlDbType.NVarChar, -1)
                {
                    Value = JsonSerializer.Serialize(plan.NewRows.Select(r => new { t = r.TargetTableDefId, d = r.TargetRowDefId, k = r.RowKey, s = r.Ordinal })),
                },
                new("@apply", false),
                new("@maxConflicts", Math.Max(0, maxConflicts)),
                new("@now", System.Data.SqlDbType.DateTime2) { Scale = 3, Value = DBNull.Value },   // лише гілка @apply = 1
                new("@copied", System.Data.SqlDbType.BigInt) { Direction = System.Data.ParameterDirection.Output },
                new("@rowsCreated", System.Data.SqlDbType.BigInt) { Direction = System.Data.ParameterDirection.Output },
                new("@instancesCreated", System.Data.SqlDbType.BigInt) { Direction = System.Data.ParameterDirection.Output },
            });

            long copies = 0;
            long conflictCells = 0;
            long rowsCreated = 0;
            long instancesCreated = 0;
            long skippedArchived = 0;
            var conflicts = new List<VersionMigrationAnchorConflict>();
            var targets = new List<VersionMigrationAnchorTarget>();
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    copies = reader.GetInt64(0);
                    conflictCells = reader.GetInt64(1);
                    rowsCreated = reader.GetInt64(2);
                    instancesCreated = reader.GetInt64(3);
                    skippedArchived = reader.GetInt64(4);
                }

                await reader.NextResultAsync(ct).ConfigureAwait(false);
                (long Doc, int Table, string RowKey, int Column)? current = null;
                List<NonMonthConflictValue>? values = null;
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var key = (reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3));
                    if (current != key)
                    {
                        if (current is { } done && values is not null)
                        {
                            conflicts.Add(new VersionMigrationAnchorConflict(done.Doc, done.Table, done.RowKey, done.Column, values));
                        }

                        current = key;
                        values = [];
                    }

                    values!.Add(new NonMonthConflictValue(reader.GetInt32(4), AnchorValueText(reader)));
                }

                if (current is { } last && values is not null)
                {
                    conflicts.Add(new VersionMigrationAnchorConflict(last.Doc, last.Table, last.RowKey, last.Column, values));
                }

                // Третій набір: куди крок пише (документ × таблиця × якір) — для перевірки поданих/затверджених аркушів.
                await reader.NextResultAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    targets.Add(new VersionMigrationAnchorTarget(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2)));
                }
            }

            return new VersionMigrationAnchorFill(copies, conflictCells, conflicts, rowsCreated, instancesCreated, targets)
            {
                SkippedArchived = skippedArchived,
            };
        }
        finally
        {
            if (opened)
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Значення комірки текстом для переліку конфліктів (колонки 5..10 читача <see cref="AnchorFillSql"/>): текст як є,
    /// число — інваріантно без хвостових нулів, дата — ISO 8601, логічне — <c>true</c>/<c>false</c>, запис довідника —
    /// <c>#id</c>; одиниця (якщо є) — <c> [unit id]</c>.
    /// </summary>
    private static string? AnchorValueText(System.Data.Common.DbDataReader reader)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string? text = !reader.IsDBNull(5) ? reader.GetString(5)
            : !reader.IsDBNull(6) ? reader.GetDecimal(6).ToString("G29", inv)
            : !reader.IsDBNull(7) ? reader.GetDateTime(7).ToString("yyyy-MM-ddTHH:mm:ss.fff", inv)
            : !reader.IsDBNull(8) ? (reader.GetBoolean(8) ? "true" : "false")
            : !reader.IsDBNull(9) ? "#" + reader.GetInt32(9).ToString(inv)
            : null;
        return reader.IsDBNull(10) ? text : $"{text} [unit {reader.GetInt32(10).ToString(inv)}]";
    }

    /// <summary>D-10: пари «аркуш, X → Y» з пунктів <c>Renamed</c> плану (код таблиці; шлях <c>SHEET.TABLE</c>).</summary>
    private static List<(string Sheet, string From, string To)> TableCodeRenames(VersionMigrationPlan plan)
        => plan.Items
            .Where(i => i.Kind == "Renamed" && i.Field == "Code" && i.OldValue is not null && i.NewValue is not null
                        && i.Path.Count(c => c == '.') == 1)
            .Select(i => (Sheet: i.Path[..i.Path.IndexOf('.', StringComparison.Ordinal)], From: i.OldValue!, To: i.NewValue!))
            .Distinct()
            .ToList();

    /// <summary>Передачі проєкту, яких торкається мапа: аркуш і таблиця з мапи, період — від «з місяця» або будь-який.</summary>
    private IQueryable<Domain.Entities.Documents.EditAssignment> RenamedAssignmentsQuery(
        int projectId, List<(string Sheet, string From, string To)> renames, int fromPeriod)
    {
        var sheetCodes = renames.Select(r => r.Sheet).Distinct().ToList();
        var fromCodes = renames.Select(r => r.From).Distinct().ToList();
        var documents = PairDocuments(projectId);
        return db.EditAssignments
            .Where(a => a.SheetCode != null && sheetCodes.Contains(a.SheetCode)
                        && a.TableCode != null && fromCodes.Contains(a.TableCode)
                        && (a.PeriodKeyValue == null || a.PeriodKeyValue >= fromPeriod)
                        && documents.Any(d => d.Id == a.DocumentId));
    }

    /// <summary>Новий код таблиці для точної пари «аркуш + таблиця»; <c>null</c> — пари в мапі немає.</summary>
    private static string? RenameTargetOf(List<(string Sheet, string From, string To)> renames, string? sheetCode, string? tableCode)
        => renames.FirstOrDefault(r =>
            string.Equals(r.Sheet, sheetCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.From, tableCode, StringComparison.OrdinalIgnoreCase)).To;

    /// <summary>Відбиток мапи переносу (колонки, рядки, таблиці, аркуші, шапка, нові рядки, Lookup) - для звірки при відновленні.</summary>
    private static string PlanHash(VersionMigrationPlan plan)
    {
        var payload = JsonSerializer.Serialize(new
        {
            s = plan.Sheets.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value }),
            t = plan.Tables.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value }),
            c = plan.Columns.OrderBy(c => c.SourceColumnDefId).Select(c => new { c.SourceColumnDefId, c.TargetColumnDefId, c.TargetTableDefId }),
            r = plan.Rows.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value }),
            h = plan.HeaderFields.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value }),
            n = plan.NewRows.OrderBy(x => x.TargetTableDefId).ThenBy(x => x.RowKey, StringComparer.Ordinal)
                .Select(x => new { x.TargetTableDefId, x.TargetRowDefId, x.RowKey, x.Ordinal }),
            l = plan.LookupRemaps.OrderBy(x => x.HeaderFieldDefId).ThenBy(x => x.OldEntryId)
                .Select(x => new { x.HeaderFieldDefId, x.OldEntryId, x.NewEntryId }),
        });

        // RPT-STABLE Part B: «з місяця» входить у відбиток лише коли задано - відбиток переносу всього проєкту незмінний.
        if (plan.FromPeriodKey != 0)
        {
            payload += "|fp" + plan.FromPeriodKey.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // ✎ LAND-YEAR L3: мапа місяців — теж лише коли є (відбиток решти переносів незмінний).
        if (plan.MonthColumns.Count > 0)
        {
            payload += "|mc" + JsonSerializer.Serialize(plan.MonthColumns
                .OrderBy(c => c.SourceColumnDefId).ThenBy(c => c.MonthNumber)
                .Select(c => new { c.SourceColumnDefId, c.MonthNumber, c.TargetColumnDefId, c.TargetTableDefId }));
        }

        // ✎ LAND-YEAR L3 (B-static): крок «немісячне → якір» і його колонки — теж лише коли є. ⚠ Кількість
        // (NonMonthCopiedToAnchor) у відбиток НЕ входить, як і кількість значень Lookup: це дані, а не мапа; її звіряє сам
        // крок у T2 з планом, перерахованим на старті (і при відновленні) під маркером, коли писарі вже заблоковані.
        if (plan.AnchorColumns.Count > 0)
        {
            payload += "|ac" + JsonSerializer.Serialize(plan.AnchorColumns.Order());
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)))[..32];
    }
    /// <inheritdoc />
    public Task MoveDataAsync(
        int projectId, int targetVersionId, VersionMigrationPlan plan, VersionMigrationStepReporter? onStep,
        CancellationToken ct)
        => ApplyCoreAsync(projectId, targetVersionId, plan, onStep, null, MigrationPhase.Heavy, ct);

    /// <inheritdoc />
    public Task CompleteAsync(
        int projectId, int targetVersionId, VersionMigrationPlan plan, VersionMigrationStepReporter? onStep,
        Func<CancellationToken, Task>? beforeFinish, CancellationToken ct)
        => ApplyCoreAsync(projectId, targetVersionId, plan, onStep, beforeFinish, MigrationPhase.Light, ct);

    /// <summary>Яку частину переносу виконує виклик.</summary>
    private enum MigrationPhase
    {
        /// <summary>Усе в транзакції виклику (тести, застарілий шлях).</summary>
        All,

        /// <summary>Лише комірки/індекс/валідація — діапазонами на власному з'єднанні, без транзакції виклику.</summary>
        Heavy,

        /// <summary>Шапка, склад, екземпляри, нові рядки, гранти, версія проєкту і зняття маркера — в транзакції виклику.</summary>
        Light,
    }

    private async Task ApplyCoreAsync(
        int projectId, int targetVersionId, VersionMigrationPlan plan, VersionMigrationStepReporter? onStep,
        Func<CancellationToken, Task>? beforeCells, MigrationPhase phase, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (phase != MigrationPhase.Heavy)
        {
            _ = db.Database.CurrentTransaction
                ?? throw new InvalidOperationException("ApplyAsync викликано поза транзакцією: перенос мусить бути атомарним.");
        }

        using var timeout = ExtendTimeout();

        // Один момент зміни на весь перенос (раніше — SYSUTCDATETIME() у єдиному пакеті).
        var now = await db.Database
            .SqlQuery<DateTime>($"SELECT CAST(SYSUTCDATETIME() AS datetime2(3)) AS Value")
            .SingleAsync(ct)
            .ConfigureAwait(false);

        var sheets = JsonSerializer.Serialize(plan.Sheets.Select(p => new { o = p.Key, n = p.Value }));
        var tables = JsonSerializer.Serialize(plan.Tables.Select(p => new { o = p.Key, n = p.Value }));
        var columns = JsonSerializer.Serialize(plan.Columns.Select(c => new { o = c.SourceColumnDefId, n = c.TargetColumnDefId, t = c.TargetTableDefId }));
        var monthColumns = JsonSerializer.Serialize(plan.MonthColumns.Select(c => new
        {
            o = c.SourceColumnDefId, m = c.MonthNumber, n = c.TargetColumnDefId, t = c.TargetTableDefId,
        }));
        var rows = JsonSerializer.Serialize(plan.Rows.Select(p => new { o = p.Key, n = p.Value }));
        var headers = JsonSerializer.Serialize(plan.HeaderFields.Select(p => new { o = p.Key, n = p.Value }));
        var newRows = JsonSerializer.Serialize(plan.NewRows.Select(r => new { t = r.TargetTableDefId, d = r.TargetRowDefId, k = r.RowKey, s = r.Ordinal }));

        // Мапа Lookup шапки: порожня (`[]`) — параметр лише у спільному списку, текст `HeaderSql` не змінюється.
        var lookupRemap = JsonSerializer.Serialize(
            plan.LookupRemaps.Select(r => new { f = r.HeaderFieldDefId, o = r.OldEntryId, n = r.NewEntryId }));

        // ⚠ Параметри — свіжі на кожну команду: SqlParameter належить одній колекції. Мапи, документи й
        // екземпляри щоразу збираються заново з цих параметрів (прелюдія) — стан між командами НЕ тримається:
        // #-таблиці були б DDL (сторож `ForbiddenApiTests`), а таблиці-змінні живуть лише в межах пакета.
        SqlParameter[] Parameters(SqlParameter? changedOutput = null, int batchRows = BatchRows, params SqlParameter[] extra)
        {
            List<SqlParameter> list =
            [
                new("@projectId", projectId),
                TemplateParameter(),
                PrimaryParameter(),
                new("@fromPeriod", plan.FromPeriodKey),
                new("@renames", System.Data.SqlDbType.NVarChar, -1)
                {
                    Value = JsonSerializer.Serialize(plan.ColumnRenames.Select(r => new { f = r.FromColumnDefId, t = r.ToColumnDefId })),
                },
                new("@targetVersionId", targetVersionId),
                new("@finishLockTimeout", FinishLockTimeoutMilliseconds),
                new("@batch", batchRows),
                new("@now", System.Data.SqlDbType.DateTime2) { Scale = 3, Value = now },
                new("@sheets", System.Data.SqlDbType.NVarChar, -1) { Value = sheets },
                new("@tables", System.Data.SqlDbType.NVarChar, -1) { Value = tables },
                new("@columns", System.Data.SqlDbType.NVarChar, -1) { Value = columns },
                new("@monthColumns", System.Data.SqlDbType.NVarChar, -1) { Value = monthColumns },
                new("@rows", System.Data.SqlDbType.NVarChar, -1) { Value = rows },
                new("@headers", System.Data.SqlDbType.NVarChar, -1) { Value = headers },
                new("@newRows", System.Data.SqlDbType.NVarChar, -1) { Value = newRows },
                new("@lookupRemap", System.Data.SqlDbType.NVarChar, -1) { Value = lookupRemap },
            ];
            if (changedOutput is not null)
            {
                list.Add(changedOutput);
            }

            list.AddRange(extra);
            return [.. list];
        }

        // Кроків усього (для прогресу): пачкові й одиничні команди; крок «нові рядки» — лише коли вони є в плані.
        var hasNewRows = plan.NewRows.Count > 0;
        // Комірки/рядки, індекс пошуку й валідація — по одному кроку (діапазони всередині), далі шапка, склад, екземпляри, фініш.
        var steps = 2 + 1 + (hasNewRows ? 1 : 0) + 1 + 1 + 1 + 1;
        var stepsDone = phase == MigrationPhase.Light ? 2 : 0;   // два важкі кроки (комірки, індекс) вже зроблено фазою Heavy
        long totalBatches = 0;
        long totalRows = 0;
        long slowestMs = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();

        // ⚠ Прогін — ПОЗА ЦІЄЮ транзакцією на рівні приймача (задача пише його окремим з'єднанням): інакше
        // клієнт не побачив би жодного кроку до коміту. Тут лише викликається.
        async Task ReportAsync(string stage, long changedRows)
        {
            if (onStep is not null)
            {
                await onStep(stage, stepsDone, steps, changedRows, ct).ConfigureAwait(false);
            }
        }

        async Task SingleAsync(string stage, string sql)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(SingleCommand(sql), Parameters(), ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == FinishLockError)
            {
                // ✎ B135: крок 6 не дочекався глобального applock (інший перенос саме завершується) — відкат, повтор.
                throw FinishBusy(targetVersionId);
            }

            stepsDone++;
            await ReportAsync(stage, 0).ConfigureAwait(false);
        }

        async Task BatchesAsync(string stage, string sql)
        {
            var command = BatchCommand(sql);
            long total = 0;
            int changed;
            do
            {
                ct.ThrowIfCancellationRequested();
                var output = new SqlParameter("@changed", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output };
                var batchWatch = System.Diagnostics.Stopwatch.StartNew();
                await db.Database.ExecuteSqlRawAsync(command, Parameters(output), ct).ConfigureAwait(false);
                var lastMs = batchWatch.ElapsedMilliseconds;
                slowestMs = Math.Max(slowestMs, lastMs);
                changed = output.Value is int value ? value : 0;
                total += changed;
                totalBatches++;
                totalRows += changed;
                if (logger is not null)
                {
                    LogBatchProgress(logger, stage, totalBatches, totalRows, lastMs, slowestMs);
                }

                if (changed >= BatchRows)
                {
                    await ReportAsync(stage, total).ConfigureAwait(false);
                }
            }
            while (changed >= BatchRows);

            stepsDone++;
            await ReportAsync(stage, 0).ConfigureAwait(false);
        }

        // Оренда виконавця важкої частини (власник - цей виклик); null - без маркера (тести, застарілий шлях).
        string? leaseOwner = null;

        // Один діапазон = одна команда = одна транзакція на ВЛАСНОМУ з'єднанні (коміт одразу, не з транзакцією переносу).
        async Task<long> RunRangeAsync(
            SqlConnection heavy, string stage, string sql, string kind, int index, int count, SqlParameter[] commandParameters)
        {
            ct.ThrowIfCancellationRequested();
            var output = new SqlParameter("@changed", System.Data.SqlDbType.BigInt) { Direction = System.Data.ParameterDirection.Output };
            var readsBefore = RangeObserver is null ? 0 : await SessionReadsAsync(heavy, ct).ConfigureAwait(false);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await using (var tx = (SqlTransaction)await heavy.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                await using var command = heavy.CreateCommand();
                command.Transaction = tx;
                command.CommandText = sql;
                command.CommandTimeout = ApplyCommandTimeoutSeconds;
                command.Parameters.AddRange(commandParameters);
                command.Parameters.Add(output);
                command.Parameters.Add(new SqlParameter("@leaseOwner", System.Data.SqlDbType.NVarChar, 40) { Value = (object?)leaseOwner ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@leaseMinutes", LeaseMinutes));
                try
                {
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (SqlException ex) when (ex.Number == LeaseLostError)
                {
                    // Оренду перехопив інший виконавець (наша минула) - діапазон відкотився, далі не йдемо.
                    throw MigrationRunning(projectId);
                }
                catch (SqlException ex) when (ex.Number == RangeMismatchError)
                {
                    // P2-3: діапазон не переселив усе, що мав, - відкотився цілим; precedent P2-2 (Lookup шапки): ECR-CELL-0409.
                    throw new Application.Errors.ConcurrencyConflictException(
                        Domain.Errors.ErrorCodes.CellConflict,
                        $"Діапазон {index} ({kind}) переносу не переселив усі значення: {ex.Message} Перенос зупинено, повторіть його.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-CELL-0409.concurrentChange",
                            ["expected"] = "0",
                            ["actual"] = "-1",
                        });
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            var elapsed = watch.ElapsedMilliseconds;
            var changed = output.Value is long value ? value : 0;
            slowestMs = Math.Max(slowestMs, elapsed);
            totalBatches++;
            totalRows += changed;
            if (logger is not null)
            {
                LogBatchProgress(logger, stage, totalBatches, totalRows, elapsed, slowestMs);
            }

            if (RangeObserver is { } observer)
            {
                var reads = await SessionReadsAsync(heavy, ct).ConfigureAwait(false) - readsBefore;
                observer(new MigrationRangeStat(stage, kind, index, count, changed, reads, elapsed));
            }

            return changed;
        }

        // ⛔ Порядок кроків (Б1): спершу ЛЕГКІ кроки в транзакції переносу — шапка (з перевіркою ECR-CELL-0409) і склад
        // робочого процесу: якщо вони відмовляють, жодна пачка комірок ще не зафіксована, тож відмова нічого не лишає
        // напівпереробленим. Далі важка частина (комірки/рядки, індекс пошуку, валідація) — діапазонами з окремим комітом
        // на власному з'єднанні; транзакція переносу весь цей час відкрита й тримає блокування структури документів
        // проєкту та рядка проєкту (писарі чекають і дістають 409 ECR-DOC-4091, версія проєкту не перемикається).
        // Нарешті — екземпляри таблиць, нові рядки, гранти й версія проєкту: коміт транзакції.
        if (phase is MigrationPhase.All or MigrationPhase.Light)
        {
            if (plan.LookupRemaps.Count > 0)
            {
                // ⛔ P2-2: SQL, що пише нові `ValueRegistryEntryId`, мусить зачепити РІВНО стільки значень, скільки обіцяв
                // план (і показав сухий прогін). Інакше (менше — запис пропущено; більше — зачеплено зайве) відкат усього
                // переносу: мовчки неповна або надлишкова мапа лишила б шапку на записах старого довідника.
                var remapped = new SqlParameter("@lookupChanged", System.Data.SqlDbType.Int)
                {
                    Direction = System.Data.ParameterDirection.Output,
                };
                await db.Database.ExecuteSqlRawAsync(SingleCommand(HeaderSqlWithLookupRemap), Parameters(remapped), ct)
                    .ConfigureAwait(false);
                stepsDone++;
                await ReportAsync(VersionMigrationStages.Header, 0).ConfigureAwait(false);

                var actual = remapped.Value is int changed ? changed : -1;
                if (actual != plan.LookupRemappedValues)
                {
                    throw new Application.Errors.ConcurrencyConflictException(
                        Domain.Errors.ErrorCodes.CellConflict,
                        $"Перенос значень Lookup шапки зачепив {actual} значень замість очікуваних {plan.LookupRemappedValues}: " +
                        "перенос скасовано.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-CELL-0409.concurrentChange",
                            ["expected"] = plan.LookupRemappedValues.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["actual"] = actual.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        });
                }
            }
            else
            {
                await SingleAsync(VersionMigrationStages.Header, HeaderSql).ConfigureAwait(false);
            }

            await SingleAsync(VersionMigrationStages.Workflow, WorkflowSql).ConfigureAwait(false);

            // ⛔ D-10: дані, що ключуються КОДОМ таблиці (чат, передачі зрізу), — за мапою перейменування цього плану.
            await RenameTableCodesAsync(projectId, plan, now, ct).ConfigureAwait(false);

            // Усе, що може відмовити й мусить відкотитися разом із переносом (журнал, мапінги збору), — ДО важкої частини.
            if (beforeCells is not null)
            {
                await beforeCells(ct).ConfigureAwait(false);
            }
        }

        if (phase is MigrationPhase.All or MigrationPhase.Heavy)
        {
            // ── Важка частина: власне з'єднання, коміт на кожен діапазон ───────────────────────────────────
            var connectionString = db.Database.GetConnectionString()
                ?? throw new InvalidOperationException("Рядок підключення контексту порожній: перенос комірок не має з'єднання.");
            await using (var heavy = new SqlConnection(connectionString))
            {
                await heavy.OpenAsync(ct).ConfigureAwait(false);

                // ⛔ Взаємне виключення важкої частини: оренда в маркері (власник + строк). Другий запуск тієї ж цілі (реклейм
                // задачі, синхронний шлях) не входить, доки оренда не минула; після збою оренда знімається одразу
                // (`LeaseGuard`), після обриву процесу - сама спливає за `LeaseMinutes`.
                LeaseGuard? leaseGuard = null;
                if (phase == MigrationPhase.Heavy)
                {
                    leaseOwner = Guid.NewGuid().ToString("N");
                    await ClaimLeaseAsync(heavy, projectId, leaseOwner, ct).ConfigureAwait(false);
                    leaseGuard = new LeaseGuard(heavy, projectId, leaseOwner);
                }

                await using var leaseHolder = leaseGuard;

                if (phase == MigrationPhase.Heavy && MarkerPropagationDelay > TimeSpan.Zero)
                {
                    // Маркер (окрема зафіксована транзакція) мусить дійти до читачів ДО першого діапазону.
                    await Task.Delay(MarkerPropagationDelay, ct).ConfigureAwait(false);
                }

                // 1) Комірки, оформлення комірок і рядки: діапазони за ключем кластерного індексу (PeriodKey, TableRowId).
                var rowRanges = await ReadRowRangesAsync(heavy, Parameters(), ct).ConfigureAwait(false);
                if (leaseOwner is not null)
                {
                    // Читання меж - один довгий прохід: оренду подовжуємо ПЕРЕД першим комітом діапазону.
                    await ClaimLeaseAsync(heavy, projectId, leaseOwner, ct).ConfigureAwait(false);
                }

                var watermark = phase == MigrationPhase.Heavy
                    ? await ReadWatermarkAsync(heavy, projectId, ct).ConfigureAwait(false)
                    : default;
                long cellsTotal = 0;
                foreach (var range in rowRanges)
                {
                    // Відновлення після обриву: діапазон повністю ДО збереженої межі вже зафіксовано (предикати ідемпотентні,
                    // тож діапазон, що лише перетинає межу, просто виконується ще раз).
                    if (watermark.CellsPeriod is { } wp && (range.PeriodKey < wp || (range.PeriodKey == wp && range.Hi <= watermark.CellsHi)))
                    {
                        continue;
                    }

                    cellsTotal += await RunRangeAsync(
                            heavy, VersionMigrationStages.Cells, RangeCommand, "cells", range.Index, rowRanges.Count,
                            Parameters(
                                null, BatchRows,
                                new SqlParameter("@p", range.PeriodKey),
                                new SqlParameter("@lo", range.Lo),
                                new SqlParameter("@hi", range.Hi)))
                        .ConfigureAwait(false);
                    await ReportAsync(VersionMigrationStages.Cells, cellsTotal).ConfigureAwait(false);
                }

                stepsDone++;
                await ReportAsync(VersionMigrationStages.Cells, 0).ConfigureAwait(false);

                // 2) Похідний індекс пошуку: діапазони за первинним ключем Id.
                var indexRanges = await ReadIndexRangesAsync(heavy, Parameters(), ct).ConfigureAwait(false);
                long indexTotal = 0;
                foreach (var range in indexRanges)
                {
                    if (watermark.IndexHi is { } wi && range.Hi <= wi)
                    {
                        continue;
                    }

                    indexTotal += await RunRangeAsync(
                            heavy, VersionMigrationStages.Index, IndexRangeCommand, "index", range.Index, indexRanges.Count,
                            Parameters(
                                null, BatchRows,
                                new SqlParameter("@lo", range.Lo),
                                new SqlParameter("@hi", range.Hi)))
                        .ConfigureAwait(false);
                    await ReportAsync(VersionMigrationStages.Index, indexTotal).ConfigureAwait(false);
                }

                stepsDone++;
                await ReportAsync(VersionMigrationStages.Index, 0).ConfigureAwait(false);
            }
        }

        if (phase is MigrationPhase.All or MigrationPhase.Light)
        {
            // ⛔ Постумова важкої частини (замість звірки rowsAffected, якої не існує для переносу "скільки є"): жодної комірки
            // проєкту не лишилося на колонці, якої немає в цільовій версії. Інакше діапазон щось пропустив би - версію
            // проєкту не перемикаємо (T2 відкочується, маркер лишається, повтор доводить).
            {
                await using var verify = db.Database.GetDbConnection().CreateCommand();
                verify.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                verify.CommandTimeout = ApplyCommandTimeoutSeconds;
                verify.CommandText = Prelude + """
                    SELECT COUNT_BIG(*)
                    FROM   doc.CellValue cv
                    JOIN   doc.TableRow r  ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
                    JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
                    WHERE  cv.PeriodKey IN (SELECT PeriodKey FROM @periods)
                      AND  NOT EXISTS (SELECT 1 FROM @columnMap m WHERE m.n = cv.ColumnDefId)
                      AND  NOT EXISTS (SELECT 1 FROM @monthColumnMap mm WHERE mm.n = cv.ColumnDefId)
                    OPTION (MAXDOP 1, RECOMPILE);
                    """;
                verify.Parameters.AddRange(Parameters());
                var stray = Convert.ToInt64(
                    await verify.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                if (stray != 0)
                {
                    throw new Application.Errors.ConcurrencyConflictException(
                        Domain.Errors.ErrorCodes.CellConflict,
                        $"Після переносу комірок {stray} комірок проєкту лишилося на колонках, яких немає в цільовій версії: " +
                        "версію проєкту не перемкнуто, повторіть перенос.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-CELL-0409.concurrentChange",
                            ["expected"] = "0",
                            ["actual"] = stray.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        });
                }
            }

            // P2-1: результати валідації зникають лише тут, у транзакції перемикання версії.
            await BatchesAsync(VersionMigrationStages.Validation, ValidationBatchSql).ConfigureAwait(false);

            // Далі — знову у транзакції переносу (атомарно з перемиканням версії проєкту).
            await SingleAsync(VersionMigrationStages.Instances, InstancesSql).ConfigureAwait(false);

            // Без нових рядків у плані команду не посилаємо зовсім.
            if (hasNewRows)
            {
                await BatchesAsync(VersionMigrationStages.NewRows, NewRowsBatchSql).ConfigureAwait(false);
            }

            // ✎ LAND-YEAR L3 (B-static): немісячний ввід, якого немає в якорі року, — з найпізнішого місяця, у ТІЙ САМІЙ
            // транзакції перемикання версії (екземпляри й комірки вже на цільовій версії). Кількість мусить дорівнювати
            // плану (сухому прогону): розбіжність — відкат T2 (ECR-CELL-0409), маркер лишається, повтор перераховує план.
            if (plan.AnchorColumns.Count > 0)
            {
                await using var fill = db.Database.GetDbConnection().CreateCommand();
                fill.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                fill.CommandTimeout = ApplyCommandTimeoutSeconds;
                fill.CommandText = Prelude + AnchorFillSql;
                var copied = new SqlParameter("@copied", System.Data.SqlDbType.BigInt) { Direction = System.Data.ParameterDirection.Output };
                var rowsCreated = new SqlParameter("@rowsCreated", System.Data.SqlDbType.BigInt) { Direction = System.Data.ParameterDirection.Output };
                var instancesCreated = new SqlParameter("@instancesCreated", System.Data.SqlDbType.BigInt) { Direction = System.Data.ParameterDirection.Output };
                fill.Parameters.AddRange(Parameters(
                    copied, BatchRows,
                    new SqlParameter("@anchorColumns", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(plan.AnchorColumns) },
                    new SqlParameter("@apply", true),
                    // ⚠ Не `new SqlParameter(name, 0)`: літерал 0 обирає перевантаження (string, SqlDbType) — значення null.
                    new SqlParameter("@maxConflicts", System.Data.SqlDbType.Int) { Value = 0 },
                    rowsCreated,
                    instancesCreated));
                await fill.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                // Екземпляри, рядки й комірки — кожне рівно як у плані; інакше відкат T2.
                var expected = (Cells: plan.NonMonthCopiedToAnchor, Rows: plan.NonMonthAnchorRowsCreated, Instances: plan.NonMonthAnchorInstancesCreated);
                var actual = (
                    Cells: copied.Value is long c ? c : -1,
                    Rows: rowsCreated.Value is long r ? r : -1,
                    Instances: instancesCreated.Value is long i ? i : -1);
                if (actual != expected)
                {
                    throw new Application.Errors.ConcurrencyConflictException(
                        Domain.Errors.ErrorCodes.CellConflict,
                        $"Перенос немісячних значень у якір року: комірок {actual.Cells}, рядків {actual.Rows}, екземплярів " +
                        $"{actual.Instances} замість очікуваних {expected.Cells}/{expected.Rows}/{expected.Instances}: версію проєкту " +
                        "не перемкнуто, повторіть перенос.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-CELL-0409.concurrentChange",
                            ["expected"] = expected.Cells.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["actual"] = actual.Cells.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["expectedRows"] = expected.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["actualRows"] = actual.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["expectedInstances"] = expected.Instances.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["actualInstances"] = actual.Instances.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        });
                }
            }

            await SingleAsync(VersionMigrationStages.Finish, FinishSql).ConfigureAwait(false);
        }

        if (logger is not null)
        {
            var seconds = Math.Round(started.Elapsed.TotalSeconds, 1);
            LogApplyDone(logger, projectId, totalBatches, totalRows, seconds, slowestMs);
        }
    }

    // Складають команду зі СТАТИЧНИХ текстів цього класу (прелюдія + оператор) — не з вводу користувача.
    private static string SingleCommand(string body) => Prelude + body;

    private readonly record struct KeyRange(int Index, int PeriodKey, long Lo, long Hi);

    /// <summary>На скільки хвилин береться (і щодіапазону подовжується) оренда виконавця важкої частини.</summary>
    public const int LeaseMinutes = 10;

    /// <summary>Номер помилки <c>THROW</c> у діапазоні: оренду втрачено.</summary>
    private const int LeaseLostError = 51000;

    /// <summary>Номер помилки <c>THROW</c> у діапазоні: розбіжність "очікувано переселене - факт".</summary>
    private const int RangeMismatchError = 51001;

    /// <summary>✎ B135: номер помилки <c>THROW</c> у <see cref="FinishLockSql"/>: applock кроку 6 не взято вчасно.</summary>
    internal const int FinishLockError = 51300;

    private static async Task ClaimLeaseAsync(SqlConnection connection, int projectId, string owner, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE doc.Project
            SET    ExternalSettingsJson = JSON_MODIFY(JSON_MODIFY(ExternalSettingsJson, '$.owner', @owner),
                       '$.leaseUntil', CONVERT(varchar(30), DATEADD(MINUTE, @minutes, SYSUTCDATETIME()), 126))
            WHERE  Id = @projectId AND ISJSON(ExternalSettingsJson) = 1
              AND  JSON_VALUE(ExternalSettingsJson, '$.migratingTo') IS NOT NULL
              AND  (JSON_VALUE(ExternalSettingsJson, '$.leaseUntil') IS NULL
                    OR TRY_CAST(JSON_VALUE(ExternalSettingsJson, '$.leaseUntil') AS datetime2) < SYSUTCDATETIME()
                    OR JSON_VALUE(ExternalSettingsJson, '$.owner') = @owner);
            """;
        command.Parameters.Add(new SqlParameter("@owner", System.Data.SqlDbType.NVarChar, 40) { Value = owner });
        command.Parameters.Add(new SqlParameter("@minutes", LeaseMinutes));
        command.Parameters.Add(new SqlParameter("@projectId", projectId));
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            throw MigrationRunning(projectId);
        }
    }

    /// <summary>Знімає оренду, коли важка частина закінчилась (успіхом чи збоєм): повтор не чекає, доки вона спливе.</summary>
    private sealed class LeaseGuard(SqlConnection connection, int projectId, string owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE doc.Project
                    SET    ExternalSettingsJson = JSON_MODIFY(JSON_MODIFY(ExternalSettingsJson, '$.owner', NULL), '$.leaseUntil', NULL)
                    WHERE  Id = @projectId AND ISJSON(ExternalSettingsJson) = 1 AND JSON_VALUE(ExternalSettingsJson, '$.owner') = @owner;
                    """;
                command.Parameters.Add(new SqlParameter("@owner", System.Data.SqlDbType.NVarChar, 40) { Value = owner });
                command.Parameters.Add(new SqlParameter("@projectId", projectId));
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // звільнення оренди - best effort: з'єднання могло обірватися, оренда тоді спливе сама
            catch (Exception)
#pragma warning restore CA1031
            {
                // оренда спливе за LeaseMinutes
            }
        }
    }

    /// <summary>Остання зафіксована межа важкої частини (watermark у маркері проєкту).</summary>
    private readonly record struct Watermark(int? CellsPeriod, long CellsHi, long? IndexHi);

    private static async Task<Watermark> ReadWatermarkAsync(SqlConnection connection, int projectId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TRY_CAST(JSON_VALUE(ExternalSettingsJson, '$.wmCellsPeriod') AS int),
                   TRY_CAST(JSON_VALUE(ExternalSettingsJson, '$.wmCellsHi') AS bigint),
                   TRY_CAST(JSON_VALUE(ExternalSettingsJson, '$.wmIndexHi') AS bigint)
            FROM   doc.Project
            WHERE  Id = @projectId AND ISJSON(ExternalSettingsJson) = 1
            """;
        command.Parameters.Add(new SqlParameter("@projectId", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return default;
        }

        return new Watermark(
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? long.MinValue : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static async Task<long> SessionReadsAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
        return Convert.ToInt64(
            await probe.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Діапазони переносу комірок: для кожного періоду проєкту — послідовні інтервали <c>TableRowId</c>, у кожному
    /// ≈ <see cref="RangeRows"/> рядків ПРОЄКТУ. Межі рахуються ОДНИМ проходом по ключу <c>(PeriodKey, Id)</c>.
    /// </summary>
    private async Task<List<KeyRange>> ReadRowRangesAsync(SqlConnection connection, SqlParameter[] commandParameters, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = ApplyCommandTimeoutSeconds;
        command.CommandText = Prelude + """
            SELECT PeriodKey FROM @periods ORDER BY PeriodKey;

            SELECT x.PeriodKey, x.Id
            FROM (
                SELECT r.PeriodKey, r.Id, ROW_NUMBER() OVER (PARTITION BY r.PeriodKey ORDER BY r.Id) AS rn
                FROM   doc.TableRow r
                JOIN   @instances i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
                WHERE  r.PeriodKey IN (SELECT PeriodKey FROM @periods)
            ) x
            WHERE x.rn % @rangeRows = 0
            ORDER BY x.PeriodKey, x.Id
            OPTION (MAXDOP 1);
            """;
        command.Parameters.AddRange(commandParameters);
        command.Parameters.Add(new SqlParameter("@rangeRows", Math.Max(1, RangeRows)));

        var periods = new List<int>();
        var cuts = new Dictionary<int, List<long>>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                periods.Add(reader.GetInt32(0));
            }

            await reader.NextResultAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var period = reader.GetInt32(0);
                if (!cuts.TryGetValue(period, out var list))
                {
                    cuts[period] = list = [];
                }

                list.Add(reader.GetInt64(1));
            }
        }

        var ranges = new List<KeyRange>();
        foreach (var period in periods)
        {
            var lo = long.MinValue;
            foreach (var cut in cuts.GetValueOrDefault(period) ?? [])
            {
                ranges.Add(new KeyRange(ranges.Count, period, lo, cut));
                lo = cut + 1;
            }

            ranges.Add(new KeyRange(ranges.Count, period, lo, long.MaxValue));
        }

        return ranges;
    }

    /// <summary>Діапазони індексу пошуку за первинним ключем <c>Id</c> (≈ <c>4 × RangeRows</c> рядків документів проєкту).</summary>
    private async Task<List<KeyRange>> ReadIndexRangesAsync(SqlConnection connection, SqlParameter[] commandParameters, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = ApplyCommandTimeoutSeconds;
        command.CommandText = Prelude + """
            SELECT x.Id
            FROM (
                SELECT v.Id, ROW_NUMBER() OVER (ORDER BY v.Id) AS rn
                FROM   doc.DocumentIndexValue v
                JOIN   @docs d ON d.Id = v.DocumentId
            ) x
            WHERE x.rn % @rangeRows = 0
            ORDER BY x.Id
            OPTION (MAXDOP 1);
            """;
        command.Parameters.AddRange(commandParameters);
        command.Parameters.Add(new SqlParameter("@rangeRows", Math.Max(1, RangeRows) * 4L));

        var ranges = new List<KeyRange>();
        var lo = long.MinValue;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var cut = reader.GetInt64(0);
                ranges.Add(new KeyRange(ranges.Count, 0, lo, cut));
                lo = cut + 1;
            }
        }

        ranges.Add(new KeyRange(ranges.Count, 0, lo, long.MaxValue));
        return ranges;
    }

    /// <summary>
    /// Пачкова команда: оператор, перед його <c>;</c> — <c>OPTION (MAXDOP 1, RECOMPILE)</c> (табличні змінні мають
    /// відомий розмір лише після перекомпіляції, інакше план бере їх за 1 рядок; MAXDOP 1 — без паралельного плану:
    /// на пачці TOP(@batch) exchange-воркери зависали на CXSYNC_PORT), далі <c>@changed = @@ROWCOUNT</c>.
    /// </summary>
    private static string BatchCommand(string body)
        => Prelude + body.TrimEnd().TrimEnd(';') + " OPTION (MAXDOP 1, RECOMPILE);\nSET @changed = @@ROWCOUNT;";

    /// <summary>
    /// Подовжує <c>CommandTimeout</c> контексту на час операції переносу й повертає його, коли її завершено.
    /// Глобальний таймаут застосунку (<c>Database:CommandTimeoutSeconds</c>) не змінюється.
    /// </summary>
    private ExtendedTimeout ExtendTimeout()
    {
        var previous = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(ApplyCommandTimeoutSeconds);
        return new ExtendedTimeout(db, previous);
    }

    private sealed class ExtendedTimeout(EcrDbContext db, int? previous) : IDisposable
    {
        public void Dispose() => db.Database.SetCommandTimeout(previous);
    }

    /// <inheritdoc />
    public async Task<int> CountDenyGrantsAsync(
        IReadOnlyCollection<int> sheetIds, IReadOnlyCollection<int> tableIds, IReadOnlyCollection<int> columnIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sheetIds);
        ArgumentNullException.ThrowIfNull(tableIds);
        ArgumentNullException.ThrowIfNull(columnIds);

        var keys = sheetIds.Select(i => new { k = (byte)ResourceKind.Sheet, i })
            .Concat(tableIds.Select(i => new { k = (byte)ResourceKind.Table, i }))
            .Concat(columnIds.Select(i => new { k = (byte)ResourceKind.Column, i }))
            .ToList();
        if (keys.Count == 0)
        {
            return 0;
        }

        var json = JsonSerializer.Serialize(keys);
        return await db.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value
                FROM   sec.ResourceGrant g
                JOIN   OPENJSON({json}) WITH (k tinyint, i int) x ON x.k = g.ResourceKind AND x.i = g.ResourceId
                """)
            .SingleAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountSheetCopiesAsync(int projectId, IReadOnlyCollection<int> sheetDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sheetDefIds);
        if (sheetDefIds.Count == 0)
        {
            return 0;
        }

        var ids = sheetDefIds.Distinct().ToList();
        return await (from s in db.DocumentSheets.AsNoTracking()
                      join d in PairDocuments(projectId).AsNoTracking() on s.DocumentId equals d.Id
                      where s.CopyOfId != null && ids.Contains(s.SheetDefId)
                      select s.Id)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> CountUnmappedBindingsAsync(
        int sourceVersionId, IReadOnlyDictionary<int, int> columnMap, CancellationToken ct)
        => CountUnmappedBindingsAsync(sourceVersionId, columnMap, new Dictionary<int, IReadOnlyList<int>>(), ct);

    /// <inheritdoc />
    public async Task<int> CountUnmappedBindingsAsync(
        int sourceVersionId, IReadOnlyDictionary<int, int> columnMap,
        IReadOnlyDictionary<int, IReadOnlyList<int>> monthColumnMap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columnMap);
        ArgumentNullException.ThrowIfNull(monthColumnMap);

        // З'єднання через ColumnDef → TableDef → SheetDef: власного TemplateVersionId прив'язка не має.
        var source = await (
                from binding in db.CalculationBindings.AsNoTracking()
                where binding.IsActive
                join column in db.ColumnDefs.AsNoTracking() on binding.ColumnDefId equals column.Id
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                where sheet.TemplateVersionId == sourceVersionId && !column.IsDeleted
                select new { binding.ColumnDefId, binding.MethodologyId, binding.OutputCode })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (source.Count == 0)
        {
            return 0;
        }

        var targetColumns = columnMap.Values.Concat(monthColumnMap.Values.SelectMany(v => v)).Distinct().ToList();
        var targetBindings = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && targetColumns.Contains(b.ColumnDefId))
            .Select(b => new { b.ColumnDefId, b.MethodologyId, b.OutputCode })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Коди виходів порівнюються без урахування регістру — як і в CalculationBindingStore.
        var carried = targetBindings
            .Select(b => (b.ColumnDefId, b.MethodologyId, Output: b.OutputCode.ToUpperInvariant()))
            .ToHashSet();

        // ✎ LAND-YEAR L3 (D-13): прив'язка немісячної колонки перенесена, коли КОЖНА з її місячних колонок має прив'язку
        // того самого виходу (12 прив'язок на одну стару — так їх кладе будівник Land, етап YearMonths).
        return source.Count(b =>
        {
            var output = b.OutputCode.ToUpperInvariant();
            if (monthColumnMap.TryGetValue(b.ColumnDefId, out var months) && months.Count > 0)
            {
                return !months.All(n => carried.Contains((n, b.MethodologyId, output)));
            }

            return !columnMap.TryGetValue(b.ColumnDefId, out var target) || !carried.Contains((target, b.MethodologyId, output));
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CollectionMappingRef>> ListCollectionMappingsAsync(
        int sourceVersionId, CancellationToken ct)
    {
        // З'єднання через ColumnDef → TableDef → SheetDef: мапінг власного TemplateVersionId не має.
        var rows = await (
                from map in db.EntityFieldMaps.AsNoTracking()
                where map.TargetKind == Domain.Entities.External.FieldTargetKind.Column && map.TargetColumnDefId != null
                join column in db.ColumnDefs.AsNoTracking() on map.TargetColumnDefId equals (int?)column.Id
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                where sheet.TemplateVersionId == sourceVersionId
                select new
                {
                    map.Id,
                    ColumnDefId = column.Id,
                    map.IsActive,
                    TableDefId = table.Id,
                    map.TargetRowKey,
                    Path = sheet.Code + "." + table.Code + "." + column.Code,
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Id)
            .Select(r => new CollectionMappingRef(r.Id, r.ColumnDefId, r.IsActive, r.Path, r.TableDefId, r.TargetRowKey))];
    }

    /// <inheritdoc />
    public async Task<int> RemapCollectionMappingsAsync(
        IReadOnlyDictionary<int, int> columnMap, IReadOnlyCollection<int> mappingIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columnMap);
        ArgumentNullException.ThrowIfNull(mappingIds);

        _ = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "RemapCollectionMappingsAsync викликано поза транзакцією: перенос мапінгів мусить бути атомарним із переносом документів.");

        var pairs = columnMap
            .Where(p => p.Key != p.Value)
            .Select(p => new { o = p.Key, n = p.Value })
            .ToList();
        if (pairs.Count == 0 || mappingIds.Count == 0)
        {
            return 0;
        }

        var json = JsonSerializer.Serialize(pairs);
        var idsJson = JsonSerializer.Serialize(mappingIds);

        // ⚠ Колонка-відповідник — з ІНШОЇ версії (o <> n), тож повторний запуск нічого не міняє. Унікальність
        // `UQ_EntityFieldMap` — за (сутність, поле), а не за колонкою: переніс її не порушує.
        return await db.Database
            .ExecuteSqlAsync($"""
                UPDATE m
                SET    m.TargetColumnDefId = x.n
                FROM   ext.EntityFieldMap m
                JOIN   OPENJSON({json}) WITH (o int, n int) x ON x.o = m.TargetColumnDefId
                JOIN   OPENJSON({idsJson}) WITH (id int '$') k ON k.id = m.Id
                WHERE  m.TargetKind = 0
                """, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProjectsOnVersion> CountOtherProjectsOnVersionAsync(
        int projectId, int versionId, CancellationToken ct)
    {
        // ✎ B74 P3 (D-576): «на версії» — пара (проєкт, шаблон версії): рядок doc.ProjectTemplate з цією чинною версією,
        // або основний шаблон проєкту на ній (Project.TemplateVersionId; рядка пари в старих даних може не бути). Документи —
        // лише документи цього шаблону (TemplateId = шаблон; NULL — коли шаблон основний), а не всі документи проєкту.
        var templateId = await db.TemplateVersions.AsNoTracking()
            .Where(v => v.Id == versionId).Select(v => v.TemplateId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var pairs = db.ProjectTemplates.AsNoTracking()
            .Where(t => t.TemplateId == templateId && t.CurrentTemplateVersionId == versionId);

        var projects = await db.Projects.AsNoTracking()
            .CountAsync(p => p.Id != projectId
                             && (p.TemplateVersionId == versionId || pairs.Any(t => t.ProjectId == p.Id)), ct)
            .ConfigureAwait(false);
        if (projects == 0)
        {
            return new ProjectsOnVersion(0, 0);
        }

        var documents = await (
                from d in db.Documents.AsNoTracking()
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                where p.Id != projectId
                      && ((p.TemplateVersionId == versionId && (d.TemplateId == null || d.TemplateId == templateId))
                          || (d.TemplateId == templateId && pairs.Any(t => t.ProjectId == p.Id)))
                select d.Id)
            .CountAsync(ct)
            .ConfigureAwait(false);

        return new ProjectsOnVersion(projects, documents);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventMapRef>> ListEventMapsAsync(
        IReadOnlyCollection<long> documentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (documentIds.Count == 0)
        {
            return [];
        }

        var ids = documentIds.ToList();
        var maps = await db.SourceEventMaps.AsNoTracking()
            .Where(m => ids.Contains(m.DocumentId))
            .Select(m => new { m.Id, m.SourceEntityId, m.DocumentId, m.TableDefId, m.IsActive })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (maps.Count == 0)
        {
            return [];
        }

        var mapIds = maps.Select(m => m.Id).ToList();
        var fields = await db.SourceEventFieldMaps.AsNoTracking()
            .Where(f => mapIds.Contains(f.SourceEventMapId))
            .Select(f => new { f.Id, f.SourceEventMapId, f.TargetColumnDefId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byMap = fields.ToLookup(f => f.SourceEventMapId);

        return [.. maps.Select(m => new EventMapRef(
            m.Id, m.SourceEntityId, m.DocumentId, m.TableDefId, m.IsActive,
            [.. byMap[m.Id].Select(f => new EventFieldRef(f.Id, f.TargetColumnDefId))]))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RowWindowMapRef>> ListRowWindowMapsAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableDefIds);
        if (tableDefIds.Count == 0)
        {
            return [];
        }

        var ids = tableDefIds.ToList();
        return await db.RowWindowMaps.AsNoTracking()
            .Where(m => ids.Contains(m.TableDefId))
            .OrderBy(m => m.Id)
            .Select(m => new RowWindowMapRef(
                m.Id, m.TableDefId, m.TargetColumnDefId, m.StartColumnDefId, m.EndColumnDefId,
                m.SelectorColumnDefId, m.IsActive))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemapEventAndWindowMapsAsync(
        int projectId, ExtendedMappingRemap remap, IReadOnlyDictionary<int, int> columnMap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remap);
        ArgumentNullException.ThrowIfNull(columnMap);

        _ = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "RemapEventAndWindowMapsAsync викликано поза транзакцією: перенос мапінгів мусить бути атомарним із переносом документів.");

        using var timeout = ExtendTimeout();

        if (remap.Events.Count > 0)
        {
            var maps = JsonSerializer.Serialize(remap.Events.Select(e => new { id = e.MapId, t = e.TableDefId }));
            var fields = JsonSerializer.Serialize(
                remap.Events.SelectMany(e => e.Fields).Select(f => new { id = f.FieldId, n = f.ColumnDefId }));

            await db.Database
                .ExecuteSqlAsync($"""
                    UPDATE m
                    SET    m.TableDefId = x.t
                    FROM   ext.SourceEventMap m
                    JOIN   OPENJSON({maps}) WITH (id int, t int) x ON x.id = m.Id
                    """, ct)
                .ConfigureAwait(false);
            await db.Database
                .ExecuteSqlAsync($"""
                    UPDATE f
                    SET    f.TargetColumnDefId = x.n
                    FROM   ext.SourceEventFieldMap f
                    JOIN   OPENJSON({fields}) WITH (id int, n int) x ON x.id = f.Id
                    """, ct)
                .ConfigureAwait(false);
        }

        if (remap.Windows.Count > 0)
        {
            // ⚠ Таблиця й усі колонки — одним оператором: FK_RWM_Target/Start/End/Selector складені
            // (TableDefId, ColumnDefId), і перевіряються по закінченні оператора.
            var windows = JsonSerializer.Serialize(remap.Windows.Select(w => new
            {
                id = w.MapId,
                t = w.TableDefId,
                tg = w.TargetColumnDefId,
                s = w.StartColumnDefId,
                e = w.EndColumnDefId,
                sel = w.SelectorColumnDefId,
            }));

            await db.Database
                .ExecuteSqlAsync($"""
                    UPDATE w
                    SET    w.TableDefId = x.t, w.TargetColumnDefId = x.tg, w.StartColumnDefId = x.s,
                           w.EndColumnDefId = x.e, w.SelectorColumnDefId = x.sel
                    FROM   ext.RowWindowMap w
                    JOIN   OPENJSON({windows}) WITH (id int, t int, tg int, s int, e int, sel int) x ON x.id = w.Id
                    """, ct)
                .ConfigureAwait(false);
        }

        // Провенанс підтягувань екземплярів ЦЬОГО проєкту: «чинний запис комірки» шукається за колонкою, і
        // без перенесення KeptManual/попередній статус для переїхалих комірок пропали б. PeriodKey — з періодів
        // проєкту (партиціонована таблиця).
        var pairs = columnMap.Where(p => p.Key != p.Value).Select(p => new { o = p.Key, n = p.Value }).ToList();
        if (remap.Windows.Count > 0 && pairs.Count > 0)
        {
            var json = JsonSerializer.Serialize(pairs);
            await db.Database
                .ExecuteSqlAsync($"""
                    UPDATE v
                    SET    v.ColumnDefId = x.n
                    FROM   ext.RowWindowValue v
                    JOIN   doc.Period p        ON p.ProjectId = {projectId} AND p.PeriodKey = v.PeriodKey
                    JOIN   doc.TableInstance ti ON ti.PeriodKey = v.PeriodKey AND ti.Id = v.TableInstanceId
                    JOIN   doc.Document d       ON d.Id = ti.DocumentId AND d.ProjectId = {projectId}
                    JOIN   OPENJSON({json}) WITH (o int, n int) x ON x.o = v.ColumnDefId
                    """, ct)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListForeignMethodologyKeysAsync(
        int targetVersionId, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Методології з активною прив'язкою на таблицях ЦІЛЬОВОЇ версії (власного TemplateVersionId прив'язка не має).
        var methodologyIds = await (
                from binding in db.CalculationBindings.AsNoTracking()
                where binding.IsActive
                join column in db.ColumnDefs.AsNoTracking() on binding.ColumnDefId equals column.Id
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                where sheet.TemplateVersionId == targetVersionId && !column.IsDeleted
                select binding.MethodologyId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (methodologyIds.Count == 0)
        {
            return [];
        }

        // Історичні періоди беруть старіші версії методології, тож перевіряються ВСІ з початком дії.
        var versions = await (
                from v in db.MethodologyVersions.AsNoTracking()
                join m in db.Methodologies.AsNoTracking() on v.MethodologyId equals m.Id
                where methodologyIds.Contains(v.MethodologyId) && v.EffectiveFrom != null
                select new { v.Id, MethodologyCode = m.Code, v.Version })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return [];
        }

        var versionIds = versions.Select(v => v.Id).ToList();
        var rules = await db.MethodologyRules.AsNoTracking()
            .Where(r => versionIds.Contains(r.MethodologyVersionId) && r.IsActive)
            .Select(r => new { r.MethodologyVersionId, r.Code, r.MatchJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var inputs = await db.MethodologyRequiredInputs.AsNoTracking()
            .Where(i => versionIds.Contains(i.MethodologyVersionId))
            .Select(i => new { i.MethodologyVersionId, i.ColumnDefId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // (версія методології, звідки ключ, ColumnDefId)
        var refs = rules
            .SelectMany(r => MatchJsonColumnIds(r.MatchJson).Select(id => (r.MethodologyVersionId, Source: "rule " + r.Code, ColumnId: id)))
            .Concat(inputs.Select(i => (i.MethodologyVersionId, Source: "required input", ColumnId: i.ColumnDefId)))
            .ToList();
        if (refs.Count == 0)
        {
            return [];
        }

        var wanted = refs.Select(r => r.ColumnId).Distinct().ToList();
        var templateId = await db.TemplateVersions.AsNoTracking()
            .Where(v => v.Id == targetVersionId).Select(v => v.TemplateId).SingleAsync(ct).ConfigureAwait(false);
        var columns = await (
                from column in db.ColumnDefs.AsNoTracking()
                where wanted.Contains(column.Id)
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                join version in db.TemplateVersions.AsNoTracking() on sheet.TemplateVersionId equals version.Id
                where version.TemplateId == templateId && version.Id != targetVersionId
                select new { column.Id, Path = sheet.Code + "." + table.Code + "." + column.Code })
            .ToDictionaryAsync(c => c.Id, c => c.Path, ct)
            .ConfigureAwait(false);

        // ⛔ C1: ключ, чия колонка має відповідник за шляхом (аркуш/таблиця/колонка) у цільовій версії, перекладається
        // при читанні (`MethodologyKeyLocalizer`) і перенос НЕ блокує. Відмова лишається лише для ключів, які в
        // цільовій версії втратили колонку (вилучена чи змінений код) — їх переклад неможливий, і правило/вхід
        // мовчки перестали б діяти.
        var translatable = await new ColumnPathMapper(db)
            .MapToVersionAsync(columns.Keys.ToList(), targetVersionId, ct)
            .ConfigureAwait(false);

        var byVersion = versions.ToDictionary(v => v.Id);
        return [.. refs
            .Where(r => columns.ContainsKey(r.ColumnId) && !translatable.ContainsKey(r.ColumnId))
            .Select(r => $"{byVersion[r.MethodologyVersionId].MethodologyCode} {byVersion[r.MethodologyVersionId].Version} "
                         + $"{r.Source}: {columns[r.ColumnId]}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(limit)];
    }

    /// <summary>Ключі плаского предиката-<c>MatchJson</c>, що є числами (<c>ColumnDefId</c>); зламаний JSON — порожньо.</summary>
    private static IEnumerable<int> MatchJsonColumnIds(string matchJson)
    {
        try
        {
            using var document = JsonDocument.Parse(matchJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return [.. document.RootElement.EnumerateObject()
                .Select(p => int.TryParse(
                    p.Name, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : (int?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<GrantedUsers> ListUsersWithGrantsAsync(VersionMigrationPlan plan, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var keys = plan.Sheets.Keys.Select(i => new { k = (byte)ResourceKind.Sheet, i })
            .Concat(plan.Tables.Keys.Select(i => new { k = (byte)ResourceKind.Table, i }))
            .Concat(plan.Columns.Select(c => new { k = (byte)ResourceKind.Column, i = c.SourceColumnDefId }))
            .Concat(plan.MonthColumns.Select(c => c.SourceColumnDefId).Distinct()
                .Select(i => new { k = (byte)ResourceKind.Column, i }))
            .ToList();
        if (keys.Count == 0)
        {
            return new GrantedUsers([], Overflow: false);
        }

        var json = JsonSerializer.Serialize(keys);
        var ids = await db.Database
            .SqlQuery<int>($"""
                SELECT DISTINCT a.UserId AS Value
                FROM   sec.RoleAssignment a
                JOIN   sec.ResourceGrant g ON g.RoleId = a.RoleId
                JOIN   OPENJSON({json}) WITH (k tinyint, i int) x ON x.k = g.ResourceKind AND x.i = g.ResourceId
                WHERE  a.UserId IS NOT NULL
                """)
            .OrderBy(id => id)
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Зайвий (limit+1-й) рядок — ознака переповнення: решту не видно, тож скидається весь кеш.
        return ids.Count > limit
            ? new GrantedUsers([.. ids.Take(limit)], Overflow: true)
            : new GrantedUsers(ids, Overflow: false);
    }

    /// <summary>Рядок підрахунку значень комірок.</summary>
    private sealed record CellUsageRow(int ColumnDefId, int? RowDefId, long Values);

    /// <summary>Рядок підрахунку значень шапки.</summary>
    private sealed record HeaderUsageRow(int HeaderFieldDefId, long Values);

    // Сам перенос. Порядок — від листків до коренів, бо ключі складені: FK_CellValue_Column — пара
    // (TableDefId, ColumnDefId), тож комірка міняє обидва поля одним оператором. Те, чому в новій версії
    // немає місця, прибирається: план уже гарантував, що там немає жодного введеного значення (режим Safe
    // інакше відмовляє), тобто зникають лише порожні комірки й порожні рядки, екземпляри таблиць і склад аркушів.
    // ⚠ Історія лишається на тих описах, де її записали: calc.SubmissionSnapshot — зріз ПОДАННЯ, який і має
    // посилатися на версію, за якою подавали; aud.* — незмінний журнал.
    //
    // ── Команди переносу (N-3, Б1) ──────────────────────────────────────────────────────────────────
    // Раніше весь перенос був ОДНИМ пакетом SQL під глобальним CommandTimeout (60 с): проєкт із ~138 тис.
    // значень (Land RC11) не вконувався і давав 500. N-3 розбив його на пачки TOP (@batch) в ОДНІЙ транзакції;
    // на 4,87 млн комірок це стало квадратичним (див. RangeCommand) і давало величезний журнал та годинний відкат.
    // Б1 (2026-10-21): комірки/рядки/індекс пошуку/валідація — діапазонами за ключем з КОМІТОМ на кожен діапазон
    // (власне з'єднання, ~50 тис. рядків TableRow), решта — як і була, в транзакції переносу, яка весь час відкрита.
    //
    // ⚠ ПОСЛАБЛЕННЯ АТОМАРНОСТІ (свідоме, рішення координатора 2026-10-21):
    //  • зафіксовані діапазони НЕ відкочуються разом із транзакцією переносу. Якщо перенос обірвано (збій, Cancel,
    //    перезапуск процесу), проєкт лишається на СТАРІЙ версії, але частина комірок уже лежить на колонках ЦІЛЬОВОЇ.
    //    Відкат — лише бекапом бази; «зворотна мапа» не пише: її не відновить порожні комірки/рядки, що вже видалені, і
    //    потребувала б старих TableDefId у плані. Єдиний безпечний шлях — повторити той самий перенос: усі предикати
    //    ідемпотентні, а планувальник бачить значення на колонках цільової версії як «уже перенесені» (не як втрату);
    //  • зміна версії проєкту, склад аркушів, екземпляри таблиць, гранти й аудит лишаються атомарними з комітом
    //    транзакції переносу — «проєкт на новій версії» і «усе перенесено» не розходяться.
    // ⛔ Гарантія «документ не читається/не пишеться під час переносу»: писарі беруть структуру документа спільно
    // (applock транзакційний), а транзакція переносу тримає її ВИНЯТКОВО від початку до коміту, тож 409 ECR-DOC-4091
    // працює як раніше. Читання не блокуються (RCSI): під час важкої частини читач може побачити частину комірок
    // порожніми — доки перенос у черзі задач, клієнт показує прогрес («jobs.migrate*»), і це лишається відомим ризиком.

    /// <summary>
    /// Прелюдія кожної команди: мапи, документи, періоди й екземпляри таблиць проєкту — у табличних змінних,
    /// з параметрів. Стану між командами немає (#-таблиці були б DDL, який сторож не пускає).
    /// </summary>
    private const string Prelude = """
        SET XACT_ABORT ON;

        -- ✎ B74 P3 (D-576): лише документи шаблону пари (@templateId; основний — і TemplateId NULL). Кожен оператор
        -- переносу йде через @docs/@instances, тож документи інших шаблонів проєкту не зачіпаються. NULL — увесь проєкт.
        DECLARE @docs TABLE (Id bigint PRIMARY KEY);
        INSERT @docs (Id) SELECT Id FROM doc.Document
        WHERE  ProjectId = @projectId
          AND  (@templateId IS NULL OR TemplateId = @templateId OR (TemplateId IS NULL AND @isPrimary = 1));

        DECLARE @periods TABLE (PeriodKey int PRIMARY KEY);
        INSERT @periods (PeriodKey) SELECT PeriodKey FROM doc.Period WHERE ProjectId = @projectId AND PeriodKey >= @fromPeriod;

        DECLARE @sheetMap TABLE (o int PRIMARY KEY, n int NOT NULL);
        INSERT @sheetMap SELECT o, n FROM OPENJSON(@sheets) WITH (o int, n int);

        DECLARE @tableMap TABLE (o int PRIMARY KEY, n int NOT NULL);
        INSERT @tableMap SELECT o, n FROM OPENJSON(@tables) WITH (o int, n int);

        DECLARE @columnMap TABLE (o int PRIMARY KEY, n int NOT NULL, t int NOT NULL);
        INSERT @columnMap SELECT o, n, t FROM OPENJSON(@columns) WITH (o int, n int, t int);

        -- ✎ LAND-YEAR L3: немісячна колонка o × місяць m → місячна колонка n таблиці t (порожня поза переносом на рік).
        DECLARE @monthColumnMap TABLE (o int NOT NULL, m int NOT NULL, n int NOT NULL, t int NOT NULL, PRIMARY KEY (o, m));
        INSERT @monthColumnMap SELECT o, m, n, t FROM OPENJSON(@monthColumns) WITH (o int, m int, n int, t int);

        DECLARE @rowMap TABLE (o int PRIMARY KEY, n int NOT NULL);
        INSERT @rowMap SELECT o, n FROM OPENJSON(@rows) WITH (o int, n int);

        DECLARE @headerMap TABLE (o int PRIMARY KEY, n int NOT NULL);
        INSERT @headerMap SELECT o, n FROM OPENJSON(@headers) WITH (o int, n int);

        DECLARE @instances TABLE (PeriodKey int NOT NULL, Id bigint NOT NULL, TableDefId int NOT NULL,
                                  PRIMARY KEY (PeriodKey, Id));
        INSERT @instances (PeriodKey, Id, TableDefId)
        SELECT ti.PeriodKey, ti.Id, ti.TableDefId
        FROM   doc.TableInstance ti
        JOIN   @periods p ON p.PeriodKey = ti.PeriodKey
        JOIN   @docs d    ON d.Id = ti.DocumentId;

        """;

    /// <summary>
    /// Пачкові оператори в порядку виконання (порядок — від листків до коренів, як і був у єдиному пакеті).
    /// Кожен — РІВНО один оператор з <c>TOP (@batch)</c>; після нього виконавець бере <c>@@ROWCOUNT</c> в
    /// <c>@changed</c>, і пачка, що змінила менше за <see cref="BatchRows"/>, була останньою.
    /// </summary>
    /// <remarks>
    /// ⚠ Предикат кожного оператора мусить виключати вже зроблене, інакше цикл не скінчиться:
    /// перенесена комірка/рядок/значення індексу має колонку-відповідник (<c>m.o &lt;&gt; m.n</c> — відповідник
    /// завжди з ІНШОЇ версії), знятий рядок — <c>ModifiedAt = @now</c>, видалене зникло.
    /// </remarks>
    private const string RangeCommandText = """
        DECLARE @c bigint = 0;

        -- 0. ✎ LAND-YEAR L3: немісячна колонка «копії на місяць» → місячна колонка МІСЯЦЯ ПЕРІОДУ (m = PeriodKey % 100),
        -- НА МІСЦІ: власник місячної колонки m річної версії — екземпляр періоду (рік, m), тобто саме той, де значення
        -- вже лежить. Перед мапою 1:1; перенесене стоїть на колонці цільової версії (mm.n ніколи не є mm.o) — повтор
        -- діапазону нічого не знаходить. Поза переносом на рік мапа порожня.
        UPDATE cv
        SET    cv.ColumnDefId = mm.n, cv.TableDefId = mm.t
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r      ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @monthColumnMap mm ON mm.o = cv.ColumnDefId AND mm.m = cv.PeriodKey % 100
        WHERE  cv.PeriodKey = @p AND cv.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        UPDATE cs
        SET    cs.ColumnDefId = mm.n
        FROM   doc.CellStyle cs
        JOIN   doc.TableRow r      ON r.PeriodKey = cs.PeriodKey AND r.Id = cs.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @monthColumnMap mm ON mm.o = cs.ColumnDefId AND mm.m = cs.PeriodKey % 100
        WHERE  cs.PeriodKey = @p AND cs.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        UPDATE fo
        SET    fo.ColumnDefId = mm.n
        FROM   doc.CellFormulaOverride fo
        JOIN   doc.TableRow r      ON r.PeriodKey = fo.PeriodKey AND r.Id = fo.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @monthColumnMap mm ON mm.o = fo.ColumnDefId AND mm.m = fo.PeriodKey % 100
        WHERE  fo.PeriodKey = @p AND fo.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        -- 1. Комірки: у колонку-відповідник, разом із таблицею (складений FK). Діапазон за ключем кластерного
        -- індексу (PeriodKey, TableRowId) — seek, а не скан з початку.
        UPDATE cv
        SET    cv.ColumnDefId = m.n, cv.TableDefId = m.t
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r      ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @columnMap m    ON m.o = cv.ColumnDefId
        WHERE  cv.PeriodKey = @p AND cv.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  m.o <> m.n
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        -- Порожні комірки колонок, яких нова версія не має.
        DELETE cv
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r      ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  cv.PeriodKey = @p AND cv.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  NOT EXISTS (SELECT 1 FROM @columnMap m WHERE m.n = cv.ColumnDefId)
          AND  NOT EXISTS (SELECT 1 FROM @monthColumnMap mm WHERE mm.n = cv.ColumnDefId OR mm.o = cv.ColumnDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        -- 1б. CELL-STYLE L3b: користувацьке оформлення комірок (doc.CellStyle) їде за колонкою так само, як
        -- значення; без місця в новій версії — знімається (колір порожньої комірки колонки, якої немає, нікому
        -- не потрібен). Зовнішніх ключів на колонку немає (див. `CellStyle`), тож порядок тут — лише послідовність.
        UPDATE cs
        SET    cs.ColumnDefId = m.n
        FROM   doc.CellStyle cs
        JOIN   doc.TableRow r      ON r.PeriodKey = cs.PeriodKey AND r.Id = cs.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @columnMap m    ON m.o = cs.ColumnDefId
        WHERE  cs.PeriodKey = @p AND cs.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  m.o <> m.n
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        DELETE cs
        FROM   doc.CellStyle cs
        JOIN   doc.TableRow r      ON r.PeriodKey = cs.PeriodKey AND r.Id = cs.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  cs.PeriodKey = @p AND cs.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  NOT EXISTS (SELECT 1 FROM @columnMap m WHERE m.n = cs.ColumnDefId)
          AND  NOT EXISTS (SELECT 1 FROM @monthColumnMap mm WHERE mm.n = cs.ColumnDefId OR mm.o = cs.ColumnDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        -- 1в. ✎ D-461: позначки «ручне значення замість формули» (doc.CellFormulaOverride) їдуть за колонкою, як
        -- значення; без місця в новій версії — знімаються. Те, чого цільова формула не дозволяє, знімає T2
        -- (`FormulaOverrideMigration`), бо дозвіл — властивість формули, а не колонки.
        UPDATE fo
        SET    fo.ColumnDefId = m.n
        FROM   doc.CellFormulaOverride fo
        JOIN   doc.TableRow r      ON r.PeriodKey = fo.PeriodKey AND r.Id = fo.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @columnMap m    ON m.o = fo.ColumnDefId
        WHERE  fo.PeriodKey = @p AND fo.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  m.o <> m.n
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        DELETE fo
        FROM   doc.CellFormulaOverride fo
        JOIN   doc.TableRow r      ON r.PeriodKey = fo.PeriodKey AND r.Id = fo.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  fo.PeriodKey = @p AND fo.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  NOT EXISTS (SELECT 1 FROM @columnMap m WHERE m.n = fo.ColumnDefId)
          AND  NOT EXISTS (SELECT 1 FROM @monthColumnMap mm WHERE mm.n = fo.ColumnDefId OR mm.o = fo.ColumnDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        -- 2. Рядки: описи рядків — за ключем; рядки без місця в новій версії знімаються.
        UPDATE r
        SET    r.RowDefId = m.n
        FROM   doc.TableRow r
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @rowMap m       ON m.o = r.RowDefId
        WHERE  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  m.o <> m.n
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        UPDATE r
        SET    r.IsDeleted = 1, r.ModifiedAt = @now
        FROM   doc.TableRow r
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  r.RowDefId IS NOT NULL
          AND  r.ModifiedAt <> @now
          AND  NOT EXISTS (SELECT 1 FROM @rowMap m WHERE m.n = r.RowDefId)
          AND  EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        -- 3. Екземпляри таблиць, яких нова версія не має: порожні — прибираються повністю.
        DELETE cv
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r      ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  cv.PeriodKey = @p AND cv.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  NOT EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        -- CELL-STYLE L3b: оформлення комірок таблиць, яких немає в новій версії, — до видалення їхніх рядків.
        DELETE cs
        FROM   doc.CellStyle cs
        JOIN   doc.TableRow r      ON r.PeriodKey = cs.PeriodKey AND r.Id = cs.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  cs.PeriodKey = @p AND cs.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  NOT EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        -- ✎ D-461: позначки ручних значень таблиць, яких немає в новій версії, — до видалення їхніх рядків.
        DELETE fo
        FROM   doc.CellFormulaOverride fo
        JOIN   doc.TableRow r      ON r.PeriodKey = fo.PeriodKey AND r.Id = fo.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  fo.PeriodKey = @p AND fo.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  NOT EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        DELETE r
        FROM   doc.TableRow r
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  NOT EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        -- Watermark відновлення: остання виконана межа (період, TableRowId) - у ТІЙ САМІЙ транзакції, що й діапазон.
        -- Рядок проєкту тут не заблоковано (зовнішньої транзакції в важкій частині немає); без маркера - нічого не пише.
        -- Звірка діапазону (P2-3): жодна комірка/оформлення діапазону не лишилась на вихідній колонці, яку мапа переселяє.
        -- Розбіжність - THROW: діапазон відкочується цілим, перенос зупиняється (ECR-CELL-0409), маркер лишається.
        DECLARE @bad bigint;
        SELECT @bad = COUNT_BIG(*)
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r  ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @columnMap m    ON m.o = cv.ColumnDefId
        WHERE  cv.PeriodKey = @p AND cv.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  m.o <> m.n
        OPTION (MAXDOP 1, RECOMPILE);
        IF @bad > 0 THROW 51001, N'Діапазон комірок: частина значень не переселена.', 1;

        SELECT @bad = COUNT_BIG(*)
        FROM   doc.CellStyle cs
        JOIN   doc.TableRow r  ON r.PeriodKey = cs.PeriodKey AND r.Id = cs.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @columnMap m    ON m.o = cs.ColumnDefId
        WHERE  cs.PeriodKey = @p AND cs.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  m.o <> m.n
        OPTION (MAXDOP 1, RECOMPILE);
        IF @bad > 0 THROW 51001, N'Діапазон оформлення комірок: частина записів не переселена.', 1;

        -- ✎ LAND-YEAR L3: жодне значення/оформлення діапазону не лишилось на немісячній колонці, яку мапа місяців
        -- переселяє (DELETE вище їх не чіпає навмисно: розбіжність — відкат діапазону, а не мовчазне видалення).
        SELECT @bad = COUNT_BIG(*)
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r  ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  cv.PeriodKey = @p AND cv.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  EXISTS (SELECT 1 FROM @monthColumnMap mm WHERE mm.o = cv.ColumnDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        IF @bad > 0 THROW 51001, N'Діапазон комірок: частина значень не переселена в місячні колонки.', 1;

        SELECT @bad = COUNT_BIG(*)
        FROM   doc.CellStyle cs
        JOIN   doc.TableRow r  ON r.PeriodKey = cs.PeriodKey AND r.Id = cs.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  cs.PeriodKey = @p AND cs.TableRowId BETWEEN @lo AND @hi
          AND  r.PeriodKey = @p AND r.Id BETWEEN @lo AND @hi
          AND  EXISTS (SELECT 1 FROM @monthColumnMap mm WHERE mm.o = cs.ColumnDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        IF @bad > 0 THROW 51001, N'Діапазон оформлення комірок: частина записів не переселена в місячні колонки.', 1;

        IF @leaseOwner IS NOT NULL
        BEGIN
            UPDATE doc.Project
            SET    ExternalSettingsJson = JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(ExternalSettingsJson, '$.wmCellsPeriod', @p), '$.wmCellsHi', @hi),
                       '$.leaseUntil', CONVERT(varchar(30), DATEADD(MINUTE, @leaseMinutes, SYSUTCDATETIME()), 126))
            WHERE  Id = @projectId AND ISJSON(ExternalSettingsJson) = 1
              AND  JSON_VALUE(ExternalSettingsJson, '$.migratingTo') IS NOT NULL
              AND  JSON_VALUE(ExternalSettingsJson, '$.owner') = @leaseOwner;
            IF @@ROWCOUNT = 0 THROW 51000, N'Оренду важкої частини переносу втрачено.', 1;
        END

        SET @changed = @c;
        """;

    /// <summary>
    /// Команда діапазону комірок/рядків: прелюдія (мапи, документи, екземпляри — ОДИН раз на діапазон, а не на пачку
    /// по 5 тис.) і дев'ять операторів у порядку «від листків до коренів» над рядками <c>TableRowId ∈ [@lo, @hi]</c> періоду
    /// <c>@p</c>. Усі предикати ідемпотентні (перенесене має колонку/опис ЦІЛЬОВОЇ версії, <c>m.o ≠ m.n</c>; видалене зникло),
    /// тож повтор діапазону після обриву нічого не псує.
    /// </summary>
    /// <remarks>
    /// ⚠ Раніше це були дев'ять циклів <c>UPDATE/DELETE TOP (@batch)</c> з предикатом без ключа: кожна пачка
    /// перечитувала кластерний індекс <c>(PeriodKey, TableRowId, ColumnDefId)</c> з початку крізь уже перенесені
    /// («мертві») рядки — квадратичність (≈ 75 тис. комірок/хв на старті → ≈ 15 тис./хв наприкінці на 4,87 млн).
    /// Діапазон за ключем читає лише своє. ⚠ Окремі <c>UPDATE</c>-и комірок і оформлення лишилися окремими: один
    /// оператор не може міняти дві таблиці, а об'єднання через <c>OUTPUT</c> у тимчасову таблицю — DDL.
    /// </remarks>
    private static readonly string RangeCommand = Prelude + RangeCommandText;

    /// <summary>
    /// Діапазон індексу пошуку (<c>doc.DocumentIndexValue</c>, ключ <c>Id</c>): зниклі колонки видаляються, решта
    /// перейменовується. ⚠ <c>DELETE</c> не чіпає рядки з колонкою ЦІЛЬОВОЇ версії (<c>m.n</c>): повтор діапазону після
    /// обриву не повинен видаляти вже перенесене.
    /// </summary>
    private static readonly string IndexRangeCommand = Prelude + """
        DECLARE @c bigint = 0;

        DELETE x
        FROM   doc.DocumentIndexValue x
        JOIN   @docs d ON d.Id = x.DocumentId
        WHERE  x.Id BETWEEN @lo AND @hi
          AND  NOT EXISTS (SELECT 1 FROM @columnMap m WHERE m.o = x.ColumnDefId OR m.n = x.ColumnDefId)
          -- ✎ LAND-YEAR L3: індекс без періоду не знає місяця — значення немісячної колонки відходять разом з нею,
          -- а вже перенесене на місячну (mm.n) лишається.
          AND  NOT EXISTS (SELECT 1 FROM @monthColumnMap mm WHERE mm.n = x.ColumnDefId)
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        UPDATE x
        SET    x.ColumnDefId = m.n
        FROM   doc.DocumentIndexValue x
        JOIN   @docs d      ON d.Id = x.DocumentId
        JOIN   @columnMap m ON m.o = x.ColumnDefId
        WHERE  x.Id BETWEEN @lo AND @hi
          AND  m.o <> m.n
        OPTION (MAXDOP 1, RECOMPILE);
        SET @c += @@ROWCOUNT;

        IF @leaseOwner IS NOT NULL
        BEGIN
            UPDATE doc.Project
            SET    ExternalSettingsJson = JSON_MODIFY(JSON_MODIFY(ExternalSettingsJson, '$.wmIndexHi', @hi),
                       '$.leaseUntil', CONVERT(varchar(30), DATEADD(MINUTE, @leaseMinutes, SYSUTCDATETIME()), 126))
            WHERE  Id = @projectId AND ISJSON(ExternalSettingsJson) = 1
              AND  JSON_VALUE(ExternalSettingsJson, '$.migratingTo') IS NOT NULL
              AND  JSON_VALUE(ExternalSettingsJson, '$.owner') = @leaseOwner;
            IF @@ROWCOUNT = 0 THROW 51000, N'Оренду важкої частини переносу втрачено.', 1;
        END

        SET @changed = @c;
        """;

    /// <summary>
    /// Результати валідації описували стару структуру: їх перераховують заново (їх багато — пачками, які зникають).
    /// ⛔ Видаляються в ТРАНЗАКЦІЇ ПЕРЕМИКАННЯ версії (T2), а не в діапазонах: обрив/відмова до коміту T2 лишає результати
    /// для ще чинної старої версії (P2-1), і вони зникають лише разом з перемиканням.
    /// </summary>
    private const string ValidationBatchSql = """
        DELETE TOP (@batch) v
        FROM   wf.ValidationResult v
        JOIN   @docs d ON d.Id = v.DocumentId
        WHERE  v.PeriodKey >= @fromPeriod;
        """;

    /// <summary>
    /// ✎ LAND-YEAR L3 (B-static): немісячний ввід → якір року (після прелюдії). Якір — найменший ключ календаря проєкту
    /// (<c>doc.Period</c>) у році (той самий, що в <c>MonthColumnRouter</c>/<c>PeriodCalendar.AnchorOf</c>), і лише якщо він у
    /// межах переносу (<c>@periods</c>). Кандидат — введена (<c>IsCalculated = 0</c>, без позначки D-461) непорожня комірка
    /// живого рядка пізнішого місяця того самого року, чия колонка (після мапи) — у <c>@anchorColumns</c>, а значення в
    /// якорі (той самий документ, копія аркуша, таблиця після мапи й <c>RowKey</c>) немає. Групи «рядок якоря × колонка»:
    /// одне значення по всіх місяцях — копія НАЙПІЗНІШОГО; різні — конфлікт.
    /// </summary>
    /// <remarks>
    /// ✎ Land doc 96: якір може не мати ні екземпляра таблиці (T8_00 є лише у 202609..202611), ні рядків — тоді
    /// екземпляр і рядки СТВОРЮЮТЬСЯ в якорі (лише для груп, які копіюються): екземпляр — <c>doc.TableInstanceSeq</c>,
    /// <c>CreatedAt = ModifiedAt = @now</c>, <c>SheetCopyId</c> як у місяця (так, як <c>RowStore.CreateInstancesCoreAsync</c>);
    /// рядок — <c>doc.TableRowSeq</c> і <c>@now</c>, як крок нових рядків (<c>NewRowsBatchSql</c>), з <c>RowKey</c>,
    /// <c>RowDefId</c> і <c>Ordinal</c> НАЙПІЗНІШОГО місяця, де цей рядок живий. Знятий рядок якоря з тим самим ключем
    /// (<c>UQ_TableRow_Key</c>) відновлюється, а не дублюється. Рядки, які heavy-частина знімає (<c>RowDefId</c> без
    /// відповідника), не беруться ні до, ні після неї — план однаковий.
    /// <c>@apply = 0</c> (сухий прогін, план) — три набори: (копій, конфліктних комірок, рядків, екземплярів), перелік
    /// значень перших <c>@maxConflicts</c> конфліктних груп і пари «документ × таблиця × якір», куди писався б запис
    /// (для перевірки поданих/затверджених аркушів). <c>@apply = 1</c> (T2, після екземплярів і нових рядків) — запис:
    /// екземпляри, рядки, потім комірки (порожня заглушка оновлюється, відсутня вставляється, рядок — <c>ModifiedAt</c>);
    /// кількості — у <c>@copied</c>, <c>@rowsCreated</c>, <c>@instancesCreated</c>. Конфліктні групи не пишуться ніколи.
    /// </remarks>
    private const string AnchorFillSql = """
        DECLARE @anchorCols TABLE (n int PRIMARY KEY);
        INSERT @anchorCols (n) SELECT DISTINCT CAST(value AS int) FROM OPENJSON(@anchorColumns);

        DECLARE @anchors TABLE (Yr int PRIMARY KEY, PeriodKey int NOT NULL);
        INSERT @anchors (Yr, PeriodKey)
        SELECT p.PeriodKey / 100, MIN(p.PeriodKey)
        FROM   doc.Period p
        WHERE  p.ProjectId = @projectId
        GROUP BY p.PeriodKey / 100;
        DELETE a FROM @anchors a WHERE NOT EXISTS (SELECT 1 FROM @periods s WHERE s.PeriodKey = a.PeriodKey);

        -- ✎ LAND-YEAR L3 (a′, рішення координатора): УСІ відсутні екземпляри таблиць цільової версії в якорі року — як
        -- RowStore.FindMissingTableDefsAsync для якоря: склад документа (оригінал і КОЖНА копія аркуша, SheetCopyId = Id
        -- рядка складу копії), аркуш цільової версії (через мапу аркушів або вже перемаплений WorkflowSql), живі таблиці;
        -- F-13: документ без жодного гарячого екземпляра в якорі, але з архівними — заархівований рік, нічого не створюється.
        -- ⚠ Таблиці з активною прив'язкою дозволу «рядки» (PM-10) пропускаються: рядки з дозволу планує сервіс дозволів лише
        -- на шляху відкриття (RowStore.CreateInstancesCoreAsync); такий екземпляр створить перше відкриття, як і раніше.
        DECLARE @missingInst TABLE (AnchorPeriod int NOT NULL, DocumentId bigint NOT NULL, CopyId bigint NOT NULL, TableDefId int NOT NULL,
                                    PRIMARY KEY (AnchorPeriod, DocumentId, CopyId, TableDefId));
        INSERT @missingInst (AnchorPeriod, DocumentId, CopyId, TableDefId)
        SELECT DISTINCT a.PeriodKey, s.DocumentId, CASE WHEN s.CopyOfId IS NULL THEN 0 ELSE s.Id END, td.Id
        FROM   @anchors a
        CROSS JOIN doc.DocumentSheet s
        JOIN   @docs d ON d.Id = s.DocumentId
        CROSS APPLY (SELECT COALESCE((SELECT m.n FROM @sheetMap m WHERE m.o = s.SheetDefId), s.SheetDefId) AS SheetId) k
        JOIN   cfg.TableDef td ON td.SheetDefId = k.SheetId AND td.IsDeleted = 0
        WHERE  EXISTS (SELECT 1 FROM @sheetMap m WHERE m.n = k.SheetId)
          AND  NOT EXISTS (SELECT 1 FROM doc.TableInstance ti
                           WHERE ti.PeriodKey = a.PeriodKey AND ti.DocumentId = s.DocumentId
                             AND ISNULL(ti.SheetCopyId, 0) = CASE WHEN s.CopyOfId IS NULL THEN 0 ELSE s.Id END
                             AND (ti.TableDefId = td.Id OR EXISTS (SELECT 1 FROM @tableMap tm WHERE tm.o = ti.TableDefId AND tm.n = td.Id)))
          AND  NOT EXISTS (SELECT 1 FROM cfg.PermitSheetBinding b WHERE b.SheetTableDefId = td.Id AND b.IsActive = 1 AND b.Mode = 0)
          AND  NOT (NOT EXISTS (SELECT 1 FROM doc.TableInstance h WHERE h.PeriodKey = a.PeriodKey AND h.DocumentId = s.DocumentId)
                    AND EXISTS (SELECT 1 FROM arc.TableInstance x WHERE x.PeriodKey = a.PeriodKey AND x.DocumentId = s.DocumentId));

        -- ⛔ B69 «архів незмінний» (рішення людини 13.11): той самий охоронець F-13, що й для @missingInst, — документ без
        -- жодного гарячого екземпляра в якорі, але з arc.TableInstance, має заархівований якір. Для нього крок копії
        -- пропускається ЦІЛКОМ (ні екземплярів, ні рядків, ні комірок) — пропуск, не відмова, як і для (a′). Рахується ДО
        -- створення екземплярів (на заархівований документ воно однаково не пише).
        DECLARE @archivedDocs TABLE (AnchorPeriod int NOT NULL, DocumentId bigint NOT NULL, PRIMARY KEY (AnchorPeriod, DocumentId));
        INSERT @archivedDocs (AnchorPeriod, DocumentId)
        SELECT a.PeriodKey, d.Id
        FROM   @anchors a
        CROSS JOIN @docs d
        WHERE  NOT EXISTS (SELECT 1 FROM doc.TableInstance h WHERE h.PeriodKey = a.PeriodKey AND h.DocumentId = d.Id)
          AND  EXISTS (SELECT 1 FROM arc.TableInstance x WHERE x.PeriodKey = a.PeriodKey AND x.DocumentId = d.Id);

        DECLARE @structInstances bigint = 0;
        IF @apply = 1
        BEGIN
            -- Як RowStore.CreateInstancesCoreAsync + MaterializeFixedRowsAsync: екземпляр із послідовності, CreatedAt = ModifiedAt,
            -- і рядки КОЖНОГО живого RowDef таблиці (RowKey, Ordinal, RowDefId) — у тій самій транзакції.
            INSERT doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt, SheetCopyId)
            SELECT m.AnchorPeriod, NEXT VALUE FOR doc.TableInstanceSeq, m.DocumentId, m.TableDefId, @now, @now, NULLIF(m.CopyId, 0)
            FROM   @missingInst m;
            SET @structInstances = @@ROWCOUNT;

            INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, IsOrphaned, ModifiedAt)
            SELECT ti.PeriodKey, NEXT VALUE FOR doc.TableRowSeq, ti.Id, rd.RowKey, rd.Id, rd.Ordinal, 0, 0, @now
            FROM   @missingInst m
            JOIN   doc.TableInstance ti ON ti.PeriodKey = m.AnchorPeriod AND ti.DocumentId = m.DocumentId
                                       AND ti.TableDefId = m.TableDefId AND ISNULL(ti.SheetCopyId, 0) = m.CopyId
            JOIN   cfg.RowDef rd        ON rd.TableDefId = m.TableDefId AND rd.IsDeleted = 0;
        END

        DECLARE @anchorInst TABLE (DocumentId bigint NOT NULL, CopyId bigint NOT NULL, TableDefId int NOT NULL,
                                   PeriodKey int NOT NULL, Id bigint NOT NULL);
        INSERT @anchorInst (DocumentId, CopyId, TableDefId, PeriodKey, Id)
        SELECT ti.DocumentId, ISNULL(ti.SheetCopyId, 0), COALESCE(tm.n, ti.TableDefId), ti.PeriodKey, ti.Id
        FROM   @anchors a
        JOIN   @instances i        ON i.PeriodKey = a.PeriodKey
        JOIN   doc.TableInstance ti ON ti.PeriodKey = i.PeriodKey AND ti.Id = i.Id
        LEFT JOIN @tableMap tm     ON tm.o = ti.TableDefId;

        -- Пізніші місяці року: екземпляри таблиць, що лишаються в цільовій версії (ключ — таблиця ПІСЛЯ мапи).
        DECLARE @monthInst TABLE (DocumentId bigint NOT NULL, CopyId bigint NOT NULL, TableDefId int NOT NULL,
                                  PeriodKey int NOT NULL, Id bigint NOT NULL, AnchorPeriod int NOT NULL);
        INSERT @monthInst (DocumentId, CopyId, TableDefId, PeriodKey, Id, AnchorPeriod)
        SELECT ti.DocumentId, ISNULL(ti.SheetCopyId, 0), COALESCE(tm.n, ti.TableDefId), ti.PeriodKey, ti.Id, a.PeriodKey
        FROM   @anchors a
        JOIN   @instances i         ON i.PeriodKey > a.PeriodKey AND i.PeriodKey <= a.Yr * 100 + 12
        JOIN   doc.TableInstance ti ON ti.PeriodKey = i.PeriodKey AND ti.Id = i.Id
        LEFT JOIN @tableMap tm      ON tm.o = ti.TableDefId
        WHERE  tm.o IS NOT NULL OR EXISTS (SELECT 1 FROM @tableMap x WHERE x.n = ti.TableDefId);

        -- B69: документи із заархівованим якорем (× рік), у яких крок мав би джерело (екземпляри пізніших місяців), —
        -- у звіт (NonMonthSkippedArchived), а їхні місяці — геть із кроку.
        DECLARE @skippedArchived bigint =
            (SELECT COUNT_BIG(*) FROM (SELECT DISTINCT mi.AnchorPeriod, mi.DocumentId
                                       FROM   @monthInst mi
                                       JOIN   @archivedDocs ad ON ad.AnchorPeriod = mi.AnchorPeriod AND ad.DocumentId = mi.DocumentId) z);
        DELETE mi
        FROM   @monthInst mi
        WHERE  EXISTS (SELECT 1 FROM @archivedDocs ad WHERE ad.AnchorPeriod = mi.AnchorPeriod AND ad.DocumentId = mi.DocumentId);

        DECLARE @cand TABLE (
            DocumentId bigint NOT NULL, CopyId bigint NOT NULL, TableDefId int NOT NULL, RowKey nvarchar(100) NOT NULL,
            ColumnDefId int NOT NULL, PeriodKey int NOT NULL, AnchorPeriod int NOT NULL,
            AnchorInstId bigint NULL, AnchorRowId bigint NULL, H binary(32) NOT NULL,
            ValueString nvarchar(1000) NULL, ValueNumeric decimal(34, 16) NULL, ValueDate datetime2(3) NULL, ValueBool bit NULL,
            ValueRegistryEntryId int NULL, ValueUnitId int NULL);
        INSERT @cand (DocumentId, CopyId, TableDefId, RowKey, ColumnDefId, PeriodKey, AnchorPeriod, AnchorInstId, AnchorRowId, H,
                      ValueString, ValueNumeric, ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId)
        SELECT mi.DocumentId, mi.CopyId, mi.TableDefId, r.RowKey, k.Col, cv.PeriodKey, mi.AnchorPeriod, ai.Id, ar.Id,
               CAST(HASHBYTES('SHA2_256', CONCAT(
                   N's', LEN(cv.ValueString), N':', cv.ValueString,
                   N'|n', CONVERT(nvarchar(64), cv.ValueNumeric),
                   N'|d', CONVERT(nvarchar(30), cv.ValueDate, 126),
                   N'|b', CONVERT(nvarchar(1), cv.ValueBool),
                   N'|r', cv.ValueRegistryEntryId,
                   N'|u', cv.ValueUnitId)) AS binary(32)),
               cv.ValueString, cv.ValueNumeric, cv.ValueDate, cv.ValueBool, cv.ValueRegistryEntryId, cv.ValueUnitId
        FROM   @monthInst mi
        JOIN   doc.TableRow r       ON r.PeriodKey = mi.PeriodKey AND r.TableInstanceId = mi.Id
        JOIN   doc.CellValue cv     ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id
        LEFT JOIN @columnMap cm     ON cm.o = cv.ColumnDefId
        CROSS APPLY (SELECT COALESCE(cm.n, cv.ColumnDefId) AS Col) k
        JOIN   @anchorCols ac       ON ac.n = k.Col
        LEFT JOIN @anchorInst ai    ON ai.PeriodKey = mi.AnchorPeriod AND ai.DocumentId = mi.DocumentId
                                   AND ai.CopyId = mi.CopyId AND ai.TableDefId = mi.TableDefId
        LEFT JOIN doc.TableRow ar   ON ar.PeriodKey = ai.PeriodKey AND ar.TableInstanceId = ai.Id
                                   AND ar.RowKey = r.RowKey AND ar.IsDeleted = 0
        WHERE  r.IsDeleted = 0
          AND  (r.RowDefId IS NULL OR EXISTS (SELECT 1 FROM @rowMap m WHERE m.o = r.RowDefId OR m.n = r.RowDefId))
          -- Таблиця з прив'язкою дозволу без екземпляра якоря — екземпляр створить відкриття (див. @missingInst).
          AND  NOT (ai.Id IS NULL
                    AND NOT EXISTS (SELECT 1 FROM @missingInst x WHERE x.AnchorPeriod = mi.AnchorPeriod AND x.DocumentId = mi.DocumentId
                                      AND x.CopyId = mi.CopyId AND x.TableDefId = mi.TableDefId)
                    AND EXISTS (SELECT 1 FROM cfg.PermitSheetBinding b WHERE b.SheetTableDefId = mi.TableDefId AND b.IsActive = 1 AND b.Mode = 0))
          AND  cv.IsEmpty = 0
          AND  cv.IsCalculated = 0
          AND  NOT EXISTS (SELECT 1 FROM doc.CellFormulaOverride fo
                           WHERE fo.PeriodKey = cv.PeriodKey AND fo.TableRowId = cv.TableRowId AND fo.ColumnDefId = cv.ColumnDefId)
          -- Значення якоря (на цільовій колонці або ще на вихідній, до важкої частини) — недоторканне.
          AND  NOT EXISTS (SELECT 1 FROM doc.CellValue av
                           WHERE av.PeriodKey = ar.PeriodKey AND av.TableRowId = ar.Id AND av.IsEmpty = 0
                             AND (av.ColumnDefId = k.Col
                                  OR EXISTS (SELECT 1 FROM @columnMap x WHERE x.n = k.Col AND x.o = av.ColumnDefId)))
        OPTION (RECOMPILE);

        DECLARE @groups TABLE (AnchorPeriod int NOT NULL, DocumentId bigint NOT NULL, CopyId bigint NOT NULL, TableDefId int NOT NULL,
                               RowKey nvarchar(100) NOT NULL, ColumnDefId int NOT NULL, Kinds int NOT NULL,
                               AnchorInstId bigint NULL, AnchorRowId bigint NULL,
                               PRIMARY KEY (AnchorPeriod, DocumentId, CopyId, TableDefId, RowKey, ColumnDefId));
        INSERT @groups (AnchorPeriod, DocumentId, CopyId, TableDefId, RowKey, ColumnDefId, Kinds, AnchorInstId, AnchorRowId)
        SELECT AnchorPeriod, DocumentId, CopyId, TableDefId, RowKey, ColumnDefId, COUNT(DISTINCT H), MAX(AnchorInstId), MAX(AnchorRowId)
        FROM   @cand
        GROUP BY AnchorPeriod, DocumentId, CopyId, TableDefId, RowKey, ColumnDefId;

        -- Рядки й екземпляри, яких бракує якорю: для КОЖНОЇ таблиці (документ × копія × таблиця × якір), куди крок копіює
        -- хоч одне значення, — усі живі RowKey пізніших місяців, яких немає серед живих рядків якоря (фіксована T8_00
        -- отримує всі свої рядки, а не лише заповнені). Рядки, які heavy-частина знімає, і рядки кроку нових рядків
        -- (@newRows, з'являються лише в T2) не беруться — план до й після важкої частини однаковий.
        DECLARE @needRows TABLE (AnchorPeriod int NOT NULL, DocumentId bigint NOT NULL, CopyId bigint NOT NULL, TableDefId int NOT NULL,
                                 RowKey nvarchar(100) NOT NULL, AnchorInstId bigint NULL, RowDefId int NULL, Ordinal int NULL,
                                 PRIMARY KEY (AnchorPeriod, DocumentId, CopyId, TableDefId, RowKey));
        INSERT @needRows (AnchorPeriod, DocumentId, CopyId, TableDefId, RowKey, AnchorInstId)
        SELECT DISTINCT mi.AnchorPeriod, mi.DocumentId, mi.CopyId, mi.TableDefId, r.RowKey, ai.Id
        FROM   (SELECT DISTINCT AnchorPeriod, DocumentId, CopyId, TableDefId FROM @groups WHERE Kinds = 1) t
        JOIN   @monthInst mi        ON mi.AnchorPeriod = t.AnchorPeriod AND mi.DocumentId = t.DocumentId
                                   AND mi.CopyId = t.CopyId AND mi.TableDefId = t.TableDefId
        JOIN   doc.TableRow r       ON r.PeriodKey = mi.PeriodKey AND r.TableInstanceId = mi.Id AND r.IsDeleted = 0
        LEFT JOIN @anchorInst ai    ON ai.PeriodKey = mi.AnchorPeriod AND ai.DocumentId = mi.DocumentId
                                   AND ai.CopyId = mi.CopyId AND ai.TableDefId = mi.TableDefId
        WHERE  (r.RowDefId IS NULL OR EXISTS (SELECT 1 FROM @rowMap m WHERE m.o = r.RowDefId OR m.n = r.RowDefId))
          AND  NOT EXISTS (SELECT 1 FROM OPENJSON(@newRows) WITH (d int) nw WHERE nw.d = r.RowDefId)
          AND  NOT EXISTS (SELECT 1 FROM doc.TableRow ar
                           WHERE ar.PeriodKey = ai.PeriodKey AND ar.TableInstanceId = ai.Id AND ar.RowKey = r.RowKey AND ar.IsDeleted = 0)
          -- Фіксовані рядки екземпляра, який створює крок (a′), заводяться разом із ним (у сухому прогоні їх ще немає).
          AND  NOT EXISTS (SELECT 1 FROM @missingInst x
                           JOIN   cfg.RowDef rd ON rd.TableDefId = x.TableDefId AND rd.IsDeleted = 0
                           WHERE  x.AnchorPeriod = mi.AnchorPeriod AND x.DocumentId = mi.DocumentId AND x.CopyId = mi.CopyId
                             AND  x.TableDefId = mi.TableDefId AND rd.RowKey = r.RowKey);

        DECLARE @needInst TABLE (AnchorPeriod int NOT NULL, DocumentId bigint NOT NULL, CopyId bigint NOT NULL, TableDefId int NOT NULL,
                                 PRIMARY KEY (AnchorPeriod, DocumentId, CopyId, TableDefId));
        INSERT @needInst (AnchorPeriod, DocumentId, CopyId, TableDefId)
        SELECT DISTINCT nr.AnchorPeriod, nr.DocumentId, nr.CopyId, nr.TableDefId
        FROM   @needRows nr
        WHERE  nr.AnchorInstId IS NULL
          AND  NOT EXISTS (SELECT 1 FROM @missingInst x WHERE x.AnchorPeriod = nr.AnchorPeriod AND x.DocumentId = nr.DocumentId
                             AND x.CopyId = nr.CopyId AND x.TableDefId = nr.TableDefId);

        SET @copied = 0;
        SET @rowsCreated = 0;
        SET @instancesCreated = 0;
        IF @apply = 0
        BEGIN
            DECLARE @rc bigint = (SELECT COUNT_BIG(*) FROM @needRows);
            DECLARE @ic bigint = (SELECT COUNT_BIG(*) FROM @needInst) + (SELECT COUNT_BIG(*) FROM @missingInst);
            SELECT CAST(ISNULL(SUM(CASE WHEN Kinds = 1 THEN 1 ELSE 0 END), 0) AS bigint) AS Copies,
                   CAST(ISNULL(SUM(CASE WHEN Kinds > 1 THEN 1 ELSE 0 END), 0) AS bigint) AS Conflicts,
                   @rc AS RowsCreated,
                   @ic AS InstancesCreated,
                   @skippedArchived AS SkippedArchived
            FROM   @groups;

            SELECT c.DocumentId, c.TableDefId, c.RowKey, c.ColumnDefId, c.PeriodKey,
                   c.ValueString, c.ValueNumeric, c.ValueDate, c.ValueBool, c.ValueRegistryEntryId, c.ValueUnitId
            FROM   (SELECT TOP (@maxConflicts) AnchorPeriod, DocumentId, CopyId, TableDefId, RowKey, ColumnDefId
                    FROM   @groups
                    WHERE  Kinds > 1
                    ORDER BY AnchorPeriod, DocumentId, CopyId, TableDefId, RowKey, ColumnDefId) g
            JOIN   @cand c ON c.AnchorPeriod = g.AnchorPeriod AND c.DocumentId = g.DocumentId AND c.CopyId = g.CopyId
                          AND c.TableDefId = g.TableDefId AND c.RowKey = g.RowKey AND c.ColumnDefId = g.ColumnDefId
            ORDER BY c.DocumentId, c.TableDefId, c.RowKey, c.ColumnDefId, c.AnchorPeriod, c.PeriodKey;

            SELECT DocumentId, TableDefId, AnchorPeriod FROM @groups WHERE Kinds = 1
            UNION
            SELECT DocumentId, TableDefId, AnchorPeriod FROM @missingInst;
        END
        ELSE
        BEGIN
            -- 1. Екземпляри якоря (як RowStore.CreateInstancesCoreAsync: послідовність, CreatedAt = ModifiedAt, копія аркуша).
            INSERT doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt, SheetCopyId)
            SELECT n.AnchorPeriod, NEXT VALUE FOR doc.TableInstanceSeq, n.DocumentId, n.TableDefId, @now, @now, NULLIF(n.CopyId, 0)
            FROM   @needInst n;
            SET @instancesCreated = @@ROWCOUNT + @structInstances;

            UPDATE nr
            SET    nr.AnchorInstId = ti.Id
            FROM   @needRows nr
            JOIN   doc.TableInstance ti ON ti.PeriodKey = nr.AnchorPeriod AND ti.DocumentId = nr.DocumentId
                                       AND ti.TableDefId = nr.TableDefId AND ISNULL(ti.SheetCopyId, 0) = nr.CopyId
            WHERE  nr.AnchorInstId IS NULL;

            -- 2. Рядки: RowDefId і Ordinal — з НАЙПІЗНІШОГО місяця, де рядок живий.
            UPDATE nr
            SET    nr.RowDefId = src.RowDefId, nr.Ordinal = src.Ordinal
            FROM   @needRows nr
            CROSS APPLY (SELECT TOP (1) r.RowDefId, r.Ordinal
                         FROM   @monthInst mi
                         JOIN   doc.TableRow r ON r.PeriodKey = mi.PeriodKey AND r.TableInstanceId = mi.Id
                         WHERE  mi.AnchorPeriod = nr.AnchorPeriod AND mi.DocumentId = nr.DocumentId
                           AND  mi.CopyId = nr.CopyId AND mi.TableDefId = nr.TableDefId
                           AND  r.RowKey = nr.RowKey AND r.IsDeleted = 0
                         ORDER BY mi.PeriodKey DESC) src;

            -- Знятий рядок якоря з тим самим ключем (UQ_TableRow_Key) — відновлюється замість вставки.
            UPDATE r
            SET    r.IsDeleted = 0, r.RowDefId = nr.RowDefId, r.Ordinal = nr.Ordinal, r.ModifiedAt = @now
            FROM   doc.TableRow r
            JOIN   @needRows nr ON r.PeriodKey = nr.AnchorPeriod AND r.TableInstanceId = nr.AnchorInstId AND r.RowKey = nr.RowKey
            WHERE  r.IsDeleted = 1;
            SET @rowsCreated = @@ROWCOUNT;

            INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, IsOrphaned, ModifiedAt)
            SELECT nr.AnchorPeriod, NEXT VALUE FOR doc.TableRowSeq, nr.AnchorInstId, nr.RowKey, nr.RowDefId, nr.Ordinal, 0, 0, @now
            FROM   @needRows nr
            WHERE  NOT EXISTS (SELECT 1 FROM doc.TableRow y
                               WHERE y.PeriodKey = nr.AnchorPeriod AND y.TableInstanceId = nr.AnchorInstId AND y.RowKey = nr.RowKey);
            SET @rowsCreated += @@ROWCOUNT;

            -- 3. Комірки: значення найпізнішого місяця груп без конфлікту, у рядок якоря (за RowKey).
            DECLARE @pick TABLE (AnchorPeriod int NOT NULL, AnchorRowId bigint NOT NULL, ColumnDefId int NOT NULL, TableDefId int NOT NULL,
                                 ValueString nvarchar(1000) NULL, ValueNumeric decimal(34, 16) NULL, ValueDate datetime2(3) NULL,
                                 ValueBool bit NULL, ValueRegistryEntryId int NULL, ValueUnitId int NULL,
                                 PRIMARY KEY (AnchorPeriod, AnchorRowId, ColumnDefId));
            INSERT @pick (AnchorPeriod, AnchorRowId, ColumnDefId, TableDefId,
                          ValueString, ValueNumeric, ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId)
            SELECT x.AnchorPeriod, ar.Id, x.ColumnDefId, x.TableDefId,
                   x.ValueString, x.ValueNumeric, x.ValueDate, x.ValueBool, x.ValueRegistryEntryId, x.ValueUnitId
            FROM   (SELECT c.AnchorPeriod, c.DocumentId, c.CopyId, c.TableDefId, c.RowKey, c.ColumnDefId,
                           c.ValueString, c.ValueNumeric, c.ValueDate, c.ValueBool, c.ValueRegistryEntryId, c.ValueUnitId,
                           ROW_NUMBER() OVER (PARTITION BY c.AnchorPeriod, c.DocumentId, c.CopyId, c.TableDefId, c.RowKey, c.ColumnDefId
                                              ORDER BY c.PeriodKey DESC) AS Latest
                    FROM   @cand c) x
            JOIN   @groups g ON g.AnchorPeriod = x.AnchorPeriod AND g.DocumentId = x.DocumentId AND g.CopyId = x.CopyId
                            AND g.TableDefId = x.TableDefId AND g.RowKey = x.RowKey AND g.ColumnDefId = x.ColumnDefId AND g.Kinds = 1
            JOIN   doc.TableInstance ai ON ai.PeriodKey = x.AnchorPeriod AND ai.DocumentId = x.DocumentId
                                       AND ai.TableDefId = x.TableDefId AND ISNULL(ai.SheetCopyId, 0) = x.CopyId
            JOIN   doc.TableRow ar ON ar.PeriodKey = ai.PeriodKey AND ar.TableInstanceId = ai.Id AND ar.RowKey = x.RowKey AND ar.IsDeleted = 0
            WHERE  x.Latest = 1;

            DECLARE @n bigint = 0;

            -- Порожня комірка-заглушка якоря (IsEmpty = 1) — оновлюється; значення якоря (IsEmpty = 0) сюди не потрапляє.
            UPDATE av
            SET    av.ValueString = p.ValueString, av.ValueNumeric = p.ValueNumeric, av.ValueDate = p.ValueDate,
                   av.ValueBool = p.ValueBool, av.ValueRegistryEntryId = p.ValueRegistryEntryId, av.ValueUnitId = p.ValueUnitId,
                   av.TableDefId = p.TableDefId, av.IsCalculated = 0, av.IsEmpty = 0
            FROM   doc.CellValue av
            JOIN   @pick p ON av.PeriodKey = p.AnchorPeriod AND av.TableRowId = p.AnchorRowId AND av.ColumnDefId = p.ColumnDefId
            WHERE  av.IsEmpty = 1;
            SET @n += @@ROWCOUNT;

            INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric, ValueDate,
                                  ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
            SELECT p.AnchorPeriod, p.AnchorRowId, p.ColumnDefId, p.TableDefId, p.ValueString, p.ValueNumeric, p.ValueDate,
                   p.ValueBool, p.ValueRegistryEntryId, p.ValueUnitId, 0, 0
            FROM   @pick p
            WHERE  NOT EXISTS (SELECT 1 FROM doc.CellValue av
                               WHERE av.PeriodKey = p.AnchorPeriod AND av.TableRowId = p.AnchorRowId AND av.ColumnDefId = p.ColumnDefId);
            SET @n += @@ROWCOUNT;

            -- Рядок якоря змінився: ModifiedAt піднімає RowVersion (оптимістичне блокування бачить зміну).
            UPDATE r
            SET    r.ModifiedAt = @now
            FROM   doc.TableRow r
            JOIN   (SELECT DISTINCT AnchorPeriod, AnchorRowId FROM @pick) p ON r.PeriodKey = p.AnchorPeriod AND r.Id = p.AnchorRowId;

            SET @copied = @n;
        END
        """;

    /// <summary>Екземпляри таблиць (їх мало: документи × таблиці × періоди) — одним оператором.</summary>
    private const string InstancesSql = """
        DELETE ti
        FROM   doc.TableInstance ti
        JOIN   @instances i ON i.PeriodKey = ti.PeriodKey AND i.Id = ti.Id
        WHERE  ti.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  NOT EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId);

        UPDATE ti
        SET    ti.TableDefId = m.n, ti.ModifiedAt = @now
        FROM   doc.TableInstance ti
        JOIN   @instances i ON i.PeriodKey = ti.PeriodKey AND i.Id = ti.Id
        JOIN   @tableMap m  ON m.o = ti.TableDefId
        WHERE  ti.PeriodKey IN (SELECT PeriodKey FROM @periods);
        """;

    /// <summary>
    /// 4. Рядки фіксованих таблиць, яких стара версія не мала: у кожен наявний екземпляр.
    /// ⚠ <c>NEXT VALUE FOR</c> не сумісний з <c>TOP</c> в одному <c>SELECT</c>, тому <c>TOP</c> — у похідній таблиці.
    /// Вставлений рядок виключається <c>NOT EXISTS</c> за <c>RowKey</c>, тож цикл скінченний.
    /// </summary>
    private const string NewRowsBatchSql = """
        INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, IsOrphaned, ModifiedAt)
        SELECT x.PeriodKey, NEXT VALUE FOR doc.TableRowSeq, x.Id, x.k, x.d, x.s, 0, 0, @now
        FROM (
            SELECT TOP (@batch) ti.PeriodKey, ti.Id, nr.k, nr.d, nr.s
            FROM   doc.TableInstance ti
            JOIN   @instances i ON i.PeriodKey = ti.PeriodKey AND i.Id = ti.Id
            JOIN   OPENJSON(@newRows) WITH (t int, d int, k nvarchar(100), s int) nr ON nr.t = ti.TableDefId
            WHERE  ti.PeriodKey IN (SELECT PeriodKey FROM @periods)
              AND  NOT EXISTS (SELECT 1 FROM doc.TableRow y
                               WHERE y.PeriodKey = ti.PeriodKey AND y.TableInstanceId = ti.Id AND y.RowKey = nr.k)
        ) x;
        """;

    /// <summary>5. Шапка.</summary>
    private const string HeaderSql = """
        DELETE h
        FROM   doc.DocumentHeaderValue h
        JOIN   @docs d ON d.Id = h.DocumentId
        WHERE  NOT EXISTS (SELECT 1 FROM @headerMap m WHERE m.o = h.HeaderFieldDefId);

        UPDATE h
        SET    h.HeaderFieldDefId = m.n
        FROM   doc.DocumentHeaderValue h
        JOIN   @docs d      ON d.Id = h.DocumentId
        JOIN   @headerMap m ON m.o = h.HeaderFieldDefId;
        """;

    /// <summary>
    /// 5б. Шапка з мапою Lookup (лише коли <c>plan.LookupRemaps</c> непорожній): той самий <c>DELETE</c>, а єдиний
    /// <c>UPDATE</c> разом із перейменуванням поля переписує <c>ValueRegistryEntryId</c> за мапою.
    /// </summary>
    /// <remarks>
    /// ⚠ Спершу окремий <c>UPDATE</c> переписує запис за мапою — умова за СТАРИМ <c>HeaderFieldDefId</c> і старим
    /// записом, ще до перейменування поля; його <c>@@ROWCOUNT</c> іде у <c>@lookupChanged</c>, і сховище звіряє його з
    /// планом (P2-2: інакше — відкат). Запис без пари мапи лишається як був. Далі <c>DELETE</c> зниклих полів і
    /// перейменування, як у <c>HeaderSql</c>. Без <c>#</c>-таблиць і DDL: лише <c>OPENJSON</c> параметра.
    /// </remarks>
    private const string HeaderSqlWithLookupRemap = """
        UPDATE h
        SET    h.ValueRegistryEntryId = r.n
        FROM   doc.DocumentHeaderValue h
        JOIN   @docs d      ON d.Id = h.DocumentId
        JOIN   @headerMap m ON m.o = h.HeaderFieldDefId
        JOIN   OPENJSON(@lookupRemap) WITH (f int, o bigint, n bigint) r
               ON r.f = h.HeaderFieldDefId AND r.o = h.ValueRegistryEntryId
        WHERE  h.IsEmpty = 0;
        SET @lookupChanged = @@ROWCOUNT;

        DELETE h
        FROM   doc.DocumentHeaderValue h
        JOIN   @docs d ON d.Id = h.DocumentId
        WHERE  NOT EXISTS (SELECT 1 FROM @headerMap m WHERE m.o = h.HeaderFieldDefId);

        UPDATE h
        SET    h.HeaderFieldDefId = m.n
        FROM   doc.DocumentHeaderValue h
        JOIN   @docs d      ON d.Id = h.DocumentId
        JOIN   @headerMap m ON m.o = h.HeaderFieldDefId;
        """;

    /// <summary>6. Склад документа і робочий процес: аркуш той самий, змінився лише його опис.</summary>
    private const string WorkflowSql = """
        DELETE a
        FROM   wf.ApprovalState a
        JOIN   @docs d ON d.Id = a.DocumentId
        WHERE  a.PeriodKey >= @fromPeriod
          AND  NOT EXISTS (SELECT 1 FROM @sheetMap m WHERE m.o = a.SheetDefId);

        UPDATE a
        SET    a.SheetDefId = m.n
        FROM   wf.ApprovalState a
        JOIN   @docs d     ON d.Id = a.DocumentId
        JOIN   @sheetMap m ON m.o = a.SheetDefId
        WHERE  a.PeriodKey >= @fromPeriod;

        UPDATE e
        SET    e.SheetDefId = m.n
        FROM   wf.ApprovalEvent e
        JOIN   @docs d     ON d.Id = e.DocumentId
        JOIN   @sheetMap m ON m.o = e.SheetDefId
        WHERE  e.PeriodKey >= @fromPeriod;

        -- RPT-STABLE Part B: у режимі «з місяця» склад документа не втрачає аркушів, яких нова версія не має: їх
        -- ще потребують періоди до X (екземпляри таблиць і стани лишилися на старих Id).
        DELETE s
        FROM   doc.DocumentSheet s
        JOIN   @docs d ON d.Id = s.DocumentId
        WHERE  @fromPeriod = 0
          AND  NOT EXISTS (SELECT 1 FROM @sheetMap m WHERE m.o = s.SheetDefId);

        UPDATE s
        SET    s.SheetDefId = m.n
        FROM   doc.DocumentSheet s
        JOIN   @docs d     ON d.Id = s.DocumentId
        JOIN   @sheetMap m ON m.o = s.SheetDefId;

        -- ⛔ D-7: історія дозволів (J01) адресована Id таблиці/колонки/поля шапки ВЕРСІЇ — переїжджає за тими самими
        -- мапами, що й комірки та шапка. Таблиця/колонка/поле без відповідника: історія йде разом із їхніми значеннями.
        -- @columnMap (o, n, t) несе цільову таблицю, тож TableDefId береться з неї (вірно й для таблиці, перейменованої
        -- мапою). Частини місяця партиціоновані — лише періоди переносу (@periods, як екземпляри): у режимі «з місяця»
        -- частини періодів до X лишаються на старих Id разом зі своїми екземплярами. Сегменти й шапка не періодні — як
        -- HeaderSql, вони переїжджають і в режимі «з місяця» (та сама межа, що вже діє для шапки).
        DELETE p
        FROM   doc.CellPermitPart p
        JOIN   @docs d ON d.Id = p.DocumentId
        WHERE  p.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  NOT EXISTS (SELECT 1 FROM @columnMap c WHERE c.o = p.ColumnDefId)
          AND  NOT EXISTS (SELECT 1 FROM @monthColumnMap mm WHERE mm.o = p.ColumnDefId AND mm.m = p.PeriodKey % 100);

        UPDATE p
        SET    p.TableDefId = c.t, p.ColumnDefId = c.n
        FROM   doc.CellPermitPart p
        JOIN   @docs d      ON d.Id = p.DocumentId
        JOIN   @columnMap c ON c.o = p.ColumnDefId
        WHERE  p.PeriodKey IN (SELECT PeriodKey FROM @periods);

        -- ✎ LAND-YEAR L3: частини місяця немісячної колонки — у місячну колонку свого місяця (як комірки).
        UPDATE p
        SET    p.TableDefId = mm.t, p.ColumnDefId = mm.n
        FROM   doc.CellPermitPart p
        JOIN   @docs d      ON d.Id = p.DocumentId
        JOIN   @monthColumnMap mm ON mm.o = p.ColumnDefId AND mm.m = p.PeriodKey % 100
        WHERE  p.PeriodKey IN (SELECT PeriodKey FROM @periods);

        DELETE g
        FROM   doc.PermitSegment g
        JOIN   @docs d ON d.Id = g.DocumentId
        WHERE  (g.ColumnDefId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @columnMap c WHERE c.o = g.ColumnDefId))
           OR  (g.HeaderFieldDefId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @headerMap h WHERE h.o = g.HeaderFieldDefId));

        UPDATE g
        SET    g.TableDefId = c.t, g.ColumnDefId = c.n
        FROM   doc.PermitSegment g
        JOIN   @docs d      ON d.Id = g.DocumentId
        JOIN   @columnMap c ON c.o = g.ColumnDefId;

        UPDATE g
        SET    g.HeaderFieldDefId = h.n
        FROM   doc.PermitSegment g
        JOIN   @docs d      ON d.Id = g.DocumentId
        JOIN   @headerMap h ON h.o = g.HeaderFieldDefId;
        """;

    /// <summary>
    /// 6a. Копія грантів ролей на нові Id ресурсів (частина <see cref="FinishSql"/>; окремо — для тесту гонки).
    /// </summary>
    /// <remarks>
    /// ✎ B130: <c>WITH (UPDLOCK, HOLDLOCK)</c> у <c>NOT EXISTS</c>. Ролі й ресурси шаблону спільні для проєктів, а переноси
    /// РІЗНИХ проєктів не серіалізуються (замок — рядок <c>doc.Project</c> і applock структури документа свого проєкту).
    /// Під RCSI <c>NOT EXISTS</c> не бачить незакомічений грант іншого переносу тієї самої ролі на той самий новий Id —
    /// обидва вставляють, другий падає на <c>UQ_ResourceGrant</c> і відкочує весь перенос. Діапазонний U-замок змушує
    /// другий чекати коміту першого й тоді пропустити вже наявний грант. ✎ B130: <c>FORCESEEK</c> — без нього план
    /// анти-з'єднання сканував <c>sec.ResourceGrant</c> під діапазонними U-замками (замок на всю таблицю до коміту
    /// переносу) і давав дедлок 1205 з вставкою іншої транзакції навіть за обох хінтів (тест гонки 6a).
    /// Порядок замків у <see cref="FinishSql"/> однаковий для всіх переносів: 6a (<c>sec.ResourceGrant</c>), далі 6b
    /// (<c>cfg.ColumnRename</c>) — інших писарів обох таблиць в одній транзакції немає.
    /// </remarks>
    internal const string GrantCopyInsertSql = """
        INSERT sec.ResourceGrant (RoleId, ResourceKind, ResourceId, [Level], IsDeny)
        SELECT g.RoleId, g.ResourceKind, m.n, g.[Level], g.IsDeny
        FROM   sec.ResourceGrant g
        JOIN   @grantMap m ON m.k = g.ResourceKind AND m.o = g.ResourceId
        WHERE  NOT EXISTS (SELECT 1 FROM sec.ResourceGrant t
                           WITH (UPDLOCK, HOLDLOCK, FORCESEEK (UQ_ResourceGrant (RoleId, ResourceKind, ResourceId)))
                           WHERE t.RoleId = g.RoleId AND t.ResourceKind = g.ResourceKind AND t.ResourceId = m.n);
        """;

    /// <summary>6b. Спільна пара перейменування колонок (частина <see cref="FinishSql"/>; окремо — для тесту гонки).</summary>
    /// <remarks>
    /// ✎ B127: <c>UPDLOCK, HOLDLOCK</c> — з тієї самої причини, що й у <see cref="GrantCopyInsertSql"/>. ✎ B130:
    /// <c>DISTINCT</c> — повтор пари в <c>@renames</c> інакше дав би дві вставки того самого ключа (порядок і унікальність
    /// пар — у SQL, а не в залежності від викликача).
    /// <para>
    /// ✎ B130 (CI 43721871030, дедлок 1205): <c>FORCESEEK</c> по <c>PK_ColumnRename</c>. Без нього оптимізатор міг читати
    /// NOT EXISTS через покривний <c>IX_ColumnRename_To</c> (To + ключ кластера): транзакція тримала RangeS-U на прогалині
    /// <c>IX_ColumnRename_To</c> і чекала RangeI-N на прогалині PK, а інша — навпаки (граф system_health: обидві
    /// прогалини — «нескінченність» (ffffffffffff), бо Id колонок нових версій завжди найбільші, тож одночасні переноси
    /// РІЗНИХ шаблонів впиралися в ту саму останню прогалину). Читання лише по PK ставить усіх у чергу на одній прогалині
    /// PK ще до вставки, нічого іншого не тримаючи, — циклу немає. Те саме для 6a (<c>UQ_ResourceGrant</c>) — індекс
    /// названо явно, щоб план не залежав від статистики. ✎ B135: основний захист тепер — глобальний applock кроку 6
    /// (<see cref="FinishLockSql"/>); хінти — страховка від писарів поза переносом.
    /// </para>
    /// </remarks>
    internal const string ColumnRenameInsertSql = """
        INSERT cfg.ColumnRename (FromColumnDefId, ToColumnDefId)
        SELECT DISTINCT r.f, r.t
        FROM   OPENJSON(@renames) WITH (f int, t int) r
        WHERE  NOT EXISTS (SELECT 1 FROM cfg.ColumnRename x
                           WITH (UPDLOCK, HOLDLOCK, FORCESEEK (PK_ColumnRename (FromColumnDefId, ToColumnDefId)))
                           WHERE x.FromColumnDefId = r.f AND x.ToColumnDefId = r.t);
        """;

    /// <summary>
    /// ✎ B130/B135: початок <see cref="FinishSql"/> — транзакційний виключний applock кроку 6, ОДИН на всю базу
    /// (<c>ecr:migrate-finish</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Кроки 6a/6b пишуть спільні рядки (гранти ролей на ресурси версій, пари перейменувань), а переноси різних проєктів
    /// інакше не серіалізуються. Діапазонних замків мало навіть між різними шаблонами: DELETE 6a бере X на allow-рядки
    /// нових Id, а NOT EXISTS іншої транзакції тримає RangeS-U на спільній останній прогалині <c>UQ_ResourceGrant</c> тієї
    /// самої ролі (1205, рецензія B130); applock на шаблон цього не закривав. Крок 6 триває секунди, переноси рідкісні —
    /// глобальна черга прибирає і цей ризик, і залежність від планів 6a/6b; хінти лишаються страховкою.
    /// </para>
    /// <para>
    /// Порядок замків без циклу: перенос бере рядок <c>doc.Project</c> (<c>LockProjectVersionAsync</c>) і applock структури
    /// документів СВОГО проєкту на початку транзакції, а цей applock — останнім, у <see cref="FinishSql"/>, і ніде інде
    /// його не беруть. Хто чекає на нього, не тримає нічого, що потрібне власникові (чужі проєкт і документи), а власник
    /// після нього пише лише спільні таблиці кроку 6 і свій проєкт. Очікування обмежене
    /// (<see cref="DefaultFinishLockTimeoutMilliseconds"/>): тоді 409 «повторіть перенос».
    /// </para>
    /// </remarks>
    internal const string FinishLockSql = """
        DECLARE @finishLock int;
        EXEC @finishLock = sp_getapplock @Resource = N'ecr:migrate-finish', @LockMode = N'Exclusive',
                                         @LockOwner = N'Transaction', @LockTimeout = @finishLockTimeout;
        IF @finishLock < 0
            THROW 51300, N'Перенос версії: не дочекався замка кроку 6 (sp_getapplock < 0).', 1;
        """;

    /// <summary>
    /// Крок 6 переносу (остання команда транзакції переносу): глобальний applock кроку 6
    /// (<see cref="FinishLockSql"/>), 6a — гранти ролей на нові Id ресурсів (<see cref="GrantCopyInsertSql"/>), 6b —
    /// пари перейменувань і їх прив'язка до проєкту (<see cref="ColumnRenameInsertSql"/>), 6c — шкала версій і версія
    /// проєкту, прибирання.
    /// </summary>
    internal const string FinishSql = $"""
        {FinishLockSql}

        -- 6a. Гранти ролей на аркуш/таблицю/колонку (sec.ResourceGrant): Id цих
        -- ресурсів свої в кожної версії, а грант не має ProjectId і діє на всіх,
        -- хто сидить на старій версії. Тому КОПІЮЄМО на нові Id за кодом (не
        -- переносимо), інакше deny мовчки перестає діяти (fail-open). Заборона
        -- перекриває вже наявний дозвіл на новому Id; повторний перенос нічого
        -- не дублює (UQ_ResourceGrant).
        -- ✎ LAND-YEAR L3: грант немісячної колонки копіюється на КОЖНУ з її місячних колонок (ключ — (k, o, n)).
        DECLARE @grantMap TABLE (k tinyint NOT NULL, o int NOT NULL, n int NOT NULL, PRIMARY KEY (k, o, n));
        INSERT @grantMap (k, o, n)
        SELECT 1, o, n FROM @sheetMap UNION ALL
        SELECT 2, o, n FROM @tableMap UNION ALL
        SELECT 3, o, n FROM @columnMap UNION ALL
        SELECT 3, o, n FROM @monthColumnMap;

        -- ⚠ DELETE + INSERT, а не UPDATE IsDeny: відбиток груп у ключі кешу профілю
        -- (count і max(Id) грантів ролі) від UPDATE не міняється, і профіль без
        -- заборони жив би до 60 хв. Новий рядок дає новий max(Id) → профіль перебудується.
        DELETE t
        FROM   sec.ResourceGrant t
        JOIN   @grantMap m ON m.k = t.ResourceKind AND m.n = t.ResourceId
        JOIN   sec.ResourceGrant g ON g.RoleId = t.RoleId AND g.ResourceKind = m.k AND g.ResourceId = m.o
        WHERE  g.IsDeny = 1 AND t.IsDeny = 0;

        {GrantCopyInsertSql}

        -- 6b. RPT-STABLE Part B: збережена мапа перейменувань колонок (псевдонім для SSRS і міжперіодних формул).
        {ColumnRenameInsertSql}
        -- ✎ B127: UPDLOCK/HOLDLOCK - переноси РІЗНИХ проєктів не серіалізуються (замок - рядок doc.Project і applock
        -- структури документа свого проєкту), а пара (f, t) спільна для шаблону: без діапазонного замка дві одночасні
        -- вставки тієї самої пари давали б PK_ColumnRename і відкат усього переносу. Прив'язка нижче - своя для проєкту.

        -- ✎ B127: пара діє для родоводу документів ЛИШЕ цього проєкту (cfg.ColumnRename спільний для шаблону, а перенос - на проєкт).
        INSERT cfg.ColumnRenameProject (FromColumnDefId, ToColumnDefId, ProjectId)
        SELECT DISTINCT r.f, r.t, @projectId
        FROM   OPENJSON(@renames) WITH (f int, t int) r
        WHERE  NOT EXISTS (SELECT 1 FROM cfg.ColumnRenameProject x
                           WHERE x.FromColumnDefId = r.f AND x.ToColumnDefId = r.t AND x.ProjectId = @projectId);

        -- 6c. RPT-STABLE Part B: часова шкала версій. Перший рядок (поточна версія на першому періоді) засівається, якщо
        -- його немає; далі рядки з ефективного початку й пізніші замінюються одним (X, нова). Увесь проєкт (@fromPeriod = 0)
        -- лишає ОДИН рядок - нову версію з першого періоду.
        DECLARE @firstPeriod int = (SELECT MIN(PeriodKey) FROM doc.Period WHERE ProjectId = @projectId);
        -- ✎ B74 PT01: шкала на пару (проєкт, шаблон). Перенос лише в межах шаблону (обробник відмовляє іншому шаблону),
        -- тож шаблон старої і нової версії один.
        DECLARE @targetTemplate int = (SELECT TemplateId FROM cfg.TemplateVersion WHERE Id = @targetVersionId);
        -- ✎ B74 P3 (D-576): стара версія — версія ПАРИ (doc.ProjectTemplate, до оновлення нижче); версія проєкту — лише
        -- для основного шаблону (рядка пари в старих даних може не бути) і для переносу без пари (@templateId NULL).
        DECLARE @oldVersion int = (SELECT TemplateVersionId FROM doc.Project WHERE Id = @projectId AND @isPrimary = 1);
        IF @templateId IS NOT NULL
            SET @oldVersion = COALESCE(
                (SELECT CurrentTemplateVersionId FROM doc.ProjectTemplate WHERE ProjectId = @projectId AND TemplateId = @targetTemplate),
                @oldVersion);
        IF @firstPeriod IS NOT NULL
        BEGIN
            IF @fromPeriod = 0 OR @fromPeriod <= @firstPeriod
            BEGIN
                DELETE FROM doc.ProjectTemplateVersion WHERE ProjectId = @projectId AND TemplateId = @targetTemplate;
                INSERT doc.ProjectTemplateVersion (ProjectId, TemplateId, FromPeriodKey, TemplateVersionId)
                VALUES (@projectId, @targetTemplate, @firstPeriod, @targetVersionId);
            END
            ELSE
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM doc.ProjectTemplateVersion WHERE ProjectId = @projectId AND TemplateId = @targetTemplate)
                    INSERT doc.ProjectTemplateVersion (ProjectId, TemplateId, FromPeriodKey, TemplateVersionId)
                    VALUES (@projectId, @targetTemplate, @firstPeriod, @oldVersion);
                DELETE FROM doc.ProjectTemplateVersion
                WHERE  ProjectId = @projectId AND TemplateId = @targetTemplate AND FromPeriodKey >= @fromPeriod;
                INSERT doc.ProjectTemplateVersion (ProjectId, TemplateId, FromPeriodKey, TemplateVersionId)
                VALUES (@projectId, @targetTemplate, @fromPeriod, @targetVersionId);
            END
        END

        -- ✎ B74 P1 (подвійний запис): чинна версія шаблону в проєкті - той самий пакет, що й Project нижче.
        UPDATE doc.ProjectTemplate SET CurrentTemplateVersionId = @targetVersionId
        WHERE  ProjectId = @projectId AND TemplateId = @targetTemplate;
        IF @@ROWCOUNT = 0
            INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId)
            VALUES (@projectId, @targetTemplate, @targetVersionId);

        -- 7. Нарешті — версія проєкту: ✎ B74 P3 (D-576) лише коли шаблон пари основний (до P6 версію основного дублює
        -- проєкт); перенос приєднаного шаблону версію проєкту не чіпає.
        UPDATE doc.Project SET TemplateVersionId = @targetVersionId WHERE Id = @projectId AND @isPrimary = 1;

        -- ⛔ Маркер «міграція в процесі» (`migratingTo`) знімається ТІЄЮ САМОЮ транзакцією (тим самим пакетом), що перемикає
        -- версію: інакше проєкт опинився б на новій версії з маркером (усе заблоковано) або на старій без нього.
        -- ✎ B74 P3: маркер — на ПРОЄКТІ, не на парі: поки йде перенос одного шаблону, писарі всіх шаблонів проєкту чекають.
        UPDATE doc.Project
        SET    ExternalSettingsJson =
                   CASE WHEN ISJSON(ExternalSettingsJson) = 1 AND JSON_VALUE(ExternalSettingsJson, '$.migratingTo') IS NOT NULL
                        THEN NULLIF(
                                 JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(
                                     ExternalSettingsJson,
                                     '$.migratingTo', NULL), '$.planHash', NULL), '$.startedAt', NULL), '$.startedBy', NULL),
                                     '$.owner', NULL), '$.leaseUntil', NULL), '$.wmCellsPeriod', NULL),
                                     '$.wmCellsHi', NULL), '$.wmIndexHi', NULL),
                                 CONCAT(CHAR(123), CHAR(125)))
                        ELSE ExternalSettingsJson END
        WHERE  Id = @projectId;
        """;
}
