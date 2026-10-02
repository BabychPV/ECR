using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Перенос документів проєкту на нову версію шаблону (ФВ-7.5) — набором, одним пакетом SQL.</summary>
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
    public async Task<VersionMigrationScope> ReadScopeAsync(int projectId, CancellationToken ct)
    {
        var documentIds = await db.Documents
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId)
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

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

        var parameters = new object[]
        {
            new SqlParameter("@projectId", projectId),
            new SqlParameter("@targetVersionId", targetVersionId),
            Json("@sheets", plan.Sheets.Select(p => new { o = p.Key, n = p.Value })),
            Json("@tables", plan.Tables.Select(p => new { o = p.Key, n = p.Value })),
            Json("@columns", plan.Columns.Select(c => new { o = c.SourceColumnDefId, n = c.TargetColumnDefId, t = c.TargetTableDefId })),
            Json("@rows", plan.Rows.Select(p => new { o = p.Key, n = p.Value })),
            Json("@headers", plan.HeaderFields.Select(p => new { o = p.Key, n = p.Value })),
            Json("@newRows", plan.NewRows.Select(r => new { t = r.TargetTableDefId, d = r.TargetRowDefId, k = r.RowKey, s = r.Ordinal })),
        };

        await db.Database.ExecuteSqlRawAsync(ApplySql, parameters, ct).ConfigureAwait(false);
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
                WHERE  g.IsDeny = 1
                """)
            .SingleAsync(ct)
            .ConfigureAwait(false);
    }

    private static SqlParameter Json(string name, IEnumerable<object> rows)
        => new(name, System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(rows) };

    /// <summary>Рядок підрахунку значень комірок.</summary>
    private sealed record CellUsageRow(int ColumnDefId, int? RowDefId, long Values);

    /// <summary>Рядок підрахунку значень шапки.</summary>
    private sealed record HeaderUsageRow(int HeaderFieldDefId, long Values);

    /// <summary>Сам перенос.</summary>
    /// <remarks>
    /// Порядок — від листків до коренів, бо ключі складені:
    /// <c>FK_CellValue_Column</c> — пара <c>(TableDefId, ColumnDefId)</c>, тож
    /// комірка міняє обидва поля одним оператором. Те, чому в новій версії
    /// немає місця, прибирається: план уже гарантував, що там немає жодного
    /// введеного значення (режим <c>Safe</c> інакше відмовляє), тобто зникають
    /// лише порожні комірки й порожні рядки, екземпляри таблиць і склад аркушів.
    ///
    /// ⚠ Історія лишається на тих описах, де її записали: <c>calc.SubmissionSnapshot</c>
    /// — зріз ПОДАННЯ, який і має посилатися на версію, за якою подавали;
    /// <c>aud.*</c> — незмінний журнал.
    /// </remarks>
    private const string ApplySql = """
        SET NOCOUNT ON;
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

        DECLARE @now datetime2(3) = SYSUTCDATETIME();

        -- 1. Комірки: у колонку-відповідник, разом із таблицею (складений FK).
        UPDATE cv
        SET    cv.ColumnDefId = m.n, cv.TableDefId = m.t
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i   ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @columnMap m   ON m.o = cv.ColumnDefId
        WHERE  cv.PeriodKey IN (SELECT PeriodKey FROM @periods);

        -- Порожні комірки колонок, яких нова версія не має.
        DELETE cv
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i   ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  cv.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  NOT EXISTS (SELECT 1 FROM @columnMap m WHERE m.n = cv.ColumnDefId);

        -- 2. Рядки: описи рядків — за ключем; рядки без місця в новій версії знімаються.
        UPDATE r
        SET    r.RowDefId = m.n
        FROM   doc.TableRow r
        JOIN   @instances i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        JOIN   @rowMap m    ON m.o = r.RowDefId
        WHERE  r.PeriodKey IN (SELECT PeriodKey FROM @periods);

        UPDATE r
        SET    r.IsDeleted = 1, r.ModifiedAt = @now
        FROM   doc.TableRow r
        JOIN   @instances i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  r.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  r.RowDefId IS NOT NULL
          AND  NOT EXISTS (SELECT 1 FROM @rowMap m WHERE m.n = r.RowDefId)
          AND  EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId);

        -- 3. Екземпляри таблиць, яких нова версія не має: порожні — прибираються повністю.
        DELETE cv
        FROM   doc.CellValue cv
        JOIN   doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
        JOIN   @instances i   ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  cv.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  NOT EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId);

        DELETE r
        FROM   doc.TableRow r
        JOIN   @instances i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
        WHERE  r.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  NOT EXISTS (SELECT 1 FROM @tableMap t WHERE t.o = i.TableDefId);

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

        -- 4. Рядки фіксованих таблиць, яких стара версія не мала: у кожен наявний екземпляр.
        INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, IsOrphaned, ModifiedAt)
        SELECT ti.PeriodKey, NEXT VALUE FOR doc.TableRowSeq, ti.Id, nr.k, nr.d, nr.s, 0, 0, @now
        FROM   doc.TableInstance ti
        JOIN   @instances i ON i.PeriodKey = ti.PeriodKey AND i.Id = ti.Id
        JOIN   OPENJSON(@newRows) WITH (t int, d int, k nvarchar(100), s int) nr ON nr.t = ti.TableDefId
        WHERE  ti.PeriodKey IN (SELECT PeriodKey FROM @periods)
          AND  NOT EXISTS (SELECT 1 FROM doc.TableRow x
                           WHERE x.PeriodKey = ti.PeriodKey AND x.TableInstanceId = ti.Id AND x.RowKey = nr.k);

        -- 5. Шапка й похідний індекс пошуку.
        DELETE h
        FROM   doc.DocumentHeaderValue h
        JOIN   @docs d ON d.Id = h.DocumentId
        WHERE  NOT EXISTS (SELECT 1 FROM @headerMap m WHERE m.o = h.HeaderFieldDefId);

        UPDATE h
        SET    h.HeaderFieldDefId = m.n
        FROM   doc.DocumentHeaderValue h
        JOIN   @docs d      ON d.Id = h.DocumentId
        JOIN   @headerMap m ON m.o = h.HeaderFieldDefId;

        DELETE x
        FROM   doc.DocumentIndexValue x
        JOIN   @docs d ON d.Id = x.DocumentId
        WHERE  NOT EXISTS (SELECT 1 FROM @columnMap m WHERE m.o = x.ColumnDefId);

        UPDATE x
        SET    x.ColumnDefId = m.n
        FROM   doc.DocumentIndexValue x
        JOIN   @docs d      ON d.Id = x.DocumentId
        JOIN   @columnMap m ON m.o = x.ColumnDefId;

        -- 6. Склад документа і робочий процес: аркуш той самий, змінився лише його опис.
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

        -- Результати валідації описували стару структуру: їх перераховують заново.
        DELETE v
        FROM   wf.ValidationResult v
        JOIN   @docs d ON d.Id = v.DocumentId;

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

        UPDATE t
        SET    t.IsDeny = 1
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
