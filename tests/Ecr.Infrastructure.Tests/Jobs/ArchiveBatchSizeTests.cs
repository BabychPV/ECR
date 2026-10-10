using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// `@BatchSize` у `arc.usp_ArchiveYear` справді ділить копію на пакети (аудит L10-14).
/// </summary>
/// <remarks>
/// ⚠ Пакет 3 на 10 комірках і 5 рядках: межа пакета падає ПОСЕРЕДИНІ рядка
/// (ключ комірки — пара `TableRowId, ColumnDefId`), тож перевіряється саме
/// двоколонковий keyset, а не лише «пакет = рядок».
///
/// Мутації: (1) у циклі комірок поставити `BREAK` після першого INSERT —
/// звірка 50010, тест червоний; (2) у нижній межі пакета `ColumnDefId &lt;= @hiCol`
/// замінити на `&lt;` — губиться остання комірка кожного пакета, 50010.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveBatchSizeTests(SqlServerFixture sql)
{
    private const int Period = 202702;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L10-14")]
    public async Task Малий_пакет_переносить_усі_комірки_рядки_й_екземпляри_без_втрат()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: Period, columnCount: 3, rowCount: 5, ct: CancellationToken.None);

        // Числові колонки — друга й третя (перша текстова): 5 × 2 = 10 комірок.
        var value = 1.0000000000000001m;
        foreach (var row in doc.RowIds)
        {
            foreach (var column in doc.ColumnDefIds.Skip(1))
            {
                await ExecuteAsync($"""
                    INSERT INTO doc.CellValue
                        (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                    VALUES ({Period}, {row}, {column}, {doc.TableDefId},
                            {value.ToString(CultureInfo.InvariantCulture)}, 0, 0);
                    """);
                value += 1m;
            }
        }

        await ExecuteAsync($"""
            UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01'
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period WHERE PeriodKey = {Period});
            """);

        var cells = await CountAsync("doc.CellValue");
        var rows = await CountAsync("doc.TableRow");
        var instances = await CountAsync("doc.TableInstance");
        var sum = await ScalarAsync<decimal>($"SELECT SUM(ValueNumeric) FROM doc.CellValue WHERE PeriodKey = {Period}");
        Assert.True(cells >= 10 && rows >= 5, $"фікстура замала: комірок {cells}, рядків {rows}");

        await ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {doc.ProjectId}, "
            + $"@FromPeriodKey = {Period}, @ToPeriodKey = {Period}, @BatchSize = 3;");

        Assert.Equal(0, await CountAsync("doc.CellValue"));
        Assert.Equal(cells, await CountAsync("arc.CellValue"));
        Assert.Equal(rows, await CountAsync("arc.TableRow"));
        Assert.Equal(instances, await CountAsync("arc.TableInstance"));
        Assert.Equal(sum, await ScalarAsync<decimal>($"SELECT SUM(ValueNumeric) FROM arc.CellValue WHERE PeriodKey = {Period}"));

        // Без дублів: кожна пара ключа в архіві рівно один раз.
        Assert.Equal(cells, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM (SELECT DISTINCT TableRowId, ColumnDefId FROM arc.CellValue WHERE PeriodKey = {Period}) AS d"));

        // Період повертається: спільна база лишається такою, як до тесту.
        await ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {doc.ProjectId}, @FromPeriodKey = {Period}, @ToPeriodKey = {Period};");
        Assert.Equal(cells, await CountAsync("doc.CellValue"));
    }

    /// <summary>
    /// `@BatchSize` у `arc.usp_RestoreYear`: повернення йде пакетами, кожен — окрема
    /// транзакція (аудит 09.10c, U1-03).
    /// </summary>
    /// <remarks>
    /// ⛔ Доказ «окремих транзакцій» — збій на ОСТАННІЙ комірці: пакети до неї
    /// закомічені. Однією транзакцією на рік (як було) відкотилося б усе,
    /// а журнал і ескалація замків росли б разом із роком. Після Z6-01 `CATCH`
    /// прибирає закомічене з doc.*, тож доказ — число прибраних комірок у журналі.
    /// Мутація: обгорнути крок 1 у `BEGIN TRAN … COMMIT` — прибрано 0 комірок,
    /// тест червоний; `ColumnDefId &lt;= @hiCol` → `&lt;` — звірка 50011 на повторі;
    /// прибрати прибирання з `CATCH` (Z6-01) — у doc.* лишаються комірки, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "U1-03")]
    public async Task Розархівація_пакетами_комітить_пакети_окремо_і_повтор_доводить_без_дублів()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: Period, columnCount: 3, rowCount: 5, ct: CancellationToken.None);

        var value = 2.0000000000000003m;
        foreach (var row in doc.RowIds)
        {
            foreach (var column in doc.ColumnDefIds.Skip(1))
            {
                await ExecuteAsync($"""
                    INSERT INTO doc.CellValue
                        (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                    VALUES ({Period}, {row}, {column}, {doc.TableDefId},
                            {value.ToString(CultureInfo.InvariantCulture)}, 0, 0);
                    """);
                value += 1m;
            }
        }

        await ExecuteAsync($"""
            UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01'
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period WHERE PeriodKey = {Period});
            """);

        var cells = await CountAsync("doc.CellValue");
        var rows = await CountAsync("doc.TableRow");
        var instances = await CountAsync("doc.TableInstance");
        var sum = await ScalarAsync<decimal>($"SELECT SUM(ValueNumeric) FROM doc.CellValue WHERE PeriodKey = {Period}");
        Assert.True(cells >= 10 && rows >= 5, $"фікстура замала: комірок {cells}, рядків {rows}");

        await ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {doc.ProjectId}, "
            + $"@FromPeriodKey = {Period}, @ToPeriodKey = {Period};");
        Assert.Equal(cells, await CountAsync("arc.CellValue"));

        // Остання за ключем комірка періоду — комірка нашого документа: його рядки
        // створено останніми, тож їхні Id найбільші.
        var lastRow = doc.RowIds.Max();
        var lastColumn = doc.ColumnDefIds.Skip(1).Max();
        await ExecuteAsync($"""
            CREATE TRIGGER doc.TR_RestoreBatchFailTest ON doc.CellValue AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted
                            WHERE PeriodKey = {Period} AND TableRowId = {lastRow} AND ColumnDefId = {lastColumn})
                    THROW 50999, N'Штучний збій на останньому пакеті (тест).', 1;
            END
            """);

        try
        {
            await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
                $"EXEC arc.usp_RestoreYear @ProjectId = {doc.ProjectId}, "
                + $"@FromPeriodKey = {Period}, @ToPeriodKey = {Period}, @BatchSize = 3;"));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER doc.TR_RestoreBatchFailTest;");
        }

        // ⛔ Пакети до збою закомічені — копія не одна транзакція на рік. Після Z6-01 `CATCH` прибирає
        // часткову копію з doc.* (інакше F-13 показував би її як гарячі дані з дірками), тож доказ —
        // журнал прибирання: прибрано стільки комірок, скільки закомітили пакети ДО збійного.
        // Однією транзакцією на рік прибирати було б нічого (0), а без пакетів — усе (cells).
        Assert.Equal(0, await CountAsync("doc.CellValue"));
        Assert.Equal(0, await CountAsync("doc.TableRow"));
        Assert.Equal(0, await CountAsync("doc.TableInstance"));
        var journal = await ScalarAsync<string>(
            $"SELECT TOP 1 ErrorMessage FROM itg.ArchiveRun WHERE ProjectId = {doc.ProjectId} "
            + "AND Direction = N'FromArchive' ORDER BY Id DESC");
        var undone = System.Text.RegularExpressions.Regex.Match(journal ?? string.Empty, @"комірок (\d+)");
        Assert.True(undone.Success, $"У журналі немає сліду прибирання: {journal}");
        Assert.InRange(int.Parse(undone.Groups[1].Value, CultureInfo.InvariantCulture), 1, cells - 1);

        // Архів цілий: до повної звірки його не чіпають.
        Assert.Equal(cells, await CountAsync("arc.CellValue"));
        Assert.Equal(rows, await CountAsync("arc.TableRow"));
        Assert.Equal(instances, await CountAsync("arc.TableInstance"));

        // Повтор доводить повернення: без PK-конфлікту, без дублів, архів прибрано.
        await ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {doc.ProjectId}, "
            + $"@FromPeriodKey = {Period}, @ToPeriodKey = {Period}, @BatchSize = 3;");

        Assert.Equal(cells, await CountAsync("doc.CellValue"));
        Assert.Equal(rows, await CountAsync("doc.TableRow"));
        Assert.Equal(instances, await CountAsync("doc.TableInstance"));
        Assert.Equal(sum, await ScalarAsync<decimal>($"SELECT SUM(ValueNumeric) FROM doc.CellValue WHERE PeriodKey = {Period}"));
        Assert.Equal(cells, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM (SELECT DISTINCT TableRowId, ColumnDefId FROM doc.CellValue WHERE PeriodKey = {Period}) AS d"));
        Assert.Equal(0, await CountAsync("arc.CellValue"));
        Assert.Equal(0, await CountAsync("arc.TableRow"));
        Assert.Equal(0, await CountAsync("arc.TableInstance"));
    }

    /// <summary>
    /// V8-02 (Z6-01): після збою посеред КРОКУ 1 <c>arc.usp_RestoreYear</c> читачі зрізу беруть архів, а не
    /// закомічену частину року з гарячої схеми.
    /// </summary>
    /// <remarks>
    /// Доти <c>NormalizedCellStore</c> переходив на архів лише за порожньою гарячою вибіркою: частина комірок
    /// (пакети до збою) показувалася як повний зріз, решта — порожніми, хоча журнал (<c>Failed</c>) і архів кажуть
    /// «рік в архіві».
    /// Мутація: прибрати перевірку <c>ArchivedOfHotInstancesAsync</c> у <c>SliceOrArchiveAsync</c>/
    /// <c>SlicesOrArchiveAsync</c> — зріз коротший за архів, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "V8-02")]
    public async Task Після_збою_посеред_розархівації_зріз_читається_з_архіву_а_не_частково_з_гарячої_схеми()
    {
        const int period = 202805;
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: period, columnCount: 3, rowCount: 5, ct: CancellationToken.None);

        var value = 1m;
        foreach (var row in doc.RowIds)
        {
            foreach (var column in doc.ColumnDefIds.Skip(1))
            {
                await ExecuteAsync($"""
                    INSERT INTO doc.CellValue
                        (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                    VALUES ({period}, {row}, {column}, {doc.TableDefId},
                            {value.ToString(CultureInfo.InvariantCulture)}, 0, 0);
                    """);
                value += 1m;
            }
        }

        await ExecuteAsync($"""
            UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01'
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period WHERE PeriodKey = {period});
            """);

        var expected = await InstanceCellsAsync("doc", doc.TableInstanceId, period);
        Assert.True(expected >= 10, $"фікстура замала: комірок {expected}");

        await ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {doc.ProjectId}, "
            + $"@FromPeriodKey = {period}, @ToPeriodKey = {period};");

        var lastRow = doc.RowIds.Max();
        var lastColumn = doc.ColumnDefIds.Skip(1).Max();
        await ExecuteAsync($"""
            CREATE TRIGGER doc.TR_RestorePartialReadTest ON doc.CellValue AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted
                            WHERE PeriodKey = {period} AND TableRowId = {lastRow} AND ColumnDefId = {lastColumn})
                    THROW 50999, N'Штучний збій на останньому пакеті (тест).', 1;
            END
            """);

        // ⚠ Z6-01: при збої процедура сама прибирає часткову копію з doc.*. Цей тест — про ІНШИЙ
        // випадок (V8-02): прибирання теж не вдалося, і часткова копія лишилася. Імітуємо збій
        // прибирання — інакше гаряча схема порожня і шлях читача «є гаряча частина» не перевіряється.
        await ExecuteAsync($"""
            CREATE TRIGGER doc.TR_RestoreCleanupFailTest ON doc.CellValue AFTER DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted WHERE PeriodKey = {period})
                    THROW 50998, N'Штучний збій прибирання часткової копії (тест).', 1;
            END
            """);

        try
        {
            await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
                $"EXEC arc.usp_RestoreYear @ProjectId = {doc.ProjectId}, "
                + $"@FromPeriodKey = {period}, @ToPeriodKey = {period}, @BatchSize = 3;"));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER doc.TR_RestorePartialReadTest;");
            await ExecuteAsync("DROP TRIGGER doc.TR_RestoreCleanupFailTest;");
        }

        // Передумова: у гарячій схемі справді частина комірок екземпляра, архів цілий.
        Assert.InRange(await InstanceCellsAsync("doc", doc.TableInstanceId, period), 1, expected - 1);
        Assert.Equal(expected, await InstanceCellsAsync("arc", doc.TableInstanceId, period));

        await using (var db = sql.CreateContext())
        {
            var store = new NormalizedCellStore(db, new ArchiveAwareCellReader(db));

            Assert.Equal(expected, (await store.ReadSliceAsync(doc.TableInstanceId, CancellationToken.None)).Count);
            Assert.Equal(
                expected, (await store.ReadSliceAsync(doc.TableInstanceId, doc.PeriodKey, CancellationToken.None)).Count);
            Assert.Equal(
                expected,
                (await store.ReadSlicesAsync([doc.TableInstanceId], CancellationToken.None))[doc.TableInstanceId].Count);
            Assert.Equal(
                expected,
                (await store.ReadSlicesAsync([doc.TableInstanceId], doc.PeriodKey, CancellationToken.None))[doc.TableInstanceId].Count);
        }

        // Контроль: повтор доводить повернення — тепер джерело гаряча схема, і зріз той самий.
        await ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {doc.ProjectId}, "
            + $"@FromPeriodKey = {period}, @ToPeriodKey = {period}, @BatchSize = 3;");
        Assert.Equal(0, await InstanceCellsAsync("arc", doc.TableInstanceId, period));

        await using (var db = sql.CreateContext())
        {
            var store = new NormalizedCellStore(db, new ArchiveAwareCellReader(db));
            Assert.Equal(expected, (await store.ReadSliceAsync(doc.TableInstanceId, CancellationToken.None)).Count);
        }
    }

    /// <summary>Комірки одного екземпляра в схемі <paramref name="schema"/> (<c>doc</c> чи <c>arc</c>).</summary>
    private Task<int> InstanceCellsAsync(string schema, long tableInstanceId, int period)
        => ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {schema}.CellValue c "
            + $"JOIN {schema}.TableRow r ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId "
            + $"WHERE r.TableInstanceId = {tableInstanceId} AND r.PeriodKey = {period}");

    [Theory]
    [InlineData("0")]
    [InlineData("NULL")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "U1-03")]
    public async Task Непозитивний_пакет_розархівації_відхиляється_до_будь_якої_зміни(string batchSize)
    {
        var runsBefore = await ScalarAsync<int>("SELECT COUNT(*) FROM itg.ArchiveRun");

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = 1, @FromPeriodKey = {Period}, "
            + $"@ToPeriodKey = {Period}, @BatchSize = {batchSize};"));

        Assert.Equal(50015, error.Number);
        Assert.Equal(runsBefore, await ScalarAsync<int>("SELECT COUNT(*) FROM itg.ArchiveRun"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("NULL")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L10-14")]
    public async Task Непозитивний_пакет_відхиляється_до_будь_якої_зміни(string batchSize)
    {
        var runsBefore = await ScalarAsync<int>("SELECT COUNT(*) FROM itg.ArchiveRun");

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = 1, @FromPeriodKey = {Period}, "
            + $"@ToPeriodKey = {Period}, @BatchSize = {batchSize};"));

        Assert.Equal(50015, error.Number);
        Assert.Equal(runsBefore, await ScalarAsync<int>("SELECT COUNT(*) FROM itg.ArchiveRun"));
    }

    private Task<int> CountAsync(string table)
        => ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE PeriodKey = {Period}");

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;

        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
}
