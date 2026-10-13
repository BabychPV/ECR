// tests/Ecr.Infrastructure.Tests/Jobs/CrossPeriodFanOutTests.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// WW-03 (G2): після зміни періоду перерахунок ставиться в періоди-читачі — coalesced, без закритих,
/// з обмеженим ланцюжком.
/// </summary>
/// <remarks>
/// ⛔ Що було: правка періоду k не перераховувала періоди, що її читають (`[Period:-1]` лютого після правки
/// січня; 7.0 періоду m після правки Water Report місяця k) — число лишалося старим без жодної помилки.
/// Постановка — <c>EnqueueCoalescedAsync</c> (НЕ <c>EnqueueExclusive</c>: той витісняє задачу, що виконується).
/// </remarks>
[Collection("SqlServer")]
public sealed class CrossPeriodFanOutTests(SqlServerFixture sql)
{
    private const int Version = 1;
    private const int ColumnDefId = 9001;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();

    private readonly List<(string Target, string PayloadJson)> _enqueued = [];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WW-03")]
    public async Task Правка_періоду_1_ставить_читачів_2_відкритий_і_пропускає_3_закритий()
    {
        var (document, builder) = await ArrangeAsync();
        Arrange(document, Dep(5, 1));

        await using var db = builder.CreateContext();
        var fanOut = new CrossPeriodFanOut(_rows, _versions, _periods, _jobs, db);

        var count = await fanOut.EnqueueReadersAsync(
            document.DocumentId, new PeriodKey(202601), [(document.TableDefId, ColumnDefId)],
            onlyEarlierReaders: false, hops: 1, CancellationToken.None);

        Assert.Equal(1, count);
        var queued = Assert.Single(_enqueued);
        Assert.Equal(FormulaRecalculationTarget.Of(document.DocumentId, 202602, createdByUserId: null), queued.Target);

        // Елемент `cells` — «колонка таблиці змінилась у періоді 1», не адреса комірки; ланка = 1.
        using var json = JsonDocument.Parse(queued.PayloadJson);
        var cell = Assert.Single(json.RootElement.GetProperty("Cells").EnumerateArray().ToList());
        Assert.Equal(0, cell.GetProperty("RowId").GetInt32());
        Assert.Equal(document.TableDefId, cell.GetProperty("TableDefId").GetInt32());
        Assert.Equal(ColumnDefId, cell.GetProperty("ColumnDefId").GetInt32());
        Assert.Equal(1, cell.GetProperty("SourcePeriod").GetInt32());
        Assert.Equal(1, cell.GetProperty("Hops").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WW-03")]
    public async Task Колонка_яку_ніхто_не_читає_з_іншого_періоду_нічого_не_ставить()
    {
        var (document, builder) = await ArrangeAsync();
        Arrange(document, Dep(5, 1));

        await using var db = builder.CreateContext();
        var fanOut = new CrossPeriodFanOut(_rows, _versions, _periods, _jobs, db);

        var count = await fanOut.EnqueueReadersAsync(
            document.DocumentId, new PeriodKey(202601), [(document.TableDefId, ColumnDefId + 1)],
            onlyEarlierReaders: false, hops: 1, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_enqueued);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WW-03")]
    public async Task Ланцюжок_читачів_обмежений_щоб_розбіжний_цикл_не_ставив_задачі_безкінечно()
    {
        var (document, builder) = await ArrangeAsync();
        Arrange(document, Dep(5, 1));

        await using var db = builder.CreateContext();
        var fanOut = new CrossPeriodFanOut(_rows, _versions, _periods, _jobs, db);

        var count = await fanOut.EnqueueReadersAsync(
            document.DocumentId, new PeriodKey(202601), [(document.TableDefId, ColumnDefId)],
            onlyEarlierReaders: false, CrossPeriodFanOut.MaxHops + 1, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_enqueued);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WW-03")]
    public async Task Після_повного_перерахунку_року_ставляться_лише_читачі_раніше_за_джерело()
    {
        // Період 2 змінився; формули читають #2. Читачі: 1 (раніше — стале після прогону за зростанням)
        // і 3 (пізніше — уже пораховано від остаточного джерела).
        var (document, builder) = await ArrangeAsync();
        Arrange(document, Dep(5, 2));
        _periods.FindPeriodStateAsync(document.DocumentId, 202601, Arg.Any<CancellationToken>())
            .Returns(PeriodState.Open);

        await using var db = builder.CreateContext();
        var fanOut = new CrossPeriodFanOut(_rows, _versions, _periods, _jobs, db);

        var count = await fanOut.EnqueueReadersAsync(
            document.DocumentId, new PeriodKey(202602), [(document.TableDefId, ColumnDefId)],
            onlyEarlierReaders: true, hops: 1, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(
            FormulaRecalculationTarget.Of(document.DocumentId, 202601, createdByUserId: null),
            Assert.Single(_enqueued).Target);
    }

    private static FormulaDependency Dep(byte kind, int offset)
        => FormulaDependency.ForFormula(555, kind, tableDefId: null, "R1", ColumnDefId, null, (short)offset, 0);

    private void Arrange(TestDocument document, FormulaDependency dependency)
    {
        // Залежність адресує таблицю документа: `TableDefId` лише відомий після побудови документа.
        var edge = FormulaDependency.ForFormula(
            555, dependency.DependsOnKind, document.TableDefId, "R1", ColumnDefId, null, dependency.PeriodOffset, 0);

        _rows.GetTableInstancesAsync(document.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<TableInstanceRef>)
                [new TableInstanceRef(1, document.DocumentId, document.TableDefId, Version, call.ArgAt<PeriodKey>(1).Value)]);
        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>())
            .Returns([edge]);

        _periods.FindPeriodStateAsync(document.DocumentId, 202602, Arg.Any<CancellationToken>())
            .Returns(PeriodState.Open);
        _periods.FindPeriodStateAsync(document.DocumentId, 202603, Arg.Any<CancellationToken>())
            .Returns(PeriodState.Closed);

        _jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                _enqueued.Add((call.ArgAt<string>(0), JsonSerializer.Serialize(call.ArgAt<object?>(1))));
                return "job";
            });
    }

    /// <summary>Документ із екземплярами таблиць у періодах 2026-01 (відкритий), 2026-02 (відкритий), 2026-03 (закритий).</summary>
    private async Task<(TestDocument Document, TestDocumentBuilder Builder)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var setup = builder.CreateContext();
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);

        var january = await setup.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == 202601);
        january.AdvanceTo(PeriodState.Open, DateTime.UtcNow);

        foreach (var (sequence, state) in new[] { (2, PeriodState.Open), (3, PeriodState.Closed) })
        {
            var key = new PeriodKey(202600 + sequence);
            var period = new Period(
                document.ProjectId, key, (byte)sequence,
                new DateOnly(2026, sequence, 1), new DateOnly(2026, sequence, 28));
            period.AdvanceTo(state, DateTime.UtcNow);
            setup.Periods.Add(period);

            var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            setup.TableInstances.Add(new TableInstance(
                key, instanceId, document.DocumentId, document.TableDefId, DateTime.UtcNow));
        }

        await setup.SaveChangesAsync();

        return (document, builder);
    }
}
