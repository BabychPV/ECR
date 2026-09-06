using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Errors;

/// <summary>
/// Родина коду помилки збігається із суб'єктом відмови (<c>P-25</c>).
/// </summary>
/// <remarks>
/// ⛔ Родина в коді — це МАРШРУТ на клієнті, а не оздоба: <c>ROW</c> іде в
/// обробник помилок рядка сітки документа, <c>CELL</c> — у комірку. Код із
/// чужою родиною доїжджає до обробника, у якого для цієї відмови немає ні
/// місця, ні тексту, і виглядає як збій редактора документа — на екрані, де
/// жодного документа не відкрито.
///
/// ⚠ Перевіряється РІВНО код, а не текст: клієнт розрізняє причини за кодом,
/// і саме тому підміна родини не падала й не логувалася — вона була видима
/// лише користувачеві. Кожен тест нижче червоний на невиправленому коді
/// (<c>D-134</c>).
/// </remarks>
public sealed class ErrorFamilyTests
{
    private static readonly DateTime Now = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IProjectStore _projects = Substitute.For<IProjectStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAuditReader _auditReader = Substitute.For<IAuditReader>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly FakeUserStore _users = new();

    public ErrorFamilyTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _user.CorrelationId.Returns("test");
        _hasher.Hash(Arg.Any<string>()).Returns("hash");

        // Права видані: предмет цих тестів — родина коду, а не доступ. За
        // самим доступом стежить `EndpointCoverageTests`.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Document.View")
                .Permission("Project.Manage")
                .Permission("Security.ManageUsers")
                .Permission("Security.ViewAudit")
                .Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Хибний_розмір_сторінки_у_переліку_проєктів_дає_ECR_REQ_0422()
    {
        // ⛔ Регресія: `ECR-CELL-0422`. Хибний `limit` у переліку ПРОЄКТІВ
        // приходив клієнтові як помилка валідації комірки — тобто в обробник
        // помилок сітки документа, якої на цьому екрані немає взагалі. Це
        // рядок 1 із `P-25` і найнаочніший приклад чужої родини.
        var handler = new ListProjectsHandler(_projects, _access, _user);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(new CursorRequest(Limit: 0), CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Перевернуте_вікно_аудиту_дає_ECR_REQ_0422()
    {
        // ⛔ Регресія: `ECR-CELL-0422`. Журнал аудиту комірок має, і саме тому
        // підміна виглядала правдоподібно — але відмова тут про ПАРАМЕТР
        // ЗАПИТУ («кінець раніше за початок»), а не про значення в комірці.
        var handler = new GetCellChangesHandler(_auditReader, _access, _user);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(
                from: Now, to: Now.AddDays(-1), documentId: null,
                new CursorRequest(), CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Відсутній_проєкт_при_клонуванні_дає_ECR_PRJ_0404()
    {
        // ⛔ Регресія подвійна (`P-25`, рядок 4). Було `ECR-PRD-0422`: цифри
        // коду кажуть 422, а `NotFoundException` віддає 404 — суперечність
        // усередині ОДНОГО коду, тож клієнт, який виводить статус із коду,
        // читав з однієї відповіді два різні. І суб'єкт був чужий: немає
        // ПРОЄКТУ, а не «період поза межами проєкту».
        _periods.FindProjectAsync(404, Arg.Any<CancellationToken>()).Returns((Project?)null);

        var handler = new CloneProjectHandler(_periods, _uow, _audit, _user, _clock, _access);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(
                sourceProjectId: 404, newCode: "KASH_2027", CancellationToken.None));

        Assert.Equal("ECR-PRJ-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Відсутній_проєкт_при_активації_дає_ECR_PRJ_0404()
    {
        // ⛔ Регресія: `ECR-ROW-0404`. `ROW` — це рядок ТАБЛИЦІ ДОКУМЕНТА, і
        // «проєкту не існує» доїжджало до обробника помилок сітки (`P-25`,
        // рядок 2).
        _periods.FindProjectAsync(404, Arg.Any<CancellationToken>()).Returns((Project?)null);

        var handler = new ActivateProjectHandler(_periods, _access, _user, _uow, _clock);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(projectId: 404, CancellationToken.None));

        Assert.Equal("ECR-PRJ-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Відсутній_користувач_дає_ECR_SEC_0404()
    {
        // ⛔ Регресія: `ECR-ROW-0404`. Суб'єкт відмови — запис каталогу
        // безпеки, і код для нього вже існував (`ECR-SEC-0404`); запозичувати
        // родину рядка сітки не було жодної потреби (`P-25`, рядок 2).
        var handler = new SetReceivesAlertsHandler(_users, _access, _user, _uow);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(userId: 404, value: true, CancellationToken.None));

        Assert.Equal("ECR-SEC-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Дублікат_імені_користувача_дає_ECR_USR_0409()
    {
        // ⛔ Регресія: `ECR-ROW-0409` — дублікат ОБЛІКОВОГО ЗАПИСУ подавався
        // як дублікат `RowKey` у таблиці документа (`P-25`, рядок 3). Форма
        // створення користувача сітки не має, тому відмова приходила туди, де
        // для неї немає ні місця, ні тексту.
        _users.Add(new Domain.Entities.Security.User("ivanov", "Іванов", AuthProvider.Local));

        var handler = new CreateUserHandler(
            _users,
            _hasher,
            _access,
            new DisableBootstrapAdminHandler(_users, _uow, _audit, _user, _clock),
            _uow,
            _audit,
            _user,
            _clock);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(
                userName: "ivanov",
                displayName: "Інший Іванов",
                provider: AuthProvider.Local,
                windowsSid: null,
                initialPassword: "Tengiz-2026-Password!",
                roleCodes: [],
                email: null,
                CancellationToken.None));

        Assert.Equal("ECR-USR-0409", error.ErrorCode);
    }
}
