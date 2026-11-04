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

    /// <inheritdoc />
    public async Task<int?> LockProjectVersionAsync(int projectId, CancellationToken ct)
    {
        _ = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "LockProjectVersionAsync викликано поза транзакцією: блок рядка проєкту звільнився б одразу.");

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
    public async Task<IReadOnlyList<int>> ListPeriodKeysAsync(int projectId, CancellationToken ct)
        => await db.Periods
            .AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .OrderBy(p => p.PeriodKeyValue)
            .Select(p => p.PeriodKeyValue)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> ListDocumentIdsAsync(int projectId, CancellationToken ct)
        => await db.Documents
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId)
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
                db.Documents.Where(d => d.ProjectId == projectId),
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
                join d in db.Documents.AsNoTracking() on h.DocumentId equals d.Id
                where d.ProjectId == projectId && !h.IsEmpty && fieldIds.Contains(h.HeaderFieldDefId)
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
                new("@fromPeriod", plan.FromPeriodKey),
                new("@renames", System.Data.SqlDbType.NVarChar, -1)
                {
                    Value = JsonSerializer.Serialize(plan.ColumnRenames.Select(r => new { f = r.FromColumnDefId, t = r.ToColumnDefId })),
                },
                new("@targetVersionId", targetVersionId),
                new("@batch", batchRows),
                new("@now", System.Data.SqlDbType.DateTime2) { Scale = 3, Value = now },
                new("@sheets", System.Data.SqlDbType.NVarChar, -1) { Value = sheets },
                new("@tables", System.Data.SqlDbType.NVarChar, -1) { Value = tables },
                new("@columns", System.Data.SqlDbType.NVarChar, -1) { Value = columns },
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
            await db.Database.ExecuteSqlRawAsync(SingleCommand(sql), Parameters(), ct).ConfigureAwait(false);
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
    public async Task<int> CountUnmappedBindingsAsync(
        int sourceVersionId, IReadOnlyDictionary<int, int> columnMap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columnMap);

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

        var targetColumns = columnMap.Values.Distinct().ToList();
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

        return source.Count(b => !columnMap.TryGetValue(b.ColumnDefId, out var target)
                                 || !carried.Contains((target, b.MethodologyId, b.OutputCode.ToUpperInvariant())));
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
        var projects = await db.Projects.AsNoTracking()
            .CountAsync(p => p.TemplateVersionId == versionId && p.Id != projectId, ct)
            .ConfigureAwait(false);
        if (projects == 0)
        {
            return new ProjectsOnVersion(0, 0);
        }

        var documents = await (
                from d in db.Documents.AsNoTracking()
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                where p.TemplateVersionId == versionId && p.Id != projectId
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

        DECLARE @docs TABLE (Id bigint PRIMARY KEY);
        INSERT @docs (Id) SELECT Id FROM doc.Document WHERE ProjectId = @projectId;

        DECLARE @periods TABLE (PeriodKey int PRIMARY KEY);
        INSERT @periods (PeriodKey) SELECT PeriodKey FROM doc.Period WHERE ProjectId = @projectId AND PeriodKey >= @fromPeriod;

        DECLARE @sheetMap TABLE (o int PRIMARY KEY, n int NOT NULL);
        INSERT @sheetMap SELECT o, n FROM OPENJSON(@sheets) WITH (o int, n int);

        DECLARE @tableMap TABLE (o int PRIMARY KEY, n int NOT NULL);
        INSERT @tableMap SELECT o, n FROM OPENJSON(@tables) WITH (o int, n int);

        DECLARE @columnMap TABLE (o int PRIMARY KEY, n int NOT NULL, t int NOT NULL);
        INSERT @columnMap SELECT o, n, t FROM OPENJSON(@columns) WITH (o int, n int, t int);

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
        """;

    /// <summary>6a. Гранти, версія проєкту, прибирання #-таблиць.</summary>
    private const string FinishSql = """
        -- 6a. Гранти ролей на аркуш/таблицю/колонку (sec.ResourceGrant): Id цих
        -- ресурсів свої в кожної версії, а грант не має ProjectId і діє на всіх,
        -- хто сидить на старій версії. Тому КОПІЮЄМО на нові Id за кодом (не
        -- переносимо), інакше deny мовчки перестає діяти (fail-open). Заборона
        -- перекриває вже наявний дозвіл на новому Id; повторний перенос нічого
        -- не дублює (UQ_ResourceGrant).
        DECLARE @grantMap TABLE (k tinyint NOT NULL, o int NOT NULL, n int NOT NULL, PRIMARY KEY (k, o));
        INSERT @grantMap (k, o, n)
        SELECT 1, o, n FROM @sheetMap UNION ALL
        SELECT 2, o, n FROM @tableMap UNION ALL
        SELECT 3, o, n FROM @columnMap;

        -- ⚠ DELETE + INSERT, а не UPDATE IsDeny: відбиток груп у ключі кешу профілю
        -- (count і max(Id) грантів ролі) від UPDATE не міняється, і профіль без
        -- заборони жив би до 60 хв. Новий рядок дає новий max(Id) → профіль перебудується.
        DELETE t
        FROM   sec.ResourceGrant t
        JOIN   @grantMap m ON m.k = t.ResourceKind AND m.n = t.ResourceId
        JOIN   sec.ResourceGrant g ON g.RoleId = t.RoleId AND g.ResourceKind = m.k AND g.ResourceId = m.o
        WHERE  g.IsDeny = 1 AND t.IsDeny = 0;

        INSERT sec.ResourceGrant (RoleId, ResourceKind, ResourceId, [Level], IsDeny)
        SELECT g.RoleId, g.ResourceKind, m.n, g.[Level], g.IsDeny
        FROM   sec.ResourceGrant g
        JOIN   @grantMap m ON m.k = g.ResourceKind AND m.o = g.ResourceId
        WHERE  NOT EXISTS (SELECT 1 FROM sec.ResourceGrant t
                           WHERE t.RoleId = g.RoleId AND t.ResourceKind = g.ResourceKind AND t.ResourceId = m.n);

        -- 6b. RPT-STABLE Part B: збережена мапа перейменувань колонок (псевдонім для SSRS і міжперіодних формул).
        INSERT cfg.ColumnRename (FromColumnDefId, ToColumnDefId)
        SELECT r.f, r.t
        FROM   OPENJSON(@renames) WITH (f int, t int) r
        WHERE  NOT EXISTS (SELECT 1 FROM cfg.ColumnRename x WHERE x.FromColumnDefId = r.f AND x.ToColumnDefId = r.t);

        -- 6c. RPT-STABLE Part B: часова шкала версій. Перший рядок (поточна версія на першому періоді) засівається, якщо
        -- його немає; далі рядки з ефективного початку й пізніші замінюються одним (X, нова). Увесь проєкт (@fromPeriod = 0)
        -- лишає ОДИН рядок - нову версію з першого періоду.
        DECLARE @firstPeriod int = (SELECT MIN(PeriodKey) FROM doc.Period WHERE ProjectId = @projectId);
        DECLARE @oldVersion int = (SELECT TemplateVersionId FROM doc.Project WHERE Id = @projectId);
        IF @firstPeriod IS NOT NULL
        BEGIN
            IF @fromPeriod = 0 OR @fromPeriod <= @firstPeriod
            BEGIN
                DELETE FROM doc.ProjectTemplateVersion WHERE ProjectId = @projectId;
                INSERT doc.ProjectTemplateVersion (ProjectId, FromPeriodKey, TemplateVersionId)
                VALUES (@projectId, @firstPeriod, @targetVersionId);
            END
            ELSE
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM doc.ProjectTemplateVersion WHERE ProjectId = @projectId)
                    INSERT doc.ProjectTemplateVersion (ProjectId, FromPeriodKey, TemplateVersionId)
                    VALUES (@projectId, @firstPeriod, @oldVersion);
                DELETE FROM doc.ProjectTemplateVersion WHERE ProjectId = @projectId AND FromPeriodKey >= @fromPeriod;
                INSERT doc.ProjectTemplateVersion (ProjectId, FromPeriodKey, TemplateVersionId)
                VALUES (@projectId, @fromPeriod, @targetVersionId);
            END
        END

        -- 7. Нарешті — версія проєкту.
        -- ⛔ Маркер «міграція в процесі» (`migratingTo`) знімається ТІЄЮ САМОЮ командою, що перемикає версію: інакше
        -- проєкт опинився б на новій версії з маркером (усе заблоковано) або на старій без нього (напівстан видно).
        UPDATE doc.Project
        SET    TemplateVersionId = @targetVersionId,
               ExternalSettingsJson =
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
