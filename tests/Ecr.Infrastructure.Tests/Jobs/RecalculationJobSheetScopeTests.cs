// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobSheetScopeTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Q-331: перерахунок з <c>SheetDefId</c> звужує прив'язки методологій
/// (<c>RecalculationJob.BindingsAsync</c>) до таблиць ЦЬОГО аркуша.
/// </summary>
/// <remarks>
/// ⛔ Директива паритету зі старою системою, прогалина 2 (Q-327 → Q-331):
/// до цього пакета кнопка «Recalculate» аркуша перераховувала ВЕСЬ документ —
/// підказка (Q-327) лише чесно про це попереджала, контракт не звужувався.
/// Цей тест доводить МУТАЦІЄЮ, що звуження реальне: прибери фільтр за
/// <c>SheetDefId</c> у <c>BindingsAsync</c> — і тест впаде, бо прив'язка
/// СУСІДНЬОГО аркуша потрапить у список.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobSheetScopeTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SheetDefId_звужує_прив_язки_методологій_до_свого_аркуша()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        // ⚠ Другий аркуш ТОГО САМОГО документа — інша таблиця, інший
        // екземпляр, інша прив'язка методології. `TestDocumentBuilder` будує
        // лише один аркуш, тому решта складається тут вручну — тим самим
        // шляхом (SEQUENCE, а не IDENTITY), яким користується сам будівник.
        await using var setup = builder.CreateContext();

        var sheet2 = new SheetDef(document.TemplateVersionId, EcrCode.Create("SHEET2X"), Name("Sheet 2"), 2);
        setup.SheetDefs.Add(sheet2);
        await setup.SaveChangesAsync();

        var table2 = new TableDef(
            sheet2.Id, EcrCode.Create("TBL2X"), Name("Table 2"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        setup.TableDefs.Add(table2);
        await setup.SaveChangesAsync();

        var column2 = new ColumnDef(table2.Id, EcrCode.Create("C1_2X"), Name("Col 1"), 1, CellDataType.Decimal);
        setup.ColumnDefs.Add(column2);
        await setup.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2Id = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var instance2 = new TableInstance(
            document.PeriodKey, instance2Id, document.DocumentId, table2.Id, DateTime.UtcNow);
        setup.TableInstances.Add(instance2);
        await setup.SaveChangesAsync();

        // ⚠ Друга колонка першого (будованого) аркуша — прив'язка методології
        // адресує КОЛОНКУ, і будівник заводить її разом із таблицею.
        var binding1 = new CalculationBinding(
            document.TableDefId, document.ColumnDefIds[1], methodologyId: 501, "tons", "{}");
        var binding2 = new CalculationBinding(table2.Id, column2.Id, methodologyId: 502, "tons", "{}");
        setup.CalculationBindings.AddRange(binding1, binding2);
        await setup.SaveChangesAsync();

        var order = new List<string>();
        var runner = new RecordingBindingsRunner();

        await using var db = builder.CreateContext();

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: null,
            SheetDefId: document.SheetDefId);

        var job = new RecalculationJob(db, runner, RunHandler(), FormulaService(Rows(order)), new TestClock(DateTime.UtcNow));

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ: лише прив'язка ПЕРШОГО (заданого) аркуша
        // дійшла до оркестратора методологій — прив'язка сусіднього
        // (`table2`/`binding2`, методологія 502) НЕ дійшла.
        var binding = Assert.Single(runner.Bindings);
        Assert.Equal(501, binding.MethodologyId);
        Assert.Equal(document.TableInstanceId, binding.TableInstanceId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SheetDefId_відсутній_дає_прив_язки_УСЬОГО_документа()
    {
        // ⚠ Контрольний тест: поведінка до Q-331 (`SheetDefId = null`)
        // лишається незмінною — обидві прив'язки документа доходять до
        // оркестратора, як і раніше.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var setup = builder.CreateContext();

        var sheet2 = new SheetDef(document.TemplateVersionId, EcrCode.Create("SHEET3X"), Name("Sheet 3"), 2);
        setup.SheetDefs.Add(sheet2);
        await setup.SaveChangesAsync();

        var table2 = new TableDef(
            sheet2.Id, EcrCode.Create("TBL3X"), Name("Table 3"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        setup.TableDefs.Add(table2);
        await setup.SaveChangesAsync();

        var column2 = new ColumnDef(table2.Id, EcrCode.Create("C1_3X"), Name("Col 1"), 1, CellDataType.Decimal);
        setup.ColumnDefs.Add(column2);
        await setup.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2Id = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var instance2 = new TableInstance(
            document.PeriodKey, instance2Id, document.DocumentId, table2.Id, DateTime.UtcNow);
        setup.TableInstances.Add(instance2);
        await setup.SaveChangesAsync();

        var binding1 = new CalculationBinding(
            document.TableDefId, document.ColumnDefIds[1], methodologyId: 601, "tons", "{}");
        var binding2 = new CalculationBinding(table2.Id, column2.Id, methodologyId: 602, "tons", "{}");
        setup.CalculationBindings.AddRange(binding1, binding2);
        await setup.SaveChangesAsync();

        var order = new List<string>();
        var runner = new RecordingBindingsRunner();

        await using var db = builder.CreateContext();

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: null,
            SheetDefId: null);

        var job = new RecalculationJob(db, runner, RunHandler(), FormulaService(Rows(order)), new TestClock(DateTime.UtcNow));

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);

        var methodologyIds = runner.Bindings.Select(b => b.MethodologyId).OrderBy(id => id).ToList();
        Assert.Equal(2, methodologyIds.Count);
        Assert.Equal(601, methodologyIds[0]);
        Assert.Equal(602, methodologyIds[1]);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Джерело рядків, яке не заважає фазі формул — тест лише про прив'язки.</summary>
    private static IRowStore Rows(List<string> order)
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]))
            .AndDoes(_ => order.Add("Формули"));

        return rows;
    }

    private static RecalculationService FormulaService(IRowStore rows)
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return new(
            Substitute.For<ICellStore>(),
            rows,
            Substitute.For<Ecr.Application.Ports.IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<Ecr.Application.Ports.IRegistryStore>(),
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<Ecr.Application.Ports.IUnitOfWork>());
    }

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<Ecr.Application.Ports.IPeriodStore>(),
            Substitute.For<Ecr.Application.Ports.IWorkflowStore>(),
            Substitute.For<Ecr.Application.Ports.ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<Ecr.Application.Ports.IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            new TestClock(DateTime.UtcNow));

    /// <summary>Оркестратор, що запам'ятовує прив'язки, з якими його викликали.</summary>
    private sealed class RecordingBindingsRunner : ICalculationRunner
    {
        public List<CalculationBindingRef> Bindings { get; } = [];

        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            Bindings.AddRange(bindings);

            return Task.FromResult(new ModuleProfile());
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
