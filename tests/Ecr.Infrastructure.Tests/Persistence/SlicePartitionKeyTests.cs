using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// O3b (<c>WR-05</c>): зріз і пакет зрізів із ключем партиції повертають рівно
/// те саме, що колишній запит лише за <c>Id</c>.
/// </summary>
/// <remarks>
/// ⛔ Прискорення — лише план (seek в одній партиції замість скану
/// <c>UQ_TableInstance</c> по всіх). Ці тести стережуть РЕЗУЛЬТАТ: екземпляр не
/// в першій партиції, пакет із двох партицій, екземпляр, чийого періоду немає в
/// <c>doc.Period</c>, і порожня таблиця. Що предикат справді стоїть у SQL,
/// стереже <c>PartitionKeyQueryTests</c> (фабрика
/// <see cref="RowStore.TableInstancesInPeriodQuery"/>), а що зріз її справді
/// ВИКОРИСТОВУЄ — перевірка «чужий період → порожньо» нижче.
///
/// ⚠ Період 203004 — рік, якого немає ні в чиїх <c>doc.Period</c> (див.
/// <c>TableInstanceByIdLookupTests</c>, там 203003): видалення рядків періоду
/// свого проєкту справді прибирає ключ із множини.
/// </remarks>
[Collection("SqlServer")]
public sealed class SlicePartitionKeyTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зріз_і_пакет_у_не_першій_партиції_такі_самі_як_без_ключа()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var april = await builder.BuildAsync(periodKey: 202604, columnCount: 3, rowCount: 4, ct: ct);
        var november = await builder.BuildAsync(periodKey: 202611, columnCount: 3, rowCount: 4, ct: ct);

        await using var db = builder.CreateContext();
        var store = new NormalizedCellStore(db);
        await FillAsync(store, april, 1m, ct);
        await FillAsync(store, november, 100m, ct);

        foreach (var doc in new[] { april, november })
        {
            var expected = await UnboundedAsync(db, doc.TableInstanceId);
            Assert.Equal(doc.RowIds.Count * (doc.ColumnDefIds.Count - 1), expected.Count);

            Assert.Equal(expected, Keys(await store.ReadSliceAsync(doc.TableInstanceId, ct)));
            Assert.Equal(expected, Keys(await store.ReadSliceAsync(doc.TableInstanceId, doc.PeriodKey, ct)));

            var inPeriod = await store.ReadSlicesAsync([doc.TableInstanceId], doc.PeriodKey, ct);
            Assert.Equal(expected, Keys(Assert.Single(inPeriod).Value));
        }

        // Пакет із двох партицій без ключа — той самий, що й до O3b.
        var both = await store.ReadSlicesAsync([april.TableInstanceId, november.TableInstanceId], ct);
        Assert.Equal(await UnboundedAsync(db, april.TableInstanceId), Keys(both[april.TableInstanceId]));
        Assert.Equal(await UnboundedAsync(db, november.TableInstanceId), Keys(both[november.TableInstanceId]));

        // ⛔ Ключ справді в запиті: з ЧУЖИМ періодом екземпляр не знаходиться.
        // Без предиката на PeriodKey обидва виклики повернули б повний зріз.
        Assert.Empty(await store.ReadSliceAsync(november.TableInstanceId, april.PeriodKey, ct));
        Assert.Empty(await store.ReadSlicesAsync([november.TableInstanceId], april.PeriodKey, ct));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Період_без_рядка_в_Period_не_дає_порожнього_зрізу()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 203004, columnCount: 3, rowCount: 2, ct: ct);

        await using var db = builder.CreateContext();
        var store = new NormalizedCellStore(db);
        await FillAsync(store, doc, 7m, ct);

        await ExecuteAsync("DELETE FROM doc.Period WHERE ProjectId = @p;", ("@p", doc.ProjectId));

        // Передумова: шлях через doc.Period цей екземпляр НЕ бачить — інакше
        // тест не доводив би, що запасний запит потрібен.
        Assert.Equal(0, await RowStore.TableInstancesByIdQuery(db, [doc.TableInstanceId]).CountAsync());

        var expected = await UnboundedAsync(db, doc.TableInstanceId);
        Assert.NotEmpty(expected);

        Assert.Equal(expected, Keys(await store.ReadSliceAsync(doc.TableInstanceId, ct)));
        Assert.Equal(expected, Keys(await store.ReadSliceAsync(doc.TableInstanceId, doc.PeriodKey, ct)));
        Assert.Equal(expected, Keys((await store.ReadSlicesAsync([doc.TableInstanceId], ct))[doc.TableInstanceId]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порожня_таблиця_без_періоду_не_йде_в_запит_без_ключа()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202611, columnCount: 3, rowCount: 2, ct: ct);

        var executed = new List<string>();
        await using var counting = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);

        var slice = await new NormalizedCellStore(counting).ReadSliceAsync(doc.TableInstanceId, ct);

        // Зріз порожній, екземпляр є: зріз + перевірка існування за ключем —
        // і жодного запиту без ключа (кожен із двох несе doc.Period).
        Assert.Empty(slice);
        Assert.Equal(2, executed.Count);
        Assert.All(executed, command => Assert.Contains("[doc].[Period]", command, StringComparison.Ordinal));
    }

    /// <summary>Заповнює всі нерядкові колонки всіх рядків.</summary>
    private static async Task FillAsync(NormalizedCellStore store, TestDocument doc, decimal seed, CancellationToken ct)
    {
        var records = doc.RowIds
            .SelectMany(rowId => doc.ColumnDefIds.Skip(1).Select(columnId => new CellRecord(
                new CellAddress(doc.PeriodKey, rowId, columnId),
                doc.TableDefId,
                new CellValueData { ValueNumeric = seed + rowId + columnId })))
            .ToList();

        await store.ApplyAsync(new CellChangeSet(doc.TableInstanceId, records, [], doc.RowIds, 1, false), ct);
    }

    /// <summary>Колишній запит зрізу — лише за <c>Id</c>, без ключа партиції.</summary>
    private static async Task<List<string>> UnboundedAsync(EcrDbContext db, long tableInstanceId)
        => [.. (await (
                from instance in db.TableInstances.AsNoTracking()
                where instance.Id == tableInstanceId
                join row in db.TableRows.AsNoTracking()
                    on new { P = instance.PeriodKeyValue, I = instance.Id }
                    equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
                join cell in db.CellValues.AsNoTracking()
                    on new { P = row.PeriodKeyValue, R = row.Id }
                    equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
                where !row.IsDeleted
                select new { cell.PeriodKeyValue, cell.TableRowId, cell.ColumnDefId, cell.TableDefId, cell.ValueNumeric, cell.ValueString, cell.IsCalculated, cell.IsEmpty })
            .ToListAsync())
            .Select(c => Key(c.PeriodKeyValue, c.TableRowId, c.ColumnDefId, c.TableDefId, c.ValueNumeric, c.ValueString, c.IsCalculated, c.IsEmpty))
            .Order(StringComparer.Ordinal)];

    private static List<string> Keys(IEnumerable<CellRecord> records)
        => [.. records
            .Select(r => Key(r.Address.PeriodKey.Value, r.Address.TableRowId, r.Address.ColumnDefId, r.TableDefId,
                r.Value.ValueNumeric, r.Value.ValueString, r.Value.IsCalculated, r.Value.IsEmpty))
            .Order(StringComparer.Ordinal)];

    private static string Key(
        int period, long rowId, int columnId, int tableDefId, decimal? number, string? text, bool calculated, bool empty)
        => FormattableString.Invariant($"{period}|{rowId}|{columnId}|{tableDefId}|{number}|{text}|{calculated}|{empty}");

    private async Task ExecuteAsync(string sqlText, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
