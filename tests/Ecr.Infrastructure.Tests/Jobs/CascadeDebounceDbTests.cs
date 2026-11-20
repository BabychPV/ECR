// tests/Ecr.Infrastructure.Tests/Jobs/CascadeDebounceDbTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
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
/// ✎ PSS-07 (аудит 20.11, <c>D-561</c>, рішення людини: «затримка каскаду — максимум 15 с»): перерахунок залежних —
/// каскад від зміни іншого документа чи довідника — стає доступним через <see cref="CalculationTrigger.CascadeDebounce"/>,
/// і завершення кількох джерел за це вікно дають ОДИН перерахунок споживача за період. Пряма правка й автоперерахунок
/// (<c>-auto</c>, D-174) та «з затвердженими» (дія адміністратора) — без відкладення (ФВ-12.2).
/// </summary>
/// <remarks>
/// Без очікування вікна: «доступність» читається з <c>AvailableAt</c> проти годинника СУБД і перевіряється claim'ом
/// «зараз». Між постановками — справжні паузи (200 мс), щоб довести, що вікно від ПЕРШОЇ не зсувається.
/// <para>
/// МУТАЦІЇ (локально): (1) <c>CascadeDebounce = 0</c> — червоні <c>Пять_завершень…</c> (claim «зараз» бере задачу) і
/// <c>Відкладення_каскаду…</c>; (2) <c>CascadeDebounce = 30 с</c> — червоний <c>Відкладення_каскаду_не_більше_15_с</c>;
/// (3) відкладення і для «з затвердженими» чи <c>-auto</c> — червоний <c>Пряма_зміна…</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PSS-07")]
[Trait("Requirement", "ФВ-12.2")]
public sealed class CascadeDebounceDbTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const long Consumer = 9_400_001;

    private static readonly PeriodKey Period = new(202603);

    [Fact]
    public async Task Пять_завершень_джерел_за_вікно_дають_один_перерахунок_споживача()
    {
        await using var host = NewHost();
        var trigger = Trigger(host);

        var first = await trigger.RequestDependentsAsync(Consumer, Period, sourceHop: 1, CancellationToken.None);
        Assert.NotNull(first);
        var availableAt = (await RowAsync(first!))!.AvailableAt;

        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(200);
            Assert.Equal(first, await trigger.RequestDependentsAsync(Consumer, Period, sourceHop: 1, CancellationToken.None));
        }

        // Одна задача на ціль, вікно від ПЕРШОЇ постановки не подовжилось.
        await using (var db = Sql.CreateContext())
        {
            Assert.Equal(1, await db.JobProgresses.CountAsync(p => p.Lane == JobLanes.Recalc && p.State == "Queued"));
        }

        Assert.Equal(availableAt, (await RowAsync(first!))!.AvailableAt);

        // До кінця вікна claim її не бере — п'ять завершень джерел не встигли б дати п'ять прогонів.
        Assert.Null(await host.Queue.ClaimAsync([JobLanes.Recalc], "test/host", JobQueueLimits.DefaultLease, CancellationToken.None));
    }

    [Fact]
    public async Task Відкладення_каскаду_не_більше_15_с_і_не_нуль()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), CalculationTrigger.MaxCascadeDebounce);
        Assert.InRange(CalculationTrigger.CascadeDebounce, TimeSpan.FromSeconds(1), CalculationTrigger.MaxCascadeDebounce);

        await using var host = NewHost();
        var jobId = await Trigger(host).RequestDependentsAsync(Consumer, Period, sourceHop: 1, CancellationToken.None);
        var delay = await AvailableInAsync(jobId!);

        // Годинник СУБД: між постановкою й читанням минають мілісекунди — нижня межа з запасом у 2 с.
        Assert.InRange(delay, CalculationTrigger.CascadeDebounce - TimeSpan.FromSeconds(2), CalculationTrigger.MaxCascadeDebounce);
    }

    [Fact]
    public async Task Пряма_зміна_авто_і_з_затвердженими_не_відкладаються()
    {
        await using var host = NewHost();
        var trigger = Trigger(host);

        // D-174: автоперерахунок після збору PI / правки джерела — сам документ, не каскад.
        var auto = await trigger.RequestAsync(Consumer, Period, CancellationToken.None);
        Assert.True(await AvailableInAsync(auto!) <= TimeSpan.Zero);

        // «З затвердженими» — явна дія адміністратора.
        var approved = await trigger.RequestDependentsAsync(
            Consumer + 1, Period, sourceHop: 1, new ApprovedRecalculationOverride(9, "Акт 12"), CancellationToken.None);
        Assert.True(await AvailableInAsync(approved!) <= TimeSpan.Zero);

        var claimed = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var job = await host.Queue.ClaimAsync([JobLanes.Recalc], "test/host", JobQueueLimits.DefaultLease, CancellationToken.None);
            claimed.Add(job!.Claim.JobId);
        }

        Assert.Equal(new[] { auto, approved }.Order(), claimed.Order());
    }

    [Fact]
    public async Task Постановка_без_відкладення_на_ціль_відкладеного_каскаду_робить_його_доступним_одразу()
    {
        // Відкладена постановка (каскад) і, поки вона чекає вікна, пряма постановка на ту саму ціль (кнопка
        // «Перерахувати»): злиття лишає раніший момент — людина не чекає вікна каскаду.
        await using var host = NewHost();
        var scheduler = Scheduler(host);
        var target = Ecr.Application.Documents.RecalculateDocumentHandler.TargetOf(Consumer, Period);
        var cascade = await scheduler.EnqueueCoalescedDelayedAsync<IRecalculationJob>(
            target, new { DocumentId = Consumer, PeriodKey = Period.Value, RecalculateSubmitted = true },
            CalculationTrigger.CascadeDebounce, CancellationToken.None);
        Assert.True(await AvailableInAsync(cascade) > TimeSpan.FromSeconds(5));

        var manual = await scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
            target, new { DocumentId = Consumer, PeriodKey = Period.Value }, CancellationToken.None, createdByUserId: 7);

        Assert.Equal(cascade, manual);
        Assert.True(await AvailableInAsync(cascade) <= TimeSpan.Zero);
    }

    /// <summary>Скільки ще до доступності задачі за годинником СУБД (від'ємне — уже доступна).</summary>
    private async Task<TimeSpan> AvailableInAsync(string jobId)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DATEDIFF_BIG(millisecond, SYSUTCDATETIME(), AvailableAt) FROM itg.JobProgress WHERE JobId = @id;";
        command.Parameters.AddWithValue("@id", jobId);
        return TimeSpan.FromMilliseconds((long)(await command.ExecuteScalarAsync())!);
    }

    private static DbBackgroundJobScheduler Scheduler(Host host)
        => new(host.Queue, new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()), new JobQueueSignal());

    private static CalculationTrigger Trigger(Host host)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(PeriodState.Open);

        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetSheetsAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApprovalState>());

        var documents = Substitute.For<IDocumentStore>();
        documents.GetIncludedSheetIdsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var sheetCopies = Substitute.For<IDocumentSheetCopyStore>();
        sheetCopies.ListGroupsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SheetCopyGroupInfo>());

        return new CalculationTrigger(Scheduler(host), periods, workflow, documents, sheetCopies);
    }
}
