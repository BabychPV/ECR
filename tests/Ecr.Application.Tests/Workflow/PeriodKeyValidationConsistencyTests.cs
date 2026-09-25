// tests/Ecr.Application.Tests/Workflow/PeriodKeyValidationConsistencyTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// B-16 (UX-аудит, четвертий раунд): <c>periodKey</c> зовнішнього запиту
/// проходить через ОДИН спільний валідатор (<see cref="Ecr.Domain.ValueObjects.PeriodKey.Parse"/>)
/// на кожному шляху, що приймає його від клієнта — не лише там, де хтось про
/// це згадав.
/// </summary>
/// <remarks>
/// ⛔ До цього <c>SubmitSheetHandler</c>, <c>ReopenDocumentHandler</c>,
/// <c>RecallSheetHandler.HandleAsync</c>, <c>ApproveSheetHandler</c> і
/// <c>DocumentDataExporter</c> будували <c>PeriodKey</c> первинним
/// конструктором (<c>new PeriodKey(periodKey)</c>) — той нічого не перевіряє,
/// бо ним ЕЖ матеріалізує й збережені значення. Замір: `RecallSheetHandler`
/// уже мав <c>PeriodKey.Parse</c> у сусідньому <c>CanRecallAsync</c> — кнопка
/// відмовляла на невірному періоді, а сама дія відкликання мовчки приймала
/// його. Кожен тест нижче доводить, що виняток летить ДО того, як обробник
/// торкнеться якогось стore — для цього досить лише <c>ICurrentUser</c>
/// (де він потрібен раніше перевірки), решта залежностей — незайняті підробки.
/// </remarks>
public sealed class PeriodKeyValidationConsistencyTests
{
    private const int InvalidPeriodKey = 0;
    private const int UserId = 5;

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task SubmitSheetHandler_відмовляє_на_невірному_періоді()
    {
        var handler = new SubmitSheetHandler(
            Substitute.For<ICellStore>(),
            Substitute.For<IRowStore>(),
            Substitute.For<IWorkflowStore>(),
            Substitute.For<IDocumentStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<IAccessDecisionService>(),
            new Ecr.Application.Validation.ValidationEngine(Substitute.For<IFormulaEngine>()),
            Substitute.For<IDocumentHeaderStore>(),
            new ReportSnapshotSync(Substitute.For<IReportSnapshotBuilder>(), Substitute.For<IDocumentStore>()),
            Substitute.For<IUnitOfWork>(),
            User(),
            Substitute.For<IClock>(),
            Substitute.For<ISheetEditGate>(),
            Substitute.For<Ecr.Application.Recalculation.ISubmitRecalculation>());

        var error = await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(documentId: 1, sheetDefId: 1, InvalidPeriodKey, CancellationToken.None));

        Assert.Equal(ErrorCodes.PeriodOutOfProject, error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task ReopenDocumentHandler_відмовляє_на_невірному_періоді()
    {
        var handler = new ReopenDocumentHandler(
            Substitute.For<IWorkflowStore>(),
            Substitute.For<IAccessDecisionService>(),
            Substitute.For<IUnitOfWork>(),
            User(),
            Substitute.For<IClock>());

        var error = await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(documentId: 1, sheetDefId: 1, InvalidPeriodKey, "причина", CancellationToken.None));

        Assert.Equal(ErrorCodes.PeriodOutOfProject, error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task RecallSheetHandler_HandleAsync_відмовляє_на_невірному_періоді_як_і_CanRecallAsync()
    {
        var handler = new RecallSheetHandler(
            Substitute.For<IWorkflowStore>(),
            Substitute.For<IDocumentStore>(),
            Substitute.For<IAccessDecisionService>(),
            new ReportSnapshotSync(Substitute.For<IReportSnapshotBuilder>(), Substitute.For<IDocumentStore>()),
            Substitute.For<IUnitOfWork>(),
            User(),
            Substitute.For<IClock>());

        var error = await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(documentId: 1, sheetDefId: 1, InvalidPeriodKey, "причина", CancellationToken.None));

        Assert.Equal(ErrorCodes.PeriodOutOfProject, error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task ApproveSheetHandler_відмовляє_на_невірному_періоді()
    {
        var handler = new ApproveSheetHandler(
            Substitute.For<IWorkflowStore>(),
            Substitute.For<IAccessDecisionService>(),
            new ReportSnapshotSync(Substitute.For<IReportSnapshotBuilder>(), Substitute.For<IDocumentStore>()),
            Substitute.For<IUnitOfWork>(),
            User(),
            Substitute.For<IClock>(),
            Substitute.For<IAuditWriter>());

        var error = await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(
                documentId: 1, sheetDefId: 1, InvalidPeriodKey, approved: true, reason: null, CancellationToken.None));

        Assert.Equal(ErrorCodes.PeriodOutOfProject, error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task DocumentDataExporter_відмовляє_на_невірному_періоді()
    {
        var exporter = new DocumentDataExporter(
            Substitute.For<ICellStore>(),
            Substitute.For<IRowStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<IRegistryStore>());

        var error = await Assert.ThrowsAsync<DomainException>(
            () => exporter.ExportAsync(
                documentId: 1, InvalidPeriodKey, DocumentExportFormat.Csv, includeFormulas: false, CancellationToken.None));

        Assert.Equal(ErrorCodes.PeriodOutOfProject, error.ErrorCode);
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        return user;
    }
}
