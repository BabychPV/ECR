// tests/Ecr.Infrastructure.Tests/Jobs/RunCalculationOutOfOrderCompletionTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Завершення прогонів одного документа й періоду НЕ в порядку їх створення —
/// напряму через <see cref="RunCalculationHandler.CompleteAsync"/>, повз лок
/// документа задачі (<c>RecalculationDocumentLock</c>).
/// </summary>
/// <remarks>
/// ⛔ Дефект, який доводить тест. <c>SwitchCurrentRunAsync</c> клав «старий →
/// Superseded» і «свій → Current» в один <c>SaveChanges</c>, а EF шле UPDATE за
/// зростанням ключа. Коли старіший прогін (менший Id) завершувався ПІСЛЯ
/// новішого, «свій → Current» ішов першим, поки новіший ще <c>Current</c>, —
/// <c>DbUpdateException</c> на <c>UX_CalculationRun_Current</c>. А якби порядок
/// UPDATE був інший, старіший прогін перекрив би новіші числа старішими.
/// <para>
/// Мутація: прибрати відмову старішому прогону в <c>CalculationResultStore.SwitchCurrentRunAsync</c>
/// — перший тест червоніє (<c>DbUpdateException</c> або старіший стає актуальним).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RunCalculationOutOfOrderCompletionTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Старіший_прогін_що_завершився_після_новішого_не_стає_актуальним()
    {
        var (builder, older, newer, documentId, period) = await ArrangeAsync();

        await CompleteAsync(builder, newer);

        // ⛔ До фіксу — DbUpdateException на UX_CalculationRun_Current.
        await CompleteAsync(builder, older);

        await using var check = builder.CreateContext();
        var runs = await check.CalculationRuns
            .AsNoTracking()
            .Where(r => r.DocumentId == documentId && r.PeriodKey == period)
            .ToDictionaryAsync(r => r.Id);

        Assert.Equal(CalculationRun.CurrentStatus, runs[newer].Status);
        Assert.Equal(CalculationRun.SupersededStatus, runs[older].Status);
        Assert.NotNull(runs[older].FinishedAt);

        // Причина — конвертом каталогу, а не текстом винятку бази.
        Assert.True(
            JobProgressMessageCodec.TryDecode(runs[older].ErrorMessage, out var reason),
            $"Причина відмови не конверт: «{runs[older].ErrorMessage}».");
        Assert.Equal("jobs.calculationRunSupersededByNewer", reason!.Key);
        Assert.Equal(newer.ToString(System.Globalization.CultureInfo.InvariantCulture), reason.Params!["runId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Завершення_в_порядку_створення_перемикає_актуальність_як_і_раніше()
    {
        var (builder, older, newer, documentId, period) = await ArrangeAsync();

        await CompleteAsync(builder, older);
        await CompleteAsync(builder, newer);

        await using var check = builder.CreateContext();
        var runs = await check.CalculationRuns
            .AsNoTracking()
            .Where(r => r.DocumentId == documentId && r.PeriodKey == period)
            .ToDictionaryAsync(r => r.Id);

        Assert.Equal(CalculationRun.CurrentStatus, runs[newer].Status);
        Assert.Equal(CalculationRun.SupersededStatus, runs[older].Status);
        Assert.Null(runs[older].ErrorMessage);
    }

    /// <summary>Два прогони в <c>Running</c> одного документа й періоду: старіший і новіший.</summary>
    private async Task<(TestDocumentBuilder Builder, long Older, long Newer, long DocumentId, int Period)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        var period = document.PeriodKey.Value;

        await using var db = builder.CreateContext();
        var older = new CalculationRun(document.ProjectId, period, null, Now, documentId: document.DocumentId);
        db.CalculationRuns.Add(older);
        await db.SaveChangesAsync();

        var newer = new CalculationRun(document.ProjectId, period, 7, Now, documentId: document.DocumentId);
        db.CalculationRuns.Add(newer);
        await db.SaveChangesAsync();

        Assert.True(older.Id < newer.Id);
        return (builder, older.Id, newer.Id, document.DocumentId, period);
    }

    /// <summary>Завершення окремим контекстом — як окрема задача черги.</summary>
    private static async Task CompleteAsync(TestDocumentBuilder builder, long runId)
    {
        await using var db = builder.CreateContext();
        var clock = new TestClock(Now);

        var handler = new RunCalculationHandler(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>());

        await handler.CompleteAsync(runId, new ModuleProfile(), CancellationToken.None);
    }
}
