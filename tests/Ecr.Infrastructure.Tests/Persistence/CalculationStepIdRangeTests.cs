// tests/Ecr.Infrastructure.Tests/Persistence/CalculationStepIdRangeTests.cs
using System.Data.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-105 / D2-06: перевірка «старих Id» кроків трейсу читає MAX лише в партиції періоду
/// запису, а не скан усієї <c>calc.CalculationStep</c>.
/// </summary>
/// <remarks>
/// ⚠ Справжній SQL Server і справжній запит: перехоплювач команд EF бачить текст, який іде в базу.
/// Унікальність Id потрібна лише в межах ключа <c>PK_CalculationStep (PeriodKey, Id)</c>, тож
/// старий крок ІНШОГО періоду, що стоїть рівно на наступному значенні послідовності, запису не
/// заважає — а старий крок ТОГО САМОГО періоду й далі оминається
/// (<see cref="CalculationStepIdRaceTests"/>).
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationStepIdRangeTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D2-06")]
    public async Task MAX_кроків_трейсу_звужено_до_партиції_періоду()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        long runId;
        await using (var seed = chain.CreateContext())
        {
            var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
            seed.CalculationRuns.Add(run);
            await seed.SaveChangesAsync(CancellationToken.None);
            runId = run.Id;
        }

        var commands = new CommandTextRecorder();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(commands)
            .Options);

        await new CalculationResultStore(db, new TestClock(Now)).WriteTraceAsync(
            runId,
            [new CalculationOutput(document.DocumentId, "R-1", [], [new CalculationTraceStep(1, "S1", "a+b", 1m, TraceJson: null)])],
            TraceLevel.Full,
            CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати `.Where(s => s.PeriodKey == periodKey)` у
        // `ReserveStepIdRangeAsync` → MAX без предиката партиції, червоний.
        var max = Assert.Single(commands.Texts, t =>
            t.Contains("MAX(", StringComparison.OrdinalIgnoreCase)
            && t.Contains("[CalculationStep]", StringComparison.Ordinal));
        Assert.Contains("[PeriodKey] =", max, StringComparison.Ordinal);

        await using var check = chain.CreateContext();
        Assert.Equal(1, await check.CalculationSteps.AsNoTracking().CountAsync(s => s.CalculationRunId == runId));
    }

    /// <summary>Запам'ятовує текст кожної команди читання.</summary>
    private sealed class CommandTextRecorder : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            lock (Texts)
            {
                Texts.Add(command.CommandText);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            lock (Texts)
            {
                Texts.Add(command.CommandText);
            }

            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
