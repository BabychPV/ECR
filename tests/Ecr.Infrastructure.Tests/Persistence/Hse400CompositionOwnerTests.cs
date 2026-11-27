// tests/Ecr.Infrastructure.Tests/Persistence/Hse400CompositionOwnerTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// b14 document-move P1 (H1/H3): склад HSE400 для Land у МІСЯЧНОМУ проєкті версії «рік + місячні колонки» береться з
/// екземпляра-ВЛАСНИКА місяця (період рік·100+m), а не «першого» — після перенесення річного документа в LANDUI2026
/// січнева копія місяця та F_ старого «всі місяці» прогону не можуть перемогти власника.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально, 26.11): (1) база 4980a1837 без P1 («перше значення») — червоні три тести «власник/конфлікт»
/// (запит-храповик зелений: 6 команд і на базі); (2) у <c>OwnerFirst</c> рівень власника не обирається (умова власника
/// завжди хибна) — червоні обидва тести «власник перемагає», конфлікт і контролі зелені.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "b14-document-move-P1")]
public sealed class Hse400CompositionOwnerTests(SqlServerFixture sql)
{
    private const int Year = 2096;
    private const int Month = 10;
    private const int Anchor = (Year * 100) + 1;
    private const int March = (Year * 100) + 3;
    private const int Owner = (Year * 100) + Month;
    private const string Stream = "FG_Internal";
    private const string InputKey = "Vddg";
    private const string CalculatedKey = "F_Vddg";
    private const string MonthCode = "M10";

    [Fact]
    public async Task Ввід_власника_місяця_перемагає_січневу_копію()
    {
        var s = await ArrangeAsync(PeriodKind.Monthly, Anchor, Owner);
        await WriteAsync(s, Anchor, 0.5m);   // застаріла копія в якорі (перенесений річний документ)
        await WriteAsync(s, Owner, 0.81m);   // власник жовтня

        var rows = await ReadAsync(s, EmptyResults());

        var october = rows.Where(r => r.Month == Month).Select(r => r.Value).ToList();
        Assert.Contains(0.81m, october);
        Assert.DoesNotContain(0.5m, october);
    }

    [Fact]
    public async Task F_прогону_власника_перемагає_F_старого_прогону_якоря()
    {
        var s = await ArrangeAsync(PeriodKind.Monthly, Anchor, Owner);
        await WriteAsync(s, Owner, 0.81m);
        var results = Substitute.For<ICalculationResultStore>();
        results.ReadCurrentAsync(s.DocumentId, Anchor, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CalculationResultRow>>(
                [new CalculationResultRow(1, CalculatedKey, MonthCode, 1.11m, 1, null, s.Instances[Anchor])]));
        results.ReadCurrentAsync(s.DocumentId, Owner, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CalculationResultRow>>(
                [new CalculationResultRow(1, CalculatedKey, MonthCode, 2.22m, 1, null, s.Instances[Owner])]));

        var rows = await ReadAsync(s, results);

        var october = rows.Where(r => r.Month == Month).Select(r => r.Value).ToList();
        Assert.Contains(2.22m, october);
        Assert.DoesNotContain(1.11m, october);
    }

    [Fact]
    public async Task Різні_значення_одного_рівня_без_власника_дають_порожньо()
    {
        // Власник жовтня є, але значення в ньому немає; дві копії в інших екземплярах різні — не «перша».
        var s = await ArrangeAsync(PeriodKind.Monthly, Anchor, March, Owner);
        await WriteAsync(s, Anchor, 0.5m);
        await WriteAsync(s, March, 0.6m);

        var rows = await ReadAsync(s, EmptyResults());

        Assert.DoesNotContain(rows, r => r.Month == Month && (r.Value == 0.5m || r.Value == 0.6m));
    }

    [Fact]
    public async Task Однакові_копії_без_власника_дають_значення()
    {
        var s = await ArrangeAsync(PeriodKind.Monthly, Anchor, March, Owner);
        await WriteAsync(s, Anchor, 0.7m);
        await WriteAsync(s, March, 0.7m);

        var rows = await ReadAsync(s, EmptyResults());

        Assert.Contains(rows, r => r.Month == Month && r.Value == 0.7m);
    }

    [Fact]
    public async Task Річний_проєкт_без_змін_усі_місяці_з_якоря()
    {
        // Річний документ (HSE400_2026 до перенесення): єдиний екземпляр 202601 тримає всі місяці — правило власника не діє.
        var s = await ArrangeAsync(PeriodKind.Yearly, Anchor);
        await WriteAsync(s, Anchor, 0.42m);

        var rows = await ReadAsync(s, EmptyResults());

        Assert.Contains(rows, r => r.Month == Month && r.Value == 0.42m);
    }

    [Fact]
    public async Task Версія_без_річної_розкладки_правило_власника_не_застосовує()
    {
        // Місячний проєкт, але версія PerMonthCopies: як до P1 — значення не губиться навіть за різних копій.
        var s = await ArrangeAsync(PeriodKind.Monthly, Anchor, March, Owner, yearLayout: false);
        await WriteAsync(s, Anchor, 0.5m);
        await WriteAsync(s, March, 0.6m);

        var rows = await ReadAsync(s, EmptyResults());

        Assert.Contains(rows, r => r.Month == Month && (r.Value == 0.5m || r.Value == 0.6m));
    }

