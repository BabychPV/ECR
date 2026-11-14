using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// R-1 (аудит 2026-11, рішення PO 11.11, варіант А): сирі вʼюхи SSRS показують заархівований рік прозоро — стабільна
/// <c>rpt.v_&lt;Шаблон&gt;_&lt;Аркуш&gt;_&lt;Таблиця&gt;</c> (колонка <c>_IsArchived</c>) і довга <c>rpt.v_DocumentCells</c>
/// (колонка <c>IsArchived</c>) читають і <c>arc.*</c>; під час архівації чи повернення рядок не дублюється (гаряча
/// копія має перевагу); <c>rpt_reader</c> і далі не читає <c>arc.*</c> напряму.
/// </summary>
/// <remarks>
/// ⚠ Свій період (202806), якого не торкається жоден інший тест збірки: процедури архіву працюють з партицією цілком.
/// Без архівної гілки у <c>05-rpt-views.sql</c>: після архівації вʼюхи віддають 0 рядків (і немає колонки
/// <c>_IsArchived</c>); без <c>NOT EXISTS</c> в архівній гілці стан «обидві схеми» дає два рядки.
/// </remarks>
[Collection("SqlServer")]
public sealed partial class ArchivedYearReportViewsTests(SqlServerFixture sql)
{
    private const int Period = 202806;
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "R-1")]
    public async Task Архівний_рік_видно_у_вʼюхах_SSRS_з_позначкою_і_без_дублів_після_повернення()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: Period, ct: CancellationToken.None);
        await ExecuteAsync(
            $"UPDATE doc.Project SET Status = 3, ClosedAt = '2020-01-01' WHERE Id = {doc.ProjectId}");
        await ExecuteAsync($"""
            INSERT INTO doc.CellValue
                (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({Period}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 5.25, 0, 0)
            """);

        string stable, column;
        await using (var db = sql.CreateContext())
        {
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            version.Publish(publishedByUserId: 1, Now);
            await db.SaveChangesAsync();
            await new ReportViewGenerator(db).GenerateAsync(doc.TemplateVersionId, CancellationToken.None);

            var template = await db.Templates.Where(t => t.Id == doc.TemplateId).Select(t => t.Code).SingleAsync();
            var table = await db.TableDefs.Where(t => t.Id == doc.TableDefId).Select(t => t.Code).SingleAsync();
            column = await db.ColumnDefs.Where(c => c.Id == doc.ColumnDefIds[1]).Select(c => c.Code).SingleAsync();
            stable = NotIdentifier().Replace($"v_{template}_{doc.SheetCode}_{table}", "_");
        }

        // 1. До архівації — один гарячий рядок.
        Assert.Equal((1, 0, 5.25m), await StableAsync(stable, column, doc.DocumentId));
        Assert.Equal((1, 0), await LongAsync(doc.DocumentId));

        // 2. Після архівації гарячі партиції порожні, а вʼюхи віддають той самий рядок з архіву.
        await ArchiveAsync(doc.ProjectId);
        Assert.Equal(0, await ScalarAsync<int>($"SELECT COUNT(*) FROM doc.CellValue WHERE PeriodKey = {Period}"));
        Assert.Equal((1, 1, 5.25m), await StableAsync(stable, column, doc.DocumentId));
        Assert.Equal((1, 1), await LongAsync(doc.DocumentId));

        // 3. Проміжний стан «обидві схеми» (крок 1 повернення вже скопіював екземпляр, архів ще не прибрано):
        //    рівно один рядок, гарячий.
        await ExecuteAsync($"""
            INSERT INTO doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt, SheetCopyId)
            SELECT a.PeriodKey, a.Id, a.DocumentId, a.TableDefId, a.CreatedAt, a.ModifiedAt, a.SheetCopyId
              FROM arc.TableInstance AS a WHERE a.PeriodKey = {Period} AND a.Id = {doc.TableInstanceId};
            INSERT INTO doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, IsOrphaned, ModifiedAt)
            SELECT a.PeriodKey, a.Id, a.TableInstanceId, a.RowKey, a.RowDefId, a.Ordinal, a.IsDeleted, a.IsOrphaned, a.ModifiedAt
              FROM arc.TableRow AS a WHERE a.PeriodKey = {Period} AND a.TableInstanceId = {doc.TableInstanceId};
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric, ValueDate,
                                       ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
            SELECT a.PeriodKey, a.TableRowId, a.ColumnDefId, a.TableDefId, a.ValueString, a.ValueNumeric, a.ValueDate,
                   a.ValueBool, a.ValueRegistryEntryId, a.ValueUnitId, a.IsCalculated, a.IsEmpty
              FROM arc.CellValue AS a
             WHERE a.PeriodKey = {Period}
               AND a.TableRowId IN (SELECT r.Id FROM arc.TableRow AS r
                                     WHERE r.PeriodKey = {Period} AND r.TableInstanceId = {doc.TableInstanceId});
            """);
        Assert.True(await ScalarAsync<int>($"SELECT COUNT(*) FROM arc.CellValue WHERE PeriodKey = {Period}") > 0);
        Assert.Equal((1, 0, 5.25m), await StableAsync(stable, column, doc.DocumentId));
        Assert.Equal((1, 0), await LongAsync(doc.DocumentId));

        // 4. Повернення (повтор після часткової копії пропускає наявне) — архів порожній, рядок один і гарячий.
        await ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {doc.ProjectId}, @FromPeriodKey = {Period}, @ToPeriodKey = {Period}");
        Assert.Equal(0, await ScalarAsync<int>($"SELECT COUNT(*) FROM arc.CellValue WHERE PeriodKey = {Period}"));
        Assert.Equal((1, 0, 5.25m), await StableAsync(stable, column, doc.DocumentId));

        // 5. Права: обліковий запис SSRS читає архівний рік КРІЗЬ вʼюху (ланцюг власності), а arc.* напряму — ні.
        await ArchiveAsync(doc.ProjectId);
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await RunAsync(connection, """
                IF USER_ID(N'rpt_probe') IS NULL CREATE USER rpt_probe WITHOUT LOGIN;
                ALTER ROLE rpt_reader ADD MEMBER rpt_probe;
                """);
            await RunAsync(connection, "EXECUTE AS USER = N'rpt_probe';");
            try
            {
                await using var read = connection.CreateCommand();
                read.CommandText =
                    $"SELECT COUNT(*) FROM rpt.[{stable}] WHERE _DocumentId = {doc.DocumentId} AND _PeriodKey = {Period} "
                    + $"AND _IsArchived = 1 AND [{column}] = 5.25";
                Assert.Equal(1, Convert.ToInt32(await read.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

                await using var direct = connection.CreateCommand();
                direct.CommandText = "SELECT COUNT(*) FROM arc.CellValue";
                var denied = await Assert.ThrowsAsync<SqlException>(() => direct.ExecuteScalarAsync());
                Assert.Equal(229, denied.Number);
            }
            finally
            {
                await RunAsync(connection, "REVERT;");
            }
        }

        // ⛔ Сторож ланцюга власності: схеми вʼюх, гарячих і архівних таблиць мають ОДНОГО власника. Інакше rpt_reader
        // отримає 229 на архівній гілці.
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(DISTINCT principal_id) FROM sys.schemas WHERE name IN (N'rpt', N'doc', N'arc')"));
    }

    private async Task<(int Rows, int Archived, decimal Value)> StableAsync(string view, string column, long documentId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*), COALESCE(MAX(CAST(_IsArchived AS int)), -1), COALESCE(MAX([{column}]), 0) "
            + $"FROM rpt.[{view}] WHERE _DocumentId = {documentId} AND _PeriodKey = {Period} AND [{column}] IS NOT NULL";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetDecimal(2));
    }

    private async Task<(int Rows, int Archived)> LongAsync(long documentId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), COALESCE(MAX(CAST(IsArchived AS int)), -1) FROM rpt.v_DocumentCells "
            + $"WHERE DocumentId = {documentId} AND PeriodKey = {Period} AND ValueNumeric IS NOT NULL";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>Архівація так, як її виконує система (див. <c>RestoreYearTests.ArchiveAsync</c>).</summary>
    private async Task ArchiveAsync(int projectId)
    {
        await ExecuteAsync($"""
            UPDATE doc.Project SET Status = 4
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period WHERE PeriodKey = {Period});
            """);
        await ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {projectId}, @FromPeriodKey = {Period}, @ToPeriodKey = {Period}");
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await RunAsync(connection, text);
    }

    private static async Task RunAsync(SqlConnection connection, string text)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;
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

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotIdentifier();
}
