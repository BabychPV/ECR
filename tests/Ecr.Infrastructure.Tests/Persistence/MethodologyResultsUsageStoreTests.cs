// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyResultsUsageStoreTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <see cref="MethodologyStore.AnyCalculationResultsAsync"/> на РЕАЛЬНОМУ SQL Server
/// (C12-03): чи «версією щось порахували» — підстава послабити гард <c>strictBackdated</c>.
/// </summary>
/// <remarks>
/// Мутація: прибрати `WHERE MethodologyVersionId = …` у запиті — «без результатів» стає
/// «з результатами» (або навпаки), і один із двох перевірочних випадків червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyResultsUsageStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.9")]
    public async Task Версія_з_рядком_результату_відрізняється_від_версії_без_нього()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        await using var db = chain.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var methodology = new Methodology(
            EcrCode.Create($"MRU_{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync(CancellationToken.None);

        var used = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        var unused = new MethodologyVersion(methodology.Id, "2.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.AddRange(used, unused);
        await db.SaveChangesAsync(CancellationToken.None);

        // Прогін НЕ поточний: «будь-який статус» — слід лишається, хоч прогін витіснено.
        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync(CancellationToken.None);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {used.Id},
                    {document.PeriodKey.Value}, {document.DocumentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
            """);

        var store = new MethodologyStore(db);

        Assert.True(await store.AnyCalculationResultsAsync([used.Id], CancellationToken.None));
        Assert.False(await store.AnyCalculationResultsAsync([unused.Id], CancellationToken.None));
        Assert.True(await store.AnyCalculationResultsAsync([unused.Id, used.Id], CancellationToken.None));
        Assert.False(await store.AnyCalculationResultsAsync([], CancellationToken.None));
    }
}
