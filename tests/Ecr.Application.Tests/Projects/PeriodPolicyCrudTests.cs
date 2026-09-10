using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Projects;

/// <summary>
/// CRUD політик періодів (T6/#37). До цих обробників завести чи змінити
/// політику можна було лише сідингом або рукою DBA.
/// </summary>
public sealed class PeriodPolicyCrudTests
{
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public PeriodPolicyCrudTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Project.Manage").Build());
        _periods.ListPoliciesAsync(Arg.Any<CancellationToken>()).Returns(new List<PeriodPolicy>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Створення_додає_політику_з_поточними_offsets()
    {
        var handler = new CreatePeriodPolicyHandler(_periods, _access, _user, _uow);

        var dto = await handler.HandleAsync("ECR_Long", 0, 15, 60, 90, CancellationToken.None);

        Assert.Equal("ECR_Long", dto.Code);
        Assert.Equal(90, dto.YearGraceOffsetDays);
        _periods.Received(1).AddPolicy(Arg.Is<PeriodPolicy>(p => p.Code == "ECR_Long"));
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Зайнятий_код_дає_ECR_PRD_4091_а_не_падає_на_UNIQUE()
    {
        _periods.ListPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PeriodPolicy> { new(EcrCode.Create("ECR_Standard"), 0, 15, 45, 45) });

        var handler = new CreatePeriodPolicyHandler(_periods, _access, _user, _uow);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync("ECR_Standard", 0, 15, 45, 45, CancellationToken.None));

        Assert.Equal(ErrorCodes.PeriodPolicyDuplicate, error.ErrorCode);
        _periods.DidNotReceiveWithAnyArgs().AddPolicy(default!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D-134")]
    public async Task D_134_Створення_з_грейсом_довшим_за_жорстке_закриття_відхиляється()
    {
        var handler = new CreatePeriodPolicyHandler(_periods, _access, _user, _uow);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync("BAD", 0, graceOffsetDays: 50, hardCloseOffsetDays: 45,
                yearGraceOffsetDays: 45, CancellationToken.None));

        Assert.Equal("ECR-PRD-4225", error.ErrorCode);
        _periods.DidNotReceiveWithAnyArgs().AddPolicy(default!);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Оновлення_змінює_offsets_наявної_політики()
    {
        var policy = new PeriodPolicy(EcrCode.Create("STD"), 0, 15, 45, 45);
        _periods.GetPolicyAsync(1, Arg.Any<CancellationToken>()).Returns(policy);

        var handler = new UpdatePeriodPolicyHandler(_periods, _access, _user, _uow);
        var dto = await handler.HandleAsync(1, 2, 20, 60, 90, CancellationToken.None);

        Assert.Equal(2, dto.OpenOffsetDays);
        Assert.Equal(20, dto.GraceOffsetDays);
        Assert.Equal(60, dto.HardCloseOffsetDays);
        Assert.Equal(90, dto.YearGraceOffsetDays);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D-134")]
    public async Task D_134_Оновлення_з_грейсом_довшим_за_жорстке_закриття_відхиляється()
    {
        var policy = new PeriodPolicy(EcrCode.Create("STD"), 0, 15, 45, 45);
        _periods.GetPolicyAsync(1, Arg.Any<CancellationToken>()).Returns(policy);

        var handler = new UpdatePeriodPolicyHandler(_periods, _access, _user, _uow);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(1, 0, graceOffsetDays: 100, hardCloseOffsetDays: 45,
                yearGraceOffsetDays: 45, CancellationToken.None));

        Assert.Equal("ECR-PRD-4225", error.ErrorCode);

        // ⚠ Відмова не змінює стан наявної політики — часткове застосування
        // залишило б її в суперечливому вигляді.
        Assert.Equal(15, policy.GraceOffsetDays);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Оновлення_неіснуючої_політики_дає_ECR_PRD_0422()
    {
        _periods.GetPolicyAsync(999, Arg.Any<CancellationToken>())
            .Returns<PeriodPolicy>(_ => throw new NotFoundException(
                "ECR-PRD-0422", "Політику періодів 999 не знайдено."));

        var handler = new UpdatePeriodPolicyHandler(_periods, _access, _user, _uow);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(999, 0, 15, 45, 45, CancellationToken.None));

        Assert.Equal("ECR-PRD-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Без_права_Project_Manage_створення_відхиляється()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());

        var handler = new CreatePeriodPolicyHandler(_periods, _access, _user, _uow);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync("NEW", 0, 15, 45, 45, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }
}
