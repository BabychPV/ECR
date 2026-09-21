using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>WR-02</c>: форма запиту на шляху запису не залежить від РОЗМІРУ батчу.
/// </summary>
/// <remarks>
/// ⛔ Продовження <c>WritePathPlanCacheTests</c>, і межа між ними точна.
/// <c>WR-01</c> прибрав залежність сигнатури запиту від ЗНАЧЕНЬ
/// (<c>Precision</c>/<c>Scale</c>/довжина рядка) — той набір і перебирає
/// значення при сталому розмірі батчу, навмисно. Лишалася друга залежність:
/// список <c>VALUES</c> входить у ТЕКСТ запиту, тож кожен розмір чанка давав
/// власний план. Лінійка <c>MS-01</c> після <c>WR-01</c> бачила рівно це —
/// <b>сім</b> планів <c>MERGE doc.CellValue</c>, по одному на розмір батчу.
/// Тут перебираються РОЗМІРИ при сталих значеннях.
///
/// ⛔ Другий доказ — число КОМАНД, і без нього перший неповний. План один і в
/// тому разі, якби батч на 30 000 комірок і далі різався на 300 однакових
/// <c>MERGE</c> по 100: текст той самий, план той самий, а 300 походів до
/// сервера в одній транзакції лишилися б. Тому тут дві різні величини, а не
/// одна двічі.
///
/// ⚠ Лічильник — <see cref="SqlClientCommandCounter"/>, а НЕ
/// <c>DbCommandCounter</c>. <c>NormalizedCellStore</c> бере з'єднання й
/// створює <c>connection.CreateCommand()</c> напряму, тож перехоплювач EF
/// цього <c>MERGE</c> не бачить узагалі — замір вийшов би «0 команд», тобто
/// хибнозелений (<c>MS-01</c> §5).
/// </remarks>
[Collection("SqlServer")]
public sealed class TvpBatchWriteTests(SqlServerFixture sql)
{
    /// <summary>Рядків у вимірюваному батчі.</summary>
    private const int Rows = 500;

    /// <summary>Колонок у вимірюваному батчі.</summary>
    /// <remarks>
    /// 500 × 60 = 30 000 комірок — це профіль «вставка найбільшої таблиці» з
    /// критерію №1 директиви, а не кругле число. До <c>WR-02</c> він давав
    /// 300 послідовних <c>MERGE</c> (чанк 100) і ~200 <c>INSERT</c> аудиту
    /// (чанк 150); ціль §5 — «2» на обидва.
    /// </remarks>
    private const int Columns = 60;

