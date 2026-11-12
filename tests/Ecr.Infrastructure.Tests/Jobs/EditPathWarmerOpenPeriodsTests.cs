// tests/Ecr.Infrastructure.Tests/Jobs/EditPathWarmerOpenPeriodsTests.cs
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-190: <see cref="EditPathWarmer.OpenPeriodVersionsAsync"/> бере версії лише відкритих (<c>Open</c>/<c>Grace</c>)
/// періодів — <c>Scheduled</c> і <c>Closed</c> не гріються — найновіші першими й не більше стелі.
/// </summary>
/// <remarks>
/// ⚠ База спільна з іншими тестами колекції: перевіряються лише власні версії (входить / не входить) і форма
/// стелі, а не повний перелік. Мутація: прибрати <c>p.State IN (...)</c> — червоний (Scheduled/Closed входять).
/// </remarks>
[Collection("SqlServer")]
public sealed class EditPathWarmerOpenPeriodsTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лише_версії_відкритих_періодів_найновіші_першими_у_межах_стелі()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var open = await builder.BuildAsync();
        var scheduled = await builder.BuildAsync();
        var closed = await builder.BuildAsync();
        var grace = await builder.BuildAsync();

        await SetStateAsync(builder, open, PeriodState.Open);
        await SetStateAsync(builder, closed, PeriodState.Closed);
        await SetStateAsync(builder, grace, PeriodState.Grace);

        await using var db = builder.CreateContext();
        var all = await EditPathWarmer.OpenPeriodVersionsAsync(db, EditPathWarmer.MaxVersionsLimit, CancellationToken.None);

        Assert.Contains(open.TemplateVersionId, all);
        Assert.Contains(grace.TemplateVersionId, all);
        Assert.DoesNotContain(scheduled.TemplateVersionId, all);
        Assert.DoesNotContain(closed.TemplateVersionId, all);
        Assert.Equal(all.OrderDescending(), all);
        Assert.Equal(all.Distinct(), all);

        var one = await EditPathWarmer.OpenPeriodVersionsAsync(db, 1, CancellationToken.None);
        Assert.Equal([all[0]], one);
    }

    private static async Task SetStateAsync(TestDocumentBuilder builder, TestDocument document, PeriodState state)
    {
        await using var db = builder.CreateContext();
        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.AdvanceTo(state, At);
        await db.SaveChangesAsync();
    }
}
