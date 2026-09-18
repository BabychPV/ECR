using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Q-184: реальні знахідки <c>ConsistencyCheckJob</c> на живих даних, а не
/// текст файлу.
/// </summary>
/// <remarks>
/// ⛔ Old <c>ConsistencyCheckJobTests</c> робив лише <c>File.ReadAllText</c> +
/// <c>Assert.Contains</c> — доведено мутацією, що заміна анти-джойну
/// виявлення на завжди-хибний лишала перевірений підрядок незмінним. Тут
/// кожен тест реально запускає <see cref="ConsistencyCheckJob.ExecuteAsync"/>
/// проти справжніх рядків і звіряє записану знахідку в
/// <c>aud.ConsistencyIssue</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class ConsistencyCheckJobDetectionTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "T10-41")]
    public async Task Знахідка_видима_в_метриках()
    {
        // ⛔ D-134 (директива №11, T10 #41): EcrMetrics.RecordConsistencyIssues
        // існував і не мав жодного викликача — ConsistencyCheckJob писав
        // знахідки лише в aud.ConsistencyIssue, журнал, який ніхто не читає
        // проактивно. Прибери виклик metrics.RecordIssues у
        // ConsistencyCheckJob.ExecuteAsync — цей тест почервоніє.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 1, ct: CancellationToken.None);

        await InsertOrphanCellAsync(doc, -900_103);

        var metrics = Substitute.For<IConsistencyMetrics>();
        await RunJobAsync(metrics);

        metrics.Received(1).RecordIssues(Arg.Is<int>(count => count > 0), "ORPHANED_CELL");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.7")]
    public async Task Виявляє_осиротілу_комірку()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 1, ct: CancellationToken.None);

        await InsertOrphanCellAsync(doc, -900_101);

        await RunJobAsync();

        var issue = await FindIssueAsync("ORPHANED_CELL", "doc.CellValue", doc.RowIds[0]);
        Assert.NotNull(issue);
        Assert.Equal(2, issue.Value.Severity);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.7")]
    public async Task Виявляє_порушений_FK_у_гібридному_режимі()
    {
        // ⚠ У нормалізованій моделі FK_TableRow_Instance якраз ЗАБОРОНЯЄ цей
        // стан — навмисно, і саме тому знахідок тут за звичайних умов не
        // буває (див. коментар ConsistencyCheckJob.BrokenReferencesAsync).
        // Щоб довести, що перевірка справді щось шукає, а не завжди мовчить
        // на порожній множині, тимчасово вимикаємо обмеження — так само, як
        // його реально міг обійти запис міграцією до появи FK.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 1, ct: CancellationToken.None);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var orphanRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        const long MissingTableInstanceId = -900_202;

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync(CancellationToken.None);

            await ExecuteAsync(connection,
                "ALTER TABLE doc.TableRow NOCHECK CONSTRAINT FK_TableRow_Instance;");

            try
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, Ordinal, ModifiedAt)
                    VALUES (@p, @id, @instance, @key, 1, SYSUTCDATETIME());
                    """;
                insert.Parameters.AddWithValue("@p", doc.PeriodKey.Value);
                insert.Parameters.AddWithValue("@id", orphanRowId);
                insert.Parameters.AddWithValue("@instance", MissingTableInstanceId);
                insert.Parameters.AddWithValue("@key", "ORPHAN-ROW");
                await insert.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally
            {
                // ⚠ `WITH NOCHECK` при повторному вмиканні — обов'язково: звичайне
                // `CHECK CONSTRAINT` перевалідувало б ВСЮ таблицю і впало б на
                // щойно вставленому навмисно зламаному рядку.
                await ExecuteAsync(connection,
                    "ALTER TABLE doc.TableRow WITH NOCHECK CHECK CONSTRAINT FK_TableRow_Instance;");
            }
        }

        try
        {
            await RunJobAsync();

            var issue = await FindIssueAsync("BROKEN_FK", "doc.TableRow", orphanRowId);
            Assert.NotNull(issue);
            Assert.Equal(3, issue.Value.Severity);
        }
        finally
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await ExecuteAsync(connection,
                $"DELETE FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {orphanRowId};");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.14")]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Звіряє_архів_із_джерелом_за_контрольними_сумами()
    {
        long runId;
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var run = new ArchiveRun(
                projectId: 1, ArchiveRun.ToArchive, fromPeriodKey: 202601, toPeriodKey: 202601,
                triggeredByUserId: null, utcNow: new DateTime(2026, 2, 1, 2, 0, 0, DateTimeKind.Utc));
            run.RecordChecksums(sourceJson: """{"count":10}""", targetJson: """{"count":9}""");
            run.Complete("Succeeded", new DateTime(2026, 2, 1, 2, 5, 0, DateTimeKind.Utc), errorMessage: null);

            db.ArchiveRuns.Add(run);
            await db.SaveChangesAsync(CancellationToken.None);
            runId = run.Id;
        }

        await RunJobAsync();

        var issue = await FindIssueAsync("ARCHIVE_CHECKSUM", "itg.ArchiveRun", runId);
        Assert.NotNull(issue);
        Assert.Equal(3, issue.Value.Severity);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Виявляє_колонку_Calculated_без_жодної_прив_язки()
    {
        // ⛔ Діра, яку лишив по собі `#284`. `#276` вимагав джерело від БУДЬ-ЯКОЇ
        // обчислюваної колонки на ПУБЛІКАЦІЇ СТРУКТУРИ; `#284` звузив вимогу до
        // `Formula`, бо джерело `Calculated`-колонки живе поза версією шаблону і
        // заводить його методолог уже ПІСЛЯ публікації. Відтоді
        // `Calculated`-колонку, яка так і не отримала прив'язки, не перевіряло
        // НІЩО: оператор бачить порожню клітинку, яку не має права заповнити.
        //
        // ⚠ Мутаційний доказ: заміни в `ConsistencyCheckJob` анти-джойн
        // `!db.CalculationBindings.Any(...)` на `db.CalculationBindings.Any(...)`
        // — або прибери виклик `UnboundCalculatedColumnsAsync` із `RunAsync` —
        // і цей тест почервоніє на `Assert.NotNull`.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 1, ct: CancellationToken.None);

        var (columnId, tableCode, columnCode) = await AddCalculatedColumnAsync(builder, doc);

        await RunJobAsync();

        var issue = await FindIssueAsync("UNBOUND_CALCULATED_COLUMN", "cfg.ColumnDef", columnId);

        Assert.NotNull(issue);
        Assert.Equal(2, issue.Value.Severity);

        // ⚠ Названо КОНКРЕТНУ колонку у формі `{tableCode}.{columnCode}`:
        // «у версії є проблеми» змусило б конфігуратора шукати винуватця серед
        // сотень колонок руками — саме те, чого уникає `ECR-TMPL-4227`.
        Assert.Contains($"{tableCode}.{columnCode}", issue.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонка_Calculated_із_чинною_прив_язкою_знахідки_не_дає()
    {
        // ⚠ Друга половина, без якої перша нічого не означає: перевірка не
        // доповідає про ТИП колонки, вона доповідає про відсутність ДЖЕРЕЛА.
        // Без цього тесту та сама зелень вийшла б і з перевірки «будь-яка
        // Calculated-колонка — знахідка», тобто з перевірки, що доповідає про
        // кожну правильно налаштовану колонку в системі.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 1, ct: CancellationToken.None);

        var (columnId, _, _) = await AddCalculatedColumnAsync(builder, doc);

        await using (var db = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create($"M{columnId}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Bound methodology" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync(CancellationToken.None);

            db.CalculationBindings.Add(new CalculationBinding(
                doc.TableDefId, columnId, methodology.Id, "OUT", "{}"));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await RunJobAsync();

        Assert.Null(await FindIssueAsync("UNBOUND_CALCULATED_COLUMN", "cfg.ColumnDef", columnId));
    }

    /// <summary>
    /// Додає в таблицю документа колонку типу <c>Calculated</c> і повертає її
    /// ідентифікатор разом із кодами таблиці й колонки.
    /// </summary>
    /// <remarks>
    /// ⚠ Будівник заводить лише <c>String</c>/<c>Decimal</c>: тип
    /// <c>Calculated</c> потрібен саме цій перевірці, і додавати його в
    /// будівник означало б змінити структуру, на яку спираються всі інші
    /// тести колекції.
    /// </remarks>
    private static async Task<(int ColumnId, string TableCode, string ColumnCode)> AddCalculatedColumnAsync(
        TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();

        var tableCode = await db.TableDefs
            .Where(t => t.Id == doc.TableDefId)
            .Select(t => t.Code)
            .SingleAsync(CancellationToken.None);

        var columnCode = $"OUT{doc.TableDefId}";

        var column = new ColumnDef(
            doc.TableDefId,
            EcrCode.Create(columnCode),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Methodology output" }),
            99,
            CellDataType.Calculated);

        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync(CancellationToken.None);

        return (column.Id, tableCode, columnCode);
    }

    private Task RunJobAsync() => RunJobAsync(Substitute.For<IConsistencyMetrics>());

    private async Task RunJobAsync(IConsistencyMetrics metrics)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);

        var job = new ConsistencyCheckJob(
            db, Substitute.For<IOrphanScanner>(),
            new TestClock(new DateTime(2026, 2, 1, 3, 0, 0, DateTimeKind.Utc)),
            metrics);

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    private async Task<(byte Severity, string Message)?> FindIssueAsync(string ruleCode, string entityType, long entityId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) Severity, Message FROM aud.ConsistencyIssue
            WHERE RuleCode = @rule AND EntityType = @type AND EntityId = @id;
            """;
        command.Parameters.AddWithValue("@rule", ruleCode);
        command.Parameters.AddWithValue("@type", entityType);
        command.Parameters.AddWithValue("@id", entityId);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            return null;
        }

        return ((byte)reader.GetByte(0), reader.GetString(1));
    }

    /// <summary>
    /// Вставляє комірку з посиланням на запис довідника, якого не існує.
    /// </summary>
    /// <remarks>
    /// ⛔ Директива registry-lookup, PR A1: `FK_CellValue_Entry` тепер
    /// забороняє САМЕ це посилання звичайним записом — і це правильно.
    /// `NOCHECK`/`WITH NOCHECK CHECK` тут — той самий прийом, що вже
    /// стоїть нижче для `FK_TableRow_Instance`: чесно відтворює дані, які
    /// потрапили в базу ПОЗА звичайним шляхом (до міграції, ручне
    /// втручання DBA), а не обхід перевірки, яку мав пройти звичайний
    /// запис.
    /// </remarks>
    private async Task InsertOrphanCellAsync(TestDocument doc, long registryEntryId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await ExecuteAsync(connection, "ALTER TABLE doc.CellValue NOCHECK CONSTRAINT FK_CellValue_Entry;");
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT doc.CellValue
                    (PeriodKey, TableRowId, TableDefId, ColumnDefId, ValueRegistryEntryId, IsCalculated, IsEmpty)
                VALUES (@p, @r, @t, @c, @reg, 0, 0);
                """;
            command.Parameters.AddWithValue("@p", doc.PeriodKey.Value);
            command.Parameters.AddWithValue("@r", doc.RowIds[0]);
            command.Parameters.AddWithValue("@t", doc.TableDefId);
            command.Parameters.AddWithValue("@c", doc.ColumnDefIds[0]);
            command.Parameters.AddWithValue("@reg", registryEntryId);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        finally
        {
            // ⚠ `WITH NOCHECK` при повторному вмиканні — обов'язково: звичайне
            // `CHECK CONSTRAINT` перевалідувало б ВСЮ таблицю і впало б на
            // щойно вставленому навмисно зламаному рядку.
            await ExecuteAsync(connection, "ALTER TABLE doc.CellValue WITH NOCHECK CHECK CONSTRAINT FK_CellValue_Entry;");
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
