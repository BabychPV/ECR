using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Перенос документів проєкту на нову версію шаблону (ФВ-7.5) — набором, пачками SQL в одній транзакції.</summary>
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
public sealed class DocumentVersionMigrationStore(EcrDbContext db) : IDocumentVersionMigrationStore
{
    /// <summary>N-3: скільки рядків змінює одна команда переносу.</summary>
    public const int BatchRows = 5_000;

    /// <summary>N-3: <c>CommandTimeout</c> кожної команди переносу, с (глобальний — 60).</summary>
    public const int ApplyCommandTimeoutSeconds = 300;

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
    public async Task<IReadOnlyList<long>> ListDocumentIdsAsync(int projectId, CancellationToken ct)
        => await db.Documents
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId)
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<VersionMigrationScope> ReadScopeAsync(int projectId, CancellationToken ct)
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
            .Where(a => a.Status == DocumentStatus.Submitted || a.Status == DocumentStatus.Approved)
            .Join(db.Documents.Where(d => d.ProjectId == projectId), a => a.DocumentId, d => d.Id, (a, _) => a.Id)
            .CountAsync(ct)
            .ConfigureAwait(false);

        return new VersionMigrationScope(
            documentIds,
            [.. cells.Select(c => new VersionMigrationCellUsage(c.ColumnDefId, c.RowDefId, c.Values))],
            header.ToDictionary(h => h.HeaderFieldDefId, h => h.Values),
            locked);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(int projectId, int targetVersionId, VersionMigrationPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _ = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("ApplyAsync викликано поза транзакцією: перенос мусить бути атомарним.");

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

        // ⚠ Параметри — свіжі на кожну команду: SqlParameter належить одній колекції. Мапи, документи й
        // екземпляри щоразу збираються заново з цих параметрів (прелюдія) — стан між командами НЕ тримається:
        // #-таблиці були б DDL (сторож `ForbiddenApiTests`), а таблиці-змінні живуть лише в межах пакета.
        SqlParameter[] Parameters(SqlParameter? changedOutput = null)
        {
            List<SqlParameter> list =
            [
                new("@projectId", projectId),
                new("@targetVersionId", targetVersionId),
                new("@batch", BatchRows),
                new("@now", System.Data.SqlDbType.DateTime2) { Scale = 3, Value = now },
                new("@sheets", System.Data.SqlDbType.NVarChar, -1) { Value = sheets },
                new("@tables", System.Data.SqlDbType.NVarChar, -1) { Value = tables },
                new("@columns", System.Data.SqlDbType.NVarChar, -1) { Value = columns },
                new("@rows", System.Data.SqlDbType.NVarChar, -1) { Value = rows },
                new("@headers", System.Data.SqlDbType.NVarChar, -1) { Value = headers },
                new("@newRows", System.Data.SqlDbType.NVarChar, -1) { Value = newRows },
            ];
            if (changedOutput is not null)
            {
                list.Add(changedOutput);
            }

            return [.. list];
        }

        async Task SingleAsync(string sql)
            => await db.Database.ExecuteSqlRawAsync(SingleCommand(sql), Parameters(), ct).ConfigureAwait(false);

        async Task BatchesAsync(string sql)
        {
            var command = BatchCommand(sql);
            int changed;
            do
            {
                ct.ThrowIfCancellationRequested();
                var output = new SqlParameter("@changed", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output };
                await db.Database.ExecuteSqlRawAsync(command, Parameters(output), ct).ConfigureAwait(false);
                changed = output.Value is int value ? value : 0;
            }
            while (changed >= BatchRows);
        }

        foreach (var sql in CellAndRowBatches)
        {
            await BatchesAsync(sql).ConfigureAwait(false);
        }

        await SingleAsync(InstancesSql).ConfigureAwait(false);

        // Без нових рядків у плані команду не посилаємо зовсім.
        if (plan.NewRows.Count > 0)
        {
            await BatchesAsync(NewRowsBatchSql).ConfigureAwait(false);
        }

        await SingleAsync(HeaderSql).ConfigureAwait(false);

        foreach (var sql in IndexBatches)
        {
            await BatchesAsync(sql).ConfigureAwait(false);
        }

        await SingleAsync(WorkflowSql).ConfigureAwait(false);
        await BatchesAsync(ValidationBatchSql).ConfigureAwait(false);
        await SingleAsync(FinishSql).ConfigureAwait(false);
    }

    // Складають команду зі СТАТИЧНИХ текстів цього класу (прелюдія + оператор) — не з вводу користувача.
    private static string SingleCommand(string body) => Prelude + body;

    /// <summary>
    /// Пачкова команда: оператор, перед його <c>;</c> — <c>OPTION (RECOMPILE)</c> (табличні змінні мають відомий
    /// розмір лише після перекомпіляції, інакше план бере їх за 1 рядок), далі <c>@changed = @@ROWCOUNT</c>.
    /// </summary>
    private static string BatchCommand(string body)
        => Prelude + body.TrimEnd().TrimEnd(';') + " OPTION (RECOMPILE);\nSET @changed = @@ROWCOUNT;";

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
    // ── Команди переносу (N-3) ──────────────────────────────────────────────────────────────────────
    // Раніше весь перенос був ОДНИМ пакетом SQL під глобальним CommandTimeout (60 с): проєкт із ~138 тис.
    // значень (Land RC11) не вкладався і давав 500. Тепер це послідовність команд В ОДНІЙ транзакції
    // (атомарність та сама: збій у будь-якій — відкат усього), великі оператори йдуть пачками по BatchRows,
    // і кожна команда має власний таймаут ApplyCommandTimeoutSeconds. Пачка завжди прибирає свої рядки з
    // власного предиката (змінена колонка/опис більше не збігається, видалене зникло), тож цикл «доки пачка
    // повна» скінченний.

    /// <summary>
    /// Прелюдія кожної команди: мапи, документи, періоди й екземпляри таблиць проєкту — у табличних змінних,
    /// з параметрів. Стану між командами немає (#-таблиці були б DDL, який сторож не пускає).
    /// </summary>
    private const string Prelude = """
        SET XACT_ABORT ON;

        DECLARE @docs TABLE (Id bigint PRIMARY KEY);
        INSERT @docs (Id) SELECT Id FROM doc.Document WHERE ProjectId = @projectId;

        DECLARE @periods TABLE (PeriodKey int PRIMARY KEY);
        INSERT @periods (PeriodKey) SELECT PeriodKey FROM doc.Period WHERE ProjectId = @projectId;

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
    private static readonly string[] CellAndRowBatches =
    [
        // 1. Комірки: у колонку-відповідник, разом із таблицею (складений FK).
        """
        UPDATE TOP (@batch) cv
        SET    cv.ColumnDefId = m.n, cv.TableDefId = m.t
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r      ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @columnMap m    ON m.o = cv.ColumnDefId
        WHERE  cv.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  m.o <> m.n;
        """,

        // Порожні комірки колонок, яких нова версія не має.
        """
        DELETE TOP (@batch) cv
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r      ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  cv.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  NOT EXISTS (SELECT 1 FROM @columnMap m WHERE m.n = cv.ColumnDefId);
        """,

        // 2. Рядки: описи рядків — за ключем; рядки без місця в новій версії знімаються.
        """
        UPDATE TOP (@batch) r
        SET    r.RowDefId = m.n
        FROM   doc.TableRow r
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @rowMap m       ON m.o = r.RowDefId
        WHERE  r.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  m.o <> m.n;
        """,

        """
        UPDATE TOP (@batch) r
        SET    r.IsDeleted = 1, r.ModifiedAt = @now
        FROM   doc.TableRow r
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  r.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  r.RowDefId IS NOT NULL
          AND  r.ModifiedAt <> @now
          AND  NOT EXISTS (SELECT 1 FROM @rowMap m WHERE m.n = r.RowDefId)
          AND  EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId);
        """,

        // 3. Екземпляри таблиць, яких нова версія не має: порожні — прибираються повністю.
        """
        DELETE TOP (@batch) cv
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r      ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  cv.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  NOT EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId);
        """,

        """
        DELETE TOP (@batch) r
        FROM   doc.TableRow r
        JOIN   @instances i    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  r.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  NOT EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId);
        """,
    ];

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

    /// <summary>5. Похідний індекс пошуку (за кількістю — як комірки) — пачками.</summary>
    private static readonly string[] IndexBatches =
    [
        """
        DELETE TOP (@batch) x
        FROM   doc.DocumentIndexValue x
        JOIN   @docs d ON d.Id = x.DocumentId
        WHERE  NOT EXISTS (SELECT 1 FROM @columnMap m WHERE m.o = x.ColumnDefId);
        """,

        """
        UPDATE TOP (@batch) x
        SET    x.ColumnDefId = m.n
        FROM   doc.DocumentIndexValue x
        JOIN   @docs d      ON d.Id = x.DocumentId
        JOIN   @columnMap m ON m.o = x.ColumnDefId
        WHERE  m.o <> m.n;
        """,
    ];

    /// <summary>6. Склад документа і робочий процес: аркуш той самий, змінився лише його опис.</summary>
    private const string WorkflowSql = """
        DELETE a
        FROM   wf.ApprovalState a
        JOIN   @docs d ON d.Id = a.DocumentId
        WHERE  NOT EXISTS (SELECT 1 FROM @sheetMap m WHERE m.o = a.SheetDefId);

        UPDATE a
        SET    a.SheetDefId = m.n
        FROM   wf.ApprovalState a
        JOIN   @docs d     ON d.Id = a.DocumentId
        JOIN   @sheetMap m ON m.o = a.SheetDefId;

        UPDATE e
        SET    e.SheetDefId = m.n
        FROM   wf.ApprovalEvent e
        JOIN   @docs d     ON d.Id = e.DocumentId
        JOIN   @sheetMap m ON m.o = e.SheetDefId;

        DELETE s
        FROM   doc.DocumentSheet s
        JOIN   @docs d ON d.Id = s.DocumentId
        WHERE  NOT EXISTS (SELECT 1 FROM @sheetMap m WHERE m.o = s.SheetDefId);

        UPDATE s
        SET    s.SheetDefId = m.n
        FROM   doc.DocumentSheet s
        JOIN   @docs d     ON d.Id = s.DocumentId
        JOIN   @sheetMap m ON m.o = s.SheetDefId;
        """;

    /// <summary>Результати валідації описували стару структуру: їх перераховують заново (їх багато — пачками).</summary>
    private const string ValidationBatchSql = """
        DELETE TOP (@batch) v
        FROM   wf.ValidationResult v
        JOIN   @docs d ON d.Id = v.DocumentId;
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

        -- 7. Нарешті — версія проєкту.
        UPDATE doc.Project SET TemplateVersionId = @targetVersionId WHERE Id = @projectId;
        """;
}
