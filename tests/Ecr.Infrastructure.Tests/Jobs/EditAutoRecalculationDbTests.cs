// tests/Ecr.Infrastructure.Tests/Jobs/EditAutoRecalculationDbTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
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
/// ✎ D-577 (Q2, рішення людини 26.11): автоперерахунок методологій після правки — тихо, з відкладенням і злиттям (без
/// шторму на масовій вставці), лише в періоди, куди можна писати, і лише для документа з методологіями.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально, не комітити): (1) <c>CalculationTrigger.EditDebounce = TimeSpan.Zero</c> — червоний
/// <c>Q2_сплеск_правок_дає_одну_відкладену_задачу_авто</c> (claim «зараз» бере задачу); (2) прибрати перевірку
/// <c>WritableAsync</c> у <c>RequestAfterEditAsync</c> — червоний <c>Q2_закритий_період_не_ставиться</c>; (3) у
/// <c>EditAutoRecalculation.OnEditedAsync</c> прибрати ворота прив'язок (<c>withMethodologies.Count == 0</c>) — червоний
/// <c>Q2_документ_без_методологій_нічого_не_ставить</c>.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "D-577")]
public sealed class EditAutoRecalculationDbTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const long Document = 9_577_001;

    private static readonly PeriodKey Period = new(202604);

    [Fact]
    public async Task Q2_сплеск_правок_дає_одну_відкладену_задачу_авто()
    {
        await using var host = NewHost();
        var trigger = Trigger(host, PeriodState.Open);

        var first = await trigger.RequestAfterEditAsync(Document, Period, CancellationToken.None);
        Assert.NotNull(first);
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(first, await trigger.RequestAfterEditAsync(Document, Period, CancellationToken.None));
        }

        await using (var db = Sql.CreateContext())
        {
            var rows = await db.JobProgresses.Where(p => p.Lane == JobLanes.Recalc && p.State == "Queued").ToListAsync();
            var row = Assert.Single(rows);
            Assert.EndsWith(CalculationTrigger.AutoTargetOf(Document, Period), row.TargetKey, StringComparison.Ordinal);
            Assert.Null(row.CreatedByUserId);
            Assert.Contains("\"recalculateSubmitted\":true", row.Payload!, StringComparison.OrdinalIgnoreCase);
        }

        // Вікно злиття — до EditDebounce від першої правки; зараз claim задачі не бере.
        var delay = await AvailableInAsync(first!);
        Assert.InRange(delay, CalculationTrigger.EditDebounce - TimeSpan.FromSeconds(3), CalculationTrigger.EditDebounce);
        Assert.Null(await host.Queue.ClaimAsync([JobLanes.Recalc], "test/host", JobQueueLimits.DefaultLease, CancellationToken.None));
    }

    [Fact]
    public async Task Q2_закритий_період_не_ставиться()
    {
        await using var host = NewHost();
        Assert.Null(await Trigger(host, PeriodState.Closed).RequestAfterEditAsync(Document, Period, CancellationToken.None));
        Assert.Null(await Trigger(host, PeriodState.Scheduled).RequestAfterEditAsync(Document, Period, CancellationToken.None));

        await using var db = Sql.CreateContext();
        Assert.Equal(0, await db.JobProgresses.CountAsync(p => p.Lane == JobLanes.Recalc));
    }

    /// <summary>
    /// ✎ D-577 (рішення координатора): річна розкладка — правка НЕ-місячної колонки якоря (загальні дані року) ставить і
    /// місяці-читачі (скрипт методології читає поле — <c>calc.MethodologyReadField</c>); якщо скрипт поле не читає — лише
    /// період правки (без зайвих прогонів).
    /// </summary>
    /// <remarks>
    /// Мутація (локально): у <c>EditAutoRecalculation.WithReaderMonthsAsync</c> одразу повертати <c>keys</c> — червоний
    /// перший крок (ставиться лише якір).
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Q2_правка_загальних_даних_якоря_будить_місяці_читачі(bool scriptReadsColumn)
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202601);
        await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202602));
        await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202603));
        var yearlyColumn = doc.ColumnDefIds[0];

        string columnCode;
        await using (var db = builder.CreateContext())
        {
            columnCode = (await db.ColumnDefs.AsNoTracking().FirstAsync(c => c.Id == yearlyColumn)).Code;
            var methodology = new Methodology(
                EcrCode.Create($"EY_{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            var version = new MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                MethodologyEngine.Script);
            version.Publish(2, "q2 anchor", new DateOnly(2026, 1, 1), testsPassed: true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            db.MethodologyVersions.Add(version);
            db.CalculationBindings.Add(new CalculationBinding(doc.TableDefId, doc.ColumnDefIds[^1], methodology.Id, "E_CO2", "{}"));
            await db.SaveChangesAsync();
            db.MethodologyReadFields.Add(new MethodologyReadField(version.Id, MethodologyReadField.RecordedMarker, otherMonths: false));
            db.MethodologyReadFields.Add(new MethodologyReadField(version.Id, scriptReadsColumn ? columnCode : "SomethingElse", otherMonths: false));
            await db.SaveChangesAsync();
        }

        var column = new ColumnDef(doc.TableDefId, EcrCode.Create(columnCode),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "y" }), 1, CellDataType.Decimal);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(column, yearlyColumn);
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            doc.TemplateVersionId, 0, [], new Dictionary<int, ColumnDef> { [yearlyColumn] = column },
            new Dictionary<(int, string), RowDef>()));
        var layouts = Substitute.For<ITemplateLayoutRegistry>();
        layouts.HasAny.Returns(true);
        layouts.IsYearVersion(doc.TemplateVersionId).Returns(true);

        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAfterEditAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns("job");

        await using (var db = builder.CreateContext())
        {
            var edit = new EditAutoRecalculation(db, trigger, metadata: metadata, layouts: layouts);
            var enqueued = await edit.OnEditedAsync(
                doc.DocumentId, [202601], new HashSet<int> { doc.TemplateVersionId },
                new Dictionary<int, IReadOnlySet<int>> { [202601] = new HashSet<int> { yearlyColumn } }, CancellationToken.None);
            Assert.Equal(scriptReadsColumn ? 3 : 1, enqueued);
        }

        await trigger.Received(1).RequestAfterEditAsync(doc.DocumentId, new PeriodKey(202601), Arg.Any<CancellationToken>());
        await trigger.Received(scriptReadsColumn ? 1 : 0).RequestAfterEditAsync(doc.DocumentId, new PeriodKey(202603), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Q2_документ_без_методологій_нічого_не_ставить_а_з_методологією_ставить_період_правки()
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var plain = await builder.BuildAsync(periodKey: Period.Value);
        var bound = await builder.BuildAsync(periodKey: Period.Value);

        await using (var db = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create($"EA_{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            db.CalculationBindings.Add(new CalculationBinding(bound.TableDefId, bound.ColumnDefIds[^1], methodology.Id, "E_CO2", "{}"));
            await db.SaveChangesAsync();
        }

        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAfterEditAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns("job-1");

        await using (var db = builder.CreateContext())
        {
            var edit = new EditAutoRecalculation(db, trigger);
            Assert.Equal(0, await edit.OnEditedAsync(plain.DocumentId, [Period.Value], CancellationToken.None));
            Assert.Equal(1, await edit.OnEditedAsync(bound.DocumentId, [Period.Value, Period.Value], CancellationToken.None));
        }

        await trigger.DidNotReceive().RequestAfterEditAsync(plain.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await trigger.Received(1).RequestAfterEditAsync(bound.DocumentId, Period, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ✎ M5 (D-442, рішення людини): payload автоперерахунку після правки погоджувача несе його аркуш (<c>approverSheets</c>) і
    /// <c>skipIfFresh</c>; злиття з правкою БЕЗ звільнення знімає звільнення (перетин), а «пропустити, якщо актуально» лишається
    /// лише коли обидві постановки його мають.
    /// </summary>
    /// <remarks>Мутація (локально): у <c>DbJobQueue</c> прибрати злиття <c>$.approverSheets</c> — червоний другий крок.</remarks>
    [Fact]
    public async Task M5_payload_авто_несе_аркуш_погоджувача_а_злиття_без_нього_звільнення_знімає()
    {
        await using var host = NewHost();
        var trigger = Trigger(host, PeriodState.Open);

        var jobId = await trigger.RequestAfterEditAsync(Document, Period, [77, 78], CancellationToken.None);
        Assert.NotNull(jobId);
        Assert.Contains("\"approverSheets\":[77,78]", await PayloadAsync(jobId!), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"skipIfFresh\":true", await PayloadAsync(jobId!), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(jobId, await trigger.RequestAfterEditAsync(Document, Period, [78], CancellationToken.None));
        Assert.Contains("\"approverSheets\":[78]", await PayloadAsync(jobId!), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(jobId, await trigger.RequestAfterEditAsync(Document, Period, CancellationToken.None));
        Assert.Contains("\"approverSheets\":[]", await PayloadAsync(jobId!), StringComparison.OrdinalIgnoreCase);

        // Власний перерахунок (без skipIfFresh) — злитий запит має виконатись.
        Assert.Equal(jobId, await trigger.RequestAsync(Document, Period, CancellationToken.None));
        Assert.Contains("\"skipIfFresh\":false", await PayloadAsync(jobId!), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ✎ Огляд H1: період, у який хук документа-джерела вже поставив власний перерахунок (з місяцями правки), автоперерахунок
    /// не ставить (інакше злиття без місяців розширило б його на весь рік).
    /// </summary>
    /// <remarks>Мутація (локально): прибрати фільтр <c>edit.Claimed</c> — червоний.</remarks>
    [Fact]
    public async Task H1_період_власного_перерахунку_хука_не_ставиться_вдруге()
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: Period.Value);
        await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202605));
        await BindAsync(builder, doc);

        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAfterEditAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns("job");

        await using (var db = builder.CreateContext())
        {
            var enqueued = await new EditAutoRecalculation(db, trigger).OnEditedAsync(
                doc.DocumentId, new EditedPeriods([Period.Value, 202605]) { Claimed = new HashSet<int> { Period.Value } },
                CancellationToken.None);
            Assert.Equal(1, enqueued);
        }

        await trigger.DidNotReceive().RequestAfterEditAsync(doc.DocumentId, Period, Arg.Any<CancellationToken>());
        await trigger.Received(1).RequestAfterEditAsync(doc.DocumentId, new PeriodKey(202605), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ✎ Огляд H2 (c): документ, у якого рядків за період більше за межу <see cref="EditAutoRecalculation.MaxRowsKey"/>, —
    /// без автоперерахунку (прогін не тримає лок документа після кожної правки); на межі й нижче — ставиться; 0 — без межі.
    /// </summary>
    /// <remarks>Мутація (локально): у <c>WithoutHeavyAsync</c> повертати <c>periods</c> одразу — червоний (межа 3).</remarks>
    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(0, 1)]
    public async Task H2_важкий_документ_без_автоперерахунку(int maxRows, int expected)
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: Period.Value, rowCount: 4);
        await BindAsync(builder, doc);

        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAfterEditAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns("job");
        var configuration = Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder(),
                new Dictionary<string, string?>
            {
                [EditAutoRecalculation.MaxRowsKey] = maxRows.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

        await using var db = builder.CreateContext();
        Assert.Equal(expected, await new EditAutoRecalculation(db, trigger, configuration).OnEditedAsync(
            doc.DocumentId, [Period.Value], CancellationToken.None));
    }

    /// <summary>
    /// ✎ Огляд M6: річна розкладка — правка МІСЯЧНОЇ колонки <c>X_M01</c>, яку скрипт читає за ІНШИЙ місяць (<c>row.X[m]</c>,
    /// <c>OtherMonths</c>), будить місяці року; читання лише свого місяця — лише період правки.
    /// </summary>
    /// <remarks>Мутація (локально): у <c>AnyReaderAsync</c> прибрати умову <c>OtherMonths</c> для основ місячних — червоний (true).</remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task M6_правка_місячної_колонки_яку_читають_за_інший_місяць_будить_рік(bool otherMonths)
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202601);
        await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202602));
        await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202603));
        var monthColumn = doc.ColumnDefIds[0];

        var version = await BindAsync(builder, doc);
        await using (var db = builder.CreateContext())
        {
            db.MethodologyReadFields.Add(new MethodologyReadField(version, MethodologyReadField.RecordedMarker, otherMonths: false));
            db.MethodologyReadFields.Add(new MethodologyReadField(version, "QTY", otherMonths));
            await db.SaveChangesAsync();
        }

        var column = new ColumnDef(doc.TableDefId, EcrCode.Create("QTY_M01"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "q" }), 1, CellDataType.Decimal);
        column.SetMonth(true, 1);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(column, monthColumn);
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            doc.TemplateVersionId, 0, [], new Dictionary<int, ColumnDef> { [monthColumn] = column },
            new Dictionary<(int, string), RowDef>()));
        var layouts = Substitute.For<ITemplateLayoutRegistry>();
        layouts.HasAny.Returns(true);
        layouts.IsYearVersion(doc.TemplateVersionId).Returns(true);
        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAfterEditAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns("job");

        await using (var db = builder.CreateContext())
        {
            var enqueued = await new EditAutoRecalculation(db, trigger, metadata: metadata, layouts: layouts).OnEditedAsync(
                doc.DocumentId, [202601], new HashSet<int> { doc.TemplateVersionId },
                new Dictionary<int, IReadOnlySet<int>> { [202601] = new HashSet<int> { monthColumn } }, CancellationToken.None);
            Assert.Equal(otherMonths ? 3 : 1, enqueued);
        }
    }

    /// <summary>
    /// ✎ Огляд L2: ворота без бази (кеш прив'язаних таблиць + знімки версій) дивляться версію КОЖНОГО періоду правки: правка
    /// двох періодів, де методологія прив'язана лише до таблиці версії другого, ставить автоперерахунок; без неї — ні.
    /// </summary>
    /// <remarks>Мутація (локально): у воротах брати лише першу версію (<c>versions.Take(1)</c>) — червоний перший рядок.</remarks>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public async Task L2_ворота_без_бази_дивляться_версію_кожного_періоду(bool secondPeriodBound, int expected)
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: Period.Value);
        await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202605));
        if (secondPeriodBound)
        {
            await BindAsync(builder, doc);
        }

        const int unboundVersion = -977_001; // менший за будь-який справжній Id: «лише перша версія» взяла б саме її
        const int unboundTable = 977_002;
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(unboundVersion, Arg.Any<CancellationToken>()).Returns(SnapshotWithTable(unboundVersion, unboundTable));
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(SnapshotWithTable(doc.TemplateVersionId, doc.TableDefId));
        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAfterEditAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns("job");

        await using var db = builder.CreateContext();
        var edit = new EditAutoRecalculation(db, trigger, boundTables: new BoundTablesCache(), metadata: metadata);
        Assert.Equal(expected, await edit.OnEditedAsync(
            doc.DocumentId, [Period.Value, 202605], new HashSet<int> { unboundVersion, doc.TemplateVersionId }, CancellationToken.None));
    }

    private static TemplateVersionSnapshot SnapshotWithTable(int versionId, int tableId)
    {
        var sheet = new SheetDef(versionId, EcrCode.Create($"L2S{tableId}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "s" }), 1);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(sheet, tableId + 1);
        var table = new TableDef(tableId + 1, EcrCode.Create($"L2T{tableId}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "t" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(table, tableId);
        sheet.AddTable(table);
        return new TemplateVersionSnapshot(versionId, 0, [sheet], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>());
    }

    private static async Task<int> BindAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();
        var methodology = new Methodology(
            EcrCode.Create($"EB_{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();
        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MethodologyEngine.Script);
        version.Publish(2, "review", new DateOnly(2026, 1, 1), testsPassed: true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.MethodologyVersions.Add(version);
        db.CalculationBindings.Add(new CalculationBinding(doc.TableDefId, doc.ColumnDefIds[^1], methodology.Id, "E_CO2", "{}"));
        await db.SaveChangesAsync();
        return version.Id;
    }

    private async Task<string> PayloadAsync(string jobId)
    {
        await using var db = Sql.CreateContext();
        return (await db.JobProgresses.AsNoTracking().SingleAsync(p => p.JobId == jobId)).Payload!;
    }

    private async Task<TimeSpan> AvailableInAsync(string jobId)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DATEDIFF_BIG(millisecond, CAST(SYSUTCDATETIME() AS datetime2(3)), AvailableAt) FROM itg.JobProgress WHERE JobId = @id;";
        command.Parameters.AddWithValue("@id", jobId);
        return TimeSpan.FromMilliseconds((long)(await command.ExecuteScalarAsync())!);
    }

    private static CalculationTrigger Trigger(Host host, PeriodState state)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(state);

        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetSheetsAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApprovalState>());

        var documents = Substitute.For<IDocumentStore>();
        documents.GetIncludedSheetIdsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var sheetCopies = Substitute.For<IDocumentSheetCopyStore>();
        sheetCopies.ListGroupsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SheetCopyGroupInfo>());

        var scheduler = new DbBackgroundJobScheduler(
            host.Queue, new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()), new JobQueueSignal());
        return new CalculationTrigger(scheduler, periods, workflow, documents, sheetCopies);
    }
}
