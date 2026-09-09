// tests/Ecr.Application.Tests/Security/AccessDiagnosticsTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Екран «Мої групи»: звідки взялися ролі — і чому не взялися (<c>H-21</c>).
/// </summary>
/// <remarks>
/// ⛔ Дефект, який тут лікується, не має жодного видимого прояву. Доменної
/// автентифікації в контурі немає, ролі доменних користувачів призначаються НА
/// ГРУПУ (<c>ФВ-6.15</c>), і в продуктиві більшість отримає нуль ролей —
/// виглядатиме це точнісінько як справна система без даних: людина входить,
/// бачить порожні переліки і йде до адміністратора, а той не має чим
/// відповісти.
///
/// ⚠ Найнебезпечніша половина — ЧУЖИЙ запис. Квитка його сесії в нас немає
/// (`P-02`), тож перелік груп там порожній ЗАВЖДИ. Порожній перелік без
/// пояснення читався б як «людина ні в яких групах не перебуває» — тобто
/// екран, заведений проти тиші, породив би власну неправду.
/// </remarks>
public sealed class AccessDiagnosticsTests
{
    private const int Actor = 9;
    private const string GroupWithRole = "S-1-5-21-100-200-300-1001";
    private const string GroupWithoutRole = "S-1-5-21-100-200-300-1002";
    private const string OwnSid = "S-1-5-21-100-200-300-500";

    private static readonly DateTime Now = new(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public AccessDiagnosticsTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Actor);
        _user.GroupSids.Returns([]);

        _users.Roles.Add(new RoleView(1, "DataEntry", true, true, [], []));
        _users.Roles.Add(new RoleView(2, "Approver", true, true, [], []));

