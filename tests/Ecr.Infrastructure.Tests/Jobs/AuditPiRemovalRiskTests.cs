// tests/Ecr.Infrastructure.Tests/Jobs/AuditPiRemovalRiskTests.cs
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ Аудит PI 31.10 (G2, G4): запобіжники від хибних видалень — повна звірка подій EF (власний шаблон порожній;
/// зникли б усі рядки мапінгу) і очищення комірки лише після справжнього видалення точок (D-462). Спершу — репро
/// (червоні на 332946e80), тепер — сторожі.
/// </summary>
public sealed class AuditPiRemovalRiskTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly SourceEventPeriod January = new(
        202601, PeriodState.Open, Period.UtcBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), Atyrau));

    /// <summary>
    /// Own template returns ZERO events (successful, not truncated, no error) while a sibling template returns
    /// anything: SourceEmpty is false, every linked row of the map is Gone, and with at most 10 linked rows the
    /// mass-removal limit does not trigger — all rows would be hard-deleted without confirmation.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "audit-PI-31.10")]
    public void Own_template_empty_but_sibling_nonempty_small_map_is_fully_deleted_without_confirmation()
    {
        var links = Enumerable.Range(1, 8)
            .Select(i => new SourceEventLinkState(
                $"E{i}", "Flaring", From.AddDays(i), SourceEventLinkStatus.Synced, 202601, 10, $"EF-E{i}"))
            .ToList();

        var plan = SourceEventSyncPlanner.Plan(new SourceEventSyncInput(
            From, To, [], Truncated: false, ErrorCode: null, links,
            start => SourceEventPeriods.Locate(start, [January]) is { } p
                ? new SourceEventPeriodTarget(p.PeriodKey, p.State, 10, false)
                : null,
            OtherTemplateIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SIBLING-EVENT" }));

        Assert.Equal(8, plan.Gone!.Count);

        // EXPECTED (safe): 100 % of the map's rows vanishing must be blocked — either as SourceEmpty or by the
        // mass-removal limit. ACTUAL: SourceEmpty=false and ExceedsRemovalLimit(8, 8)=false -> silent deletion.
        var blocked = plan.SourceEmpty || SourceEventSyncJob.ExceedsRemovalLimit(plan.Gone.Count, links.Count, out _);
        Assert.True(blocked, "all 8 linked rows would be hard-deleted without confirmation");
    }

    /// <summary>Single-template map: 10 of 10 rows vanish in a non-empty response, removed without confirmation.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "audit-PI-31.10")]
    public void Ten_of_ten_rows_vanish_with_one_unrelated_event_returned_no_confirmation_needed()
    {
        // EXPECTED (safe): losing 100 % of linked rows needs confirmation. ACTUAL: limit = max(10, 20 %) -> allowed.
        Assert.True(SourceEventSyncJob.ExceedsRemovalLimit(10, 10, out _), "10 of 10 rows removable without confirmation");
    }

    /// <summary>
    /// D-462(6): FoldMap clears the cell whenever the window holds zero raw points — it cannot tell
    /// "all points deleted from PI" from "raw points never existed for this field/period" (new map on an
    /// existing cell, history older than the catch-up horizon). EXPECTED (safe): no-history is NoData, not Clear.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "audit-PI-31.10")]
    public void FoldMap_clears_cell_when_period_never_had_raw_points()
    {
        var range = new Period.UtcRange(From, To);

        var outcome = CollectedFold.FoldMap(
            Ecr.Domain.Entities.External.AggregationKind.Sum, false, null, [], default, range, null);

        Assert.NotEqual(FoldOutcomeKind.Clear, outcome.Kind);

        // D-462 як і доти: точки вікна справді видалено в PI — комірку очищують.
        var removed = CollectedFold.FoldMap(
            Ecr.Domain.Entities.External.AggregationKind.Sum, false, null, [], default, range, null, pointsRemoved: true);
        Assert.Equal(FoldOutcomeKind.Clear, removed.Kind);
    }
}
