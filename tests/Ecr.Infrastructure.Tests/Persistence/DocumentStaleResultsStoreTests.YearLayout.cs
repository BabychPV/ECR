using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ LAND-YEAR B24: картка документа річної розкладки (<c>YearWithMonthColumns</c>) показує рік — перегляд = якір
/// (202601), а прогони методологій і правки місячних входів лежать у місяцях-власниках. <c>resultsStale</c> — АБО за
/// місяцями року, «відколи» — найраніший момент.
/// </summary>
/// <remarks>
/// ⛔ Мутація (локально, не комітити): у <c>DocumentStore.FindAsync</c> завжди брати <c>StaleResultsDirectAsync</c> —
/// річний тест червоніє (картка якоря «не застаріла»), контроль PerMonthCopies зелений.
/// </remarks>
public sealed partial class DocumentStaleResultsStoreTests
{
    private const int YearAnchor = 202601;
    private const int YearMonth = 202609;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task B24_річна_розкладка_картка_якоря_застаріла_коли_застарів_вересень()
    {
        var (db, chain) = await YearChainAsync(TemplateLayout.YearWithMonthColumns);
        await using var _ = db;
        try
        {
            var version = await VersionAsync(db);
            await MonthRunAsync(db, chain, version, YearMonth, Now);
            Assert.False((await CardAsync(db, chain, YearAnchor)).ResultsStale);

            var editedAt = Now.AddHours(1);
            await EditAsync(chain.DocumentId, YearMonth, chain.ColumnDefIds[1], editedAt);

            var card = await CardAsync(db, chain, YearAnchor);
            Assert.True(card.ResultsStale);
            Assert.True(Math.Abs((card.ResultsStaleSince!.Value - editedAt).TotalSeconds) < 1, $"{card.ResultsStaleSince:O}");
            Assert.Equal(DateTimeKind.Utc, card.ResultsStaleSince.Value.Kind);
        }
        finally
        {
            await ReleaseMonthRunsAsync(db, chain);
        }
    }

    /// <summary>Контроль: версія <c>PerMonthCopies</c> — картка періоду судить лише свій період, як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task B24_контроль_PerMonthCopies_картка_січня_не_бачить_застарілого_вересня()
    {
        var (db, chain) = await YearChainAsync(TemplateLayout.PerMonthCopies);
        await using var _ = db;
        try
        {
            var version = await VersionAsync(db);
            await MonthRunAsync(db, chain, version, YearMonth, Now);
            await EditAsync(chain.DocumentId, YearMonth, chain.ColumnDefIds[1], Now.AddHours(1));

            Assert.False((await CardAsync(db, chain, YearAnchor)).ResultsStale);
            Assert.True((await CardAsync(db, chain, YearMonth)).ResultsStale);
        }
        finally
        {
            await ReleaseMonthRunsAsync(db, chain);
        }
    }

    /// <summary>
    /// Ізоляція: актуальний прогін МІСЯЦЯ (202609) цього тесту не лишається в спільній базі колекції «SqlServer».
    /// </summary>
    /// <remarks>
    /// ⛔ Прогрів карти застарілості (<c>StaleMapWarmer.HotPeriodsAsync</c>) бере ДВА найновіші періоди з актуальним
    /// прогоном по ВСІЙ базі. Лишений тут актуальний 202609 разом із 202602 від <c>DocumentYearStaleResultsStoreTests</c>
    /// витісняв 202601 тестів прогріву (<c>Прогрів_…</c>, CI 43411406990, core-persistence) — їхні підрахунки залежали
    /// від порядку класів. Прогін знімається з актуальних (Superseded), а не видаляється: результати тесту йому не потрібні.
    /// </remarks>
    private static async Task ReleaseMonthRunsAsync(EcrDbContext db, TestDocument chain)
        => await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE calc.CalculationRun SET Status = N'Superseded' WHERE ProjectId = {chain.ProjectId} AND PeriodKey = {YearMonth} AND Status = N'Current';");

    private async Task<(EcrDbContext Db, TestDocument Chain)> YearChainAsync(TemplateLayout layout)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(periodKey: YearAnchor, ct: CancellationToken.None);
        await builder.AddPeriodAsync(chain, new PeriodKey(YearMonth));

        var db = builder.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cfg.TemplateVersion SET Layout = {(int)layout} WHERE Id = {chain.TemplateVersionId};");
        return (db, chain);
    }

    /// <summary>Актуальний прогін МІСЯЦЯ <paramref name="periodKey"/> з числом документа.</summary>
    private static async Task MonthRunAsync(
        EcrDbContext db, TestDocument chain, int versionId, int periodKey, DateTime startedAt)
    {
        var run = new CalculationRun(chain.ProjectId, periodKey, triggeredByUserId: null, startedAt, chain.DocumentId);
        run.Complete("Succeeded", startedAt.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync(CancellationToken.None);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {versionId},
                    {periodKey}, {chain.DocumentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
            """);
    }
}
