using System.Globalization;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Повторна архівація діапазону, що перекриває вже заархівовані періоди
/// (аудит 09.10c, U1-01).
/// </summary>
/// <remarks>
/// ⛔ Очищення цілі в <c>arc.usp_ArchiveYear</c> ішло на ВЕСЬ діапазон: якщо
/// частину діапазону вже перенесли (джерело звільнене <c>TRUNCATE</c>), а
/// частина ще в <c>doc.*</c>, <c>DELETE</c> стирав архів перших без копії,
/// звірка давала 0 = 0, прогін завершувався <c>Completed</c>.
/// <para>
/// ⚠ Періоди 202707–202708 не архівує жоден інший тест збірки
/// (<c>PartitionScanTests</c> лише пише в них дані). Кожен тест наприкінці
/// повертає все з архіву (<c>usp_RestoreYear</c>), щоб спільна база лишилася
/// такою, як до нього: архівація звільняє партицію ЦІЛКОМ, для всіх проєктів.
/// </para>
/// Мутації: (1) прибрати блок U1-01 з процедури — перші два тести червоні
/// (прогін <c>Completed</c>, архів 202707 порожній); (2) перевіряти лише
/// «період порожній у doc.*» замість ключів — червоніє другий тест; (3) відмовляти
/// на будь-якому непорожньому <c>arc.*</c> діапазону — червоніє третій.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveOverlapTests(SqlServerFixture sql)
{
    private const int First = 202707;
    private const int Second = 202708;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "U1-01")]
    public async Task Архівація_діапазону_що_перекриває_вже_заархівований_період_відмовляє_і_не_стирає_архів()
    {
        var yearly = await DocumentAsync(First);
        await CellAsync(yearly, 10m);
        var monthly = await DocumentAsync(Second);
        await CellAsync(monthly, 20m);

        // Річний проєкт архівують першим: партиція 202707 переїжджає в arc.*.
        await ArchiveAsync(yearly.ProjectId, First, First);
        var archivedCells = await CountAsync("arc.CellValue", First);
        var archivedRows = await CountAsync("arc.TableRow", First);
        var archivedSum = await SumAsync("arc.CellValue", First);
        Assert.True(archivedCells > 0);
        Assert.Equal(0, await CountAsync("doc.CellValue", First));

        var sourceCells = await CountAsync("doc.CellValue", Second);
        Assert.True(sourceCells > 0);

        try
        {
            // Далі — місячний, на діапазоні, що перекриває вже перенесений 202707.
            var error = await Assert.ThrowsAsync<SqlException>(
                () => ArchiveAsync(monthly.ProjectId, First, Second));

            Assert.Equal(50016, error.Number);
            // Перелік — рівно вже перенесені періоди, без 202708.
            Assert.Contains("(періоди: " + First.ToString(CultureInfo.InvariantCulture) + ")", error.Message, StringComparison.Ordinal);

            // ⛔ Архів 202707 — на місці, до рядка й до знака суми.
            Assert.Equal(archivedCells, await CountAsync("arc.CellValue", First));
            Assert.Equal(archivedRows, await CountAsync("arc.TableRow", First));
            Assert.Equal(archivedSum, await SumAsync("arc.CellValue", First));

            // Джерело 202708 не звільнене й не скопійоване.
            Assert.Equal(sourceCells, await CountAsync("doc.CellValue", Second));
            Assert.Equal(0, await CountAsync("arc.CellValue", Second));

            Assert.Equal("Failed", await LastStatusAsync(monthly.ProjectId));
            Assert.Equal(0, await ScalarAsync<int>(
                $"SELECT COUNT(*) FROM doc.Project WHERE Id = {monthly.ProjectId} AND IsArchiving = 1"));
        }
        finally
        {
            await RestoreAsync(yearly.ProjectId, First, First);
        }

        Assert.Equal(archivedCells, await CountAsync("doc.CellValue", First));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "U1-01")]
    public async Task Нові_дані_у_звільненій_партиції_не_дають_повторній_архівації_стерти_старий_архів()
    {
        var old = await DocumentAsync(First);
        await CellAsync(old, 11m);
        await ArchiveAsync(old.ProjectId, First, First);

        var archivedCells = await CountAsync("arc.CellValue", First);
        var archivedSum = await SumAsync("arc.CellValue", First);
        Assert.True(archivedCells > 0);

        // ⚠ Період у doc.* знову НЕПОРОЖНІЙ: лягли документи іншого проєкту.
        // Перевірка «період порожній у джерелі» тут мовчить — спрацювати має
        // перевірка по ключах.
        var late = await DocumentAsync(First);
        await CellAsync(late, 33m);
        var lateCells = await CountAsync("doc.CellValue", First);
        Assert.True(lateCells > 0);

        try
        {
            var error = await Assert.ThrowsAsync<SqlException>(
                () => ArchiveAsync(late.ProjectId, First, First));

            Assert.Equal(50016, error.Number);
            Assert.Equal(archivedCells, await CountAsync("arc.CellValue", First));
            Assert.Equal(archivedSum, await SumAsync("arc.CellValue", First));
            Assert.Equal(lateCells, await CountAsync("doc.CellValue", First));
        }
        finally
        {
            await RestoreAsync(old.ProjectId, First, First);
        }

        Assert.Equal(archivedCells + lateCells, await CountAsync("doc.CellValue", First));
        Assert.Equal(0, await CountAsync("arc.CellValue", First));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "U1-01")]
    public async Task Залишок_незавершеного_прогону_прибирається_і_повтор_проходить()
    {
        var doc = await DocumentAsync(Second);
        await CellAsync(doc, 5m);
        var before = await CountAsync("doc.CellValue", Second);

        // Незавершений прогін: крок 1 (копія в arc.*) пройшов, крок 2 зупинила
        // звірка 50014 — у проміжку в джерело ліг ще один рядок (той самий
        // прийом, що в `ArchiveJobTests.Запис_у_проміжку_…`).
        await ExecuteAsync($"""
            CREATE TRIGGER itg.TR_ArchiveOverlapGap ON itg.ArchiveRun AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted
                            WHERE Status = N'Running' AND LastDonePeriodKey = {Second})
                   AND NOT EXISTS (SELECT 1 FROM doc.CellValue
                                    WHERE PeriodKey = {Second}
                                      AND TableRowId = {doc.RowIds[1]}
                                      AND ColumnDefId = {doc.ColumnDefIds[2]})
                    INSERT INTO doc.CellValue
                        (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                    VALUES ({Second}, {doc.RowIds[1]}, {doc.ColumnDefIds[2]}, {doc.TableDefId}, 9, 0, 0);
            END
            """);

        try
        {
            var error = await Assert.ThrowsAsync<SqlException>(
                () => ArchiveAsync(doc.ProjectId, Second, Second));
            Assert.Equal(50014, error.Number);
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER itg.TR_ArchiveOverlapGap;");
        }

        // Залишок кроку 1 в архіві, джерело — ціле (плюс рядок із проміжку).
        Assert.Equal(before, await CountAsync("arc.CellValue", Second));
        Assert.Equal(before + 1, await CountAsync("doc.CellValue", Second));

        try
        {
            // ⚠ Повтор НЕ відмовляє: кожен рядок архіву має оригінал у doc.*,
            // тож очищення цілі прибирає лише копії, які прогін відтворить.
            await ArchiveAsync(doc.ProjectId, Second, Second);

            Assert.Equal("Completed", await LastStatusAsync(doc.ProjectId));
            Assert.Equal(before + 1, await CountAsync("arc.CellValue", Second));
            Assert.Equal(0, await CountAsync("doc.CellValue", Second));
        }
        finally
        {
            await RestoreAsync(doc.ProjectId, Second, Second);
        }

        Assert.Equal(before + 1, await CountAsync("doc.CellValue", Second));
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

    private Task CellAsync(TestDocument doc, decimal value)
        => ExecuteAsync($"""
            INSERT INTO doc.CellValue
                (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]},
                    {doc.TableDefId}, {value.ToString(CultureInfo.InvariantCulture)}, 0, 0)
            """);

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

    private Task<decimal> SumAsync(string table, int periodKey)
        => ScalarAsync<decimal>($"SELECT ISNULL(SUM(ValueNumeric), 0) FROM {table} WHERE PeriodKey = {periodKey}");

    private Task<string> LastStatusAsync(int projectId)
        => ScalarAsync<string>(
            $"SELECT TOP 1 Status FROM itg.ArchiveRun WHERE ProjectId = {projectId} "
            + "AND Direction = N'ToArchive' ORDER BY StartedAt DESC, Id DESC");

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
