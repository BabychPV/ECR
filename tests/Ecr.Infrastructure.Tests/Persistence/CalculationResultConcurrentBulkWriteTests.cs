// tests/Ecr.Infrastructure.Tests/Persistence/CalculationResultConcurrentBulkWriteTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ L1b / D1 (стенд Land 21.11, черга в базі): гілки різних документів пишуть результати ТОГО САМОГО періоду
/// одночасно — <c>SqlBulkCopy</c> у <c>calc.CalculationResult</c> (у транзакції методології, разом із кроками й
/// входами) не стає жертвою взаємного блокування (1205) і не чекає до таймауту (-2).
/// </summary>
/// <remarks>
/// Граф стенду (<c>deadlock.xml</c>): дві сесії <c>insert bulk … with (CHECK_CONSTRAINTS)</c> тримали X-замки СТОРІНОК
/// різних індексів таблиці (кластерний <c>PK_CalculationResult (PeriodKey, Id)</c> і <c>IX_CalculationResult_DocRun</c>) і
/// чекали IX на сторінку, яку тримала інша. Ключі сесій не перетинаються (свої Id з послідовності, свій документ), спільні
/// лише СТОРІНКИ на межах діапазонів — тож причина в гранулярності замка, а не в даних.
/// <para>
/// Тест — справжня конкуренція: <see cref="Writers"/> контекстів пишуть одночасно (бар'єр без затримок), кожен
/// <see cref="Rounds"/> разів, як <c>CalculationOutputWriter</c>: кілька чанків в ОДНІЙ транзакції, у чанку — масовий запис
/// результатів, кроки й входи одним <c>SaveChanges</c>.
/// </para>
/// <para>
/// МУТАЦІЯ (локально): прибрати <c>ALLOW_PAGE_LOCKS = OFF</c> (блок L1b у <c>07-partition-tables.sql</c> і міграцію
/// <c>L1bCalcRowLocksOnly</c>) — червоний <see cref="Одночасні_масові_записи_одного_періоду_без_дедлоків_і_таймаутів"/>
/// (1205 у кожному прогоні з 5) і <see cref="Індекси_таблиць_розрахунку_без_сторінкових_замків"/>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "L1b-D1")]
public sealed class CalculationResultConcurrentBulkWriteTests(SqlServerFixture sql)
{
    private const int Writers = 6;

    private const int Rounds = 3;

    /// <summary>Виходів (рядків методології) на гілку за раунд; кожен — 2 результати, 2 кроки, 1 вхід.</summary>
    private const int OutputsPerRound = 4_000;

    /// <summary>Рядків на чанк — як <c>CalculationOutputWriter.DefaultMaxRowsPerSave</c>.</summary>
    private const int RowsPerChunk = 5_000;

    private static readonly DateTime Now = new(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Одночасні_масові_записи_одного_періоду_без_дедлоків_і_таймаутів()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: 209912);
        var (versionId, unitId) = await ArrangeMethodologyAsync(builder);

        // Документи гілок — сусідні номери (саме так вони стоять у `IX_CalculationResult_DocRun`); FK на документ немає.
        var documentBase = 9_000_000_000L + (Random.Shared.NextInt64(1, 1_000_000) * 100);

        using var start = new Barrier(Writers);
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var writers = Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
        {
            start.SignalAndWait();
            for (var round = 0; round < Rounds; round++)
            {
                try
                {
                    await WriteRunAsync(builder, document.ProjectId, document.PeriodKey, documentBase + w, versionId, unitId);
                }
                catch (Exception ex)
                {
                    // Жертву 1205 стратегія EF загортає в InvalidOperationException («transient failure»).
                    var sqlError = FindSql(ex);
                    failures.Add($"гілка {w}, раунд {round}: {(sqlError is null ? ex.GetType().Name : $"SqlException {sqlError.Number}")}: "
                                 + (sqlError?.Message ?? ex.Message));
                }
            }
        })).ToList();

        await Task.WhenAll(writers);

        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures));

        await using var db = builder.CreateContext();
        var written = await db.CalculationResults.AsNoTracking()
            .Where(r => r.PeriodKey == document.PeriodKey.Value && r.DocumentId >= documentBase && r.DocumentId < documentBase + Writers)
            .CountAsync();
        Assert.Equal(Writers * Rounds * OutputsPerRound * 2, written);
    }

    /// <summary>
    /// Розгортання (<c>07-partition-tables.sql</c>) лишає на ВСІХ індексах трьох таблиць розрахунку лише рядкові замки — і
    /// після власних перебудов скрипта (<c>DROP_EXISTING</c> скидає опцію до типової).
    /// </summary>
    [Fact]
    public async Task Індекси_таблиць_розрахунку_без_сторінкових_замків()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OBJECT_NAME(i.object_id) + N'.' + i.name
              FROM sys.indexes AS i
             WHERE i.object_id IN (OBJECT_ID(N'calc.CalculationResult'), OBJECT_ID(N'calc.CalculationStep'),
                                   OBJECT_ID(N'calc.CalculationInput'))
               AND i.type IN (1, 2) AND i.allow_page_locks = 1;
            """;
        var withPageLocks = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                withPageLocks.Add(reader.GetString(0));
            }
        }

        Assert.Empty(withPageLocks);
    }

    private static SqlException? FindSql(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is SqlException sqlError)
            {
                return sqlError;
            }
        }

        return null;
    }

    /// <summary>Один прогін гілки: усі чанки — в одній транзакції, як <c>CalculationOutputWriter</c> понад один чанк.</summary>
    private static async Task WriteRunAsync(
        TestDocumentBuilder builder, int projectId, PeriodKey period, long documentId, int versionId, int unitId)
    {
        long runId;
        await using (var setup = builder.CreateContext())
        {
            var run = new CalculationRun(projectId, period.Value, triggeredByUserId: null, Now, documentId: null);
            setup.CalculationRuns.Add(run);
            await setup.SaveChangesAsync();
            runId = run.Id;
        }

        var outputs = Outputs(documentId, versionId, unitId);
        await using var db = builder.CreateContext();
        var store = new CalculationResultStore(db, new TestClock(Now));
        await new UnitOfWork(db).ExecuteInTransactionAsync(
            async ct =>
            {
                foreach (var chunk in outputs.Chunk(RowsPerChunk / 5))
                {
                    await store.WriteResultsAsync(runId, chunk, ct);
                    await store.WriteTraceAsync(runId, chunk, TraceLevel.Full, ct);
                    await db.SaveChangesAsync(ct);
                }
            },
            CancellationToken.None);
    }

    private static List<CalculationOutput> Outputs(long documentId, int versionId, int unitId)
        => [.. Enumerable.Range(1, OutputsPerRound).Select(i => new CalculationOutput(
            documentId,
            $"R{i:00000}",
            [
                new CalculationOutputValue(versionId, null, "A", i / 3m, unitId),
                new CalculationOutputValue(versionId, null, "B", i * 2.5m, unitId, CalculationResultKind.Intermediate),
            ],
            [
                new CalculationTraceStep(1, "A", "x/3", i / 3m, TraceJson: null),
                new CalculationTraceStep(2, "B", "x*2.5", i * 2.5m, TraceJson: null),
            ],
            [new CalculationArgument("X", i, null, unitId)])
        {
            TableInstanceId = 7_000 + (i % 2),
        })];

    private static async Task<(int VersionId, int UnitId)> ArrangeMethodologyAsync(TestDocumentBuilder builder)
    {
        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(
            EcrCode.Create($"L1B_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        return (version.Id, unitId);
    }
}
