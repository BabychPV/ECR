// tests/Ecr.Application.Tests/Periods/PeriodOffsetsAdministrationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// ФВ-1.6: offsets переходів періоду (відкриття, <c>Grace</c>, закриття)
/// налаштовує адміністратор — і налаштоване справді рухає межі періоду.
/// </summary>
/// <remarks>
/// ⚠ Доводиться обидві половини вимоги: «налаштовує» (обробник приймає зміну
/// від власника <c>Project.Manage</c> і відмовляє решті) і «переходів
/// періоду» (нові значення доходять до <c>Period.RecomputeBoundaries</c>).
/// Тест, що перевіряє лише DTO, пережив би політику, яку ніхто не читає.
///
/// Мутаційні докази:
/// <list type="bullet">
/// <item>у <c>PeriodPolicy.ApplyOffsets</c> прибрати <c>OpenOffsetDays = openOffsetDays;</c>
/// — перший тест червоніє на межі відкриття;</item>
/// <item>в <c>UpdatePeriodPolicyHandler.HandleAsync</c> прибрати
/// <c>PermissionCheck.RequireAsync(…)</c> — другий тест червоніє.</item>
/// </list>
/// </remarks>
public sealed class PeriodOffsetsAdministrationTests
{
    private const int AdminId = 9;
    private const int PolicyId = 1;

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly PeriodPolicy _policy = new(EcrCode.Create("STD"), 0, 15, 45, 45);

    public PeriodOffsetsAdministrationTests()
    {
        _user.UserId.Returns(AdminId);
        _periods.GetPolicyAsync(PolicyId, Arg.Any<CancellationToken>()).Returns(_policy);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task Адміністратор_змінює_offsets_і_межі_періоду_зсуваються_за_ними()
    {
        _access.BuildProfileAsync(AdminId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = AdminId }.Permission("Project.Manage").Build());

        var handler = new UpdatePeriodPolicyHandler(_periods, _access, _user, _uow);

        await handler.HandleAsync(
            PolicyId, openOffsetDays: -3, graceOffsetDays: 10, hardCloseOffsetDays: 30,
            yearGraceOffsetDays: 45, CancellationToken.None);

        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        var period = new Period(
            projectId: 1, new PeriodKey(202603), sequence: 3,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        period.RecomputeBoundaries(_policy, TimeZoneInfo.Utc);

        // Відкриття — за три дні ДО початку, Grace — через 10 днів після кінця,
        // жорстке закриття — через 30. Типова політика (0/15/45) дала б інші
        // три дати, тож збіг випадковим бути не може.
        Assert.Equal(new DateTime(2026, 2, 26, 0, 0, 0, DateTimeKind.Utc), period.ComputedOpenAt);
        Assert.Equal(new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc), period.ComputedGraceAt);
        Assert.Equal(new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc), period.ComputedCloseAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task Без_права_адміністратора_offsets_не_змінюються()
    {
        _access.BuildProfileAsync(AdminId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = AdminId }.Permission("Document.View").Build());

        var handler = new UpdatePeriodPolicyHandler(_periods, _access, _user, _uow);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(
                PolicyId, openOffsetDays: -3, graceOffsetDays: 10, hardCloseOffsetDays: 30,
                yearGraceOffsetDays: 45, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);

        // Політика та сама, що до виклику, і нічого не збережено.
        Assert.Equal(0, _policy.OpenOffsetDays);
        Assert.Equal(15, _policy.GraceOffsetDays);
        Assert.Equal(45, _policy.HardCloseOffsetDays);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }
}
