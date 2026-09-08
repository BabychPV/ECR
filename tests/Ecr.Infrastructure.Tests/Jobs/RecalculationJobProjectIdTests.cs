// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobProjectIdTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Задача перерахунку виживає payload без <c>ProjectId</c> — на **реальному**
/// SQL Server (директива №09 §1.3, §6.5 `S-25`; `W3` п. 1-2).
/// </summary>
/// <remarks>
/// ⛔ `RecalculateDocumentHandler` кладе в чергу
/// <c>new { DocumentId, PeriodKey }</c> — без <c>ProjectId</c> узагалі. При
/// розборі в non-nullable <c>int</c> це мовчки стає <c>0</c>.
/// <c>calc.CalculationRun</c> має справжній зовнішній ключ <c>FK_CR_Project</c>
/// (<c>CalculationsConfiguration.cs</c>), і `SaveChangesAsync` із
/// <c>ProjectId = 0</c> кидає <c>DbUpdateException</c> — саме тому потрібна
/// реальна база, а не мок: мок відповів би на будь-який <c>ProjectId</c>
/// однаково і не побачив би дефекту.
///
/// ⛔ Стара поведінка: створення <c>CalculationRun</c> стояло ДО <c>try</c>,
/// тож цей виняток летів МИМО catch, який мав позначити задачу <c>Failed</c> —
/// вона лишалася б `Running` назавжди. Цей тест довів би падіння на
/// невиправленому коді самим фактом необробленого <c>DbUpdateException</c>
/// (<c>D-134</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobProjectIdTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task ProjectId_відсутній_у_payload_визначається_з_документа()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        // ⛔ Саме той payload, який кладе в чергу `RecalculateDocumentHandler`:
        // `ProjectId` не переданий, отже після розбору `RecalculationRequest`
        // він — 0.
        var request = new RecalculationRequest(
            ProjectId: 0,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: null);

        await using var db = builder.CreateContext();

        var job = new RecalculationJob(
            db,
            new StubRunner(),
            RunHandler(),
            Formulas(),
            new TestClock(DateTime.UtcNow));

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ: виконання не кидає. На невиправленому коді
        // тут летів би `DbUpdateException` (`FK_CR_Project`) повз будь-який
        // catch.
        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);

        // ⚠ Фільтр за `ProjectId`, а не «єдиний рядок у базі»: база спільна на
        // весь тестовий проєкт (`EcrTest_Infrastructure`), і сусідні тести
        // могли вже лишити в ній свої прогони. `ProjectId` тут унікальний —
        // `TestDocumentBuilder` створює його з новим тегом щоразу.
        //
        // ⚠ `Status` тут НЕ перевіряється: `ICalculationResultStore`, який
        // перемикає актуальність прогону на `Succeeded`, замоканий — цей тест
        // про ІНШЕ: чи дійшов рядок до бази з правильним `ProjectId` замість
        // `0`, а не про повний життєвий цикл прогону.
        await using var verify = builder.CreateContext();
        var run = await verify.CalculationRuns
            .AsNoTracking()
            .SingleAsync(r => r.ProjectId == document.ProjectId);

        Assert.Equal(document.ProjectId, run.ProjectId);
    }

    /// <summary>Служба перерахунку формул шаблону над підставними портами.</summary>
    /// <remarks>
    /// ⚠ Цей тест — про <c>ProjectId</c>, а не про формули: порти підставні,
    /// і фаза формул чесно завершується нулем комірок. Порядок і зміст фаз
    /// доводить <c>RecalculationJobTests</c>.
    /// </remarks>
    private static Ecr.Application.Recalculation.RecalculationService Formulas()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return new(
            Substitute.For<ICellStore>(),
            Substitute.For<IRowStore>(),
            Substitute.For<Ecr.Application.Ports.IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<Ecr.Application.Ports.IUnitOfWork>());
    }

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<Ecr.Application.Ports.IPeriodStore>(),
            Substitute.For<Ecr.Application.Ports.IWorkflowStore>(),
            Substitute.For<Ecr.Application.Ports.ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<Ecr.Application.Ports.IUnitOfWork>(),
            Substitute.For<ICurrentUser>(),
            new TestClock(DateTime.UtcNow));

    /// <summary>Оркестратор-заглушка: прогін завжди «успішний і порожній».</summary>
    private sealed class StubRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }

    /// <summary>Канал прогресу, що нічого не робить.</summary>
    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
