using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-139: запис комірок бере блокування в ЄДИНОМУ порядку — період ↑, рядок ↑, колонка ↑; усі
/// блокування <c>doc.TableRow</c> (захоплення, «дотик») — до блокувань <c>doc.CellValue</c>.
/// </summary>
/// <remarks>
/// ⛔ Дефект: deadlock ~1/640 на <c>MERGE doc.CellValue</c> / <c>PK_TableRow</c>. Порядок входу набору
/// (словник клієнта, порядок екземплярів у пакеті) ніким не впорядковувався, а «дотик» рядків власника
/// йшов ПІСЛЯ <c>MERGE</c>. Перший тест детермінований (шов <c>LockOrder.Sink</c> знімає послідовність
/// звернень), другий — стрес із ОБЕРНЕНИМ порядком наборів у двох паралельних письменників.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellWriteLockOrderTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Звернення_до_періодів_і_рядків_йдуть_за_зростанням_незалежно_від_порядку_входу()
    {
        var ct = CancellationToken.None;
        var fx = await BuildAsync(rowCount: 4, ct);
        var store = new NormalizedCellStore(fx.Builder.CreateContext());

        // Свідомо ОБЕРНЕНИЙ порядок: пізніший період першим, рядки й колонки — за спаданням.
        var sets = new[] { fx.Set(fx.P3, reversed: true, claim: true), fx.Set(fx.P2, reversed: true, claim: true), fx.Set(fx.P1, reversed: true, claim: true) };

        var trace = new List<string>();
        LockOrder.Sink = trace;
        try
        {
            await store.ApplyBatchAsync(sets, ct);
        }
        finally
        {
            LockOrder.Sink = null;
        }

        // Етапи: усе по doc.TableRow (claim, touch) — до doc.CellValue (delete, upsert).
        var kinds = trace.Select(t => t.Split(' ')[0]).ToList();
        var lastRowStep = kinds.FindLastIndex(k => k is "claim" or "touch");
        var firstCellStep = kinds.FindIndex(k => k is "delete" or "upsert");
        Assert.True(lastRowStep >= 0 && firstCellStep > lastRowStep, $"Порядок етапів: {string.Join(" | ", trace)}");

        foreach (var kind in new[] { "claim", "touch" })
        {
            var steps = trace.Where(t => t.StartsWith(kind + " ", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(steps);

            var periods = steps.Select(s => int.Parse(s.Split(' ')[1][2..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(periods.OrderBy(p => p), periods);

            foreach (var step in steps)
            {
                var ids = step[(step.IndexOf("ids=", StringComparison.Ordinal) + 4)..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList();
                Assert.Equal(ids.OrderBy(i => i), ids);
            }
        }

        foreach (var kind in new[] { "delete", "upsert" })
        {
            var step = trace.Single(t => t.StartsWith(kind + " ", StringComparison.Ordinal));
            var keys = step[(kind.Length + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Split('/').Select(long.Parse).ToArray()).ToList();
            Assert.Equal(
                keys.OrderBy(k => k[0]).ThenBy(k => k[1]).ThenBy(k => k[2]).Select(k => string.Join('/', k)),
                keys.Select(k => string.Join('/', k)));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Два_паралельні_письменники_з_оберненим_порядком_наборів_не_дають_deadlock()
    {
        var ct = CancellationToken.None;
        var fx = await BuildAsync(rowCount: 6, ct);
        const int Iterations = 120;
        var deadlocks = 0;

        async Task Writer(bool reversed, int seed)
        {
            var store = new NormalizedCellStore(fx.Builder.CreateContext());
            for (var i = 0; i < Iterations; i++)
            {
                var order = reversed ? new[] { fx.P3, fx.P2, fx.P1 } : [fx.P1, fx.P2, fx.P3];
                var sets = order.Select(p => fx.Set(p, reversed, claim: false, value: seed + i)).ToArray();
                try
                {
                    await store.ApplyBatchAsync(sets, ct);
                }
                catch (SqlException ex) when (ex.Number == 1205)
                {
                    Interlocked.Increment(ref deadlocks);
                }
            }
        }

        await Task.WhenAll(Task.Run(() => Writer(false, 1000)), Task.Run(() => Writer(true, 2000)));

        Assert.Equal(0, deadlocks);
    }

    private async Task<Fixture> BuildAsync(int rowCount, CancellationToken ct)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: rowCount, ct: ct);
        var p2 = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202602), ct);
        var p3 = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202603), ct);
        var p1 = new TestPeriodInstance(doc.PeriodKey, doc.TableInstanceId, doc.RowIds, []);
        return new Fixture(builder, doc, p1, p2, p3, sql.ConnectionString);
    }

    private sealed record Fixture(
        TestDocumentBuilder Builder, TestDocument Doc, TestPeriodInstance P1, TestPeriodInstance P2, TestPeriodInstance P3, string Connection)
    {
        /// <summary>Набір на екземпляр: усі комірки + «дотик» рядків (+ заявлені версії при <paramref name="claim"/>).</summary>
        public CellChangeSet Set(TestPeriodInstance inst, bool reversed, bool claim, int value = 1)
        {
            var rows = reversed ? inst.RowIds.Reverse().ToArray() : [.. inst.RowIds];
            var cols = reversed ? Doc.ColumnDefIds.Reverse().ToArray() : [.. Doc.ColumnDefIds];
            var upserts = rows.SelectMany(r => cols.Select(c => new CellRecord(
                new CellAddress(inst.PeriodKey, r, c), Doc.TableDefId, new CellValueData { ValueNumeric = value }))).ToList();
            var deletes = rows.Take(1).Select(r => new CellAddress(inst.PeriodKey, r, cols[0])).ToList();
            upserts.RemoveAll(u => u.Address == deletes[0]);

            // Половину рядків заявляємо (захоплення), решту лише «дотикаємо».
            Dictionary<long, string>? expected = null;
            if (claim)
            {
                expected = Versions(inst.TableInstanceId).Where(p => rows.Take(rows.Length / 2).Contains(p.Key)).ToDictionary();
            }

            return new CellChangeSet(
                inst.TableInstanceId, upserts, deletes, rows, ChangedByUserId: 1, IsLateEdit: false,
                ExpectedRowVersions: expected);
        }

        private Dictionary<long, string> Versions(long instanceId)
        {
            using var connection = new SqlConnection(Connection);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, RowVersion FROM doc.TableRow WHERE TableInstanceId = @i;";
            command.Parameters.AddWithValue("@i", instanceId);
            using var reader = command.ExecuteReader();
            var result = new Dictionary<long, string>();
            while (reader.Read())
            {
                result[reader.GetInt64(0)] = Convert.ToBase64String(reader.GetFieldValue<byte[]>(1));
            }

            return result;
        }
    }
}
