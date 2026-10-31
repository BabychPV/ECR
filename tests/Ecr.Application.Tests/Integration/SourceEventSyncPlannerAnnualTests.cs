// tests/Ecr.Application.Tests/Integration/SourceEventSyncPlannerAnnualTests.cs
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// D-464: план синхронізації подій на РІЧНИХ періодах (звітність річна, PI SQL Client): вікно повної звірки — увесь
/// рік, поданий річний аркуш, подія, відкрита на момент закриття року, початок, що переїхав через межу року.
/// </summary>
/// <remarks>
/// Мутаційні докази, кожен — точковою правкою <c>SourceEventSyncPlanner.Decide</c>:
/// <list type="bullet">
/// <item>прибрати гілку <c>target.SheetLocked</c> — червоніє <see cref="Поданий_річний_аркуш_нуль_записів_і_зв_язки_без_змін"/>;</item>
/// <item>повернути <c>null</c> замість періоду для незакритої події — червоніє
/// <see cref="Незакрита_подія_закритого_року_несе_закритий_період"/>.</item>
/// </list>
/// </remarks>
public sealed class SourceEventSyncPlannerAnnualTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");

    private static readonly SourceEventPeriod Year2025 = new(
        202501, PeriodState.Closed, Period.UtcBounds(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), Atyrau));

    private static readonly SourceEventPeriod Year2026 = new(
        202601, PeriodState.Open, Period.UtcBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), Atyrau));

    private static readonly DateTime From = Year2026.Bounds.StartUtc;
    private static readonly DateTime To = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-464")]
    public void Річне_вікно_правка_за_пʼять_місяців_пишеться_а_видалена_подія_року_кандидат_на_видалення()
    {
        var may = new DateTime(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc);
        var february = new DateTime(2026, 2, 3, 8, 0, 0, DateTimeKind.Utc);

        var plan = SourceEventSyncPlanner.Plan(Input(
            [Ev("MAY", may, may.AddHours(1))],
            [Linked("MAY", "EF-MAY", may), Linked("FEB", "EF-FEB", february)],
            locked: false));

        var item = Assert.Single(plan.Items);
        Assert.Equal((SourceEventAction.Write, false, 202601), (item.Action, item.IsCreate, item.Target!.PeriodKey));
        Assert.Equal(["FEB"], plan.Gone!.Select(g => g.SourceEventId));
        Assert.Equal(["FEB"], plan.Missing.Select(g => g.SourceEventId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-464")]
    public void Поданий_річний_аркуш_нуль_записів_і_зв_язки_без_змін()
    {
        var march = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var june = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);

        var plan = SourceEventSyncPlanner.Plan(Input(
            [Ev("NEW", march, march.AddHours(1)), Ev("OLD", june, june.AddHours(2))],
            [Linked("OLD", "EF-OLD", june)],
            locked: true));

        Assert.All(plan.Items, i => Assert.Equal(SourceEventAction.SheetLocked, i.Action));
        Assert.DoesNotContain(plan.Items, i => i.IsCreate);
        Assert.Equal("EF-OLD", plan.Items.Single(i => i.Event.EventId == "OLD").RowKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-464")]
    public void Незакрита_подія_закритого_року_несе_закритий_період()
    {
        var december = new DateTime(2025, 12, 30, 8, 0, 0, DateTimeKind.Utc);

        var item = Assert.Single(SourceEventSyncPlanner.Plan(Input([Ev("LATE", december, null)], [], locked: false)).Items);

        Assert.Equal(SourceEventAction.Open, item.Action);
        Assert.Equal((202501, PeriodState.Closed), (item.Target!.PeriodKey, item.Target.State));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-464")]
    public void Початок_переїхав_через_межу_року_рядок_не_переноситься()
    {
        var december = new DateTime(2026, 12, 31, 20, 0, 0, DateTimeKind.Utc); // 1 січня 2027, 01:00 в Atyrau
        var link = Linked("E1", "EF-E1", new DateTime(2026, 12, 31, 10, 0, 0, DateTimeKind.Utc));

        var item = Assert.Single(SourceEventSyncPlanner.Plan(Input([Ev("E1", december, december.AddHours(1))], [link], locked: false)).Items);

        Assert.Equal((SourceEventAction.PeriodChanged, (SourceEventPeriodTarget?)null), (item.Action, item.Target));
    }

    private static SourceEventSyncInput Input(
        IReadOnlyList<SourceEvent> events, IReadOnlyList<SourceEventLinkState> links, bool locked)
        => new(
            From,
            To,
            events,
            false,
            null,
            links,
            start => SourceEventPeriods.Locate(start, [Year2025, Year2026]) is { } period
                ? new SourceEventPeriodTarget(
                    period.PeriodKey, period.State, period.PeriodKey == 202601 ? 26 : 25, locked && period.PeriodKey == 202601)
                : null);

    private static SourceEvent Ev(string id, DateTime start, DateTime? end)
        => new(id, "FlareEvent", "Flaring " + id, start, end, null, null, null, []);

    private static SourceEventLinkState Linked(string id, string rowKey, DateTime start)
        => new(id, "Flaring " + id, start, SourceEventLinkStatus.Synced, 202601, 26, rowKey);
}
