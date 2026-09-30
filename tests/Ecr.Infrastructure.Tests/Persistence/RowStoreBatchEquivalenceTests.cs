using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// P8, застосування імпорту 1/3: пакетні <c>GetRowsBatchAsync</c> і
/// <c>ResolveTableInstancesAsync</c> дають те саме, що поштучні методи на
/// кожен екземпляр, і коштують сталу кількість звернень незалежно від
/// кількості таблиць.
/// </summary>
/// <remarks>
/// ⚠ Поштучні методи реалізовані через пакетні з одним екземпляром, тож
/// еквівалентність стереже саме ПАКЕТНІ місця (групування за екземпляром).
/// Фільтри, спільні для обох шляхів (видалений рядок, чужий період), стереже
/// окрема предметна перевірка — еквівалентність їх не бачить.
/// </remarks>
[Collection("SqlServer")]
public sealed class RowStoreBatchEquivalenceTests(SqlServerFixture sql)
{
    /// <summary>Період книги — не 2026-05…07 (їх архівують <c>ArchiveJobTests</c>).</summary>
    private const int PeriodKeyValue = 202610;

    /// <summary>Інший період — екземпляр звідти в пакет за 202610 не потрапляє.</summary>
    private const int OtherPeriodKeyValue = 202611;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакет_рядків_тотожний_поштучному_для_кожного_екземпляра()
    {
        var world = await ArrangeAsync();
        var period = new PeriodKey(PeriodKeyValue);

        IReadOnlyDictionary<long, IReadOnlyList<RowState>> batch;
        await using (var db = world.Builder.CreateContext())
        {
            batch = await Store(db).GetRowsBatchAsync(world.Requested, period, CancellationToken.None);
        }

        var nonEmpty = 0;
        foreach (var instanceId in world.Requested)
        {
            IReadOnlyList<RowState> single;
            await using (var db = world.Builder.CreateContext())
            {
                single = await Store(db).GetRowsAsync(instanceId, period, CancellationToken.None);
            }

            // Екземпляр без рядків у пакеті відсутній — поштучний дає порожньо.
            var batched = batch.TryGetValue(instanceId, out var found) ? found : [];

            Assert.Equal(single.OrderBy(r => r.Id), batched.OrderBy(r => r.Id));
            nonEmpty += single.Count > 0 ? 1 : 0;
        }

        // ⛔ Проти хибнозеленого: «порожньо == порожньо» нічого не доводить.
        Assert.True(nonEmpty >= 12, $"Непорожніх екземплярів лише {nonEmpty}.");

        // ── Предметно: фільтри, спільні для обох шляхів ─────────────────────
        Assert.DoesNotContain(batch.Values.SelectMany(r => r), r => r.Id == world.DeletedRowId);
        Assert.False(batch.ContainsKey(world.OtherPeriodInstanceId));
        Assert.False(batch.ContainsKey(world.MissingInstanceId));

        var withDeleted = batch[world.DeletedFromInstanceId];
        Assert.Equal(world.RowsPerTable - 1, withDeleted.Count);
        Assert.All(
            world.AllInstances.Where(id => id != world.DeletedFromInstanceId),
            id => Assert.Equal(world.RowsPerTable, batch[id].Count));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакет_ідентичностей_тотожний_поштучному_і_відмовляє_на_відсутньому()
    {
        var world = await ArrangeAsync();
        var ids = world.AllInstances.Append(world.OtherPeriodInstanceId).ToList();

        IReadOnlyDictionary<long, TableInstanceRef> batch;
        await using (var db = world.Builder.CreateContext())
        {
            batch = await Store(db).ResolveTableInstancesAsync(ids, CancellationToken.None);
        }

        Assert.Equal(ids.Order(), batch.Keys.Order());

        foreach (var id in ids)
        {
            await using var db = world.Builder.CreateContext();
            var single = await Store(db).ResolveTableInstanceAsync(id, CancellationToken.None);

            Assert.Equal(single, batch[id]);
        }

        // Два різні документи в одному пакеті — кожному свій.
        Assert.Equal(2, batch.Values.Select(r => r.DocumentId).Distinct().Count());
        Assert.Equal(OtherPeriodKeyValue, batch[world.OtherPeriodInstanceId].PeriodKey);

        // Відсутній — та сама відмова, що й у поштучного, навіть серед знайдених.
        await using (var db = world.Builder.CreateContext())
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(() => Store(db).ResolveTableInstancesAsync(
                [.. ids, world.MissingInstanceId], CancellationToken.None));

            Assert.Equal("ECR-DOC-0404", error.ErrorCode);
            Assert.Equal(
                world.MissingInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                error.Details!["tableInstanceId"]);
        }
    }

