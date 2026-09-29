// tests/Ecr.Infrastructure.Tests/Jobs/FormulaRecalculationCoalesceTests.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// O1 (I2 ФВ-9.8): інкрементні задачі формул (<see cref="IFormulaRecalculationJob"/>)
/// зливаються за документо-періодом, а масив змінених комірок — об'єднується.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який доводять тести. Кожен PATCH ставив окрему задачу без цілі: у
/// замірі I2 під 42 RPS у лейні <c>default</c> накопичилось 2 087 задач на один
/// документ. Злиття «як у решти» (нова постановка поглинається, payload — першої)
/// загубило б комірки другої правки — і формули від них не порахувалися б.
/// <para>
/// Мутації: (1) <c>EnqueueSql</c> не зливає масив (<c>@mergePath</c> завжди
/// <c>NULL</c>) — червоні «дві правки» (рядок 2 лишається 20) і «50 правок»;
/// (2) постановка без цілі (<c>EnqueueAsync</c> у <c>PatchCellsHandler</c>) —
/// червоні обидва за кількістю задач; (3) <c>RequeueAsync</c> не зливає масив у
/// задачу позаду — червоний «ретрай».
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class FormulaRecalculationCoalesceTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly string FormulaCode = typeof(IFormulaRecalculationJob).FullName!;

    /// <summary>Обов'язковий тест координатора: дві правки підряд — одна задача, формули від другої.</summary>
    [Fact]
    public async Task Дві_правки_підряд_одна_задача_і_формули_від_другої_правки()
    {
        var doc = await ArrangeAsync(rows: 2);

        // Правка 1: рядок 1. Правка 2 (задача правки 1 ще не виконувалась): рядок 1 і рядок 2.
        var first = await PatchAsync(doc, (0, 30m));
        var second = await PatchAsync(doc, (0, 35m), (1, 40m));

        Assert.Equal(first, second);
        Assert.Equal(1, await QueuedFormulaJobsAsync());

        await RunQueuedFormulaJobAsync(doc);

        // ⛔ Рядок 2 правила лише ДРУГА правка: якщо злиття лишило payload першої,
        // його формула лишилася старою (20).
        Assert.Equal(70m, await OutputAsync(doc, row: 0));
        Assert.Equal(80m, await OutputAsync(doc, row: 1));
    }

    [Fact]
    public async Task П_ятдесят_правок_одного_документо_періоду_одна_задача_результат_від_останнього_входу()
    {
        var doc = await ArrangeAsync(rows: 2);
        var last = new decimal[2];

        for (var i = 0; i < 50; i++)
        {
            var row = i % 2;
            last[row] = 100m + i;
            await PatchAsync(doc, (row, last[row]));

            Assert.True(await QueuedFormulaJobsAsync() <= 1, $"Після правки {i + 1} у черзі більше однієї незапущеної задачі формул.");
        }

        Assert.Equal(1, await QueuedFormulaJobsAsync());

        await RunQueuedFormulaJobAsync(doc);

        Assert.Equal(last[0] * 2, await OutputAsync(doc, row: 0));
        Assert.Equal(last[1] * 2, await OutputAsync(doc, row: 1));
    }

    /// <summary>
    /// Ретрай задачі, позаду якої вже стоїть нова на ту саму ціль: повернута
    /// поглинається, але її комірки переходять у задачу позаду.
    /// </summary>
    [Fact]
    public async Task Ретрай_виконуваної_зливає_її_комірки_в_задачу_позаду()
    {
        var target = $"{nameof(IFormulaRecalculationJob)}~doc1-p202601-formula";

        await using var host = NewHost();
        var running = (await host.Queue.EnqueueAsync(Formula(target, rowId: 1), CancellationToken.None)).JobId;
        var claim = await host.ClaimAsync();
        Assert.Equal(running, claim?.Claim.JobId);

        // Поки виконується — нова правка стає Queued позаду (claim її не бере).
        var behind = (await host.Queue.EnqueueAsync(Formula(target, rowId: 2), CancellationToken.None)).JobId;
        Assert.NotEqual(running, behind);

        Assert.True(await host.Queue.RequeueAsync(claim!.Claim, TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("Cancelled", (await RowAsync(running))?.State);
        var payload = (await RowAsync(behind))?.Payload;
        Assert.Equal([1L, 2L], RowIdsOf(payload));
    }

    // ── Світ ─────────────────────────────────────────────────────────────────

    private static JobEnqueueRequest Formula(string target, long rowId)
        => new(
            FormulaCode, JobLanes.Default,
            $$"""{"documentId":1,"tableInstanceId":1,"periodKey":202601,"cells":[{"rowId":{{rowId}},"columnDefId":7}]}""",
            target);

    private static List<long> RowIdsOf(string? payload)
    {
        using var json = JsonDocument.Parse(payload!);
        return [.. json.RootElement.GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("rowId").GetInt64()).Order()];
    }

    private async Task<TestDocument> ArrangeAsync(int rows)
    {
        var doc = await new TestDocumentBuilder(Sql.ConnectionString)
            .BuildAsync(columnCount: 3, rowCount: rows, rowMode: TableRowMode.Dynamic);

        foreach (var rowId in doc.RowIds)
        {
            await ExecuteAsync(
                "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
                $"({doc.PeriodKey.Value}, {rowId}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 10, 0, 0), " +
                $"({doc.PeriodKey.Value}, {rowId}, {doc.ColumnDefIds[2]}, {doc.TableDefId}, 20, 1, 0);");
        }

        return doc;
    }

    /// <summary>PATCH входу через справжній обробник і чергу в базі; повертає ідентифікатор задачі.</summary>
    private async Task<string?> PatchAsync(TestDocument doc, params (int Row, decimal Value)[] cells)
    {
        await using var db = Sql.CreateContext();
        var jobs = new DbBackgroundJobScheduler(
            new DbJobQueue(db, new SystemClock()),
            new QuartzJobScheduler(null, new JobProgressStore(db), new SystemClock()),
            new JobQueueSignal());

        var rows = new List<PatchRow>();
        foreach (var (row, value) in cells)
        {
            var rowId = doc.RowIds[row];
            var rowKey = await ScalarAsync<string>(
                $"SELECT RowKey FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} AND Id = {rowId}");
            var version = Convert.ToBase64String(await ScalarAsync<byte[]>(
                $"SELECT RowVersion FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} AND Id = {rowId}"));
            rows.Add(new PatchRow(rowKey, version, [new PatchCell("IN", value)]));
        }

        var response = await FormulaRecalculationDocumentLockTests.PatchHandler(db, doc, jobs)
            .HandleAsync(new PatchCellsRequest(doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit", rows), CancellationToken.None);

        return response.RecalculationJobId;
    }

    private async Task<int> QueuedFormulaJobsAsync()
    {
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.CountAsync(p => p.Lane != null && p.JobCode == FormulaCode && p.State == "Queued");
    }

    /// <summary>Бере задачу з черги й виконує її справжньою <see cref="FormulaRecalculationJob"/>.</summary>
    private async Task RunQueuedFormulaJobAsync(TestDocument doc)
    {
        await using var host = NewHost();
        var claimed = await host.ClaimAsync();
        Assert.NotNull(claimed);
        Assert.Equal(FormulaCode, claimed.JobCode);

        await using var db = Sql.CreateContext();
        await FormulaRecalculationDocumentLockTests.IncrementalJob(db, doc)
            .ExecuteAsync(claimed.PayloadJson, NoOpProgress.Instance, CancellationToken.None);

        Assert.True(await host.Queue.CompleteAsync(claimed.Claim, CancellationToken.None));
    }

    private async Task<decimal?> OutputAsync(TestDocument doc, int row)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {doc.RowIds[row]} AND ColumnDefId = {doc.ColumnDefIds[2]}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
