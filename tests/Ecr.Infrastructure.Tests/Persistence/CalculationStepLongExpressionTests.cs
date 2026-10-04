// tests/Ecr.Infrastructure.Tests/Persistence/CalculationStepLongExpressionTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Формула, довша за колонку кроку трейсу, не валить запис прогону на
/// РЕАЛЬНОМУ SQL Server (аудит 2026-10-03, L10-01).
/// </summary>
/// <remarks>
/// ⛔ <c>MethodologyFormula.Expression</c> — до 4000 символів (D256),
/// <c>calc.CalculationStep.Expression</c> — <c>nvarchar(2000)</c>. Крок з
/// помилкою (<c>#DIV/0</c>) пишеться і за <see cref="TraceLevel.ErrorsOnly"/> —
/// типовим рівнем, — тож обрізання на <c>SaveChanges</c> робило Failed увесь
/// прогін разом із його результатами.
/// Мутаційний доказ: у <c>CalculationStep.Describe</c> повернути
/// <c>Expression = expression;</c> — <c>SaveChangesAsync</c> кидає
/// <c>DbUpdateException</c> («String or binary data would be truncated»).
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationStepLongExpressionTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L10-01")]
    public async Task Крок_з_формулою_2500_символів_і_DIV0_записується_обрізаним()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var periodKey = document.PeriodKey.Value;
        var expression = string.Concat(Enumerable.Repeat("[Flow]/([Hours]-[Hours])+", 100));
        Assert.Equal(2500, expression.Length);

        await using var db = chain.CreateContext();
        var run = new CalculationRun(document.ProjectId, periodKey, triggeredByUserId: null, Now);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        var output = new CalculationOutput(
            document.DocumentId,
            "row-1",
            [],
            [new CalculationTraceStep(1, "THERMALOXIDIZER", expression, Value: null, TraceJson: "#DIV/0")]);

        await new CalculationResultStore(db, new TestClock(Now)).WriteTraceAsync(
            run.Id, [output], TraceLevel.ErrorsOnly, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        await using var check = chain.CreateContext();
        var stored = await check.CalculationSteps.AsNoTracking()
            .Where(s => s.CalculationRunId == run.Id)
            .Select(s => s.Expression)
            .SingleAsync(CancellationToken.None);

        Assert.Equal(CalculationStep.MaxStepExpressionLength, stored!.Length);
        Assert.StartsWith(expression[..100], stored, StringComparison.Ordinal);
    }
}