    /// <summary>
    /// Розміри батчів для заміру кешу планів.
    /// </summary>
    /// <remarks>
    /// ⚠ Їх СІМ, і це не випадковість: рівно стільки планів
    /// <c>MERGE doc.CellValue</c> лічильник знайшов у кеші після <c>WR-01</c>
    /// (§3.3 директиви, «7 планів — по одному на розмір батчу»). Якби TVP не
    /// діяв, цей набір відтворив би те саме число, а не якесь інше.
    /// </remarks>
    private static readonly int[] BatchSizes = [1, 2, 3, 5, 8, 13, 21];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Батч_30000_комірок_іде_ОДНІЄЮ_командою_MERGE()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: Columns, rowCount: Rows, ct: ct);
        var store = new NormalizedCellStore(builder.CreateContext());

        var batch = FullSlice(doc);
        Assert.Equal(Rows * Columns, batch.Upserts.Count);

        using var counter = new SqlClientCommandCounter();

        // Розігрів на ОДНІЙ комірці: перший виклик тягне за собою відкриття
        // з'єднання й метадані типу, і без скидання вони зарахувалися б у
        // замір як звернення продукту.
        await store.ApplyAsync(Single(doc), ct);
        counter.Tally.Reset();

        await store.ApplyAsync(batch, ct);

        counter.AssertObserved();
        var seen = counter.Tally.Snapshot();

        Assert.Equal(1, seen["MERGE doc.CellValue"]);

        // ⚠ Розмір батчу більше не видно в параметрах: їх рівно один
        // (табличний) плюс жодного на комірку. Саме це й знімає межу 2100 —
        // і саме це робить текст запиту сталим.
        var merge = seen.Categories.Single(c => c.Category == "MERGE doc.CellValue");
        Assert.Equal(1, merge.MaxParameters);
        Assert.Equal(1, merge.InTransaction);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Аудит_на_30000_змін_іде_ОДНІЄЮ_командою_INSERT()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: Columns, rowCount: Rows, ct: ct);

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);
        var changes = AuditSlice(doc);
        Assert.Equal(Rows * Columns, changes.Count);

        using var counter = new SqlClientCommandCounter();

        await writer.WriteCellChangesAsync([changes[0]], ct);
        counter.Tally.Reset();

        await writer.WriteCellChangesAsync(changes, ct);

        counter.AssertObserved();
        var seen = counter.Tally.Snapshot();

        Assert.Equal(1, seen["INSERT aud.CellChange"]);

        // Журнал мусить лягти ПОВНІСТЮ — «одна команда» без цієї перевірки
        // означало б лише «одна команда», а не «одна команда, що все записала».
        var written = await ScalarAsync(
            "SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = @d;", doc.DocumentId, ct);
        Assert.Equal((Rows * Columns) + 1, written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сім_різних_розмірів_батчу_дають_ОДИН_план_MERGE()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 4, rowCount: BatchSizes.Max(), ct: ct);
        var store = new NormalizedCellStore(builder.CreateContext());

        // Розігрів ПЕРЕД очищенням кешу — інакше замір рахував би ще й
        // одноразову вартість старту.
        await store.ApplyAsync(Single(doc), ct);
        await ExecuteAsync("ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;", ct);

        foreach (var size in BatchSizes)
        {
            await store.ApplyAsync(SizedBatch(doc, size), ct);
        }

        var plans = await PlanCountAsync("%MERGE doc.CellValue%", ct);

        // ⛔ Підлога ОБОВ'ЯЗКОВА, і причина задокументована в
        // WritePathPlanCacheTests: форма заміру через sys.dm_exec_query_stats
        // завжди дає 0 (dbid підготовленого пакета — NULL), і на ній перша
        // редакція тесту WR-01 вийшла хибнозеленою. Нуль тут означає
        // «замір не відбувся», а не «планів немає».
        Assert.True(
            plans >= 1,
            $"Планів MERGE doc.CellValue у кеші: 0 на {BatchSizes.Length} батчів. "
            + "Замір НЕ ВІДБУВСЯ — дивись фільтр по базі в PlanCountAsync.");

        // ⛔ Стеля рівно 1, а не «≤ 2 про запас». Запас тут був би не
        // обережністю, а дірою: сім розмірів батчу — це рівно той випадок, у
        // якому старий код давав сім планів, і будь-яке послаблення з'їло б
        // різницю між «TVP діє» і «TVP діє наполовину».
        Assert.True(
            plans == 1,
            $"Планів MERGE doc.CellValue у кеші: {plans} на {BatchSizes.Length} РІЗНИХ розмірів "
            + "батчу. Текст запиту досі залежить від кількості рядків — WR-02 не діє.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сім_різних_розмірів_журналу_дають_ОДИН_план_INSERT()
    {
        // ⚠ Половина аудиту потрібна окремо: WritePathPlanCacheTests перебирає
        // ДОВЖИНИ РЯДКІВ при сталому розмірі батчу (це WR-01), а тут навпаки —
        // розміри при сталих значеннях. Список VALUES входив у текст запиту так
        // само, як у MERGE, тож і планів було стільки ж, скільки розмірів.
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 4, rowCount: BatchSizes.Max(), ct: ct);

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);
        var changes = AuditSlice(doc);

        await writer.WriteCellChangesAsync([changes[0]], ct);
        await ExecuteAsync("ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;", ct);

        foreach (var size in BatchSizes)
        {
            await writer.WriteCellChangesAsync(changes.GetRange(0, size), ct);
        }

        var plans = await PlanCountAsync("%INSERT INTO aud.CellChange%", ct);

        Assert.True(
            plans >= 1,
            $"Планів INSERT aud.CellChange у кеші: 0 на {BatchSizes.Length} батчів. "
            + "Замір НЕ ВІДБУВСЯ.");

        Assert.True(
            plans == 1,
            $"Планів INSERT aud.CellChange у кеші: {plans} на {BatchSizes.Length} РІЗНИХ розмірів "
            + "батчу. Текст запиту досі залежить від кількості рядків.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дублікат_адреси_в_батчі_падає_ГУЧНО()
    {
        // ⛔ Це перевірка первинного ключа табличного типу, і вона тут не для
        // повноти. До WR-02 відповідь на дублікат залежала від того, де
        // проходила МЕЖА ЧАНКА: дві однакові адреси в одному чанку валили
        // MERGE помилкою 8672, а розведені по сусідніх чанках проходили як два
        // послідовні записи. Тепер батч один, і відповідь одна.
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 2, ct: ct);
        var store = new NormalizedCellStore(builder.CreateContext());

        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);
        var batch = new CellChangeSet(
            doc.TableInstanceId,
            [
                new CellRecord(address, doc.TableDefId, new CellValueData { ValueNumeric = 1m }),
                new CellRecord(address, doc.TableDefId, new CellValueData { ValueNumeric = 2m }),
            ],
            [],
            [doc.RowIds[0]],
            ChangedByUserId: 1,
            IsLateEdit: false);

        await Assert.ThrowsAsync<SqlException>(() => store.ApplyAsync(batch, ct));

        // І в базі не лишилося ні першого значення, ні другого: транзакція
        // відкотилася цілком. Фільтр саме по TableRowId, а не по PeriodKey:
        // база спільна на збірку, і в тому самому періоді лежать комірки
        // сусідніх тестів.
        var written = await ScalarAsync(
            "SELECT COUNT(*) FROM doc.CellValue WHERE TableRowId = @d;", doc.RowIds[0], ct);
        Assert.Equal(0, written);
    }

    /// <summary>Увесь зріз документа — 30 000 комірок.</summary>
    private static CellChangeSet FullSlice(TestDocument doc)
    {
        var upserts = new List<CellRecord>(doc.RowIds.Count * doc.ColumnDefIds.Count);

        foreach (var rowId in doc.RowIds)
        {
            foreach (var columnId in doc.ColumnDefIds)
            {
                upserts.Add(new CellRecord(
                    new CellAddress(doc.PeriodKey, rowId, columnId),
                    doc.TableDefId,
                    new CellValueData { ValueNumeric = (rowId % 97) + (columnId % 13) }));
            }
        }

        return new CellChangeSet(
            doc.TableInstanceId, upserts, [], doc.RowIds, ChangedByUserId: 1, IsLateEdit: false);
    }

    /// <summary>Батч рівно на <paramref name="size"/> комірок.</summary>
    /// <remarks>
    /// ⚠ Значення СТАЛЕ (<c>1m</c>) навмисно: інакше тест міряв би ще й
    /// залежність від даних, тобто <c>WR-01</c>, і його падіння не казало б,
    /// котре з двох зламалося.
    /// </remarks>
    private static CellChangeSet SizedBatch(TestDocument doc, int size)
    {
        var upserts = new List<CellRecord>(size);
        for (var i = 0; i < size; i++)
        {
            upserts.Add(new CellRecord(
                new CellAddress(doc.PeriodKey, doc.RowIds[i], doc.ColumnDefIds[1]),
                doc.TableDefId,
                new CellValueData { ValueNumeric = 1m }));
        }

        return new CellChangeSet(
            doc.TableInstanceId, upserts, [], [], ChangedByUserId: 1, IsLateEdit: false);
    }

    /// <summary>Одна комірка — для розігріву.</summary>
    private static CellChangeSet Single(TestDocument doc)
        => new(
            doc.TableInstanceId,
            [
                new CellRecord(
                    new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]),
                    doc.TableDefId,
                    new CellValueData { ValueNumeric = 1m }),
            ],
            [],
            [],
            ChangedByUserId: 1,
            IsLateEdit: false);

    /// <summary>30 000 записів журналу — по одному на комірку зрізу.</summary>
    private static List<CellChangeRecord> AuditSlice(TestDocument doc)
    {
        var changes = new List<CellChangeRecord>(doc.RowIds.Count * doc.ColumnDefIds.Count);
        var at = new DateTime(2026, 1, 20, 10, 0, 0, DateTimeKind.Utc);

        for (var r = 0; r < doc.RowIds.Count; r++)
        {
            foreach (var columnId in doc.ColumnDefIds)
            {
                changes.Add(new CellChangeRecord(
                    at,
                    new CellAddress(doc.PeriodKey, doc.RowIds[r], columnId),
                    doc.DocumentId,
                    RowKey: $"R{r + 1}",
                    OldValue: null,
                    NewValue: "1",
                    ChangedByUserId: 42,
                    Origin: "UserEdit",
                    IsLateEdit: false,
                    CorrelationId: "tvp"));
            }
        }

        return changes;
    }

    /// <summary>Скільки планів із таким текстом лежить у кеші ЦІЄЇ бази.</summary>
    /// <remarks>
    /// ⚠ Запит дослівно той самий, що в <c>WritePathPlanCacheTests</c> і в
    /// лінійці (<c>HttpLoadBenchmark.MergePlansAsync</c>): два різні способи
    /// рахувати те саме розійдуться до першої розбіжності, і тоді жодне з
    /// чисел не буде зіставним із §3.2 базової лінії.
    ///
    /// ⚠ Спершу відбір планів СВОЄЇ бази, і лише потім текст — чому, див.
    /// <c>WritePathPlanCacheTests.PlanCountAsync</c>.
    /// </remarks>
    private async Task<int> PlanCountAsync(string pattern, CancellationToken ct)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET NOCOUNT ON;
            DECLARE @own TABLE (plan_handle varbinary(64) PRIMARY KEY);
            INSERT @own
            SELECT cp.plan_handle
            FROM sys.dm_exec_cached_plans AS cp
            CROSS APPLY sys.dm_exec_plan_attributes(cp.plan_handle) AS a
            WHERE a.attribute = 'dbid' AND CONVERT(int, a.value) = DB_ID();
            SELECT COUNT(*)
            FROM @own AS o
            CROSS APPLY sys.dm_exec_sql_text(o.plan_handle) AS t
            WHERE t.text LIKE @pattern AND t.dbid = DB_ID();
            """;
        command.Parameters.Add("@pattern", System.Data.SqlDbType.NVarChar, 200).Value = pattern;
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<int> ScalarAsync(string sqlText, long parameter, CancellationToken ct)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        command.Parameters.Add("@d", System.Data.SqlDbType.BigInt).Value = parameter;
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task ExecuteAsync(string sqlText, CancellationToken ct)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync(ct);
    }
}
