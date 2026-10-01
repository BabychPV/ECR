// tests/Ecr.Application.Tests/Templates/PublishReasonRequiredTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ФВ-14.7: публікація ВЕРСІЇ ШАБЛОНУ без причини відхиляється кодом
/// <c>ECR-TMPL-0422</c> з ключем <c>publishReasonRequired</c> — до будь-якого
/// блокування й запису (публікація версії звіту — окремо, див. <c>03e02ed3</c>).
/// </summary>
/// <remarks>
/// Мутаційний доказ (прогнано): у <c>PublishTemplateVersionHandler.PublishAsync</c>
/// прибрати гілку <c>string.IsNullOrWhiteSpace(reason)</c> → обидва випадки
/// червоні (виняток не кидається, далі йде блокування версії).
/// </remarks>
public sealed class PublishReasonRequiredTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-14.7")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Публікація_шаблону_без_причини_відхиляється_з_publishReasonRequired(string reason)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(PublishTemplateVersionHandler.Permission).Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        var versionStore = Substitute.For<ITemplateVersionStore>();
        var uow = Substitute.For<IUnitOfWork>();
        var audit = Substitute.For<IAuditWriter>();

        var handler = new PublishTemplateVersionHandler(
            Substitute.For<IRepository<TemplateVersion, int>>(),
            versionStore,
            Substitute.For<IFormulaEngine>(),
            Substitute.For<ICalculationBindingStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<IUnitCatalog>(),
            access,
            user,
            audit,
            uow,
            Substitute.For<IClock>(),
            Substitute.For<IReportViewGenerator>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.PublishAsync(1, userId: 9, reason, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.publishReasonRequired", error.Details!["messageKey"]);

        // Відмова до транзакції: ні блокування версії, ні збереження, ні аудиту.
        await versionStore.DidNotReceiveWithAnyArgs().LockVersionForUpdateAsync(default, default);
        await uow.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), default);
        Assert.Empty(audit.ReceivedCalls());
    }
}
