using Ecr.Application.Common;
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
/// Прив'язка проєкту до версії шаблону (<c>ФВ-1.2</c>) і клонування на
/// наступний рік (<c>ФВ-1.3</c>).
/// </summary>
/// <remarks>
/// ⚠ Обидві вимоги лишалися непокритими: обробник існував від Етапу 3 і не мав
/// жодного тесту. Ціна помилки тут висока й тиха — клон, який приніс би із
/// собою числа минулого року, виявився б уже у відправленому звіті.
/// </remarks>
public sealed class CloneProjectTests
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private readonly Project _source = new(
        EcrCode.Create("KASH_2026"),
        new LocalizedText(new Dictionary<string, string> { ["en"] = "Kashagan" }),
        new DateOnly(2026, 1, 1),
        new DateOnly(2026, 12, 31),
        templateVersionId: 42,
        PeriodKind.Monthly,
        periodPolicyId: 7,
        timeZoneId: "Asia/Almaty");

    public CloneProjectTests()
    {
        // ⛔ Q-244: CloneProjectHandler тепер виконує весь блок через
        // IUnitOfWork.ExecuteInTransactionAsync(Func<CancellationToken, Task>, ...).
        // Без цього налаштування NSubstitute ніколи не викликає передане
        // замикання — жодна перевірка нижче не виконалась би насправді.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _periods.FindProjectAsync(1, Arg.Any<CancellationToken>()).Returns(_source);

        // Право й грант видані: предмет цих тестів — правила клонування, а
        // не доступ. Саме право/грант стереже `EndpointCoverageTests`/`Q-179`.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Project.Manage")
                .Grant(ResourceKind.Project, 1, GrantLevel.Manage)
                .Build());
    }

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    private CloneProjectHandler Handler() => new(_periods, _uow, _audit, _user, _clock, _access);

    /// <summary>Проєкт, який обробник передав сховищу.</summary>
    private Project Cloned()
    {
        var call = _periods.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPeriodStore.AddProjectAsync));

        return (Project)call.GetArguments()[0]!;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.2")]
    public async Task Клон_успадковує_версію_шаблону_а_не_шаблон()
    {
        await Handler().HandleAsync(1, "KASH_2027", CancellationToken.None);

        // ⛔ Проєкт прив'язаний до ВЕРСІЇ, а не до шаблону: публікація нової
        // версії не змінює вже прив'язані проєкти автоматично. Інакше форма,
        // яку заповнюють у грудні, змінила б структуру посеред періоду.
        Assert.Equal(42, Cloned().TemplateVersionId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.3")]
    public async Task Клон_копіює_налаштування()
    {
        await Handler().HandleAsync(1, "KASH_2027", CancellationToken.None);

        var clone = Cloned();

        Assert.Equal("KASH_2027", clone.Code);
        Assert.Equal(PeriodKind.Monthly, clone.PeriodKind);
        Assert.Equal(7, clone.PeriodPolicyId);

        // ⚠ Пояс — майданчика, а не сервера: межі періоду рахуються в ньому
        // (`D-6`), і клон, який втратив би пояс, зсунув би кінець місяця.
        Assert.Equal("Asia/Almaty", clone.TimeZoneId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.3")]
    public async Task Клон_зсуває_рік_і_не_несе_ані_періодів_ані_даних()
    {
        await Handler().HandleAsync(1, "KASH_2027", CancellationToken.None);

        var clone = Cloned();

        // ⛔ Рік зсунуто: два проєкти з однаковими датами дали б однакові
        // `PeriodKey` — конфлікт у партиційному ключі (R-A6).
        Assert.Equal(new DateOnly(2027, 1, 1), clone.PeriodStart);
        Assert.Equal(new DateOnly(2027, 12, 31), clone.PeriodEnd);

        // ⛔ Періоди НЕ копіюються: їх будує календар за датами нового
        // проєкту. Скопійовані, вони принесли б стани старого року — включно
        // з `Closed`, який зробив би новий проєкт мертвим від народження.
        Assert.Empty(clone.Periods);

        // ⛔ І головне: клон — це ЧЕРНЕТКА без жодного документа. Дані
        // документів не клонуються ніколи; перенесені числа перетворилися б
        // на «минулорічні, які всі забули оновити».
        Assert.Equal(ProjectStatus.Draft, clone.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.3")]
    public async Task Клонування_потрапляє_в_аудит()
    {
        await Handler().HandleAsync(1, "KASH_2027", CancellationToken.None);

        // ⚠ Клон проєкту — структурна зміна конфігурації, і через рік питання
        // «звідки взявся цей проєкт» має мати відповідь.
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.Operation == "Clone" && r.ChangedByUserId == 9),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Анонімний_запит_клонувати_не_може()
    {
        _user.UserId.Returns((int?)null);

        await Assert.ThrowsAsync<Application.Errors.AccessDeniedException>(
            () => Handler().HandleAsync(1, "KASH_2027", CancellationToken.None));

        await _periods.DidNotReceive().AddProjectAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_проєкт_джерело_клонування_відхиляється()
    {
        // ⛔ Q-179 (аудит фази 2, авторизація). Глобальне `Project.Manage`
        // саме по собі не давало права клонувати БУДЬ-ЯКИЙ проєкт — потрібен
        // грант на КОНКРЕТНЕ джерело.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Project.Manage").Build());

        var denied = await Assert.ThrowsAsync<Application.Errors.AccessDeniedException>(
            () => Handler().HandleAsync(1, "KASH_2027", CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        await _periods.DidNotReceive().AddProjectAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>());
    }
}
