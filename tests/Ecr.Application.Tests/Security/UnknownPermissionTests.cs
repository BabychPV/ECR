// tests/Ecr.Application.Tests/Security/UnknownPermissionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Код права, якого немає в каталозі, відхиляє створення ролі (<c>H-21</c>,
/// пункт 4 директиви №06 §5).
/// </summary>
/// <remarks>
/// ⛔ <c>IUserStore.FilterUnknownAsync</c> існував і не мав жодного викликача —
/// той самий клас дефекту, що й `A7-55`: механізм оголошений, покритий тестом
/// і недосяжний.
///
/// ⚠ Наслідок був не «роль зберігається з порожніми правами», як записано в
/// реєстрі недосяжних механізмів, а гірший на вигляд і той самий по суті:
/// зовнішній ключ <c>FK_RolePerm_Perm</c> валив збереження в SQL, і
/// адміністратор отримував <c>500</c> без жодної згадки, ЯКИЙ саме код
/// хибний. Причина лишалася в журналі бази — тобто питання все одно
/// приходило до розробника.
/// </remarks>
public sealed class UnknownPermissionTests
{
    private const int Actor = 7;
    private static readonly DateTime Now = new(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public UnknownPermissionTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Actor);
        _user.CorrelationId.Returns("test");

        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission("Security.ManageRoles").Build());

        // ⛔ Каталог наповнений НАВМИСНО. Порожній набір у фікстурі означає
        // «тест не моделює каталог», і тоді невідомих прав немає за
        // визначенням — тест не перевіряв би нічого.
        _users.Permissions.Add("Template.View");
        _users.Permissions.Add("Document.View");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    [Trait("Requirement", "ФВ-6.3")]
    public async Task Невідомий_код_права_відхиляє_весь_набір()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(
                "Publishers",
                new Dictionary<string, string> { ["en"] = "Publishers" },
                ["Template.View", "Template.Publsh"],
                CancellationToken.None));

        // ⚠ У повідомленні — сам хибний код: інакше адміністратор не знає, що
        // саме виправити, і питання приходить до розробника.
        Assert.Contains("Template.Publsh", error.Message, StringComparison.Ordinal);

        // Роль не заведена: часткове створення лишило б назву зайнятою, а
        // повторити спробу було б уже нічим.
        Assert.Empty(_users.Roles);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    public async Task Відомі_коди_прав_роль_створюють()
    {
        await Handler().HandleAsync(
            "Viewers",
            new Dictionary<string, string> { ["en"] = "Viewers" },
            ["Template.View", "Document.View"],
            CancellationToken.None);

        var role = Assert.Single(_users.Roles);
        Assert.Equal("Viewers", role.Code);
    }

    private CreateRoleHandler Handler() => new(_users, _access, _uow, _audit, _user, _clock);
}
