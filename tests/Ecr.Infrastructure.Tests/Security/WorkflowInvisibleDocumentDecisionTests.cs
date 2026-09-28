// tests/Ecr.Infrastructure.Tests/Security/WorkflowInvisibleDocumentDecisionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// S2: рішення служби про подання, затвердження й повернення в роботу для
/// НЕВИДИМОГО документа — <see cref="EditDenyReason.NoGrant"/>, а не причина
/// про стан аркуша.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ для <c>DenyIfInvisible</c> у
/// <see cref="AccessDecisionService"/>, по одному на метод. Обробники перед
/// службою тепер самі дають <c>404</c> (<c>DocumentVisibility</c>), тож через
/// HTTP ця перевірка не видна — тому тут, на рівні служби з реальною базою.
/// Стан аркуша підібрано так, щоб без перевірки відмова була, але з причиною
/// про стан: approve і reopen на <c>Draft</c> → <c>BusinessRule</c>, submit на
/// <c>Submitted</c> → <c>DocumentSubmitted</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class WorkflowInvisibleDocumentDecisionTests(SqlServerFixture sql)
{
    [Theory]
    [InlineData("approve")]
    [InlineData("submit")]
    [InlineData("reopen")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S2")]
    public async Task Невидимий_документ_NoGrant_а_не_стан_аркуша(string action)
    {
        var document = await ArrangeAsync(submitted: action == "submit");

        // Грант на аркуш є, на проєкт документа — ні (лише на «інший» проєкт).
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, document.ProjectId + 100_000, GrantLevel.Read)
            .Grant(ResourceKind.Sheet, document.SheetDefId, GrantLevel.Approve)
            .Build();

        var decision = await DecideAsync(action, profile, document);

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.NoGrant, decision.Reason);
    }

    /// <summary>Контроль: для ВИДИМОГО документа причина про стан лишається.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S2")]
    public async Task Видимий_документ_зберігає_причину_про_стан_аркуша()
    {
        var document = await ArrangeAsync(submitted: false);

        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, document.ProjectId, GrantLevel.Read)
            .Grant(ResourceKind.Sheet, document.SheetDefId, GrantLevel.Approve)
            .Build();

        var decision = await DecideAsync("approve", profile, document);

        Assert.Equal(EditDenyReason.BusinessRule, decision.Reason);
    }

    private async Task<EditDecision> DecideAsync(string action, AccessProfile profile, TestDocument document)
    {
        await using var db = sql.CreateContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(profile.UserId);
        currentUser.GroupSids.Returns([]);

        var service = new AccessDecisionService(
            db,
            Substitute.For<IMetadataCache>(),
            new AccessProfileCache(memory),
            new TestClock(DateTime.UtcNow),
            currentUser,
            Substitute.For<IWorkflowStore>());

        return action switch
        {
            "approve" => await service.CanApproveAsync(
                profile, document.DocumentId, document.SheetDefId, document.PeriodKey, CancellationToken.None),
            "submit" => await service.CanSubmitAsync(
                profile, document.DocumentId, document.SheetDefId, document.PeriodKey, CancellationToken.None),
            _ => await service.CanReopenAsync(
                profile, document.DocumentId, document.SheetDefId, document.PeriodKey, CancellationToken.None),
        };
    }

    private async Task<TestDocument> ArrangeAsync(bool submitted)
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync();

        if (submitted)
        {
            await using var db = sql.CreateContext();
            var state = new ApprovalState(document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            state.Submit(1, new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        return document;
    }
}