    /// <summary>
    /// Храповик: пакетні методи коштують ОДНАКОВО на 3 і на 12 таблицях.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакетні_методи_рядків_коштують_сталу_кількість_звернень_на_3_і_12_таблиць()
    {
        var world = await ArrangeAsync();
        var period = new PeriodKey(PeriodKeyValue);
        var three = world.AllInstances.Take(3).ToList();
        var twelve = world.AllInstances.ToList();
        Assert.Equal(12, twelve.Count);

        var counter = new DbCommandCounter();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(counter)
            .Options);
        var store = Store(db);

        async Task<CommandTallySnapshot> Measure(Func<Task> action)
        {
            counter.Tally.Reset();
            await action();
            return counter.Tally.Snapshot();
        }

        var rows3 = await Measure(() => store.GetRowsBatchAsync(three, period, CancellationToken.None));
        var rows12 = await Measure(() => store.GetRowsBatchAsync(twelve, period, CancellationToken.None));
        var resolve3 = await Measure(() => store.ResolveTableInstancesAsync(three, CancellationToken.None));
        var resolve12 = await Measure(() => store.ResolveTableInstancesAsync(twelve, CancellationToken.None));

        Assert.True(rows3.Total == rows12.Total, $"GetRowsBatchAsync: 3 → {rows3.Format()}; 12 → {rows12.Format()}");
        Assert.True(resolve3.Total == resolve12.Total, $"ResolveTableInstancesAsync: 3 → {resolve3.Format()}; 12 → {resolve12.Format()}");
        Assert.Equal(1, rows12.Total);
        Assert.Equal(1, resolve12.Total);
    }

    private RowStore Store(EcrDbContext db)
        => new(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));

    private sealed record World(
        TestDocumentBuilder Builder,
        IReadOnlyList<long> AllInstances,
        IReadOnlyList<long> Requested,
        int RowsPerTable,
        long DeletedRowId,
        long DeletedFromInstanceId,
        long OtherPeriodInstanceId,
        long MissingInstanceId);

    /// <summary>
    /// Документ на 12 таблиць (базова + 3·3 + 2 на чотирьох нових аркушах) за
    /// 202610, один рядок у ньому видалено; ще документ за 202611.
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        const int rowsPerTable = 3;

        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: rowsPerTable, ct: CancellationToken.None);
        var extra = await MultiTableDocument.AddTablesAsync(
            builder, doc, [3, 3, 3, 2], rowCount: rowsPerTable, ct: CancellationToken.None);

        var other = await builder.BuildAsync(OtherPeriodKeyValue, columnCount: 2, rowCount: 2, ct: CancellationToken.None);

        var deletedFrom = extra[4];
        var deletedRowId = deletedFrom.RowIds[1];
        await ExecuteAsync("UPDATE doc.TableRow SET IsDeleted = 1 WHERE Id = @id", deletedRowId);

        var all = new List<long> { doc.TableInstanceId };
        all.AddRange(extra.Select(t => t.TableInstanceId));

        var missing = long.MaxValue - 7;

        // ⚠ Порядок запиту не збігається з порядком створення, а серед
        // запитаних — екземпляр чужого періоду й неіснуючий.
        var requested = new List<long> { other.TableInstanceId, missing };
        requested.AddRange(all.AsEnumerable().Reverse());

        return new World(
            builder, all, requested, rowsPerTable, deletedRowId, deletedFrom.TableInstanceId,
            other.TableInstanceId, missing);
    }

    private async Task ExecuteAsync(string sqlText, long id)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
