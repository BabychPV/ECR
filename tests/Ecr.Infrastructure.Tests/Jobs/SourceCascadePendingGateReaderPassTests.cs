// tests/Ecr.Infrastructure.Tests/Jobs/SourceCascadePendingGateReaderPassTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// F5 (огляд b9, п. 1): ворота подання й затвердження (<see cref="SourceCascadePendingGate"/>) бачать і ПРОХІД ЧИТАЧА —
/// окрему задачу <c>doc{id}-p{період}</c> (<c>foreignOwnerPass</c>), яку крок каскаду й прогін власника ставлять місяцям, що
/// читають їхні числа, поза планом ланцюжка. Постановка — справжнім <see cref="DbBackgroundJobScheduler"/> у справжню чергу:
/// ціль у базі має форму <c>{тип задачі}~{ціль}</c>, і доти ворота (порівнювали голі цілі) не бачили в Production жодної задачі.
/// </summary>
/// <remarks>
/// <para>
/// Шляхи, що ставлять прохід: злиття з натисканням «Перерахувати» місяця (обидва порядки) — позначка
/// <see cref="SourceCascadePendingGate.ReaderPassMergedField"/> у <c>DbJobQueue.MergePressFieldsSql</c>; поглинання при
/// поверненні виконуваного проходу в чергу (натискання позаду) — <c>DbJobQueue.AbsorbChainSql</c>.
/// </para>
/// <para>
/// МУТАЦІЯ (локально, не комітиться): <c>IsReaderPass</c> → <c>false</c> — червоні тести 1–3; позначку злиття не ставити —
/// червоні 2 і 3; ворота з голими цілями (як до огляду) — червоні 1–4.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "F5")]
[Trait("Decision", "D-567")]
public sealed class SourceCascadePendingGateReaderPassTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Period = 202603;

    private static long _doc = 9_300_000_000L + (Environment.ProcessId % 100_000 * 1000L);

    [Fact]
    public async Task Живий_прохід_читача_і_натискання_тримають_рік_документа_а_інший_документ_ні()
    {
        var doc = NextDocument();
        var other = NextDocument();
        await EnqueueAsync(doc, ReaderPass(doc, Period));
        await EnqueueAsync(other, Press(other, Period));

        Assert.True(await PendingAsync(doc, Period));

        // ✎ Огляд b11: прохід читача тримає ВЕСЬ рік документа (інший місяць теж); парно: інший документ — ні.
        Assert.True(await PendingAsync(doc, Period + 1));
        Assert.False(await PendingAsync(doc, Period + 100));
        Assert.False(await PendingAsync(NextDocument(), Period));
        Assert.True(await PendingAsync(other, Period)); // ✎ огляд b11-2 (A): натискання теж тримає рік

        // Виконується — так само тримає; закінчилась — ні.
        await SetStateAsync(doc, "Running");
        Assert.True(await PendingAsync(doc, Period));
        await SetStateAsync(doc, "Succeeded");
        Assert.False(await PendingAsync(doc, Period));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Прохід_що_злився_з_натисканням_тримає_місяць_у_будь_якому_порядку(bool pressFirst)
    {
        var doc = NextDocument();
        var first = await EnqueueAsync(doc, pressFirst ? Press(doc, Period) : ReaderPass(doc, Period));
        var second = await EnqueueAsync(doc, pressFirst ? ReaderPass(doc, Period) : Press(doc, Period));

        Assert.Equal(first, second); // одна задача на ціль
        Assert.True(await PendingAsync(doc, Period));

        // Злиття не змінює поведінки прогону: натискання лишається натисканням (черга «спершу ручні» читає foreignOwnerPass).
        var payload = (await RowAsync(first))!.Payload!;
        Assert.Contains(pressFirst ? "\"foreignOwnerPass\":false" : "\"foreignOwnerPass\":true", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Поглинутий_при_поверненні_прохід_читача_передає_позначку_натисканню_позаду()
    {
        var doc = NextDocument();
        await using var host = NewHost();
        var running = await EnqueueAsync(doc, ReaderPass(doc, Period));
        var claim = await host.ClaimLaneOfAsync<IRecalculationJob>();
        Assert.Equal(running, claim?.Claim.JobId);

        // Натискання позаду (виконувану не зливає — нова Queued).
        var behind = await EnqueueAsync(doc, Press(doc, Period));
        Assert.NotEqual(running, behind);

        // Прохід відкладено (лок документа тощо) — його поглинає натискання позаду.
        Assert.True(await host.Queue.DeferAsync(claim!.Claim, TimeSpan.Zero, CancellationToken.None));
        Assert.Equal("Cancelled", (await RowAsync(running))?.State);

        Assert.True(await PendingAsync(doc, Period));
        Assert.Contains($"\"{SourceCascadePendingGate.ReaderPassMergedField}\":true", (await RowAsync(behind))!.Payload!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Задача_каскаду_поставлена_справжнім_планувальником_тримає_місяць()
    {
        // Огляд b9: ціль у черзі — `IRecalculationJob~doc…-y…-dependents`; ворота з голою ціллю її не бачили.
        var doc = NextDocument();
        await using var db = Sql.CreateContext();
        await Scheduler(db).EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.SourceCascadeTargetOf(doc, Period / 100),
            new RecalculationRequest(0, doc, PeriodKey: null, TriggeredByUserId: null)
            {
                Year = Period / 100,
                RecalculateSubmitted = true,
                CascadePeriods = [Period],
            },
            CancellationToken.None);

        Assert.True(await PendingAsync(doc, Period));
        Assert.True(await PendingAsync(doc, Period - 1)); // ✎ огляд b11: увесь рік
        Assert.False(await PendingAsync(doc, Period + 100));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("не json", false)]
    [InlineData("[]", false)]
    [InlineData("""{"documentId":1,"periodKey":202603}""", false)]
    [InlineData("""{"documentId":1,"foreignOwnerPass":false}""", false)]
    [InlineData("""{"documentId":1,"foreignOwnerPass":true}""", true)]
    [InlineData("""{"documentId":1,"foreignOwnerPass":false,"readerPassMerged":true}""", true)]
    public void IsReaderPass_лише_прохід_чи_натискання_з_ним(string? payload, bool expected)
        => Assert.Equal(expected, SourceCascadePendingGate.IsReaderPass(payload));

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static long NextDocument() => Interlocked.Increment(ref _doc);

    private static RecalculationRequest Press(long doc, int period)
        => new(0, doc, period, TriggeredByUserId: 7);

    private static RecalculationRequest ReaderPass(long doc, int period)
        => new(0, doc, period, TriggeredByUserId: 7) { ForeignOwnerPass = true, RecalculateSubmitted = true };

    private static DbBackgroundJobScheduler Scheduler(EcrDbContext db)
        => new(
            new DbJobQueue(db, new SystemClock()),
            new QuartzJobScheduler(null, new JobProgressStore(db), new SystemClock()),
            new JobQueueSignal());

    /// <summary>Постановка тим самим шляхом, що прогін (<c>EnqueueReaderPassAsync</c>) і кнопка місяця.</summary>
    private async Task<string> EnqueueAsync(long doc, RecalculationRequest request)
    {
        await using var db = Sql.CreateContext();
        return await Scheduler(db).EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(doc, new PeriodKey(request.PeriodKey!.Value)), request, CancellationToken.None, 7);
    }

    private async Task<bool> PendingAsync(long doc, int period)
    {
        await using var db = Sql.CreateContext();
        return await new SourceCascadePendingGate(db).IsPendingAsync(doc, period, CancellationToken.None);
    }

    private async Task SetStateAsync(long doc, string state)
    {
        await using var db = Sql.CreateContext();
        var target = DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>(RecalculateDocumentHandler.TargetOf(doc, new PeriodKey(Period)));
        Assert.Equal(1, await db.JobProgresses
            .Where(p => p.TargetKey == target && (p.State == "Queued" || p.State == "Running"))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, state)));
    }
}
