using System.Globalization;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B81 (F13 #877): позначка «архівується» (<c>doc.Project.IsArchiving</c>) під час прогону архівації чи розархівації
/// ніколи не стоїть на АКТИВНОМУ проєкті — інакше активний сусід того самого ключа періоду бачив «проєкт архівований» і
/// не міг правити, поки триває прогін.
/// </summary>
/// <remarks>
/// ⚠ Період 202706 — той самий, що в <see cref="ArchiveTamperDetectionTests"/> (колекція послідовна, кожен тест лишає
/// період у гарячій схемі, а активного сусіда наприкінці переводить у Archived, щоб наступна архівація періоду не
/// впиралася в ЗАПОБІЖНИК 1).
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveFlagScopeTests(SqlServerFixture sql)
{
    private const int Period = 202706;

    /// <remarks>
    /// Розархівація: журнал пише всіх проєктів <c>doc.Period</c> діапазону, запобіжника немає. Позначку під час прогону
    /// знімає тригер на <c>itg.ArchiveRun</c> (процедура оновлює журнал посеред кроку 2, коли позначка стоїть).
    /// Червоне без фіксу: активний сусід мав <c>IsArchiving = 1</c> під час прогону. Контроль: архівований проєкт — 1.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B81-3")]
    public async Task Розархівація_не_позначає_активного_сусіда_архівованим()
    {
        var archived = await DocumentAsync();
        await ExecuteAsync($"""
            UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01'
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period WHERE PeriodKey = {Period});
            """);
        await ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {archived.ProjectId}, @FromPeriodKey = {Period}, @ToPeriodKey = {Period};");

        // Активний проєкт завів документ у той самий ключ періоду вже після архівації.
        var active = await DocumentAsync();
        await ExecuteAsync($"UPDATE doc.Project SET Status = 1 WHERE Id = {active.ProjectId};");

        await ExecuteAsync("""
            IF OBJECT_ID(N'dbo.B81Probe') IS NOT NULL DROP TABLE dbo.B81Probe;
            CREATE TABLE dbo.B81Probe (ProjectId int NOT NULL, IsArchiving bit NOT NULL);
            """);
        await ExecuteAsync($"""
            CREATE TRIGGER itg.TR_B81FlagProbe ON itg.ArchiveRun AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT EXISTS (SELECT 1 FROM dbo.B81Probe)
                    INSERT INTO dbo.B81Probe (ProjectId, IsArchiving)
                    SELECT p.Id, p.IsArchiving FROM doc.Project AS p
                     WHERE p.Id IN ({archived.ProjectId}, {active.ProjectId});
            END
            """);

        try
        {
            await ExecuteAsync(
                $"EXEC arc.usp_RestoreYear @ProjectId = {archived.ProjectId}, @FromPeriodKey = {Period}, @ToPeriodKey = {Period};");

            Assert.False(await FlagDuringRunAsync(active.ProjectId));
            Assert.True(await FlagDuringRunAsync(archived.ProjectId));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER itg.TR_B81FlagProbe; DROP TABLE dbo.B81Probe;");
            await ExecuteAsync($"UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01' WHERE Id = {active.ProjectId};");
        }
    }

    /// <remarks>
    /// Архівація: активний сусід у діапазоні неможливий за побудовою — ЗАПОБІЖНИК 1 відмовляє (50012) ДО позначки, і
    /// позначка не ставиться нікому. Фільтр <c>Status &lt;&gt; 1</c> у <c>usp_ArchiveYear</c> — захист у глибину; тест
    /// тримає саме гарантію «активного не позначено», хоч би який із двох механізмів її давав.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B81-3")]
    public async Task Архівація_з_активним_сусідом_відмовляє_і_нікого_не_позначає()
    {
        var archived = await DocumentAsync();
        await ExecuteAsync($"""
            UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01'
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period WHERE PeriodKey = {Period});
            """);
        var active = await DocumentAsync();
        await ExecuteAsync($"UPDATE doc.Project SET Status = 1 WHERE Id = {active.ProjectId};");

        try
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
                $"EXEC arc.usp_ArchiveYear @ProjectId = {archived.ProjectId}, @FromPeriodKey = {Period}, @ToPeriodKey = {Period};"));

            Assert.Equal(50012, error.Number);
            Assert.False(await IsArchivingAsync(active.ProjectId));
            Assert.False(await IsArchivingAsync(archived.ProjectId));
        }
        finally
        {
            await ExecuteAsync($"UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01' WHERE Id = {active.ProjectId};");
        }
    }

    private async Task<TestDocument> DocumentAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: Period, rowCount: 1, ct: CancellationToken.None);

        await ExecuteAsync($"""
            INSERT INTO doc.CellValue
                (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({Period}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId},
                    {3.5m.ToString(CultureInfo.InvariantCulture)}, 0, 0)
            """);

        return doc;
    }

    private async Task<bool> FlagDuringRunAsync(int projectId)
        => await ScalarAsync($"SELECT CAST(IsArchiving AS int) FROM dbo.B81Probe WHERE ProjectId = {projectId}") == 1;

    private async Task<bool> IsArchivingAsync(int projectId)
        => await ScalarAsync($"SELECT CAST(IsArchiving AS int) FROM doc.Project WHERE Id = {projectId}") == 1;

    private async Task<int> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        Assert.NotNull(value);
        Assert.IsNotType<DBNull>(value);
        return (int)value;
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
