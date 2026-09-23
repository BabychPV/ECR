using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Projects;

/// <summary>
/// Активація проєкту відкриває періоди <b>одразу</b> (директива №09 `W8` п.1,
/// `S-11`).
/// </summary>
/// <remarks>
/// ⛔ Доти <c>Period.AdvanceTo</c> кликав рівно один викликач —
/// <c>PeriodStateJob</c> на годинному розкладі. Тобто щойно активований
/// проєкт до години показував на кожній комірці «період ще не відкрито», хоч
/// за датами він давно відкритий. Причина неправдива, перевірити її оператору
/// нічим, і саме такі дрібниці ламають довіру до всього екрана.
/// </remarks>
public sealed class ActivateProjectTests
{
    // ⛔ UI-аудит, lane 2 (Q-337): `ProjectBuilder.Policy()` — 15-денний
    // пільговий строк (`graceOffsetDays: 15`); до фіксу `GraceOffsetDays`
    // ігнорувався й січень переходив у `Grace` вже 1 лютого, тож "середина
    // лютого" (10-те) давала рівно один `Open`-період. Тепер січень
    // лишається `Open` до 15 лютого (`PeriodEnd + GraceOffsetDays`) — на
    // 10-те лютого `Open` були б ОБИДВА суміжні періоди одночасно.
    // 20 лютого — вже ПІСЛЯ цього вікна: січень `Grace`, лютий — єдиний `Open`.
    /// <summary>Кінець лютого 2026: січень у Grace, лютий — єдиний Open.</summary>
    private static readonly DateTime Now = new(2026, 2, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public ActivateProjectTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        // ⚠ Грант на 10 — це `ProjectBuilder.Project()`'s дефолтний Id
        // (`Q-179`, аудит фази 2, авторизація).
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Project.Manage")
                .Grant(ResourceKind.Project, 10, GrantLevel.Manage)
                .Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.12")]
    public async Task Активація_відкриває_період_не_чекаючи_годинного_прогону()
    {
        var project = Arrange();

        await Handler().HandleAsync(project.Id, CancellationToken.None);

        Assert.Equal(ProjectStatus.Active, project.Status);

        // ⛔ Головне твердження: стан ПЕРІОДУ, а не проєкту. Активний проєкт
        // із періодом у `Scheduled` — це рівно те, що бачив оператор годину:
        // проєкт наче працює, а заповнити не можна нічого.
        Assert.Contains(project.Periods, p => p.State == PeriodState.Open);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "D-77")]
    public async Task Активація_призначає_поточний_період()
    {
        var project = Arrange();

        await Handler().HandleAsync(project.Id, CancellationToken.None);

        // ⚠ Поточний період теж не має чекати години: на нього спирається
        // кожен екран, який відкриває документ «за поточний період».
        Assert.NotNull(project.CurrentPeriodId);
        Assert.Equal(
            project.Periods.Single(p => p.State == PeriodState.Open).Id,
            project.CurrentPeriodId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_проєкт_активація_відхиляється()
    {
        // ⛔ Q-179 (аудит фази 2, авторизація). Глобальне `Project.Manage`
        // саме по собі не давало права активувати БУДЬ-ЯКИЙ проєкт —
        // потрібен грант на КОНКРЕТНИЙ.
        var project = Arrange();
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Project.Manage").Build());

        var denied = await Assert.ThrowsAsync<Application.Errors.AccessDeniedException>(
            () => Handler().HandleAsync(project.Id, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        Assert.Equal("err.ECR-AUTH-0403.noProjectManageGrant", denied.Details!["messageKey"]);
        Assert.Equal(ProjectStatus.Draft, project.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Повторна_активація_вже_активного_проєкту_відхиляється()
    {
        // ⛔ Борг локалізації (contracts/localization-debt.md): подробиця цієї
        // відмови ("Активувати можна лише чернетку…") була готовим українським
        // реченням без messageKey.
        var project = Arrange();
        await Handler().HandleAsync(project.Id, CancellationToken.None);
        Assert.Equal(ProjectStatus.Active, project.Status);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(project.Id, CancellationToken.None));

        Assert.Equal(ErrorCodes.ProjectActivationInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-PRJ-0422.notDraft", error.Details!["messageKey"]);
        Assert.Equal("Active", error.Details["status"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Активація_щойно_створеного_проєкту_будує_календар_сама()
    {
        // ⛔ Проєкт БЕЗ жодного періоду — саме таким його лишає створення.
        // Доти календар будувався РІВНО в одному місці: `GET …/periods`.
        // Отже новостворений проєкт активувати було неможливо, а відмова
        // звучала «У проєкті немає жодного періоду: активувати нічого» —
        // звинувачувала дані замість того, щоб назвати пропущений крок, про
        // який ніде не написано.
        var project = ProjectBuilder.Project(timeZoneId: "UTC");
        _periods.FindProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);
        _periods.GetPolicyAsync(project.PeriodPolicyId, Arg.Any<CancellationToken>())
            .Returns(ProjectBuilder.Policy());

        var added = new List<Period>();
        _periods.When(p => p.AddRange(Arg.Any<IEnumerable<Period>>()))
            .Do(call => added.AddRange(call.Arg<IEnumerable<Period>>()));

        await Handler().HandleAsync(project.Id, CancellationToken.None);

        Assert.Equal(ProjectStatus.Active, project.Status);
        Assert.NotEmpty(added);

        // Календар не просто створено — його періоди пройшли ті самі переходи,
        // що й у вже наявного проєкту: активація не лишає їх `Scheduled`.
        Assert.Contains(added, p => p.State == PeriodState.Open);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Календар_зберігається_ДО_призначення_поточного_періоду()
    {
        // ⛔ Порядок тут — не стиль. `Period.Id` призначає база, тож до
        // збереження він нуль; призначити поточним період із нульовим
        // ідентифікатором означало б записати в проєкт неіснуюче посилання
        // мовчки, без жодної помилки.
        var project = ProjectBuilder.Project(timeZoneId: "UTC");
        _periods.FindProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);
        _periods.GetPolicyAsync(project.PeriodPolicyId, Arg.Any<CancellationToken>())
            .Returns(ProjectBuilder.Policy());

        await Handler().HandleAsync(project.Id, CancellationToken.None);

        // ⚠ Перевіряється саме ПОРЯДОК, а не значення `CurrentPeriodId`:
        // ідентифікатор роздає база, якої в цьому тесті немає, тож тут він
        // лишиться нулем і при правильному коді. Довести можна рівно те, що
        // календар потрапляє в окреме збереження ДО того, як хтось питає в
        // періодів ідентифікатори, — і саме це робить значення ненульовим у
        // проді.
        Received.InOrder(() =>
        {
            _periods.AddRange(Arg.Any<IEnumerable<Period>>());
            _uow.SaveChangesAsync(Arg.Any<CancellationToken>());
            _uow.SaveChangesAsync(Arg.Any<CancellationToken>());
        });
    }

    /// <summary>Проєкт-чернетка з побудованим календарем 2026 року.</summary>
    private Project Arrange()
    {
        var project = ProjectBuilder.Project(timeZoneId: "UTC");
        var policy = ProjectBuilder.Policy();

        ProjectBuilder.Attach(
            project,
            PeriodCalendar.Build(project, policy, TimeZoneInfo.Utc, []));

        _periods.FindProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);

        // ⚠ Політика потрібна навіть тут, де календар уже побудований:
        // активація тепер сама добудовує календар (і перераховує межі наявних
        // періодів), тож без політики їй нема з чим працювати. Створених
        // періодів це не додасть — календар ідемпотентний.
        _periods.GetPolicyAsync(project.PeriodPolicyId, Arg.Any<CancellationToken>())
            .Returns(policy);

        return project;
    }

    private ActivateProjectHandler Handler()
        => new(
            _periods,
            _access,
            _user,
            _uow,
            new PeriodStateCalculator(),
            _clock,
            new Application.Periods.PeriodCalendarMaterializer(_periods));
}
