using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Projects;

/// <summary>
/// Створення проєкту: часовий пояс задається явно (<c>D-5</c>).
/// </summary>
/// <remarks>
/// ⛔ Пояс — вічна властивість проєкту: після відкриття першого періоду його
/// вже не змінити (<c>ФВ-1.1a</c>, <c>D-110</c>). Мовчазна підстановка
/// зробила б цю вічну властивість наслідком того, де стоїть сервер, — і
/// помітили б це лише тоді, коли хтось не встиг подати форму «вчасно», бо
/// період закрився на кілька годин раніше.
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
public sealed class CreateProjectTests
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IProjectStore _projects = Substitute.For<IProjectStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public CreateProjectTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Project.Manage").Build());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1a")]
    public async Task Без_поясу_проєкт_не_створюється_ECR_CFG_4221(string? timeZoneId)
    {
        // ⛔ Не мовчазний `UTC`. Сервер стоїть де завгодно, а межі періодів
        // рахуються в поясі МАЙДАНЧИКА (`D-68`).
        var error = await Assert.ThrowsAsync<DomainException>(
            () => Create(timeZoneId!));

        Assert.Equal("ECR-CFG-4221", error.ErrorCode);
        await _periods.DidNotReceiveWithAnyArgs().AddProjectAsync(null!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1a")]
    public async Task Невідомий_пояс_це_422_а_не_збій_сервера()
    {
        // ⚠ `TimeZoneNotFoundException` пройшов би нагору як 500, і той, хто
        // надіслав опечатку, побачив би «внутрішня помилка сервера» замість
        // назви поля, у якому помилився. `DomainException` конвеєр мапить у
        // 422 з кодом і текстом (`ExceptionHandlingMiddleware`).
        var error = await Assert.ThrowsAsync<DomainException>(
            () => Create("Asia/Atlantis"));

        Assert.Equal("ECR-CFG-4221", error.ErrorCode);
        Assert.Contains("Asia/Atlantis", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Central Asia Standard Time")]
    [InlineData("West Asia Standard Time")]
    [InlineData("UTC+13")]
    [InlineData("+05:00")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1b")]
    public async Task Windows_ідентифікатор_і_зсув_не_приймаються(string timeZoneId)
    {
        // ⛔ Саме тут була дірка. Обробник перевіряв пояс
        // `TimeZoneInfo.FindSystemTimeZoneById`, а той на Windows приймає і
        // Windows-ідентифікатори, і `UTC+13` (виміряно). Тобто вимогу «IANA»
        // (директива ПК-1 №06 §3) код проходив лише на вигляд: у базу лягало
        // `Central Asia Standard Time` — рівно те, що стояло в DEFAULT
        // колонки з першої міграції.
        var error = await Assert.ThrowsAsync<DomainException>(() => Create(timeZoneId));

        Assert.Equal("ECR-CFG-4221", error.ErrorCode);
        await _periods.DidNotReceiveWithAnyArgs().AddProjectAsync(null!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1")]
    public async Task Звітний_рік_береться_в_поясі_майданчика_а_не_сервера()
    {
        // ⛔ 31 грудня 21:00 UTC — це вже 1 січня 03:00 на `Asia/Almaty`
        // (UTC+6). Тут стояло `clock.UtcNow.Year`, тобто проєкт, створений
        // на майданчику вночі проти Нового року, отримував МИНУЛИЙ рік:
        // дванадцять періодів із ключами `202512xx` замість `202601xx`.
        // `PeriodKey` — ключ партиціонування (R-A6), тож дані поїхали б у
        // чужі партиції й у чужий архів.
        _clock.UtcNow.Returns(new DateTime(2025, 12, 31, 21, 0, 0, DateTimeKind.Utc));

        await new CreateProjectHandler(_periods, _access, _uow, _user, _clock)
            .HandleAsync(
                "KASH_2026",
                new Dictionary<string, string> { ["en"] = "Kashagan" },
                "Asia/Almaty",
                PeriodKind.Monthly,
                year: null,
                templateVersionId: 42,
                periodPolicyId: 7,
                CancellationToken.None);

        var call = _periods.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPeriodStore.AddProjectAsync));

        var project = (Project)call.GetArguments()[0]!;

        Assert.Equal(2026, project.PeriodStart.Year);
        Assert.Equal(2026, project.PeriodEnd.Year);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1a")]
    public async Task Заданий_пояс_зберігається_на_проєкті()
    {
        await Create("Asia/Almaty");

        var call = _periods.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPeriodStore.AddProjectAsync));

        var project = (Project)call.GetArguments()[0]!;

        Assert.Equal("Asia/Almaty", project.TimeZoneId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1")]
    public async Task Політики_періодів_доступні_для_вибору()
    {
        // ⛔ `A7-56`. Обробник відхиляє створення без політики
        // (`ECR-PRD-0422`), а перелічити політики не було чим — форма
        // надсилала запит без неї, і створення проєкту з інтерфейсу не
        // працювало ЖОДНОГО разу.
        _periods.ListPoliciesAsync(Arg.Any<CancellationToken>()).Returns(
            [new PeriodPolicy(EcrCode.Create("ECR_Standard"), 0, 15, 45, 45)]);

        var policies = await new ListPeriodPoliciesHandler(_periods, _access, _user)
            .HandleAsync(CancellationToken.None);

        var policy = Assert.Single(policies);
        Assert.Equal("ECR_Standard", policy.Code);
        Assert.Equal(15, policy.GraceOffsetDays);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_права_Project_Manage_перелік_політик_недоступний()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => new ListPeriodPoliciesHandler(_periods, _access, _user)
                .HandleAsync(CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    private Task<int> Create(string timeZoneId)
        => new CreateProjectHandler(_periods, _access, _uow, _user, _clock)
            .HandleAsync(
                "KASH_2026",
                new Dictionary<string, string> { ["en"] = "Kashagan" },
                timeZoneId,
                PeriodKind.Monthly,
                year: 2026,
                templateVersionId: 42,
                periodPolicyId: 7,
                CancellationToken.None);
}
