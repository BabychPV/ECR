// tests/Ecr.Application.Tests/Security/LoginGroupMatchTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Вхід із нульовою кількістю збігів за групами стає ВИДИМИМ (<c>H-21</c>).
/// </summary>
/// <remarks>
/// ⛔ Нуль збігів — <b>законний</b> стан: нового співробітника ще не додали в
/// жодну групу. Тому це <c>Warning</c>, а не відмова — інакше система не
/// пускала б саме тих, кого має зустріти порожнім, але робочим екраном.
///
/// ⛔ І водночас це найімовірніший вигляд збою на живому домені: `ФВ-6.15`
/// призначає ролі доменних користувачів НА ГРУПУ, тож неправильно налаштовані
/// групи дадуть нуль ролей більшості — і це не відрізнятиметься від справної
/// системи без даних.
///
/// ⚠ Рядок мусить нести ПЕРЕЛІК SID. «У когось немає прав» — не запит до
/// відділу AD; «група S-1-5-21-… нічого не дає» — запит.
/// </remarks>
public sealed class LoginGroupMatchTests
{
    private const string Sid = "S-1-5-21-100-200-300-500";
    private const string GroupA = "S-1-5-21-100-200-300-1001";
    private const string GroupB = "S-1-5-21-100-200-300-1002";

    private static readonly DateTime Now = new(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly RecordingLogger<LoginHandler> _log = new();

    public LoginGroupMatchTests()
    {
        _clock.UtcNow.Returns(Now);
        _users.Roles.Add(new RoleView(1, "DataEntry", true, true, [], []));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Вхід_без_жодного_збігу_пише_Warning_із_переліком_SID()
    {
        // Групи в квитку є, але жодна нічого не дає — саме той стан, який
        // сьогодні виглядає як справна система.
        _users.GroupAssignments.Add(new GroupRoleAssignment("S-1-5-21-999", "DataEntry"));

        await Handler().HandleWindowsAsync(
            Sid, "ivanov", "Іванов", [GroupA, GroupB], "10.0.0.1", CancellationToken.None);

        var warning = Assert.Single(_log.OfLevel(LogLevel.Warning));

        // ⛔ Саме перелік, а не факт: без нього рядок нічого не змінює.
        Assert.Contains(GroupA, warning.Message, StringComparison.Ordinal);
        Assert.Contains(GroupB, warning.Message, StringComparison.Ordinal);
        Assert.Contains("ivanov", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    public async Task Збіг_за_групою_нічого_не_пише()
    {
        // ⚠ Друга половина вимоги. Попередження на КОЖНОМУ вході знецінює себе
        // за тиждень: журнал, у якому попередження стоїть завжди, читають як
        // фон і перестають помічати справжнє.
        _users.GroupAssignments.Add(new GroupRoleAssignment(GroupA, "DataEntry"));

        await Handler().HandleWindowsAsync(
            Sid, "ivanov", "Іванов", [GroupA], "10.0.0.1", CancellationToken.None);

        Assert.Empty(_log.OfLevel(LogLevel.Warning));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    public async Task Квиток_БЕЗ_груп_теж_помітний()
    {
        // ⛔ Окремий і найтихіший випадок: у квитку немає жодного SID групи.
        // Це не адміністрування ролей, а налаштування контуру — Negotiate не
        // кладе заявок про членство, — і без рядка в журналі його не видно
        // взагалі: система поводиться рівно як для новачка без груп.
        await Handler().HandleWindowsAsync(
            Sid, "ivanov", "Іванов", [], "10.0.0.1", CancellationToken.None);

        var warning = Assert.Single(_log.OfLevel(LogLevel.Warning));
        Assert.Contains("ivanov", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "H-21")]
    public async Task Строкове_призначення_що_скінчилося_збігом_НЕ_вважається()
    {
        // ⚠ Призначення на групу є, але вже не діє. Порахувати його збігом
        // означало б замовкнути рівно тоді, коли людина щойно втратила права.
        _users.GroupAssignments.Add(new GroupRoleAssignment(
            GroupA, "DataEntry", ValidFrom: null, ValidTo: new DateOnly(2026, 5, 19)));

        await Handler().HandleWindowsAsync(
            Sid, "ivanov", "Іванов", [GroupA], "10.0.0.1", CancellationToken.None);

        var warning = Assert.Single(_log.OfLevel(LogLevel.Warning));
        Assert.Contains(GroupA, warning.Message, StringComparison.Ordinal);
    }

    private LoginHandler Handler() => new(_users, _hasher, _uow, _clock, _log);
}
