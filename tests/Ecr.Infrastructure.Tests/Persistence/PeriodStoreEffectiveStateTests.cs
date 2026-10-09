using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// X6-01: <c>IPeriodStore.FindPeriodStateAsync</c> повертає ЕФЕКТИВНИЙ стан періоду —
/// той самий, що бачить рішення про запис (F-08), а не збережений.
/// </summary>
/// <remarks>
/// ⛔ Доти це була проєкція <c>period.State</c>, який просуває лише годинна задача
/// станів (о :05). Від межі <c>Grace</c> до її прогону доступ уже пускав правку як
/// пізню, а <c>IsLateEdit</c> (D-70) писав <c>0</c>. Тут — справжній SQL і
/// справжні межі (<c>RecomputeBoundaries</c>), задача станів НЕ запускається.
/// </remarks>
[Collection("SqlServer")]
public sealed class PeriodStoreEffectiveStateTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-70")]
    public async Task Після_межі_Grace_до_задачі_станів_стан_Grace()
    {
        var (doc, bounds) = await ArrangeAsync(activate: true, PeriodState.Open);

        var state = await FindAsync(doc, new TestClock(bounds.GraceAt.AddMinutes(1)));

        Assert.Equal(PeriodState.Grace, state);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-70")]
    public async Task До_межі_Grace_стан_збережений()
    {
        var (doc, bounds) = await ArrangeAsync(activate: true, PeriodState.Open);

        var state = await FindAsync(doc, new TestClock(bounds.GraceAt.AddMinutes(-1)));

        Assert.Equal(PeriodState.Open, state);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-70")]
    public async Task Після_межі_закриття_збережений_Grace_стає_Closed()
    {
        var (doc, bounds) = await ArrangeAsync(activate: true, PeriodState.Grace);

        var state = await FindAsync(doc, new TestClock(bounds.CloseAt.AddMinutes(1)));

        Assert.Equal(PeriodState.Closed, state);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-70")]
    public async Task Чернетка_проєкту_за_датами_не_просувається()
    {
        // ⚠ A7-25: періоди чернетки не відкриваються за датами — як у рішенні доступу.
        var (doc, bounds) = await ArrangeAsync(activate: false, PeriodState.Open);

        var state = await FindAsync(doc, new TestClock(bounds.GraceAt.AddMinutes(1)));

        Assert.Equal(PeriodState.Open, state);
    }

    private async Task<PeriodState?> FindAsync(TestDocument doc, TestClock clock)
    {
        await using var db = CreateContext();
        return await new PeriodStore(db, clock)
            .FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, CancellationToken.None);
    }

    /// <summary>Документ, межі періоду пораховано політикою проєкту, стан збережено як задано.</summary>
    private async Task<(TestDocument Doc, Bounds Bounds)> ArrangeAsync(bool activate, PeriodState stored)
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);

        await using var db = CreateContext();
        var project = await db.Projects.SingleAsync(p => p.Id == doc.ProjectId);
        var policy = await db.PeriodPolicies.SingleAsync(p => p.Id == project.PeriodPolicyId);
        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == doc.PeriodKey.Value);

        if (activate)
        {
            project.Activate(T0);
        }

        period.RecomputeBoundaries(policy, SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo());
        period.AdvanceTo(stored, T0);
        await db.SaveChangesAsync();

        // Передумова: вікно Grace непорожнє, інакше перевіряти нема чого.
        Assert.True(period.ComputedGraceAt < period.ComputedCloseAt);
        return (doc, new Bounds(period.ComputedGraceAt, period.ComputedCloseAt));
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed record Bounds(DateTime GraceAt, DateTime CloseAt);
}
