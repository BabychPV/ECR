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