    [Fact]
    public async Task Кількість_запитів_читання_не_зросла()
    {
        // PERF-11 храповик: правило власника береться з уже обраних екземплярів — запитів стільки ж, скільки на базі.
        var s = await ArrangeAsync(PeriodKind.Monthly, Anchor, Owner);
        await WriteAsync(s, Owner, 0.81m);
        var counter = new CommandCounter();

        await using var db = sql.CreateContext(counter);
        var loader = Loader(s, db, EmptyResults());
        await loader.ReadDocumentsAsync(approvedOnly: false, CancellationToken.None);

        Assert.Equal(ExpectedCommands, counter.Count);
    }

    /// <summary>Заміряно на базі 4980a1837 тим самим сценарієм (без кешу, без формул).</summary>
    private const int ExpectedCommands = 6;

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scene(
        TestDocumentBuilder Builder,
        long DocumentId,
        string TemplateCode,
        int TableDefId,
        int MonthColumnId,
        IReadOnlyDictionary<int, long> Instances,
        IReadOnlyDictionary<int, long> InputRows);

    private async Task<Scene> ArrangeAsync(PeriodKind kind, params int[] periods)
        => await ArrangeAsync(kind, periods, yearLayout: true);

    private Task<Scene> ArrangeAsync(PeriodKind kind, int first, int second, int third, bool yearLayout)
        => ArrangeAsync(kind, [first, second, third], yearLayout);

    private async Task<Scene> ArrangeAsync(PeriodKind kind, int[] periods, bool yearLayout)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var source = await builder.BuildAsync(Anchor, columnCount: 1, rowCount: 1);
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cfg.TemplateVersion SET Layout = {(byte)(yearLayout ? TemplateLayout.YearWithMonthColumns : TemplateLayout.PerMonthCopies)} WHERE Id = {source.TemplateVersionId}");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Project SET PeriodKind = {(byte)kind} WHERE Id = {source.ProjectId}");

        var table = new TableDef(
            source.SheetDefId, EcrCode.Create(Stream), Name(Stream), 50, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var month = new ColumnDef(table.Id, EcrCode.Create(MonthCode), Name(MonthCode), 1, CellDataType.Decimal);
        month.SetMonth(true, (byte)Month);
        db.ColumnDefs.Add(month);
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create(InputKey), 1, Name(InputKey), RowKind.Item));
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create(CalculatedKey), 2, Name(CalculatedKey), RowKind.Item));
        await db.SaveChangesAsync();

        var ids = new BulkCellLoader(sql.ConnectionString, 1000);
        var instances = new Dictionary<int, long>();
        var inputRows = new Dictionary<int, long>();
        foreach (var period in periods)
        {
            var key = new PeriodKey(period);
            var instance = await ids.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            var row = await ids.ReserveIdsAsync("doc.TableRowSeq", 2, CancellationToken.None);
            db.TableInstances.Add(new TableInstance(key, instance, source.DocumentId, table.Id, now));
            db.TableRows.Add(new TableRow(key, row, instance, RowKey.Create(InputKey), 1, now));
            db.TableRows.Add(new TableRow(key, row + 1, instance, RowKey.Create(CalculatedKey), 2, now));
            await db.SaveChangesAsync();
            instances[period] = instance;
            inputRows[period] = row;
        }

        var code = await db.Templates.Where(t => t.Id == source.TemplateId).Select(t => t.Code).SingleAsync();
        return new Scene(builder, source.DocumentId, code, table.Id, month.Id, instances, inputRows);
    }

    /// <summary>Штатний запис комірки місяця в екземпляр періоду <paramref name="period"/> (<c>NormalizedCellStore</c>).</summary>
    private static async Task WriteAsync(Scene s, int period, decimal value)
    {
        await using var db = s.Builder.CreateContext();
        var store = new NormalizedCellStore(db, new ArchiveAwareCellReader(db));
        await store.ApplyAsync(
            new CellChangeSet(
                s.Instances[period],
                [new CellRecord(new CellAddress(new PeriodKey(period), s.InputRows[period], s.MonthColumnId), s.TableDefId,
                    new CellValueData { ValueNumeric = value })],
                [],
                [s.InputRows[period]],
                ChangedByUserId: 1,
                IsLateEdit: false),
            CancellationToken.None);
    }

    private async Task<IReadOnlyList<Ecr.Application.Registries.Hse400CompositionRow>> ReadAsync(
        Scene s, ICalculationResultStore results)
    {
        await using var db = sql.CreateContext();
        var (rows, _) = await Loader(s, db, results).ReadDocumentsAsync(approvedOnly: false, CancellationToken.None);
        return rows;
    }

    private static Hse400CompositionSnapshotLoader Loader(Scene s, EcrDbContext db, ICalculationResultStore results)
        => new(
            Substitute.For<IRegistrySnapshotLoader>(),
            db,
            results,
            enabled: true,
            registryCode: "P1_UNUSED",
            templateCode: s.TemplateCode,
            cache: null);

    private static ICalculationResultStore EmptyResults()
    {
        var results = Substitute.For<ICalculationResultStore>();
        results.ReadCurrentAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CalculationResultRow>>([]));
        return results;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Лічить команди EF, що читають дані.</summary>
    private sealed class CommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>>
            ReaderExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
