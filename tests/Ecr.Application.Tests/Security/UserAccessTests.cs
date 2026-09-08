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
/// Ролі й адреса користувача — те, без чого обліковий запис не працює.
/// </summary>
/// <remarks>
/// ⛔ `A7-61`: способу призначити роль наявному користувачеві не існувало
/// взагалі. Ролі видавалися лише при створенні, а форма створення надсилала
/// порожній перелік — запис виходив працездатним на вигляд і безправним
/// насправді, і виправити це було нічим, крім прямого запису в базу.
///
/// ⛔ `A7-62`: `User.Email` не присвоювався ніде, тож `NotificationJob`
/// завжди отримував порожній перелік адресатів.
/// </remarks>
// ⛔ Трейт `ФВ-6.12` знятий (директива №09 §8.2). Вимога каже, що
// НЕБЕЗПЕЧНІ права (`Calculation.Publish`, `Integration.Manage`,
// `Period.Reopen`) видаються поіменно і не входять до складених ролей, а
// seed створює ролі порожніми за ними. Жодна перевірка тут цього не
// торкається: вона питає заглушку про профіль, який сама ж і задала, і
// дивиться, чи відмовив обробник. Це про гатування входу в обробник,
// а не про склад ролей.
//
// ⚠ Самі перевірки ЛИШАЮТЬСЯ — «без права обробник відмовляє і нічого
// не зберігає» варте перевірки саме по собі (`A7-53`). Змінилася НЕ
// поведінка, а ЗАЯВКА про те, що вони покривають. Саму `ФВ-6.12` доводить
// `Ecr.Infrastructure.Tests/Persistence/SeedTests` на живій базі: ролі seed справді
// порожні за небезпечними правами.
public sealed class UserAccessTests
{
    private static readonly DateTime Now = new(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public UserAccessTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _user.CorrelationId.Returns("test");

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Security.ManageUsers").Build());

        _users.Roles.Add(new RoleView(1, "Approver", true, true, [], []));
        _users.Roles.Add(new RoleView(2, "DataEntry", true, true, [], []));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Ролі_наявного_користувача_замінюються_набором()
    {
        var user = Add("ivanov");

        var count = await Roles().HandleAsync(user.Id, ["Approver", "DataEntry"], CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(
            ["Approver", "DataEntry"],
            (await _users.ListUserRolesAsync(user.Id, CancellationToken.None)).Order());

        // ⛔ Штамп безпеки крутиться: інакше вже побудований профіль доступу
        // живе в кеші до кінця сесії, і людина або лишається без щойно
        // виданих прав, або зберігає щойно відібрані (`ФВ-6.7`).
        Assert.NotEqual(user.SecurityStamp, StampBefore);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Заміна_ролей_потрапляє_в_журнал_безпеки_обома_наборами()
    {
        // ⚠ «Хто це йому видав» — питання, на яке через рік має бути
        // відповідь, а не здогад. Тому в записі і те, що було, і те, що стало.
        var user = Add("petrov");
        await Roles().HandleAsync(user.Id, ["DataEntry"], CancellationToken.None);
        await Roles().HandleAsync(user.Id, ["Approver"], CancellationToken.None);

        var events = _audit.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditWriter.WriteSecurityEventAsync))
            .Select(c => (SecurityEventRecord)c.GetArguments()[0]!)
            .ToList();

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("UserRolesReplaced", e.EventType));

        var last = events[^1];
        Assert.Contains("DataEntry", last.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("Approver", last.DetailsJson, StringComparison.Ordinal);
        Assert.Equal(user.Id, last.TargetUserId);
        Assert.Equal(9, last.ChangedByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Невідома_роль_відхиляє_весь_набір()
    {
        // ⛔ Призначити «те, що знайшлося» гірше за відмову: людина отримала б
        // частину повноважень і вважала б, що отримала всі.
        var user = Add("sydorenko");
        await Roles().HandleAsync(user.Id, ["DataEntry"], CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(
            () => Roles().HandleAsync(user.Id, ["Approver", "NoSuchRole"], CancellationToken.None));

        Assert.Equal(
            ["DataEntry"],
            await _users.ListUserRolesAsync(user.Id, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-12.1")]
    public async Task Адреса_задається_і_прибирається_разом_із_прапорцем_алертів()
    {
        // ⛔ Поле існувало від Етапу 3 і не присвоювалося ніде: сповіщення не
        // надходили нікому.
        var user = Add("kovalenko");

        await Email().HandleAsync(user.Id, "kovalenko@ncoc.kz", CancellationToken.None);
        Assert.Equal("kovalenko@ncoc.kz", user.Email);

        user.SetReceivesAlerts(true);

        // ⚠ Прибирання адреси знімає і прапорець: увімкнений адресат, якому
        // нічого не надсилається, виглядає як налаштований (`D-125`).
        await Email().HandleAsync(user.Id, "  ", CancellationToken.None);

        Assert.Null(user.Email);
        Assert.False(user.ReceivesAlerts);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_права_ManageUsers_ні_ролі_ні_адреса_не_міняються()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());

        var user = Add("stranger");

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Roles().HandleAsync(user.Id, ["Approver"], CancellationToken.None));

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Email().HandleAsync(user.Id, "x@y.z", CancellationToken.None));

        Assert.Empty(await _users.ListUserRolesAsync(user.Id, CancellationToken.None));
        Assert.Null(user.Email);
    }

    private string StampBefore { get; set; } = string.Empty;

    private ReplaceUserRolesHandler Roles() => new(_users, _access, _uow, _user, _audit, _clock);

    private SetUserEmailHandler Email() => new(_users, _access, _uow, _user);

    private Domain.Entities.Security.User Add(string userName)
    {
        var user = new Domain.Entities.Security.User(userName, userName, AuthProvider.Local);
        _users.Add(user);
        StampBefore = user.SecurityStamp;

        return user;
    }
}
