// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.SourceMonthGate.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// B80 (N2b, R3.1 A3): подання й затвердження (на будь-якому кроці маршруту) аркуша споживача відмовляють 422
/// <c>ECR-SUB-4221</c> / <see cref="SourceMonthApprovalCheck.MessageKey"/>, коли ворота називають незатверджений місяць
/// джерела; ворота мовчать — дія як доти; відхилення не блокується.
/// </summary>
/// <remarks>
/// Коли саме ворота називають місяць (стан зв'язки, споживач, документ року, оригінал аркуша) —
/// <c>DocumentSourceMonthGateTests</c> (Infrastructure).
/// </remarks>
public sealed partial class WorkflowTransactionTests
{
    private const long SourceDocumentId = 9_099_001;
    private const int SourcePeriodKey = 202601;

    /// <summary>Мутація: прибрати виклик <c>SourceMonthApprovalCheck</c> із <c>SubmitSheetHandler</c> — червоний.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task B80_подання_за_незатвердженого_місяця_джерела_відмовляє_з_ключем_і_деталями()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        var gate = Gate(Missing());

        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Submit(world, db, Access(), sourceMonthGate: gate)
                    .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None))
            .ConfigureAwait(true);

        AssertRefusal(error);
        await gate.Received(1).FindUnapprovedSourceMonthAsync(world.DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>());
        Assert.NotEqual(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
        Assert.Equal(0, await SnapshotsAsync(world.DocumentId).ConfigureAwait(true));
    }

    /// <summary>Контроль: ворота мовчать (Any, без зв'язки, без документа року) — подання як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task B80_подання_без_перешкоди_джерела_проходить()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);

        await using var db = CreateContext();
        await Submit(world, db, Access(), sourceMonthGate: Gate(null))
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
    }

    /// <summary>
    /// Рішення N2b: і проміжний, і остаточний крок маршруту відмовляють (підписувати дані без затвердженого джерела
    /// немає сенсу), а відхилення — ні.
    /// </summary>
    /// <remarks>Мутація: прибрати виклик <c>SourceMonthApprovalCheck</c> з <c>ApproveSheetHandler</c> — червоний.</remarks>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, null)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task B80_затвердження_на_будь_якому_кроці_відмовляє_а_відхилення_проходить(int ordinal, int? next)
    {
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);
        var gate = Gate(Missing());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => ApproveWithGateAsync(world, Step(ordinal, next), gate, approved: true, reason: null))
            .ConfigureAwait(true);

        AssertRefusal(error);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));

        // Повернути на виправлення можна завжди.
        await ApproveWithGateAsync(world, Step(ordinal, next), gate, approved: false, reason: "B80: джерело ще не затверджено")
            .ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Rejected, await StatusAsync(world).ConfigureAwait(true));
    }

    /// <summary>Контроль: ворота мовчать — остаточне затвердження як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task B80_затвердження_без_перешкоди_джерела_проходить()
    {
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);

        await ApproveWithGateAsync(world, Step(ordinal: 2, next: null), Gate(null), approved: true, reason: null)
            .ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Approved, await StatusAsync(world).ConfigureAwait(true));
    }

    private static UnapprovedSourceMonth Missing()
        => new(SourceDocumentId, SourcePeriodKey, "HSE400_2099", "DOC-HSE400");

    private static ISourceMonthApprovalGate Gate(UnapprovedSourceMonth? result)
    {
        var gate = Substitute.For<ISourceMonthApprovalGate>();
        gate.FindUnapprovedSourceMonthAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return gate;
    }

    private static void AssertRefusal(BusinessRuleException error)
    {
        Assert.Equal(ErrorCodes.SubmitBlocked, error.ErrorCode);
        var details = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(error.Details);
        Assert.Equal(SourceMonthApprovalCheck.MessageKey, details["messageKey"]);
        Assert.Equal("err.ECR-SUB-4221.sourceMonthNotApproved", details["messageKey"]);
        Assert.Equal("9099001", details["sourceDocumentId"]);
        Assert.Equal("202601", details["sourcePeriodKey"]);
        Assert.Equal("HSE400_2099", details["sourceProjectCode"]);
        Assert.Equal("DOC-HSE400", details["sourceDocumentKey"]);
    }

    private async Task ApproveWithGateAsync(
        World world, ApprovalStepView step, ISourceMonthApprovalGate gate, bool approved, string? reason)
    {
        await using var db = CreateContext();
        await new ApproveSheetHandler(
                new WorkflowStore(db), AccessAt(step), new UnitOfWork(db), Approver(), new TestClock(Now), new AuditWriter(db), Documents(world),
                sourceMonthGate: gate)
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, approved, reason, CancellationToken.None)
            .ConfigureAwait(false);
    }
}
