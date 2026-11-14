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
/// <para>
/// Мутації (RED, B87): прибрати види 4–6 з <c>arc.ArchiveRowHash</c> — червоніють три правки (частини, стиль,
/// перевизначення) і доповнення еталона; звіряти відсутні в еталоні види як нуль (без пропуску в
/// <c>PeriodPrint.SameKind</c>) — червоніє еталон до B87 (хибна знахідка); не доповнювати збіглий старий еталон — він
/// же; доповнювати й при розбіжності — червоніє «еталон до B87 зі зміненою коміркою».
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

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Plan", "B87")]
    [InlineData("arc.CellPermitPart", "UPDATE arc.CellPermitPart SET ValueNumeric = ValueNumeric + 1 WHERE PeriodKey = {0} AND DocumentId = {1} AND PartNo = 2;")]
    [InlineData("arc.CellStyle", "UPDATE arc.CellStyle SET IsBold = 1 - IsBold WHERE PeriodKey = {0} AND TableRowId = {2};")]
    [InlineData("arc.CellFormulaOverride", "UPDATE arc.CellFormulaOverride SET Reason = Reason + N'!' WHERE PeriodKey = {0} AND TableRowId = {3};")]
    public async Task Пряма_правка_частин_стилів_і_перевизначень_архіву_дає_знахідку(string table, string tamper)
    {
        // ⛔ B87 (борг B70): ці три таблиці переїжджають в архів разом із періодом і так само не мають змінюватися.
        //    Кількість рядків та сама — ловить саме зміст. Комірки, рядки й екземпляри не чіпаються.
        var doc = await ArrangeArchivedAsync(withExtras: true);
        try
        {
            Assert.Equal(1, await ExecuteCountAsync(string.Format(CultureInfo.InvariantCulture, tamper, Period, doc.DocumentId, doc.RowIds[0], doc.RowIds[1])));

            await RunJobAsync();

            var finding = await TamperFindingAsync();
            Assert.True(finding is not null, $"правка {table} не дала знахідки");
            Assert.Equal(3, finding.Value.Severity);
        }
        finally
        {
            await RestoreAsync(doc.ProjectId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Plan", "B87")]
    public async Task Еталон_до_B87_не_дає_хибної_знахідки_доповнюється_і_далі_ловить_правку()
    {
        // ⛔ Шлях оновлення: на живих базах лежать еталони, записані процедурою ДО B87 — без частин, стилів і
        //    перевизначень. Їх відтворюємо, прибравши поля B87 з еталона процедури (вирази видів 1–3 в'юхи не змінились,
        //    тож решта еталона рівно така, яку писала стара процедура). Період має всі три види — звірка «відсутнє = 0»
        //    дала б тут хибну знахідку найвищої ваги.
        var doc = await ArrangeArchivedAsync(withExtras: true);
        try
        {
            var (_, written) = await ReferenceAsync(withReferenceOnly: false);
            Assert.Contains("\"pph\":", written!, StringComparison.Ordinal);
            var legacy = WithoutB87Fields(written!);
            Assert.DoesNotContain("\"sh\":", legacy, StringComparison.Ordinal);
            await ExecuteAsync($"""
                UPDATE itg.ArchiveRun SET ChecksumSourceJson = N'{legacy}', ChecksumTargetJson = N'{legacy}'
                 WHERE Direction = N'ToArchive' AND FromPeriodKey <= {Period} AND ToPeriodKey >= {Period}
                   AND ChecksumTargetJson = N'{written}';
                """);

            await RunJobAsync();

            Assert.Null(await TamperFindingAsync());

            // Збіглий старий еталон доповнено новими видами; походження — те саме (еталон процедури).
            var (source, extended) = await ReferenceAsync(withReferenceOnly: false);
            Assert.Equal(source, extended);
            Assert.StartsWith("{\"v\":1,\"origin\":\"archive\"", extended, StringComparison.Ordinal);
            Assert.Contains("\"pph\":", extended!, StringComparison.Ordinal);
            Assert.Contains("\"sh\":", extended!, StringComparison.Ordinal);
            Assert.Contains("\"oh\":", extended!, StringComparison.Ordinal);

            // Повторний прохід без правок — тиша; правка стилю після доповнення — знахідка.
            await RunJobAsync();
            Assert.Null(await TamperFindingAsync());

            await ExecuteAsync($"UPDATE arc.CellStyle SET UpdatedByUserId = UpdatedByUserId + 1 WHERE PeriodKey = {Period} AND TableRowId = {doc.RowIds[0]};");

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
    [Trait("Plan", "B87")]
    public async Task Еталон_до_B87_зі_зміненою_коміркою_дає_знахідку_і_не_доповнюється()
    {
        // ⚠ Доповнення — лише коли старі види збіглися: інакше прохід «узаконив» би змінений архів.
        var doc = await ArrangeArchivedAsync(withExtras: true);
        try
        {
            var (_, written) = await ReferenceAsync(withReferenceOnly: false);
            var legacy = WithoutB87Fields(written!);
            await ExecuteAsync($"""
                UPDATE itg.ArchiveRun SET ChecksumSourceJson = N'{legacy}', ChecksumTargetJson = N'{legacy}'
                 WHERE Direction = N'ToArchive' AND FromPeriodKey <= {Period} AND ToPeriodKey >= {Period}
                   AND ChecksumTargetJson = N'{written}';
                UPDATE arc.CellValue SET ValueNumeric = ValueNumeric + 1
                 WHERE PeriodKey = {Period} AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[1]};
                """);

            await RunJobAsync();

            Assert.NotNull(await TamperFindingAsync());
            var (_, after) = await ReferenceAsync(withReferenceOnly: false);
            Assert.Equal(legacy, after);
        }
        finally
        {
            await RestoreAsync(doc.ProjectId);
        }
    }

    /// <summary>Еталон у форматі до B87: без полів частин місяця, стилів і перевизначень.</summary>
    private static string WithoutB87Fields(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var period in node["periods"]!.AsArray())
        {
            var entry = period!.AsObject();
            foreach (var name in new[] { "pp", "pph", "s", "sh", "o", "oh" })
            {
                Assert.True(entry.Remove(name), $"еталон процедури без поля {name}");
            }
        }

        return node.ToJsonString();
    }

    /// <summary>Документ із двома комірками в <see cref="Period"/>, заархівований процедурою.</summary>
    /// <param name="withExtras">Ще й частини місяця, стиль і ручне перевизначення формули (B87).</param>
    private async Task<TestDocument> ArrangeArchivedAsync(bool withExtras = false)
    {
        await ExecuteAsync($"""
            DELETE FROM aud.ConsistencyIssue
             WHERE RuleCode = N'ARCHIVE_CHECKSUM' AND EntityType = N'{ArchiveTamperCheck.EntityType}' AND EntityId = {Period};
            """);

        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: Period, rowCount: 2, ct: CancellationToken.None);

        await CellAsync(doc, doc.RowIds[0], 12.5m);
        await CellAsync(doc, doc.RowIds[1], 7.25m);

        if (withExtras)
        {
            await ExtrasAsync(doc);
        }

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

    /// <summary>Дві частини місяця між дозволами, стиль комірки і ручне перевизначення формули в <see cref="Period"/>.</summary>
    private async Task ExtrasAsync(TestDocument doc)
    {
        long entryId;
        await using (var db = sql.CreateContext())
        {
            var text = new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Permit" });
            var registry = new Ecr.Domain.Entities.Configuration.RegistryDef(
                Ecr.Domain.ValueObjects.EcrCode.Create($"PRM_TMP{Guid.NewGuid():N}"[..20].ToUpperInvariant()), text, isTemporal: true);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync();
            var entry = new Ecr.Domain.Entities.Dictionaries.RegistryEntry(registry.Id, Ecr.Domain.ValueObjects.EcrCode.Create("A"), text);
            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            entryId = entry.Id;
        }

        await ExecuteAsync($"""
            INSERT INTO doc.CellPermitPart
                (PeriodKey, DocumentId, TableDefId, SheetCopyId, RowKey, ColumnDefId, PartNo, RegistryEntryId, DayFrom, DayTo, ValueNumeric)
            VALUES ({Period}, {doc.DocumentId}, {doc.TableDefId}, NULL, N'R1', {doc.ColumnDefIds[1]}, 1, {entryId}, '2027-06-01', '2027-06-14', 5.5),
                   ({Period}, {doc.DocumentId}, {doc.TableDefId}, NULL, N'R1', {doc.ColumnDefIds[1]}, 2, {entryId}, '2027-06-15', '2027-06-30', 7);
            INSERT INTO doc.CellStyle
                (PeriodKey, TableRowId, ColumnDefId, BackgroundHex, ForegroundHex, IsBold, UpdatedAt, UpdatedByUserId)
            VALUES ({Period}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, '#FFEE00', NULL, 1, '2027-06-03T10:11:12.345', 1);
            INSERT INTO doc.CellFormulaOverride
                (PeriodKey, TableRowId, ColumnDefId, TableInstanceId, Reason, SetAt, SetByUserId)
            VALUES ({Period}, {doc.RowIds[1]}, {doc.ColumnDefIds[1]}, {doc.TableInstanceId}, N'Ручне значення з акту',
                    '2027-06-05T06:07:08.009', 3);
            """);
    }

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

    private async Task ExecuteAsync(string text) => await ExecuteCountAsync(text);

    private async Task<int> ExecuteCountAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