        Profile("Security.ManageUsers");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Показані_ВСІ_SID_із_квитка_а_не_лише_ті_що_збіглися()
    {
        // ⛔ Саме всі. SID, який нічого не дав, і є відповіддю на «чому в мене
        // немає доступу»: з ним адміністратор іде до відділу AD. Перелік із
        // самих лише збігів у людини без прав був би порожній — тобто рівно та
        // тиша, проти якої екран заведений.
        var me = AddDomain("ivanov", OwnSid);
        _user.GroupSids.Returns([GroupWithRole, GroupWithoutRole]);
        _users.GroupAssignments.Add(new GroupRoleAssignment(GroupWithRole, "DataEntry"));

        var view = await Handler().HandleAsync(subjectUserId: null, CancellationToken.None);

        Assert.Equal(2, view.Groups.Count);
        Assert.True(view.GroupsFromTicket);
        Assert.Equal(OwnSid, view.PrincipalSid);

        var matched = Assert.Single(view.Groups, g => g.Matched);
        Assert.Equal(GroupWithRole, matched.Sid);
        Assert.Equal(["DataEntry"], matched.RoleCodes);

        Assert.Equal([GroupWithoutRole], view.UnmatchedSids);
        Assert.Equal(["DataEntry"], view.EffectiveRoleCodes);
        Assert.Equal(me.Id, view.UserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    [Trait("Requirement", "ФВ-6.15a")]
    public async Task Про_чужий_запис_система_каже_що_його_груп_НЕ_ЗНАЄ()
    {
        // ⛔ Найтонше місце кроку. Групи приходять із квитка, а квитка чужої
        // сесії в нас немає (`P-02`): порожній перелік тут означає «ми не
        // знаємо», а не «людина ні в яких групах не перебуває». Прапорець —
        // єдине, що розрізняє ці два стани.
        AddDomain("ivanov", OwnSid);
        var other = AddDomain("petrov", "S-1-5-21-100-200-300-777");

        _user.GroupSids.Returns([GroupWithRole]);
        _users.GroupAssignments.Add(new GroupRoleAssignment(GroupWithRole, "Approver"));

        var view = await Handler().HandleAsync(other.Id, CancellationToken.None);

        Assert.False(view.GroupsFromTicket);
        Assert.Empty(view.Groups);

        // ⚠ Свої групи в чужу відповідь не протікають: інакше адміністратор
        // читав би власне членство як чуже і закривав би питання хибно.
        Assert.Empty(view.EffectiveRoleCodes);

        // Натомість — що взагалі щось дає: з цим уже можна йти до AD.
        var catalogue = Assert.Single(view.GroupAssignmentsInSystem);
        Assert.Equal(GroupWithRole, catalogue.Sid);
        Assert.Equal(["Approver"], catalogue.RoleCodes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    public async Task Чужий_запис_без_права_ManageUsers_не_віддається()
    {
        AddDomain("ivanov", OwnSid);
        var other = AddDomain("petrov", "S-1-5-21-100-200-300-777");
        Profile();

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(other.Id, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    [Trait("Requirement", "ФВ-6.11")]
    public async Task Каталог_груп_не_віддається_тому_хто_не_керує_користувачами()
    {
        // ⚠ «Яка AD-група дає адміністративну роль» — відомість, яка рядовому
        // користувачеві нічого не пояснює, а зловмисникові називає ціль. Свої
        // групи людина при цьому бачить: вони і так у її квитку.
        AddDomain("ivanov", OwnSid);
        Profile();

        _user.GroupSids.Returns([GroupWithRole]);
        _users.GroupAssignments.Add(new GroupRoleAssignment(GroupWithRole, "Approver"));

        var view = await Handler().HandleAsync(subjectUserId: null, CancellationToken.None);

        Assert.Empty(view.GroupAssignmentsInSystem);
        Assert.Single(view.Groups);
        Assert.Equal(["Approver"], view.EffectiveRoleCodes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Підміна_на_час_відпустки_що_скінчилася_названа_окремо()
    {
        // ⛔ «Роль була, вчора скінчилася» і «ролі не було ніколи» —
        // різні відповіді, і плутати їх дорого: у першому випадку доступ
        // повертає продовження призначення, у другому — заведення нового.
        AddDomain("ivanov", OwnSid);
        _user.GroupSids.Returns([GroupWithRole]);
        _users.GroupAssignments.Add(new GroupRoleAssignment(
            GroupWithRole, "Approver", ValidFrom: null, ValidTo: new DateOnly(2026, 5, 19)));

        var view = await Handler().HandleAsync(subjectUserId: null, CancellationToken.None);

        Assert.Equal(["Approver"], view.ExpiredRoleCodes);
        Assert.Empty(view.EffectiveRoleCodes);
        Assert.Equal([GroupWithRole], view.UnmatchedSids);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    public async Task Особисті_призначення_відділені_від_групових()
    {
        // ⚠ Різниця не косметична: особисте призначення в доменному контурі —
        // виняток (`ФВ-6.15` каже «на групу»), і побачити його поруч із
        // груповими означає побачити, що людину тримає саме виняток.
        var me = AddDomain("ivanov", OwnSid);
        _user.GroupSids.Returns([GroupWithRole]);
        _users.GroupAssignments.Add(new GroupRoleAssignment(GroupWithRole, "Approver"));
        await _users.ReplaceRolesAsync(me.Id, ["DataEntry"], validity: null, CancellationToken.None);

        var view = await Handler().HandleAsync(subjectUserId: null, CancellationToken.None);

        Assert.Equal(["DataEntry"], view.PersonalRoleCodes);
        Assert.Equal(["Approver", "DataEntry"], view.EffectiveRoleCodes);
    }

    private GetAccessDiagnosticsHandler Handler() => new(_users, _access, _user, _clock);

    /// <summary>Профіль актора з переліченими правами.</summary>
    /// <param name="permissions">Коди прав.</param>
    private void Profile(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = Actor };
        foreach (var permission in permissions)
        {
            builder.Permission(permission);
        }

        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    /// <summary>Доменний запис у сховищі; перший стає актором.</summary>
    /// <param name="userName">Ім'я входу.</param>
    /// <param name="sid">SID у каталозі.</param>
    private Ecr.Domain.Entities.Security.User AddDomain(string userName, string sid)
    {
        var user = FakeUserStore.DomainUser(userName, sid);
        _users.Add(user);

        // Фікстура роздає ідентифікатори від одиниці; актор має збігтися з
        // тим, кого повертає `ICurrentUser`, інакше «власна» гілка не власна.
        if (_users.Users.Count == 1)
        {
            typeof(Ecr.Domain.Entities.Security.User)
                .GetProperty(nameof(Ecr.Domain.Entities.Security.User.Id))!
                .SetValue(user, Actor);
        }

        return user;
    }
}
