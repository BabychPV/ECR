using System.Globalization;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Архівація під обліковим записом без DDL-прав (аудит L10-08, `D-66`).
/// </summary>
/// <remarks>
/// ⛔ Решта тестів архівації ходить під `sa` фікстури — і саме тому не бачила,
/// що `ALTER TABLE … DROP CONSTRAINT` і динамічний `TRUNCATE … WITH (PARTITIONS)`
/// у `arc.usp_ArchiveYear` вимагають права `ALTER` від викликача: ланцюжок
/// власності покриває DML, але не DDL і не динамічний SQL. Служба DDL-прав не
/// має, отже штатна архівація під нею падала б.
///
/// Тут викликач — користувач без логіна з ЄДИНИМ правом `EXECUTE` на процедуру
/// (ні `db_datareader`, ні `db_datawriter`): усе, що процедура робить понад
/// це, мусить іти від імені власника (`WITH EXECUTE AS OWNER`).
///
/// Мутація: прибрати `WITH EXECUTE AS OWNER` з `usp_ArchiveYear` —
/// перший тест червоний (відмова в правах на `ALTER TABLE`); з
/// `usp_RestoreArchiveConstraints` — другий.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveLeastPrivilegeTests(SqlServerFixture sql)
{
    private const string LowPrivilegeUser = "ecr_test_archive_lowpriv";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L10-08")]
    public async Task Архівація_і_розархівація_проходять_під_викликачем_лише_з_EXECUTE()
    {
        await EnsureLowPrivilegeUserAsync();

        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: 202702, ct: CancellationToken.None);

        await ExecuteAsync($"""
            INSERT INTO doc.CellValue
                (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]},
                    {doc.TableDefId}, 42.125, 0, 0);
            UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01'
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period WHERE PeriodKey = 202702);
            """);

        var before = await CountAsync("doc.CellValue", 202702);
        Assert.True(before > 0);

        await ExecuteAsLowPrivilegeAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {doc.ProjectId}, @FromPeriodKey = 202702, @ToPeriodKey = 202702;");

        Assert.Equal(0, await CountAsync("doc.CellValue", 202702));
        Assert.Equal(before, await CountAsync("arc.CellValue", 202702));

        // Ключі повернуто довіреними: процедура зняла й поставила їх від імені власника.
        Assert.Equal(2, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.foreign_keys "
            + "WHERE name IN (N'FK_CellValue_Row', N'FK_TableRow_Instance') AND is_not_trusted = 0"));

        await ExecuteAsLowPrivilegeAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {doc.ProjectId}, @FromPeriodKey = 202702, @ToPeriodKey = 202702;");

        Assert.Equal(before, await CountAsync("doc.CellValue", 202702));
        Assert.Equal(0, await CountAsync("arc.CellValue", 202702));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L10-08")]
    public async Task Повернення_ключів_проходить_під_викликачем_лише_з_EXECUTE()
    {
        await EnsureLowPrivilegeUserAsync();

        try
        {
            await ExecuteAsync("""
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CellValue_Row')
                    ALTER TABLE doc.CellValue DROP CONSTRAINT FK_CellValue_Row;
                """);

            await ExecuteAsLowPrivilegeAsync("EXEC arc.usp_RestoreArchiveConstraints;");

            Assert.Equal(1, await ScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_CellValue_Row' AND is_not_trusted = 0"));
        }
        finally
        {
            // Спільна база лишається з ключем, хоч би що сталося вище.
            await ExecuteAsync("EXEC arc.usp_RestoreArchiveConstraints;");
        }
    }

    /// <summary>Користувач без логіна з єдиним правом — `EXECUTE` на три процедури.</summary>
    private Task EnsureLowPrivilegeUserAsync()
        => ExecuteAsync($"""
            IF USER_ID(N'{LowPrivilegeUser}') IS NULL
                CREATE USER [{LowPrivilegeUser}] WITHOUT LOGIN;
            GRANT EXECUTE ON OBJECT::arc.usp_ArchiveYear TO [{LowPrivilegeUser}];
            GRANT EXECUTE ON OBJECT::arc.usp_RestoreYear TO [{LowPrivilegeUser}];
            GRANT EXECUTE ON OBJECT::arc.usp_RestoreArchiveConstraints TO [{LowPrivilegeUser}];
            """);

    /// <summary>
    /// Виконує пакет від імені користувача з мінімальними правами й повертає контекст.
    /// </summary>
    /// <remarks>
    /// ⚠ З'єднання — поза пулом. Якщо процедура впаде до `REVERT`, з'єднання з
    /// пулу повернулося б під чужим контекстом, і наступний `sp_reset_connection`
    /// убив би сесію (Msg 18059) — падав би вже не цей тест, а сусідній.
    /// </remarks>
    private async Task ExecuteAsLowPrivilegeAsync(string statement)
    {
        var unpooled = new SqlConnectionStringBuilder(sql.ConnectionString) { Pooling = false }.ConnectionString;
        await using var connection = new SqlConnection(unpooled);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXECUTE AS USER = N'{LowPrivilegeUser}'; {statement} REVERT;";
        await command.ExecuteNonQueryAsync();
    }

    private Task<int> CountAsync(string table, int periodKey)
        => ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE PeriodKey = {periodKey}");

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
