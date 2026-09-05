// tests/Ecr.Application.Tests/Security/DangerousPermissionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Видача небезпечних прав — <c>D-121</c>, зміна безпекового правила.
/// </summary>
/// <remarks>
/// ⛔ Правило «видати можна лише те, що маєш» **прибрано**. Його не було в
/// пакеті, і воно замикало коло: bootstrap-запис вимикається автоматично,
/// щойно з'являється доменний адміністратор (`D-97`), а в ролі
/// <c>SystemAdministrator</c> небезпечних прав немає за seed — після цього
/// видати <c>Calculation.Publish</c> не міг уже ніхто. Той самий деадлок, що
/// й `A7-17`, тільки на день пізніше.
///
/// ⚠ Захист «чотирьох очей» стоїть у точці ВИКОРИСТАННЯ, а не видачі:
/// публікує не автор останньої правки (`D-40`), повернення періоду вимагає
/// причини, симуляція пишеться в аудит. Дублювати його в точці видачі — і
/// було тим, що замикало коло.
///
/// Натомість кожна видача лишає СЛІД: запис у <c>aud.SecurityEvent</c>.
/// </remarks>
public sealed class DangerousPermissionTests
{
    private const int Actor = 7;
    private static readonly DateTime Now = new(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public DangerousPermissionTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Actor);

        _users.Dangerous.Add("Calculation.Publish");
        _users.Dangerous.Add("System.RunJob");

        // ⚠ Актор має ЛИШЕ `Security.ManageRoles` — і жодного з небезпечних,
        // які видає. Саме цей випадок раніше відхилявся.
        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>()).Returns(Profile("Security.ManageRoles"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Носій_ManageRoles_видає_небезпечне_право_НЕ_маючи_його()
    {
        await Handler().HandleAsync(
            "Publishers",
            new Dictionary<string, string> { ["en"] = "Publishers" },
            ["Calculation.Publish", "Template.View"],
            CancellationToken.None);

        var role = Assert.Single(_users.Roles);
        Assert.Contains("Calculation.Publish", role.Permissions);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Видача_небезпечного_права_лишає_слід_в_аудиті()
    {
        await Handler().HandleAsync(
            "Publishers",
            new Dictionary<string, string> { ["en"] = "Publishers" },
            ["Calculation.Publish", "System.RunJob", "Template.View"],
            CancellationToken.None);

        // ⛔ Слід обов'язковий: заборони більше немає, тому єдине, що лишається
        // від «чотирьох очей» у точці видачі, — можливість побачити, хто і
        // кому це видав.
        await _audit.Received().WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e =>
                e.EventType == "DangerousPermissionsGranted"
                && e.ChangedByUserId == Actor
                && e.DetailsJson != null
                && e.DetailsJson.Contains("Calculation.Publish", StringComparison.Ordinal)
                && e.DetailsJson.Contains("System.RunJob", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Роль_без_небезпечних_прав_не_засмічує_журнал_безпеки()
    {
        // ⚠ Рядок «видано нуль небезпечних прав» ховає справжні: журнал, який
        // пишеться завжди, перестають читати.
        await Handler().HandleAsync(
            "Viewers",
            new Dictionary<string, string> { ["en"] = "Viewers" },
            ["Template.View"],
            CancellationToken.None);

        await _audit.DidNotReceive().WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e => e.EventType == "DangerousPermissionsGranted"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_права_ManageRoles_роль_не_створюється_взагалі()
    {
        // Прибрано саме правило «видаю лише те, що маю» — а не перевірку
        // права на створення ролі.
        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>()).Returns(Profile("Template.View"));

        await Assert.ThrowsAsync<Application.Errors.AccessDeniedException>(
            () => Handler().HandleAsync(
                "Publishers",
                new Dictionary<string, string> { ["en"] = "Publishers" },
                ["Calculation.Publish"],
                CancellationToken.None));

        Assert.Empty(_users.Roles);
    }

    private CreateRoleHandler Handler() => new(_users, _access, _uow, _audit, _user, _clock);

    private static AccessProfile Profile(params string[] permissions) => new()
    {
        CacheKey = "d121",
        UserId = Actor,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(permissions, StringComparer.Ordinal),
        Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
        Denies = new HashSet<string>(StringComparer.Ordinal),
    };
}
