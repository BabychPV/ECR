// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.Recall.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// <c>BE-31</c>: відкликання подання автором — справжня база, справжній обробник.
/// </summary>
/// <remarks>
/// ⚠ Маршрутів у базі стенда немає, тож «перший крок» для обробника —
/// <c>null</c>. Подання без маршруту лишає <c>CurrentStepId = null</c> (можна
/// відкликати); подання з кроком і підписом зсуває його — і це вже відмова.
/// </remarks>
public sealed partial class WorkflowTransactionTests
{
    private const string RecallReason = "подав не той місяць";
    private const int Colleague = 10;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відкликання_автором_повертає_чернетку_і_лишає_подію_з_причиною()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        await SubmitWithoutRouteAsync(world).ConfigureAwait(true);

        await RecallAsync(world, UserId, RecallReason).ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(world).ConfigureAwait(true));

        var events = await EventsAsync(world).ConfigureAwait(true);
        Assert.Equal([ApprovalAction.Submit, ApprovalAction.Recall], events.Select(e => e.Action));

        var recalled = events[1];
        Assert.Equal((DocumentStatus.Submitted, DocumentStatus.Draft), (recalled.FromStatus, recalled.ToStatus));
        Assert.Equal(RecallReason, recalled.Reason);
        Assert.Equal(UserId, recalled.ByUserId);

        // Зріз подання — історичний факт: відкликання його не прибирає.
        Assert.Equal(1, await SnapshotsAsync(world.DocumentId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колега_з_тим_самим_грантом_відкликати_чуже_подання_не_може()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        await SubmitWithoutRouteAsync(world).ConfigureAwait(true);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => RecallAsync(world, Colleague, RecallReason)).ConfigureAwait(true);

        Assert.Equal("err.ECR-ACCS-0403.recallNotAuthor", error.Details!["messageKey"]);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
        Assert.Single(await EventsAsync(world).ConfigureAwait(true));

        // І кнопки колезі сервер не обіцяє, а авторові — обіцяє.
        Assert.False(await CanRecallAsync(world, Colleague).ConfigureAwait(true));
        Assert.True(await CanRecallAsync(world, UserId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Після_підписаного_кроку_відкликання_відхиляється_409()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        await SubmitAsync(world).ConfigureAwait(true);
        await ApproveAsync(world, Step(ordinal: 1, next: 2), approved: true, reason: null).ConfigureAwait(true);

        // ⚠ Тип, а не лише код: саме з нього береться HTTP 409 (голий `DomainException` — 422).
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => RecallAsync(world, UserId, RecallReason)).ConfigureAwait(true);

        Assert.Equal("ECR-DOC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-DOC-0409.recallStepSigned", error.Details!["messageKey"]);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
        Assert.False(await CanRecallAsync(world, UserId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_причини_і_без_рівня_Submit_відкликання_відхиляється()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        await SubmitWithoutRouteAsync(world).ConfigureAwait(true);

        var noReason = await Assert.ThrowsAsync<DomainException>(
            () => RecallAsync(world, UserId, "  ")).ConfigureAwait(true);
        Assert.Equal("ECR-DOC-0422", noReason.ErrorCode);

        var noGrant = await Assert.ThrowsAsync<AccessDeniedException>(
            () => RecallAsync(world, UserId, RecallReason, GrantLevel.Write)).ConfigureAwait(true);
        Assert.Equal("err.ECR-ACCS-0403.recallDenied", noGrant.Details!["messageKey"]);

        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
    }

    // ────────────────────────────── дії ───────────────────────────────

    private async Task SubmitWithoutRouteAsync(World world)
    {
        await using var db = CreateContext();
        await Submit(world, db, AccessAt(step: null))
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task RecallAsync(World world, int userId, string reason, GrantLevel level = GrantLevel.Submit)
    {
        await using var db = CreateContext();
        await Recall(world, db, userId, level)
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, reason, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<bool> CanRecallAsync(World world, int userId)
    {
        await using var db = CreateContext();
        var answer = await Recall(world, db, userId, GrantLevel.Submit)
            .CanRecallAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None)
            .ConfigureAwait(false);

        return answer.CanRecall;
    }

    private static RecallSheetHandler Recall(World world, EcrDbContext db, int userId, GrantLevel level)
    {
        var documents = Documents(world);
        documents.HasSheetAsync(world.DocumentId, world.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.GetTemplateVersionIdAsync(world.DocumentId, Arg.Any<CancellationToken>())
                 .Returns(world.TemplateVersionId);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(userId, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = userId }
                  .Grant(ResourceKind.Project, world.ProjectId, level).Build());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);

        return new RecallSheetHandler(
            new WorkflowStore(db), documents, access, new ReportSnapshotSync(NoSnapshots(), documents),
            new UnitOfWork(db), user, new TestClock(Now));
    }
}
