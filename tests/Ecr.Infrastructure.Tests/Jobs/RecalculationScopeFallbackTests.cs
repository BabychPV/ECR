// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationScopeFallbackTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-13 D4: запасний шлях O3d у скоупі періодів перерахунку. Екземпляри документа, чийого періоду немає в
/// <c>doc.Period</c> (FK немає), запит з ключем партиції (<see cref="RecalculationJob.DocumentInstancesQuery"/>)
/// не бачить; без запасного пошуку лише за <c>DocumentId</c> річний прогін такого документа мовчки не рахував
/// би нічого.
/// </summary>
/// <remarks>
/// Мутація (в описі коміту): прибрати гілку <c>scopes.Count == 0</c> у <c>ScopePeriodsAsync</c> — червоний
/// «запасний шлях»; контрольний тест (період є в календарі) зелений в обох випадках.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class RecalculationScopeFallbackTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Період_екземпляра_без_рядка_doc_Period_дає_скоуп_запасним_шляхом()
    {
        // Рік, якого календар жодного проєкту в спільній базі не має: запит з ключем партиції бере
        // `PeriodKey IN (SELECT PeriodKey FROM doc.Period)` по всіх проєктах.
        var key = (2080 + Random.Shared.Next(0, 15)) * 100 + Random.Shared.Next(1, 13);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: key);

        await using (var db = builder.CreateContext())
        {
            await db.Periods.Where(p => p.ProjectId == document.ProjectId).ExecuteDeleteAsync();

            // Передумова доказу: основний запит O3d цього документа не бачить.
            Assert.Empty(await RecalculationJob.DocumentInstancesQuery(db, document.DocumentId).ToListAsync());
        }

        Assert.Equal([key], await ScopeAsync(builder, document));
    }

    [Fact]
    public async Task Період_є_в_календарі_скоуп_з_ключем_партиції()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using (var db = builder.CreateContext())
        {
            Assert.NotEmpty(await RecalculationJob.DocumentInstancesQuery(db, document.DocumentId).ToListAsync());
        }

        Assert.Equal([document.PeriodKey.Value], await ScopeAsync(builder, document));
    }

    private static async Task<IReadOnlyList<int>> ScopeAsync(TestDocumentBuilder builder, TestDocument document)
    {
        await using var db = builder.CreateContext();
        var job = new RecalculationJob(db, Substitute.For<ICalculationRunner>(), RunHandler(), Formulas(), new TestClock(DateTime.UtcNow));

        // Рік (PeriodKey = null) — нічний розклад: єдине джерело періодів — екземпляри таблиць.
        return await job.ScopePeriodsAsync(
            new RecalculationRequest(document.ProjectId, document.DocumentId, PeriodKey: null, TriggeredByUserId: null),
            new HashSet<int>(),
            CancellationToken.None);
    }

    private static Ecr.Application.Recalculation.RecalculationService Formulas()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        var header = Substitute.For<IDocumentHeaderStore>();
        header.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new(
            Substitute.For<ICellStore>(),
            Substitute.For<IRowStore>(),
            Substitute.For<IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<IRegistryStore>(),
            header,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IUnitOfWork>(), Substitute.For<ISheetEditGate>());
    }

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            Substitute.For<ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            new TestClock(DateTime.UtcNow),
            Substitute.For<IAuditWriter>());
}
