using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// Налаштування маршруту погодження проєкту (ФВ-5.17).
/// </summary>
/// <remarks>
/// ⛔ Головне тут — що маршрут узагалі МОЖНА завести. Сутність і таблиці
/// існували від Етапу 3, а способу наповнення не було: список кроків
/// приватний, ендпоінта немає, seed нічого не створює.
/// </remarks>
public sealed class ApprovalRouteHandlerTests
{
    private const int ProjectId = 10;

    private readonly IWorkflowStore _workflow = Substitute.For<IWorkflowStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ApprovalRouteHandlerTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Project.Manage").Build());

        _workflow.RoleExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Маршрут_створюється_з_переліку_ролей()
    {
        var steps = await Replace([11, 22, 33]);

        Assert.Equal(3, steps);

        var route = Added();
        Assert.Equal(ProjectId, route.ProjectId);
        Assert.Equal([11, 22, 33], route.Steps.OrderBy(s => s.Ordinal).Select(s => s.RoleId));
        Assert.Equal([1, 2, 3], route.Steps.OrderBy(s => s.Ordinal).Select(s => s.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Порожній_перелік_прибирає_маршрут()
    {
        // ⛔ Без цього маршрут, заведений помилково, лишався б назавжди, і
        // повернути одноетапне затвердження було б нічим.
        var existing = Route();
        _workflow.FindProjectRouteAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(existing);

        var steps = await Replace([]);

        Assert.Equal(0, steps);

        // ⚠ Порівняння за ПОСИЛАННЯМ, а не через `Received(...)` з аргументом:
        // `Entity<TId>.Equals` навмисно повертає `false` для нового запису
        // (`Id = 0`) — навіть для нього самого. Це правильна семантика
        // тотожності, але зіставлення аргументів NSubstitute спирається саме
        // на `Equals`, і перевірка мовчки не спрацювала б.
        var removed = _workflow.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IWorkflowStore.RemoveRouteAsync))
            .Select(c => c.GetArguments()[0])
            .ToList();

        Assert.Same(existing, Assert.Single(removed));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Неіснуюча_роль_відхиляє_весь_набір()
    {
        // ⛔ Крок на неіснуючу роль дав би маршрут, який неможливо пройти:
        // документ подали б і не затвердили ніколи, а причина була б видима
        // лише в базі.
        _workflow.RoleExistsAsync(22, Arg.Any<CancellationToken>()).Returns(false);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => Replace([11, 22]));

        Assert.Equal("ECR-SEC-0404", error.ErrorCode);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Дві_однакові_ролі_поспіль_відхиляються()
    {
        // ⚠ Та сама роль на двох різних кроках законна — наприклад, до і
        // після розрахунку. А поспіль другий крок пройде той самий
        // користувач одразу за першим, тобто погодження не додасться.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Replace([11, 11]));

        Assert.Equal("ECR-DOC-0422", error.ErrorCode);

        // Та сама роль через крок — проходить.
        Assert.Equal(3, await Replace([11, 22, 11]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Проєкт_без_маршруту_повертає_порожній_опис_а_не_помилку()
    {
        // ⚠ «Маршруту немає» — стан налаштування, а не помилка. `404` змусив
        // би клієнт розрізняти його від «проєкту немає» за тим самим кодом.
        _workflow.FindProjectRouteAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns((ApprovalRoute?)null);

        var dto = await new GetApprovalRouteHandler(_workflow, _access, _user)
            .HandleAsync(ProjectId, CancellationToken.None);

        Assert.False(dto.HasRoute);
        Assert.Empty(dto.Steps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Без_права_Project_Manage_маршрут_не_змінюється()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Replace([11]));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private Task<int> Replace(int[] roleIds)
        => new ReplaceApprovalRouteHandler(_workflow, _access, _uow, _user)
            .HandleAsync(ProjectId, roleIds, CancellationToken.None);

    /// <summary>Маршрут, який обробник передав сховищу.</summary>
    private ApprovalRoute Added()
    {
        var call = _workflow.ReceivedCalls()
            .Last(c => c.GetMethodInfo().Name == nameof(IWorkflowStore.AddRouteAsync));

        return (ApprovalRoute)call.GetArguments()[0]!;
    }

    private static ApprovalRoute Route()
        => new(
            EcrCode.Create($"PRJ_{ProjectId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Route" }),
            ProjectId);
}
