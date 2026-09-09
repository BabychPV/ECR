// tests/Ecr.Application.Tests/Security/PermissionCatalogTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Повний каталог прав — усі, а не лише вже оголошені в наявних ролях
/// (директива №11, трек T2, `#19`).
/// </summary>
/// <remarks>
/// ⛔ До цього ендпоінта клієнт (<c>SecurityPage.tsx</c>) складав перелік прав
/// перетином того, що вже оголошено в наявних ролях —
/// <c>[...new Set(roles.flatMap(r =&gt; r.permissions))]</c>. Право, якого ще
/// жодна роль не отримала, не існувало для форми створення ролі взагалі.
/// Тест <see cref="Каталог_показує_право_яке_не_має_жодного_носія"/> — це саме
/// та поведінка, якої раніше не було: фікстура наповнює каталог правом, що не
/// входить у ЖОДНУ роль, і перевіряє, що воно все одно повертається.
/// </remarks>
public sealed class PermissionCatalogTests
{
    private const int Actor = 11;

    private readonly FakeUserStore _users = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public PermissionCatalogTests()
    {
        _user.UserId.Returns(Actor);

        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission("Security.ManageRoles").Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Каталог_показує_право_яке_не_має_жодного_носія()
    {
        // ⛔ Каталог наповнений навмисно ширше за те, що видано будь-якій ролі:
        // жодна роль (`_users.Roles` порожній) не має `Report.EditDefinition`,
        // а каталог усе одно зобов'язаний його показати.
        _users.Permissions.Add("Template.View");
        _users.Permissions.Add("Report.EditDefinition");
        _users.Dangerous.Add("Report.EditDefinition");

        Assert.Empty(_users.Roles);

        var result = await Handler().HandleAsync(CancellationToken.None);

        Assert.Contains(result, p => p.Code == "Report.EditDefinition");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Небезпечне_право_позначене_окремим_прапорцем()
    {
        _users.Permissions.Add("Template.View");
        _users.Permissions.Add("Calculation.Publish");
        _users.Dangerous.Add("Calculation.Publish");

        var result = await Handler().HandleAsync(CancellationToken.None);

        var dangerous = Assert.Single(result, p => p.Code == "Calculation.Publish");
        Assert.True(dangerous.IsDangerous);

        var safe = Assert.Single(result, p => p.Code == "Template.View");
        Assert.False(safe.IsDangerous);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_права_ManageRoles_каталог_не_віддається()
    {
        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission("Template.View").Build());

        _users.Permissions.Add("Template.View");

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(CancellationToken.None));
    }

    private ListPermissionsHandler Handler() => new(_users, _access, _user);
}
