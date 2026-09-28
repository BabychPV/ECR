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
/// ⚠ Кожен тест бере СВІЙ період (202705, 202709, 202710), якого не
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
    public async Task Збій_посередині_розархівації_не_лишає_рік_відновленим_частково_і_повтор_проходить()
    {
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
                IF EXISTS (SELECT 1 FROM inserted WHERE PeriodKey = {period})
                    THROW 50999, N'Штучний збій вставки комірки (тест).', 1;
            END
            """);

        try
        {
            await Assert.ThrowsAsync<SqlException>(() => RestoreAsync(doc.ProjectId, period, period));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER doc.TR_RestoreFailTest;");
        }

        // ⛔ Нічого не відновлено частково: ані екземплярів, ані рядків без
        // своїх комірок у гарячій схемі. Без транзакції тут лишалися
        // закомічені TableInstance/TableRow, і повтор падав на PK.
        Assert.Equal(0, await CountAsync("doc.TableInstance", period));
        Assert.Equal(0, await CountAsync("doc.TableRow", period));
        Assert.Equal(0, await CountAsync("doc.CellValue", period));

        // Архів — на місці, до рядка.
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

    private Task RestoreAsync(int projectId, int from, int to)
        => ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {projectId}, "
            + $"@FromPeriodKey = {from}, @ToPeriodKey = {to}");

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
