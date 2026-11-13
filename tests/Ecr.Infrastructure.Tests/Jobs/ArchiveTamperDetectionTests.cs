using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B70: пряма правка архіву в SQL виявляється нічною перевіркою узгодженості (<c>ARCHIVE_CHECKSUM</c>, сутність
/// <c>arc.Period</c>).
/// </summary>
/// <remarks>
/// ⛔ Слово людини: «заархівовані дані не мають змінюватися; від адміна SQL ніщо не захищає». Тут архівація — справжня
/// процедура (<c>arc.usp_ArchiveYear</c>), правка — прямий SQL у тесті, перевірка — справжня задача.
/// <para>
/// ⚠ Період <see cref="Period"/> не архівує жоден інший тест збірки (його читає лише <c>PartitionScanTests</c> — за своїми
/// рядками). Процедура працює з партицією цілком, тому кожен тест повертає період у гарячу схему в <c>finally</c>
/// (<c>arc.usp_RestoreYear</c>) і на старті прибирає знахідки свого періоду від попереднього тесту.
/// </para>
/// <para>
/// Мутації (RED, B70): прибрати з <c>ConsistencyCheckJob.CheckAsync</c> виклик <c>ArchiveTamperCheck</c> — червоніють
/// правка значення, видалений рядок і базовий еталон; не записувати еталон у <c>usp_ArchiveYear</c> — червоніє
/// перевірка еталона; не пропускати період після <c>FromArchive</c> — червоніє розархівований період.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveTamperDetectionTests(SqlServerFixture sql)
{
    private const int Period = 202706;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Plan", "B70")]
    public async Task Незмінений_архів_знахідки_не_дає_а_еталон_записала_процедура()
    {
        var doc = await ArrangeArchivedAsync();
        try
        {
            // ⚠ Еталон пише ПРОЦЕДУРА в момент архівації (origin = archive), в обидва поля однаково.
            var (source, target) = await ReferenceAsync(withReferenceOnly: false);
            Assert.NotNull(target);
            Assert.Equal(source, target);
            Assert.StartsWith("{\"v\":1,\"origin\":\"archive\"", target, StringComparison.Ordinal);
            Assert.Contains($"\"p\":{Period}", target, StringComparison.Ordinal);

            await RunJobAsync();

            Assert.Null(await TamperFindingAsync());
        }
        finally
        {
            await RestoreAsync(doc.ProjectId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Plan", "B70")]
    public async Task Пряма_правка_значення_в_arc_CellValue_дає_знахідку()
    {
        var doc = await ArrangeArchivedAsync();
        try
        {
            // ⛔ Правка в обхід застосунку — те, від чого «ніщо не захищає». Кількість рядків та сама: ловить саме зміст.
            await ExecuteAsync($"""
                UPDATE arc.CellValue SET ValueNumeric = ValueNumeric + 1
                 WHERE PeriodKey = {Period} AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[1]};
                """);

            await RunJobAsync();

            var finding = await TamperFindingAsync();
            Assert.NotNull(finding);
            Assert.Equal(3, finding.Value.Severity);
            Assert.Equal(Period, finding.Value.PeriodKey);

            Assert.True(JobProgressMessageCodec.TryDecode(finding.Value.Message, out var envelope), finding.Value.Message);
            Assert.Equal(ConsistencyCheckJob.FindingKeyPrefix + ArchiveTamperCheck.FindingKey, envelope.Key);
            Assert.Equal(Period.ToString(CultureInfo.InvariantCulture), envelope.Params!["period"]);
            Assert.Equal(envelope.Params["cellsThen"], envelope.Params["cellsNow"]);
        }
        finally
        {
            await RestoreAsync(doc.ProjectId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Plan", "B70")]
    public async Task Видалений_рядок_архіву_дає_знахідку()
    {
        var doc = await ArrangeArchivedAsync();
        try
        {
            await ExecuteAsync($"""
                DELETE FROM arc.CellValue
                 WHERE PeriodKey = {Period} AND TableRowId = {doc.RowIds[1]} AND ColumnDefId = {doc.ColumnDefIds[1]};
                """);

            await RunJobAsync();

            var finding = await TamperFindingAsync();
            Assert.NotNull(finding);
            Assert.True(JobProgressMessageCodec.TryDecode(finding.Value.Message, out var envelope), finding.Value.Message);
            Assert.Equal(
                long.Parse(envelope.Params!["cellsThen"], CultureInfo.InvariantCulture) - 1,
                long.Parse(envelope.Params["cellsNow"], CultureInfo.InvariantCulture));
        }
        finally
        {
            await RestoreAsync(doc.ProjectId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Plan", "B70")]
    public async Task Архів_без_еталона_отримує_базовий_і_наступна_правка_виявляється()
    {
        // ⚠ Шлях оновлення: рік, заархівований ДО B70, еталона не має. Перший прохід записує базовий (origin =
        // baseline) і знахідки не дає; наступна правка — вже знахідка.
        var doc = await ArrangeArchivedAsync();
        try
        {
            await ExecuteAsync($"""
                UPDATE itg.ArchiveRun SET ChecksumSourceJson = NULL, ChecksumTargetJson = NULL
                 WHERE Direction = N'ToArchive' AND FromPeriodKey <= {Period} AND ToPeriodKey >= {Period};
                """);

            await RunJobAsync();

            Assert.Null(await TamperFindingAsync());
            var (source, target) = await ReferenceAsync(withReferenceOnly: true);
            Assert.NotNull(target);
            Assert.Equal(source, target);
            Assert.StartsWith("{\"v\":1,\"origin\":\"baseline\"", target, StringComparison.Ordinal);
            Assert.Contains($"\"p\":{Period}", target, StringComparison.Ordinal);

            await ExecuteAsync($"""
                UPDATE arc.CellValue SET ValueNumeric = ValueNumeric * 2 + 1
                 WHERE PeriodKey = {Period} AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[1]};
                """);

            await RunJobAsync();

            Assert.NotNull(await TamperFindingAsync());
        }
        finally
        {
            await RestoreAsync(doc.ProjectId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Plan", "B70")]
    public async Task Розархівований_період_не_звіряється_зі_старим_еталоном()
    {
        // ⚠ Після `FromArchive` архів періоду порожній законно; звірка зі старим еталоном дала б хибну знахідку
        // найвищої ваги.
        var doc = await ArrangeArchivedAsync();
        await RestoreAsync(doc.ProjectId);

        await RunJobAsync();

        Assert.Null(await TamperFindingAsync());
    }

    /// <summary>Документ із двома комірками в <see cref="Period"/>, заархівований процедурою.</summary>
    private async Task<TestDocument> ArrangeArchivedAsync()
    {
        await ExecuteAsync($"""
            DELETE FROM aud.ConsistencyIssue
             WHERE RuleCode = N'ARCHIVE_CHECKSUM' AND EntityType = N'{ArchiveTamperCheck.EntityType}' AND EntityId = {Period};
            """);

        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: Period, rowCount: 2, ct: CancellationToken.None);

        await CellAsync(doc, doc.RowIds[0], 12.5m);
        await CellAsync(doc, doc.RowIds[1], 7.25m);

        // Архівувати можна лише заархівовані проєкти періоду (ЗАПОБІЖНИК 1 процедури) — той самий перехід, що в системі.
        await ExecuteAsync($"""
            UPDATE doc.Project SET Status = 4, ClosedAt = '2020-01-01'
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period WHERE PeriodKey = {Period});
            """);

        await ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {doc.ProjectId}, @FromPeriodKey = {Period}, @ToPeriodKey = {Period};");

        return doc;
    }

    private Task CellAsync(TestDocument doc, long rowId, decimal value)
        => ExecuteAsync($"""
            INSERT INTO doc.CellValue
                (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({Period}, {rowId}, {doc.ColumnDefIds[1]}, {doc.TableDefId},
                    {value.ToString(CultureInfo.InvariantCulture)}, 0, 0)
            """);

    private Task RestoreAsync(int projectId)
        => ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {projectId}, @FromPeriodKey = {Period}, @ToPeriodKey = {Period};");

    private async Task RunJobAsync()
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);

        var job = new ConsistencyCheckJob(
            db, Substitute.For<IOrphanScanner>(), new TestClock(new DateTime(2026, 2, 1, 3, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IConsistencyMetrics>());

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    /// <summary>Еталон найновішого завершеного прогону ToArchive періоду (процедура пише його в усі прогони виклику).</summary>
    /// <param name="withReferenceOnly">Лише серед прогонів, що мають еталон (базовий пишеться в один прогін).</param>
    private async Task<(string? Source, string? Target)> ReferenceAsync(bool withReferenceOnly)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) ChecksumSourceJson, ChecksumTargetJson FROM itg.ArchiveRun
             WHERE Direction = N'ToArchive' AND Status = N'Completed'
               AND FromPeriodKey <= @period AND ToPeriodKey >= @period
               AND (@withReferenceOnly = 0 OR ChecksumTargetJson IS NOT NULL)
             ORDER BY StartedAt DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("@period", Period);
        command.Parameters.AddWithValue("@withReferenceOnly", withReferenceOnly);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task<(byte Severity, string Message, int? PeriodKey)?> TamperFindingAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) Severity, Message, PeriodKey FROM aud.ConsistencyIssue
             WHERE RuleCode = N'ARCHIVE_CHECKSUM' AND EntityType = @type AND EntityId = @period;
            """;
        command.Parameters.AddWithValue("@type", ArchiveTamperCheck.EntityType);
        command.Parameters.AddWithValue("@period", (long)Period);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            return null;
        }

        return (reader.GetByte(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2));
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
