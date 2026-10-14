// tests/Ecr.Infrastructure.Tests/Persistence/CalculationResultTableInstanceTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-401: результат, вхід і крок трейсу несуть екземпляр таблиці; ключ прив'язки кроку до результату містить
/// таблицю; перенос результатів копіює поле; <c>ReadCurrentAsync</c> віддає його.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази: прибрати <c>result.SetTable</c> з <c>WriteResultsAsync</c> —
/// <see cref="Запис_несе_таблицю_у_результат_крок_і_вхід"/> червоний; прибрати <c>TableInstanceId</c> з ключа
/// <c>PendingResults</c>/<c>ResultIdOf</c> — <see cref="Кроки_двох_таблиць_з_однаковим_ключем_прив_язані_кожен_до_свого_результату"/>
/// червоний (крок другої таблиці отримує результат першої); прибрати <c>copy.SetTable</c> з <c>CarryOverResultsAsync</c> —
/// <see cref="Перенос_зберігає_таблицю_а_NULL_лишається_NULL"/> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationResultTableInstanceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    private const string Row = "M01";
    private const long TableA = 710_001;
    private const long TableB = 710_002;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_несе_таблицю_у_результат_крок_і_вхід()
    {
        var s = await StandAsync();
        var output = s.Output(TableA, 10m);

        await using (var db = s.Builder.CreateContext())
        {
            var store = new CalculationResultStore(db, new TestClock(Now));
            await store.WriteResultsAsync(s.RunId, [output], CancellationToken.None);
            await store.WriteTraceAsync(s.RunId, [output], TraceLevel.ErrorsOnly, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var check = s.Builder.CreateContext();
        var result = await check.CalculationResults.AsNoTracking().SingleAsync(r => r.CalculationRunId == s.RunId);
        var step = await check.CalculationSteps.AsNoTracking().SingleAsync(r => r.CalculationRunId == s.RunId);
        var input = await check.CalculationInputs.AsNoTracking().SingleAsync(r => r.CalculationRunId == s.RunId);

        Assert.Equal(TableA, result.TableInstanceId);
        Assert.Equal(TableA, step.TableInstanceId);
        Assert.Equal(TableA, input.TableInstanceId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кроки_двох_таблиць_з_однаковим_ключем_прив_язані_кожен_до_свого_результату()
    {
        var s = await StandAsync();
        var a = s.Output(TableA, 10m);
        var b = s.Output(TableB, 5m);

        await using (var db = s.Builder.CreateContext())
        {
            var store = new CalculationResultStore(db, new TestClock(Now));

            // Один контекст, один прогін, однаковий (документ, рядок, версія, вихід, речовина) — різні таблиці.
            await store.WriteResultsAsync(s.RunId, [a, b], CancellationToken.None);
            await store.WriteTraceAsync(s.RunId, [a, b], TraceLevel.ErrorsOnly, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var check = s.Builder.CreateContext();
        var results = await check.CalculationResults.AsNoTracking().Where(r => r.CalculationRunId == s.RunId).ToListAsync();
        var steps = await check.CalculationSteps.AsNoTracking().Where(r => r.CalculationRunId == s.RunId).ToListAsync();

        Assert.Equal(2, results.Count);
        var resultA = Assert.Single(results, r => r.TableInstanceId == TableA);
        var resultB = Assert.Single(results, r => r.TableInstanceId == TableB);
        Assert.Equal(resultA.Id, Assert.Single(steps, x => x.TableInstanceId == TableA).ResultId);
        Assert.Equal(resultB.Id, Assert.Single(steps, x => x.TableInstanceId == TableB).ResultId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перенос_зберігає_таблицю_а_NULL_лишається_NULL()
    {
        var s = await StandAsync();

        await using var db = s.Builder.CreateContext();
        var store = new CalculationResultStore(db, new TestClock(Now));
        await store.WriteResultsAsync(
            s.RunId, [s.Output(TableA, 10m), s.Output(TableB, 5m), s.Output(null, 7m, "LEGACY")], CancellationToken.None);
        await store.SwitchCurrentRunAsync(s.RunId, "{}", CancellationToken.None);
        await db.SaveChangesAsync();

        var target = new CalculationRun(s.ProjectId, s.PeriodKey, triggeredByUserId: null, Now.AddHours(1));
        db.CalculationRuns.Add(target);
        await db.SaveChangesAsync();

        var carry = new CalculationResultStore(db, new TestClock(Now.AddHours(1)));
        Assert.Equal(3, await carry.CarryOverResultsAsync(target.Id, s.DocumentId, [s.MethodologyId], CancellationToken.None));
        await carry.SwitchCurrentRunAsync(target.Id, "{}", CancellationToken.None);
        await db.SaveChangesAsync();

        await using var read = s.Builder.CreateContext();
        var current = await new CalculationResultStore(read, new TestClock(Now))
            .ReadCurrentAsync(s.DocumentId, s.PeriodKey, CancellationToken.None);

        Assert.Equal(3, current.Count);
        Assert.Equal(10m, Assert.Single(current, r => r.TableInstanceId == TableA).Value);
        Assert.Equal(5m, Assert.Single(current, r => r.TableInstanceId == TableB).Value);
        var legacy = Assert.Single(current, r => r.TableInstanceId is null);
        Assert.Equal(7m, legacy.Value);
        Assert.Equal("LEGACY", legacy.OutputCode);
    }

    private async Task<Stand> StandAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(
            EcrCode.Create($"D401_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        return new Stand(
            builder, document.DocumentId, document.ProjectId, document.PeriodKey.Value, run.Id, methodology.Id, version.Id, unit);
    }

    private sealed record Stand(
        TestDocumentBuilder Builder,
        long DocumentId,
        int ProjectId,
        int PeriodKey,
        long RunId,
        int MethodologyId,
        int VersionId,
        int UnitId)
    {
        /// <summary>Результат рядка <c>M01</c> з одним виходом, одним кроком і одним входом.</summary>
        public CalculationOutput Output(long? tableInstanceId, decimal value, string outputCode = "EMISSION")
            => new(
                DocumentId,
                Row,
                [new CalculationOutputValue(VersionId, null, outputCode, value, UnitId)],
                [new CalculationTraceStep(1, outputCode, "@X * 1", value, null)],
                [new CalculationArgument("X", value, null, UnitId)],
                tableInstanceId);
    }
}
