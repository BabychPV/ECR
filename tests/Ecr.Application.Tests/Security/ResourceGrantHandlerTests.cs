using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Розв'язання назви ресурсу в переліку грантів ролі (<c>Q-299</c>).
/// </summary>
/// <remarks>
/// ⛔ До цієї картки <c>GET …/roles/{id}/grants</c> віддавав голий
/// <c>resourceId</c>: адміністратор бачив «Sheet 501» і не мав способу
/// дізнатися, що це за аркуш, не перебираючи вручну версії шаблонів. Тепер
/// обробник резолвить назву через <see cref="IResourceNameResolver"/> — тут
/// перевіряється саме ЙОГО поведінка (виклик, підстановка, порожній
/// результат), а не сам резолвер (той — інфраструктурна річ, живий SQL-тест
/// у <c>Ecr.Infrastructure.Tests</c>).
/// </remarks>
public sealed class ResourceGrantHandlerTests
{
    private readonly FakeUserStore _users = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly Ecr.Application.Common.ICurrentUser _currentUser =
        Substitute.For<Ecr.Application.Common.ICurrentUser>();
    private readonly IResourceNameResolver _names = Substitute.For<IResourceNameResolver>();

    public ResourceGrantHandlerTests()
    {
        _currentUser.UserId.Returns(1);
        _access.BuildProfileAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 1 }.Permission(ListResourceGrantsHandler.Permission).Build());

        _users.Roles.Add(new RoleView(10, "Auditor", true, true, [], []));
    }

    [Fact]
    public async Task Гранти_повертаються_з_розвязаною_назвою_ресурсу()
    {
        _users.GrantsByRole[10] =
        [
            new ResourceGrantDto(ResourceKind.Sheet, 501, GrantLevel.Read, IsDeny: false),
            new ResourceGrantDto(ResourceKind.Project, 7, GrantLevel.Manage, IsDeny: false),
        ];

        _names
            .ResolveAsync(Arg.Any<IReadOnlyCollection<(ResourceKind Kind, int Id)>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(ResourceKind, int), string>
            {
                [(ResourceKind.Sheet, 501)] = "BS",
                [(ResourceKind.Project, 7)] = "PRJ-2026",
            });

        var result = await Handler().HandleAsync(10, CancellationToken.None);

        Assert.Equal("BS", result.Single(g => g.ResourceKind == ResourceKind.Sheet).ResourceName);
        Assert.Equal("PRJ-2026", result.Single(g => g.ResourceKind == ResourceKind.Project).ResourceName);

        // ⚠ Джерело правди про сам грант (kind/id/level/isDeny) лишається
        // недоторканим — резолвер лише ДОДАЄ поле, не підміняє решту.
        var sheetGrant = result.Single(g => g.ResourceKind == ResourceKind.Sheet);
        Assert.Equal(501, sheetGrant.ResourceId);
        Assert.Equal(GrantLevel.Read, sheetGrant.Level);
        Assert.False(sheetGrant.IsDeny);
    }

    [Fact]
    public async Task Осиротіле_посилання_дає_null_а_не_вигадану_назву()
    {
        // ⛔ Ресурс, якого вже немає (видалений TableDef чи неіснуючий id) —
        // резолвер не знаходить пари в результаті. Обробник ПОКАЗУЄ це як
        // `null`, а не мовчки пропускає грант чи підставляє щось на кшталт
        // "Table 999": порожній стан має бути видимий, а не замаскований.
        _users.GrantsByRole[10] =
        [
            new ResourceGrantDto(ResourceKind.Table, 999, GrantLevel.Write, IsDeny: false),
        ];

        _names
            .ResolveAsync(Arg.Any<IReadOnlyCollection<(ResourceKind Kind, int Id)>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(ResourceKind, int), string>());

        var result = await Handler().HandleAsync(10, CancellationToken.None);

        Assert.Null(Assert.Single(result).ResourceName);
    }

    [Fact]
    public async Task Порожній_перелік_грантів_не_звертається_до_резолвера()
    {
        // ⚠ Немає грантів — немає що резолвити. Зайвий виклик тут — не
        // забаганка стилю: живий резолвер б'є в БД, і виклик на порожньому
        // переліку (роль щойно створена, грантів ще нуль) коштував би запиту,
        // з якого нема чого повернути.
        var result = await Handler().HandleAsync(10, CancellationToken.None);

        Assert.Empty(result);
        _ = _names.DidNotReceive().ResolveAsync(
            Arg.Any<IReadOnlyCollection<(ResourceKind Kind, int Id)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Без_права_ManageRoles_перелік_грантів_відхиляється()
    {
        _access.BuildProfileAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 1 }.Build());

        _users.GrantsByRole[10] = [new ResourceGrantDto(ResourceKind.Project, 7, GrantLevel.Read, IsDeny: false)];

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(10, CancellationToken.None));

        _ = _names.DidNotReceive().ResolveAsync(
            Arg.Any<IReadOnlyCollection<(ResourceKind Kind, int Id)>>(), Arg.Any<CancellationToken>());
    }

    private ListResourceGrantsHandler Handler() => new(_users, _access, _currentUser, _names);
}
