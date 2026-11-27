// tests/Ecr.Infrastructure.Tests/Persistence/MonthColumnRouterYearScopedTableTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// b14 document-move P2 (H4, B1): таблиця «екземпляр на період» версії «рік + місячні колонки» — «річна» для
/// маршрутизації (усі комірки — у якорі року), а лінивий екземпляр не-якірного місяця (GET зрізу за 202611, глибоке
/// посилання) отримує дзеркала рядків якоря, а не відкривається порожнім. Land/HSE400 (MonthsInColumns) і версії без
/// річної розкладки — як доти.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально, 26.11): (1) база 4980a1837 — червоні «Екземпляр_на_період_річної_версії…», «Власник…якір» і
/// «Лінивий_екземпляр…дзеркала»; (2) у <c>RowStore.MirrorAnchorRowsAsync</c> повернути фільтр
/// <c>LayoutKind == MonthsInColumns</c> — червоний лише «Лінивий_екземпляр…дзеркала».
/// </remarks>
[Collection("SqlServer")]
[Trait("Finding", "b14-document-move-P2")]
public sealed class MonthColumnRouterYearScopedTableTests(SqlServerFixture sql)
{
    private const int Jan = 202601;
    private const int Nov = 202611;

    [Theory]
    [InlineData(TableLayoutKind.PerPeriodInstance, true, false, true)]
    [InlineData(TableLayoutKind.PerPeriodInstance, false, true, false)]   // спадковий прапор «усі версії» — ні
    [InlineData(TableLayoutKind.PerPeriodInstance, false, false, false)]
    [InlineData(TableLayoutKind.MonthsInColumns, true, false, true)]
    [InlineData(TableLayoutKind.MonthsInColumns, false, true, true)]
    [InlineData(TableLayoutKind.MonthsInColumns, false, false, false)]
    [InlineData(TableLayoutKind.Static, true, true, false)]
    [InlineData(TableLayoutKind.MonthsInRows, true, true, false)]
    public void Екземпляр_на_період_річної_версії_маршрутизується_лише_за_реєстром(
        TableLayoutKind layout, bool yearSheet, bool legacyFlag, bool expected)
    {
        using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer("Server=.;Database=none").Options);
        var router = new MonthColumnRouter(db, Flag(legacyFlag), Registry(sheetId: 5, yearSheet));
        var table = new TableDef(5, EcrCode.Create("T775"), Text("T775"), 1, layout, TableRowMode.Dynamic);

        Assert.Equal(expected, router.AppliesToTable(table));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Власник_комірки_річної_таблиці_за_будь_який_місяць_якір()
    {
        var w = await ArrangeAsync();
        await using var db = w.Builder.CreateContext();
        var router = Router(db, w);

        var document = await router.GetDocumentAsync(w.Doc.DocumentId, CancellationToken.None);
        Assert.NotNull(document);

        var table = await db.TableDefs.AsNoTracking().Include(t => t.Columns).SingleAsync(t => t.Id == w.Doc.TableDefId);
        foreach (var column in table.Columns)
        {
            Assert.Equal(new PeriodKey(Jan), await router.OwnerPeriodAsync(document!, table, column, new PeriodKey(Nov), CancellationToken.None));
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лінивий_екземпляр_місяця_отримує_дзеркала_рядків_якоря()
    {
        var w = await ArrangeAsync();

        await using (var db = w.Builder.CreateContext())
        {
            var created = await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(DateTime.UtcNow), monthRouter: Router(db, w))
                .EnsureTableInstancesAsync(w.Doc.DocumentId, new PeriodKey(Nov), CancellationToken.None);
            Assert.Equal(1, created);
        }

        await using var read = w.Builder.CreateContext();
        var anchorKeys = await RowKeysAsync(read, w.Doc, Jan);
        var novKeys = await RowKeysAsync(read, w.Doc, Nov);
        Assert.Equal(3, anchorKeys.Count);
        Assert.Equal(anchorKeys, novKeys);
    }

    // ─────────────────────────────── допоміжне ───────────────────────────────

    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc);

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        // HSE401 775: Dynamic-таблиця «екземпляр на період», рядки лише динамічні (жодного RowDef).
        var doc = await builder.BuildAsync(Jan, columnCount: 2, rowCount: 0, rowMode: TableRowMode.Mixed, layoutKind: TableLayoutKind.PerPeriodInstance);
        await builder.AddPeriodAsync(doc, new PeriodKey(Nov));

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var first = await loader.ReserveIdsAsync("doc.TableRowSeq", 3, CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            db.TableRows.Add(new TableRow(new PeriodKey(Jan), first + i, doc.TableInstanceId, RowKey.Create($"DYN{i}_{doc.TableDefId}"), i + 1, DateTime.UtcNow));
        }

        await db.SaveChangesAsync();
        return new World(builder, doc);
    }

    private static async Task<List<string>> RowKeysAsync(EcrDbContext db, TestDocument doc, int period)
        => await (
                from r in db.TableRows.AsNoTracking()
                join i in db.TableInstances.AsNoTracking() on r.TableInstanceId equals i.Id
                where r.PeriodKeyValue == period && i.PeriodKeyValue == period && !r.IsDeleted
                      && i.DocumentId == doc.DocumentId && i.TableDefId == doc.TableDefId
                select new { r.RowKeyValue, r.Ordinal })
            .OrderBy(x => x.Ordinal)
            .Select(x => x.RowKeyValue)
            .ToListAsync();

    private static MonthColumnRouter Router(EcrDbContext db, World w)
        => new(db, Flag(false), Registry(w.Doc.SheetDefId, yearSheet: true, w.Doc.TemplateVersionId));

    private static IConfiguration Flag(bool routing)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = routing ? "true" : "false" })
            .Build();

    private static ITemplateLayoutRegistry Registry(int sheetId, bool yearSheet, int versionId = 0)
    {
        var registry = Substitute.For<ITemplateLayoutRegistry>();
        registry.HasAny.Returns(yearSheet);
        registry.IsYearSheet(sheetId).Returns(yearSheet);
        registry.IsYearVersion(versionId).Returns(yearSheet);
        return registry;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
