// tests/Ecr.Application.Tests/Security/GetCurrentUserHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// <c>GetCurrentUserHandler</c> і рівність <c>LevelFor</c>/<c>LevelForProject</c>
/// над однаковими вхідними даними (<c>Q-188</c>).
/// </summary>
/// <remarks>
/// ⛔ До <c>Q-188</c> <c>AccessProfile.LevelFor</c> (доменна форма) і
/// <c>GetCurrentUserHandler.LevelForProject</c> (серіалізована у рядки форма
/// для клієнта) були двома незалежними реалізаціями того самого правила
/// «заборона виграє, інакше грант, інакше <c>None</c>». Тести нижче не
/// звіряють жоден метод із заздалегідь захардкодженим числом — вони
/// звіряють один метод з іншим на тих самих вихідних грантах/заборонах,
/// пропущених через справжню серіалізацію (<see cref="GetCurrentUserHandler.HandleAsync"/>).
/// Розбіжність у порядку перевірки чи форматі ключа в одній з реалізацій
/// провалила б це порівняння — саме той дрейф, що вже стався з
/// <c>DenyReason</c> на клієнті до <c>A7-02</c>.
/// </remarks>
public sealed class GetCurrentUserHandlerTests
{
    private const int Actor = 5;

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public GetCurrentUserHandlerTests()
    {
        _user.UserId.Returns(Actor);
        _user.UserName.Returns("nakusergiy");
        _user.Language.Returns("uk");
    }

    public static TheoryData<string, GrantLevel?, bool> Сценарії => new()
    {
        // назва · грант (null — немає) · заборона
        { "лише_грант", GrantLevel.Write, false },
        { "заборона_виграє_над_грантом", GrantLevel.Manage, true },
        { "заборона_без_гранта", null, true },
        { "ані_гранта_ані_заборони", null, false },
        { "мінімальний_грант", GrantLevel.Read, false },
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.6")]
    [MemberData(nameof(Сценарії))]
    public async Task LevelFor_і_LevelForProject_погоджуються_на_тих_самих_даних(
        string _, GrantLevel? grant, bool denied)
    {
        var builder = new AccessBuilder { UserId = Actor };
        if (grant is { } level)
        {
            builder.Grant(ResourceKind.Project, AccessBuilder.ProjectId, level);
        }
        if (denied)
        {
            builder.Deny(ResourceKind.Project, AccessBuilder.ProjectId);
        }

        var profile = builder.Build();
        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>()).Returns(profile);

        // Джерело істини — доменний метод. Клієнтська проєкція мусить дати
        // рівно те саме, пройшовши через справжню серіалізацію запиту.
        var expected = profile.LevelFor(ResourceKind.Project, AccessBuilder.ProjectId);

        var view = await new GetCurrentUserHandler(_access, _user).HandleAsync(CancellationToken.None);
        var actual = GetCurrentUserHandler.LevelForProject(view, AccessBuilder.ProjectId);

        Assert.Equal(expected, actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Кілька_проєктів_одночасно_дають_однакову_відповідь_в_обох_методах()
    {
        // Три проєкти в одному профілі: грант, заборона поверх гранта,
        // і жодного запису — саме та комбінація, де порядок перевірки
        // (deny → grant → None) уперше почав би мати значення, якби
        // реалізації розійшлися.
        const int Granted = AccessBuilder.ProjectId;
        const int DeniedOverGrant = AccessBuilder.ProjectId + 1;
        const int Untouched = AccessBuilder.ProjectId + 2;

        var profile = new AccessBuilder { UserId = Actor }
            .Grant(ResourceKind.Project, Granted, GrantLevel.Manage)
            .Grant(ResourceKind.Project, DeniedOverGrant, GrantLevel.Manage)
            .Deny(ResourceKind.Project, DeniedOverGrant)
            .Build();

        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>()).Returns(profile);
        var view = await new GetCurrentUserHandler(_access, _user).HandleAsync(CancellationToken.None);

        foreach (var projectId in new[] { Granted, DeniedOverGrant, Untouched })
        {
            Assert.Equal(
                profile.LevelFor(ResourceKind.Project, projectId),
                GetCurrentUserHandler.LevelForProject(view, projectId));
        }

        // Значення самі по собі теж мають сенс, не лише збіг між методами.
        Assert.Equal(GrantLevel.Manage, GetCurrentUserHandler.LevelForProject(view, Granted));
        Assert.Equal(GrantLevel.None, GetCurrentUserHandler.LevelForProject(view, DeniedOverGrant));
        Assert.Equal(GrantLevel.None, GetCurrentUserHandler.LevelForProject(view, Untouched));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Профіль_доходить_до_клієнтської_проєкції_без_викривлень()
    {
        var profile = new AccessBuilder { UserId = Actor }
            .Permission("Security.ManageUsers")
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
            .Build();

        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>()).Returns(profile);

        var view = await new GetCurrentUserHandler(_access, _user).HandleAsync(CancellationToken.None);

        Assert.Equal(Actor, view.UserId);
        Assert.Equal("nakusergiy", view.UserName);
        Assert.Contains("Security.ManageUsers", view.Permissions);
        Assert.Equal(profile.IsSimulation, view.IsSimulation);
    }
}
