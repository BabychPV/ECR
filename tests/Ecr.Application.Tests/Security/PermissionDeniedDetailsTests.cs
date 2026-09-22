using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// <c>AccessDeniedException("ECR-AUTH-0403", ...)</c> кинутий
/// <see cref="CreateRoleHandler"/> і <see cref="CreateUserHandler"/> несе
/// словник <c>Details["permission"]</c> — те саме, що й решта п'яти
/// перевірок прав у <c>RoleAndUserHandlers.cs</c>.
/// </summary>
/// <remarks>
/// ⛔ Обидва обробники будували виняток ДВОМА аргументами (код, повідомлення)
/// — без третього, <c>Details</c>. <c>ExceptionHandlingMiddleware
/// .LocalizedDetailAsync</c> (Q-242) шукає саме <c>details["permission"]</c>,
/// щоб зібрати клієнтську <c>Detail</c> з каталогу
/// (<c>err.ECR-AUTH-0403.requiresPermission</c>); без словника він одразу
/// повертає сире `message`, написане українською для СЕРВЕРНОГО читача коду
/// — мовою, якої серед підтримних (en/ru/kz) немає. Тут перевіряється не сам
/// текст (це справа <c>LocalizedErrorTitleTests</c>-стилю тестів у
/// <c>Ecr.Api.Tests</c>), а те, що виняток ІЗ ЦИХ ДВОХ обробників несе
/// словник узагалі, — це і є те, чого бракувало.
/// </remarks>
public sealed class PermissionDeniedDetailsTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public PermissionDeniedDetailsTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _user.CorrelationId.Returns("test");

        // Профіль без жодного права: обидва обробники мають відмовити.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "Q-300")]
    public async Task CreateRoleHandler_відмова_несе_Details_з_кодом_права()
    {
        var handler = new CreateRoleHandler(_users, _access, _uow, _audit, _user, _clock);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(
                code: "Auditor",
                name: new Dictionary<string, string> { ["en"] = "Auditor" },
                permissionCodes: [],
                CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);
        Assert.NotNull(error.Details);
        Assert.Equal(ListRolesHandler.Permission, Assert.Contains("permission", error.Details!));

        // ⚠ Q-341: без цього поля подробиця доїжджає клієнту сирим
        // українським реченням незалежно від мови інтерфейсу.
        Assert.Equal("err.ECR-AUTH-0403.permission", Assert.Contains("messageKey", error.Details!));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "Q-300")]
    public async Task CreateUserHandler_відмова_несе_Details_з_кодом_права()
    {
        var handler = new CreateUserHandler(
            _users,
            _hasher,
            _access,
            new DisableBootstrapAdminHandler(_users, _uow, _audit, _user, _clock),
            _uow,
            _audit,
            _user,
            _clock);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(
                userName: "newuser",
                displayName: "Новий Користувач",
                provider: AuthProvider.Local,
                windowsSid: null,
                initialPassword: "Tengiz-2026-Password!",
                roleCodes: [],
                email: null,
                CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);
        Assert.NotNull(error.Details);
        Assert.Equal(ListUsersHandler.Permission, Assert.Contains("permission", error.Details!));

        // ⚠ Q-341: те саме поле, що й у CreateRoleHandler — той самий факт
        // («бракує права X»), і саме тому той самий ключ.
        Assert.Equal("err.ECR-AUTH-0403.permission", Assert.Contains("messageKey", error.Details!));
    }
}
