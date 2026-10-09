using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Перенос результатів у новий прогін (<see cref="CalculationResultStore.CarryOverResultsAsync"/>) на справжній базі:
/// N2-04 — стеля без мовчазного обрізання; N2-03 — перенесені числа не гасять застарілість довідника.
/// </summary>
/// <remarks>
/// Мутаційні докази: повернути <c>Take(MaxResults)</c> замість <c>CountAsync</c> і відмови —
/// <see cref="Понад_стелю_відмова_а_не_обрізання"/> червоніє (відмови немає, рядки обрізано); прибрати
/// <c>run.LimitInputsAsOf</c> з <c>CarryOverResultsAsync</c> — <see cref="Перенесені_результати_не_гасять_застарілість_довідника"/>
/// і <see cref="Ланцюжок_переносів_не_губить_найдавніший_момент_входів"/> червоніють; повернути <c>r.StartedAt</c> замість
/// <c>COALESCE(r.InputsAsOfUtc, r.StartedAt)</c> у <c>StaleResultsQuery</c> / <c>MethodologyStore</c> — перший тест червоніє
/// на відповідній половині.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationResultCarryOverTests(SqlServerFixture sql)
{
    /// <summary>Старт прогону-джерела (T1).</summary>
    private static readonly DateTime T1 = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Правка довідника (T2): після T1 і до старту нового прогону.</summary>
    private static readonly DateTime T2 = T1.AddHours(1);

    /// <summary>Старт нового прогону (T3): після правки довідника.</summary>
    private static readonly DateTime T3 = T1.AddHours(2);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Понад_стелю_відмова_а_не_обрізання()
    {
        var f = await ArrangeAsync(seedResult: false);
        await using var db = f.Builder.CreateContext();

        // Одним set-based INSERT: 50 001 рядок через EF тут тривав би хвилини.
        await InsertResultsAsync(db, f, f.SourceRunId, CalculationResultStore.CarryOverMaxResults + 1);

        var target = await StartRunAsync(db, f, T3);
        var store = new CalculationResultStore(db, new TestClock(T3));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => store.CarryOverResultsAsync(target.Id, f.Document.DocumentId, [f.MethodologyId], CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.carryOverTooLarge", error.Details!["messageKey"]);
        Assert.Equal(CalculationResultStore.CarryOverMaxResults + 1, error.Details["count"]);
        Assert.Equal(CalculationResultStore.CarryOverMaxResults, error.Details["max"]);

        // Нічого не перенесено — ні частини: прогін упаде цілком, а попередній лишиться актуальним.
        await db.SaveChangesAsync();
        Assert.Equal(0, await db.CalculationResults.AsNoTracking().CountAsync(r => r.CalculationRunId == target.Id));
        Assert.Equal(
            CalculationRun.CurrentStatus,
            await db.CalculationRuns.AsNoTracking().Where(r => r.Id == f.SourceRunId).Select(r => r.Status).SingleAsync());
    }

    /// <remarks>
    /// N2-03 / stale-for-B (D-324): правка довідника T2 між прогоном-джерелом (T1) і новим прогоном (T3), що ПЕРЕНІС
    /// результати джерела. Числа лишилися на довіднику T1, тож позначка застарілості не гасне — ні в панелі
    /// результатів (<c>MethodologyStore</c>), ні в переліку (<c>StaleResultsQuery</c>). Контроль: прогін, що
    /// ПЕРЕРАХУВАВ ті самі числа після T2 (без переносу), застарілість знімає.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перенесені_результати_не_гасять_застарілість_довідника()
    {
        var f = await ArrangeAsync();
        await using var db = f.Builder.CreateContext();

        var carried = await StartRunAsync(db, f, T3);
        var store = new CalculationResultStore(db, new TestClock(T3));

        Assert.Equal(1, await store.CarryOverResultsAsync(carried.Id, f.Document.DocumentId, [f.MethodologyId], CancellationToken.None));
        await store.SwitchCurrentRunAsync(carried.Id, "{}", CancellationToken.None);
        await db.SaveChangesAsync();

        // Перенесений прогін мірить входи від прогону-джерела, а не від власного старту.
        Assert.Equal(T1, await InputsAsOfAsync(db, carried.Id));

        await using var read = f.Builder.CreateContext();
        var freshness = await new MethodologyStore(read).GetCalculationFreshnessAsync(
            f.Document.DocumentId, f.Document.PeriodKey.Value, tableDefIds: null, CancellationToken.None);
        Assert.True(freshness.IsStale);
        Assert.Equal([f.RegistryCode], freshness.ChangedRegistryCodes);

        var stale = await StaleResultsQuery.Documents(read, f.Document.PeriodKey.Value).ToListAsync();
        Assert.Equal(T2, Assert.Single(stale, r => r.DocumentId == f.Document.DocumentId).Since);

        // Контроль: прогін, що перерахував числа ПІСЛЯ T2 (без переносу), застарілість знімає.
        var recomputed = await StartRunAsync(db, f, T3.AddHours(1));
        await new CalculationResultStore(db, new TestClock(T3.AddHours(1))).WriteResultsAsync(
            recomputed.Id, [Output(f)], CancellationToken.None);
        await new CalculationResultStore(db, new TestClock(T3.AddHours(1)))
            .SwitchCurrentRunAsync(recomputed.Id, "{}", CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Null(await InputsAsOfAsync(db, recomputed.Id));

        await using var after = f.Builder.CreateContext();
        Assert.False((await new MethodologyStore(after).GetCalculationFreshnessAsync(
            f.Document.DocumentId, f.Document.PeriodKey.Value, tableDefIds: null, CancellationToken.None)).IsStale);
        Assert.DoesNotContain(
            await StaleResultsQuery.Documents(after, f.Document.PeriodKey.Value).ToListAsync(),
            r => r.DocumentId == f.Document.DocumentId);
    }

    /// <remarks>
    /// Ланцюжок A → B → C: B переніс результати A (<c>InputsAsOf = T1</c>), C переносить результати B. Момент входів C —
    /// <c>COALESCE(B.InputsAsOfUtc, B.StartedAt)</c>, тобто T1, а не старт B: інакше другий перенос «омолодив» би числа.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ланцюжок_переносів_не_губить_найдавніший_момент_входів()
    {
        var f = await ArrangeAsync();
        await using var db = f.Builder.CreateContext();

        var second = await StartRunAsync(db, f, T3);
        var secondStore = new CalculationResultStore(db, new TestClock(T3));
        await secondStore.CarryOverResultsAsync(second.Id, f.Document.DocumentId, [f.MethodologyId], CancellationToken.None);
        await secondStore.SwitchCurrentRunAsync(second.Id, "{}", CancellationToken.None);
        await db.SaveChangesAsync();

        var third = await StartRunAsync(db, f, T3.AddHours(2));
        var thirdStore = new CalculationResultStore(db, new TestClock(T3.AddHours(2)));
        Assert.Equal(1, await thirdStore.CarryOverResultsAsync(third.Id, f.Document.DocumentId, [f.MethodologyId], CancellationToken.None));
        await thirdStore.SwitchCurrentRunAsync(third.Id, "{}", CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(T1, await InputsAsOfAsync(db, second.Id));
        Assert.Equal(T1, await InputsAsOfAsync(db, third.Id));
    }

    private sealed record Fixture(
        TestDocumentBuilder Builder,
        TestDocument Document,
        int MethodologyId,
        int VersionId,
        int UnitId,
        long SourceRunId,
        string RegistryCode);

    /// <summary>
    /// Документ, методологія, що читає довідник (правка якого — у <see cref="T2"/>), і актуальний прогін-джерело
    /// (старт <see cref="T1"/>) з одним результатом.
    /// </summary>
    private async Task<Fixture> ArrangeAsync(bool seedResult = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var registry = new RegistryDef(EcrCode.Create($"CR{tag}"), Name("Registry"), isTemporal: false);
        registry.MarkDataChanged(T2);
        db.RegistryDefs.Add(registry);

        var methodology = new Methodology(EcrCode.Create($"CO_{tag}"), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, T1);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version.Id, "F1", registry.Id, "X"));

        var source = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, T1);
        source.Complete("Succeeded", T1.AddMinutes(1), "{}", errorMessage: null);
        source.MakeCurrent();
        db.CalculationRuns.Add(source);
        await db.SaveChangesAsync();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var fixture = new Fixture(builder, document, methodology.Id, version.Id, unitId, source.Id, registry.Code);

        if (seedResult)
        {
            await new CalculationResultStore(db, new TestClock(T1)).WriteResultsAsync(
                source.Id, [Output(fixture)], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        return fixture;
    }

    private static CalculationOutput Output(Fixture f)
        => new(
            f.Document.DocumentId, "R-1",
            [new CalculationOutputValue(f.VersionId, null, "tons", 1m, f.UnitId)],
            []);

    /// <summary>Новий прогін (ще не актуальний) для того самого проєкту й періоду.</summary>
    private static async Task<CalculationRun> StartRunAsync(EcrDbContext db, Fixture f, DateTime startedAt)
    {
        var run = new CalculationRun(f.Document.ProjectId, f.Document.PeriodKey.Value, triggeredByUserId: null, startedAt);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private static async Task<DateTime?> InputsAsOfAsync(EcrDbContext db, long runId)
        => await db.CalculationRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.InputsAsOfUtc)
            .SingleAsync();

    private static async Task InsertResultsAsync(EcrDbContext db, Fixture f, long runId, int count)
    {
        var period = f.Document.PeriodKey.Value;
        var document = f.Document.DocumentId;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @first sql_variant;
            EXEC sys.sp_sequence_get_range
                @sequence_name = N'calc.CalculationResultSeq',
                @range_size = {count},
                @range_first_value = @first OUTPUT;

            INSERT INTO calc.CalculationResult
                (Id, PeriodKey, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey, OutputCode, Value, UnitId, Kind)
            SELECT CONVERT(bigint, @first) + n.Num - 1, {period}, {runId}, {f.VersionId}, {document},
                   CONCAT(N'R-', n.Num), N'EMISSION', 1, {f.UnitId}, 0
              FROM (SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Num
                      FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n;
            """);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
