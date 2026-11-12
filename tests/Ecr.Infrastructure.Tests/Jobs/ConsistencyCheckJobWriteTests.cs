using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Запис знахідок нічної перевірки узгодженості на реальному SQL Server
/// (<c>Q-169</c>).
/// </summary>
/// <remarks>
/// ⚠ Окремий файл від `ConsistencyCheckJobTests`: той клас перевіряє ТЕКСТ
/// джерела (виявлення правил), а не поведінку запису — саме тому N+1 у
/// `WriteIssuesAsync` не був помічений жодним існуючим тестом.
/// </remarks>
[Collection("SqlServer")]
public sealed class ConsistencyCheckJobWriteTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.7")]
    [Trait("Requirement", "ФВ-13.16")]
    public async Task Кілька_знахідок_записуються_одним_запитом()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 2, ct: CancellationToken.None);

        // Дві осиротілі комірки: посилання на записи довідника, яких немає.
        await InsertOrphanCellAsync(doc, doc.RowIds[0], registryEntryId: -900_001);
        await InsertOrphanCellAsync(doc, doc.RowIds[1], registryEntryId: -900_002);

        var executed = new List<string>();
        await using var counting = CreateCountingContext(executed);
        var job = new ConsistencyCheckJob(
            counting, Substitute.For<IOrphanScanner>(), new TestClock(new DateTime(2026, 2, 1, 3, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IConsistencyMetrics>());

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        // ⛔ Q-169: дві знахідки — а команд ЗАПИСУ знахідок рівно ОДНА (пакетний MERGE), не дві
        // (`IF NOT EXISTS...INSERT` на кожну). ✎ Рішення людини 12.11 (знімок): поруч тепер іде ОДНА команда
        // видалення зниклих знахідок (`DELETE … OPENJSON`) на весь прохід — вона теж не на знахідку, тому окремо.
        var writes = executed.Count(cmd => cmd.Contains("MERGE aud.ConsistencyIssue", StringComparison.Ordinal));
        Assert.Equal(1, writes);
        var deletes = executed.Count(cmd => cmd.Contains("DELETE target", StringComparison.Ordinal));
        Assert.Equal(1, deletes);

        // ⚠ Рахуємо ЛИШЕ рядки, які вставив цей тест: `aud.ConsistencyIssue`
        // спільний для всієї колекції SqlServer, і глобальний COUNT за
        // RuleCode збігався б із будь-якою іншою знахідкою ORPHANED_CELL з
        // паралельного тесту (`ConsistencyCheckJobDetectionTests`, Q-184).
        var count = await CountIssuesAsync("ORPHANED_CELL", doc.RowIds, CancellationToken.None);
        Assert.Equal(2, count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.7")]
    public async Task Знахідка_зникає_після_того_як_умову_усунуто()
    {
        // ⛔ Рішення людини 12.11: «система має реально показувати проблеми, а коли вони зникають — видаляти
        // повідомлення; узгодженість — перевірка на момент часу». Аудит live 12.11: 260 знахідок, жодна не правдива
        // тижнями — MERGE лише вставляв. RED без кроку видалення (`DeleteVanishedAsync`): знахідка лишалася.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 1, ct: CancellationToken.None);
        var rowId = doc.RowIds[0];

        await InsertOrphanCellAsync(doc, rowId, registryEntryId: -900_011);
        await RunJobAsync(FirstRun);

        Assert.Equal(1, await CountIssuesAsync("ORPHANED_CELL", [rowId], CancellationToken.None));

        // Умову усунуто: осиротілої комірки більше немає.
        await ExecuteAsync(
            $"DELETE FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} AND TableRowId = {rowId} "
            + $"AND ColumnDefId = {doc.ColumnDefIds[0]};");

        await RunJobAsync(FirstRun.AddDays(1));

        Assert.Equal(0, await CountIssuesAsync("ORPHANED_CELL", [rowId], CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пропущене_правило_і_рядки_процедури_архівації_лишаються()
    {
        // ⛔ Пропущене правило (`NON_CURRENT_RUN_ROWS` без увімкненого прибирання) нічого не доводить про свої
        // знахідки; `ARCHIVE_CHECKSUM` з `EntityType = 'Period'` пише процедура архівації, а не задача. Обидва рядки
        // мають лишитися. Мутації: видаляти за всіма кодами правил, або область лише за RuleCode без EntityType —
        // тест червоніє.
        var skipped = await InsertIssueAsync("NON_CURRENT_RUN_ROWS", "calc.CalculationRun", -770_001, "skipped");
        var foreign = await InsertIssueAsync("ARCHIVE_CHECKSUM", "Period", -770_002, "archive-proc");

        await RunJobAsync(FirstRun.AddDays(2));

        Assert.True(await ExistsAsync(skipped));
        Assert.True(await ExistsAsync(foreign));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Знахідка_записана_після_старту_проходу_не_видаляється()
    {
        // ⛔ Межа гонитви: знахідку, виявлену ПІСЛЯ старту проходу, міг записати паралельний прохід (нічний і ручний),
        // що бачив новіші дані. Мутація: прибрати `DetectedAt < @runStartedAt` — тест червоніє.
        var run = FirstRun.AddDays(3);
        var newer = await InsertIssueAsync("ORPHANED_CELL", "doc.CellValue", -770_003, "newer", run.AddHours(1));

        await RunJobAsync(run);

        Assert.True(await ExistsAsync(newer));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правдива_знахідка_лишається_одна_з_моментом_першого_виявлення_і_теперішнім_текстом()
    {
        // ⛔ Знімок: правдива знахідка не дублюється від проходу до проходу й зберігає момент ПЕРШОГО виявлення;
        // старий український текст замінюється конвертом каталогу. RED до фіксу: MERGE мав лише `WHEN NOT MATCHED`,
        // і текст не мінявся.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 1, ct: CancellationToken.None);
        var rowId = doc.RowIds[0];

        var legacy = await InsertIssueAsync("ORPHANED_CELL", "doc.CellValue", rowId, "Комірка рядка — старий текст");
        await InsertOrphanCellAsync(doc, rowId, registryEntryId: -900_012);

        await RunJobAsync(FirstRun.AddDays(4));
        await RunJobAsync(FirstRun.AddDays(5));

        Assert.Equal(1, await CountIssuesAsync("ORPHANED_CELL", [rowId], CancellationToken.None));
        var issue = await IssueByIdAsync(legacy);
        Assert.StartsWith("{\"k\":\"consistency.finding.ORPHANED_CELL\"", issue.Message, StringComparison.Ordinal);
        Assert.Equal(LegacyDetectedAt, issue.DetectedAt);
    }

    /// <summary>Момент виявлення вставлених напряму знахідок.</summary>
    private static readonly DateTime LegacyDetectedAt = new(2026, 1, 5, 2, 15, 0, DateTimeKind.Utc);

    /// <summary>Годинник першого проходу; наступні — пізніше (видаляються лише знахідки, старші за старт проходу).</summary>
    private static readonly DateTime FirstRun = new(2026, 2, 1, 3, 0, 0, DateTimeKind.Utc);

    private async Task RunJobAsync(DateTime at)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);

        var job = new ConsistencyCheckJob(
            db, Substitute.For<IOrphanScanner>(), new TestClock(at), Substitute.For<IConsistencyMetrics>());

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    private async Task<bool> ExistsAsync(long id)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM aud.ConsistencyIssue WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))! == 1;
    }
    private async Task<long> InsertIssueAsync(
        string rule, string entityType, long entityId, string message, DateTime? detectedAt = null)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT aud.ConsistencyIssue (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)
            VALUES (@at, 2, @rule, @type, @id, @message);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """;
        command.Parameters.AddWithValue("@at", detectedAt ?? LegacyDetectedAt);
        command.Parameters.AddWithValue("@rule", rule);
        command.Parameters.AddWithValue("@type", entityType);
        command.Parameters.AddWithValue("@id", entityId);
        command.Parameters.AddWithValue("@message", message);
        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private async Task<(long Id, DateTime DetectedAt, string Message, DateTime? ResolvedAt, int? ResolvedByUserId)>
        IssueByIdAsync(long id)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, DetectedAt, Message, ResolvedAt, ResolvedByUserId FROM aud.ConsistencyIssue WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return (
            reader.GetInt64(0),
            DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4));
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <remarks>
    /// ⛔ Директива registry-lookup, PR A1: `FK_CellValue_Entry` тепер
    /// забороняє САМЕ це посилання звичайним записом — і це правильно.
    /// `NOCHECK`/`WITH NOCHECK CHECK` тут чесно відтворює дані, які
    /// потрапили в базу ПОЗА звичайним шляхом (до міграції, ручне
    /// втручання DBA), а не обхід перевірки, яку мав пройти звичайний
    /// запис — `try`/`finally` гарантує повернення обмеження навіть при
    /// падінні самої вставки.
    /// </remarks>
    private async Task InsertOrphanCellAsync(TestDocument doc, long rowId, long registryEntryId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using (var disable = connection.CreateCommand())
        {
            disable.CommandText = "ALTER TABLE doc.CellValue NOCHECK CONSTRAINT FK_CellValue_Entry;";
            await disable.ExecuteNonQueryAsync(CancellationToken.None);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT doc.CellValue
                    (PeriodKey, TableRowId, TableDefId, ColumnDefId, ValueRegistryEntryId, IsCalculated, IsEmpty)
                VALUES (@p, @r, @t, @c, @reg, 0, 0);
                """;
            command.Parameters.AddWithValue("@p", doc.PeriodKey.Value);
            command.Parameters.AddWithValue("@r", rowId);
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
            await using var enable = connection.CreateCommand();
            enable.CommandText = "ALTER TABLE doc.CellValue WITH NOCHECK CHECK CONSTRAINT FK_CellValue_Entry;";
            await enable.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task<int> CountIssuesAsync(string ruleCode, IReadOnlyList<long> entityIds, CancellationToken ct)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM aud.ConsistencyIssue WHERE RuleCode = @rule AND EntityId IN ("
            + string.Join(',', entityIds) + ")";
        command.Parameters.AddWithValue("@rule", ruleCode);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Контекст, який складає кожну виконану команду в список.</summary>
    private EcrDbContext CreateCountingContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);
}
