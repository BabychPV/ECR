using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Розархівація року (<c>arc.usp_RestoreYear</c>): атомарність, сусідні
/// проєкти спільної партиції, повтор.
/// </summary>
/// <remarks>
/// ⚠ Кожен тест бере СВІЙ період (202705, 202709, 202710, 202711), якого не
/// торкається жоден інший тест збірки: база спільна для колекції, а
/// процедура працює з партицією цілком — чужий документ у тому самому
/// періоді зробив би асерти на кількостях залежними від порядку тестів.
/// </remarks>
[Collection("SqlServer")]
public sealed class RestoreYearTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_посередині_розархівації_зберігає_архів_і_повтор_проходить()
    {
        // ⚠ U1-03 (аудит 09.10c): копія тепер пакетами, кожен пакет — окрема
        // транзакція. Гарантія змістилася з «нічого не повернуто» на «архів
        // цілий, журнал Failed, читач іде в архів, повтор доводить повернення
        // без PK-конфлікту».
        //
        // ⛔ Z6-01 (аудит 8-го кола): читачі продукту (F-13) журнал не питають —
        // в архів вони йдуть лише за ПОРОЖНЬОЇ гарячої вибірки. Пакет 1 (комірка
        // 77) закомічено, пакет 2 (комірка 88) падає: без прибирання в `CATCH`
        // екземпляр лишався в doc.* з однією коміркою, і зріз віддавав 77 без 88.
        // Мутація: прибрати блок прибирання з `CATCH` `usp_RestoreYear` — у
        // doc.* лишається комірка, зріз без 88, червоний.
        const int period = 202705;
        var doc = await DocumentAsync(period);
        await CellAsync(doc, 0, 1, 77m);
        await CellAsync(doc, 1, 2, 88m);

        await ArchiveAsync(doc.ProjectId, period, period);
        var archivedInstances = await CountAsync("arc.TableInstance", period);
        var archivedRows = await CountAsync("arc.TableRow", period);
        var archivedCells = await CountAsync("arc.CellValue", period);

        // ⚠ Збій — на вставці КОМІРОК, тобто ПІСЛЯ того, як екземпляри й рядки
        // уже вставлено: саме так лягає бойовий збій (FK, місце на диску,
        // розбіжність 50011). Тригер, а не CHECK: його знімає `finally`, і він
        // не чіпає нічого, крім цього періоду.
        await ExecuteAsync($"""
            CREATE TRIGGER doc.TR_RestoreFailTest ON doc.CellValue AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE PeriodKey = {period} AND ValueNumeric = 88)
                    THROW 50999, N'Штучний збій вставки комірки (тест).', 1;
            END
            """);

        try
        {
            // Пакет у одну комірку: 77 і 88 — різні транзакції.
            await Assert.ThrowsAsync<SqlException>(() => RestoreAsync(doc.ProjectId, period, period, batchSize: 1));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER doc.TR_RestoreFailTest;");
        }

        // ⛔ Z6-01: закомічені пакети прибрано — у гарячій схемі періоду нічого,
        // тож F-13 веде читача в архів.
        Assert.Equal(0, await CountAsync("doc.CellValue", period));
        Assert.Equal(0, await CountAsync("doc.TableRow", period));
        Assert.Equal(0, await CountAsync("doc.TableInstance", period));

        await using (var db = sql.CreateContext())
        {
            var slice = await new NormalizedCellStore(db, new ArchiveAwareCellReader(db))
                .ReadSliceAsync(doc.TableInstanceId, default);
            var values = slice.Select(c => c.Value.ValueNumeric).ToList();
            Assert.Contains(77m, values);
            Assert.Contains(88m, values);
        }

        // ⛔ Архів — на місці, до рядка: до повної звірки його не чіпають.
        Assert.Equal(archivedInstances, await CountAsync("arc.TableInstance", period));
        Assert.Equal(archivedRows, await CountAsync("arc.TableRow", period));
        Assert.Equal(archivedCells, await CountAsync("arc.CellValue", period));

        var failed = await LastRunAsync(doc.ProjectId);
        Assert.Equal(("FromArchive", "Failed"), (failed.Direction, failed.Status));

        // Читач досі йде в архів: провалений прогін не рахується.
        await using (var db = sql.CreateContext())
        {
            Assert.True(await new ArchiveAwareCellReader(db).IsArchivedAsync(doc.ProjectId, doc.PeriodKey, default));
        }

        // Повтор після усунення причини — проходить, а не падає на PK.
        await RestoreAsync(doc.ProjectId, period, period);

        Assert.Equal(archivedCells, await CountAsync("doc.CellValue", period));
        Assert.Equal(archivedRows, await CountAsync("doc.TableRow", period));
        Assert.Equal(archivedInstances, await CountAsync("doc.TableInstance", period));
        Assert.Equal(0, await CountAsync("arc.CellValue", period));
        Assert.Equal(77m, await ValueAsync(doc, 0, 1));
        Assert.Equal(88m, await ValueAsync(doc, 1, 2));

        var done = await LastRunAsync(doc.ProjectId);
        Assert.Equal(("FromArchive", "Completed"), (done.Direction, done.Status));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розархівація_спільної_партиції_пише_журнал_на_кожен_повернутий_проєкт()
    {
        const int period = 202709;

        // Два проєкти в одній партиції. Архівували за A — фізично переїхав і B
        // (`usp_ArchiveYear` журналює обох). Повертають теж за A.
        var a = await DocumentAsync(period);
        await CellAsync(a, 0, 1, 11m);
        var b = await DocumentAsync(period);
        await CellAsync(b, 0, 1, 22m);

        var since = await ScalarAsync<long>("SELECT ISNULL(MAX(Id), 0) FROM itg.ArchiveRun");

        await ArchiveAsync(a.ProjectId, period, period);
        await RestoreAsync(a.ProjectId, period, period);

        // ⛔ Партиція повертається ЦІЛКОМ (фільтра за проєктом у неї немає, як і
        // в `usp_ArchiveYear`), тож і журнал мусить знати про B. Інакше
        // останній завершений прогін B — `ToArchive`, і читач іде в `arc.*`.
        //
        // ⚠ «Рівно ті, що повернуто» — це той самий набір, що архівація
        // записала `ToArchive`, а не «лише A і B»: база спільна, і в повному
        // прогоні інший тест збірки заводить проєкт із 40 періодами
        // 202601–202904 (перевірено запитом до `EcrTest_Infrastructure_*`), тож
        // чужий проєкт із періодом 202709 фізично переїжджає разом із нами.
        var archived = await ProjectsWithRunAsync("ToArchive", period, since);
        var restored = await ProjectsWithRunAsync("FromArchive", period, since);
        Assert.Contains(a.ProjectId, restored);
        Assert.Contains(b.ProjectId, restored);
        Assert.Equal(archived.Order(), restored.Order());

        await using (var db = sql.CreateContext())
        {
            var reader = new ArchiveAwareCellReader(db);
            Assert.False(await reader.IsArchivedAsync(b.ProjectId, b.PeriodKey, default));
            Assert.Equal(22m, Assert.Single(await reader.ReadAsync(b.ProjectId, b.TableInstanceId, b.PeriodKey, default)).Value.ValueNumeric);
        }

        // Правка B у гарячій схемі після розархівації — видима. Застарілий
        // архів замість неї — рівно той дефект, що ховався за журналом.
        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = 23 WHERE PeriodKey = {period} "
            + $"AND TableRowId = {b.RowIds[0]} AND ColumnDefId = {b.ColumnDefIds[1]}");

        await using (var db = sql.CreateContext())
        {
            var reader = new ArchiveAwareCellReader(db);
            Assert.Equal(23m, Assert.Single(await reader.ReadAsync(b.ProjectId, b.TableInstanceId, b.PeriodKey, default)).Value.ValueNumeric);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторна_розархівація_не_падає_на_PK_і_не_дублює_рядки()
    {
        const int period = 202710;
        var doc = await DocumentAsync(period);
        await CellAsync(doc, 0, 1, 5.25m);

        var cells = await CountAsync("doc.CellValue", period);
        var rows = await CountAsync("doc.TableRow", period);

        await ArchiveAsync(doc.ProjectId, period, period);
        await RestoreAsync(doc.ProjectId, period, period);

        // Другий виклик — той самий діапазон. Раніше: PK_TableInstance.
        await RestoreAsync(doc.ProjectId, period, period);

        Assert.Equal(cells, await CountAsync("doc.CellValue", period));
        Assert.Equal(rows, await CountAsync("doc.TableRow", period));
        Assert.Equal(5.25m, await ValueAsync(doc, 0, 1));

        var run = await LastRunAsync(doc.ProjectId);
        Assert.Equal(("FromArchive", "Completed"), (run.Direction, run.Status));
    }

    /// <summary>D4 аудиту: позначка осиротілого рядка переживає архів і повернення.</summary>
    /// <remarks>
    /// Раніше `arc.TableRow` не мав `IsOrphaned`, розархівація писала 0, і
    /// осиротілий рядок переставав блокувати подання (ECR-SUB-4221).
    /// Мутаційний доказ: прибрати `IsOrphaned` з вставки `usp_ArchiveYear` —
    /// червоніє читання з архіву; повернути `0` замість `a.IsOrphaned` у
    /// `usp_RestoreYear` — червоніє перевірка після відновлення.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Архівація_і_розархівація_зберігають_IsOrphaned_рядка()
    {
        const int period = 202711;
        var doc = await DocumentAsync(period);
        await CellAsync(doc, 0, 1, 1m);

        await ExecuteAsync(
            $"UPDATE doc.TableRow SET IsOrphaned = CASE WHEN Id = {doc.RowIds[0]} THEN 1 ELSE 0 END "
            + $"WHERE PeriodKey = {period} AND TableInstanceId = {doc.TableInstanceId}");

        await ArchiveAsync(doc.ProjectId, period, period);

        // Читання з архіву показує позначку, а не жорсткий `false`.
        await using (var db = sql.CreateContext())
        {
            var flags = await new ArchiveAwareCellReader(db)
                .ReadArchivedOrphanFlagsAsync(doc.TableInstanceId, doc.PeriodKey, default);
            Assert.True(flags.Single(f => f.Id == doc.RowIds[0]).IsOrphaned);
            Assert.False(flags.Single(f => f.Id == doc.RowIds[1]).IsOrphaned);
        }

        await RestoreAsync(doc.ProjectId, period, period);

        Assert.True(await OrphanAsync(period, doc.RowIds[0]));
        Assert.False(await OrphanAsync(period, doc.RowIds[1]));
    }

    private async Task<bool> OrphanAsync(int period, long rowId)
        => await ScalarAsync<int>(
            $"SELECT CAST(IsOrphaned AS int) FROM doc.TableRow WHERE PeriodKey = {period} AND Id = {rowId}")
            .ConfigureAwait(false) == 1;

    private async Task<TestDocument> DocumentAsync(int periodKey)
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: periodKey, ct: CancellationToken.None)
            .ConfigureAwait(false);

        await ExecuteAsync(
            $"UPDATE doc.Project SET Status = 3, ClosedAt = '2020-01-01' WHERE Id = {doc.ProjectId}")
            .ConfigureAwait(false);

        return doc;
    }

    private Task CellAsync(TestDocument doc, int row, int column, decimal value)
        => ExecuteAsync($"""
            INSERT INTO doc.CellValue
                (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[row]}, {doc.ColumnDefIds[column]},
                    {doc.TableDefId}, {value.ToString(CultureInfo.InvariantCulture)}, 0, 0)
            """);

    private Task<decimal> ValueAsync(TestDocument doc, int row, int column)
        => ScalarAsync<decimal>(
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} "
            + $"AND TableRowId = {doc.RowIds[row]} AND ColumnDefId = {doc.ColumnDefIds[column]}");

    /// <summary>Архівація так, як її виконує система (див. <c>ArchiveJobTests.ArchiveAsync</c>).</summary>
    private async Task ArchiveAsync(int projectId, int from, int to)
    {
        await ExecuteAsync(
            $"""
            UPDATE doc.Project SET Status = 4
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period
                           WHERE PeriodKey BETWEEN {from} AND {to});
            """).ConfigureAwait(false);

        await ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {projectId}, "
            + $"@FromPeriodKey = {from}, @ToPeriodKey = {to}").ConfigureAwait(false);
    }

    private Task RestoreAsync(int projectId, int from, int to, int batchSize = 500000)
        => ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {projectId}, "
            + $"@FromPeriodKey = {from}, @ToPeriodKey = {to}, @BatchSize = {batchSize}");

    private Task<int> CountAsync(string table, int periodKey)
        => ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE PeriodKey = {periodKey}");

    private async Task<List<int>> ProjectsWithRunAsync(string direction, int period, long sinceRunId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT DISTINCT ProjectId FROM itg.ArchiveRun WHERE Direction = N'{direction}' "
            + $"AND Status = N'Completed' AND FromPeriodKey <= {period} AND ToPeriodKey >= {period} "
            + $"AND Id > {sinceRunId}";

        var result = new List<int>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(reader.GetInt32(0));
        }

        return result;
    }

    private async Task<(string Direction, string Status)> LastRunAsync(int projectId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT TOP 1 Direction, Status FROM itg.ArchiveRun "
            + $"WHERE ProjectId = {projectId} ORDER BY StartedAt DESC, Id DESC";

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.True(await reader.ReadAsync().ConfigureAwait(false));

        return (reader.GetString(0), reader.GetString(1));
    }

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;

        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
}
