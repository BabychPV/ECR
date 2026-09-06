using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Workflow;

/// <summary>
/// Маршрут погодження і його проходження (ФВ-5.17).
/// </summary>
/// <remarks>
/// ⛔ До цього сутність існувала, таблиця існувала, конфігурація EF існувала —
/// і маршрут із двох кроків створити було НЕМОЖЛИВО: список кроків приватний,
/// способу наповнення немає. Той самий клас, що й `A7-25`.
/// </remarks>
public sealed class ApprovalRouteTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public void Кроки_нумеруються_маршрутом_а_не_ззовні()
    {
        // ⚠ Два кроки з однаковим номером зробили б «наступний крок»
        // невизначеним саме тоді, коли документ уже подано.
        var route = Route();

        var first = route.AddStep(roleId: 11);
        var second = route.AddStep(roleId: 22);

        Assert.Equal(1, first.Ordinal);
        Assert.Equal(2, second.Ordinal);
        Assert.Equal(2, route.Steps.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public void Конкретніший_маршрут_має_більшу_вагу()
    {
        // Порядок від конкретного до загального: проєкт+версія → проєкт →
        // версія → типовий.
        var both = new ApprovalRoute(Code("BOTH"), Name, projectId: 5, templateVersionId: 7);
        var project = new ApprovalRoute(Code("PRJ"), Name, projectId: 5);
        var version = new ApprovalRoute(Code("VER"), Name, templateVersionId: 7);
        var common = new ApprovalRoute(Code("ALL"), Name);

        Assert.True(both.Specificity > project.Specificity);
        Assert.True(project.Specificity > version.Specificity);
        Assert.True(version.Specificity > common.Specificity);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public void Невідомий_крок_повертає_маршрут_на_початок()
    {
        // ⛔ Зміна маршруту посеред погодження не має робити документ
        // незатверджуваним назавжди. Крок, якого в маршруті немає, означає
        // «почато не за цим маршрутом» — і погодження йде з першого.
        var route = Route();
        route.AddStep(roleId: 11);
        route.AddStep(roleId: 22);

        var step = route.StepAfter(stepId: 999);

        Assert.Same(route.Steps[0], step);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public void Проміжний_крок_не_робить_аркуш_затвердженим()
    {
        // ⛔ Головне твердження багатоетапності. Документ, який став би
        // `Approved` після першого підпису, потрапив би у звітність для
        // регулятора без решти погоджень (`ФВ-10.11`) — а саме заради них
        // маршрут і заводять.
        var state = new ApprovalState(documentId: 1, sheetDefId: 2, periodKey: 202603);

        state.Submit(userId: 7, Now, firstStepId: 100);
        Assert.Equal(100, state.CurrentStepId);

        state.ApproveStep(userId: 8, Now, nextStepId: 200);

        Assert.Equal(DocumentStatus.Submitted, state.Status);
        Assert.Equal(200, state.CurrentStepId);
        Assert.Null(state.ApprovedByUserId);

        // І лише останній крок робить аркуш затвердженим.
        state.ApproveStep(userId: 9, Now, nextStepId: null);

        Assert.Equal(DocumentStatus.Approved, state.Status);
        Assert.Equal(9, state.ApprovedByUserId);
        Assert.Null(state.CurrentStepId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public void Без_маршруту_затвердження_лишається_одноетапним()
    {
        // ⛔ Поведінка за замовчуванням НЕ змінюється: seed не створює жодного
        // маршруту, і система, у якій їх ніхто не завів, працює як раніше.
        var state = new ApprovalState(documentId: 1, sheetDefId: 2, periodKey: 202603);

        state.Submit(userId: 7, Now);
        Assert.Null(state.CurrentStepId);

        state.ApproveStep(userId: 8, Now, nextStepId: null);

        Assert.Equal(DocumentStatus.Approved, state.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.17")]
    public void Відхилення_і_повернення_скидають_маршрут_на_початок()
    {
        // ⚠ Інакше виправлений документ обійшов би тих, хто вже підписав до
        // правки, — тобто підпис стосувався б не того тексту.
        var state = new ApprovalState(documentId: 1, sheetDefId: 2, periodKey: 202603);

        state.Submit(userId: 7, Now, firstStepId: 100);
        state.ApproveStep(userId: 8, Now, nextStepId: 200);
        state.Reject(userId: 9, "числа за березень не сходяться", Now);

        Assert.Null(state.CurrentStepId);

        state.Submit(userId: 7, Now, firstStepId: 100);
        state.ApproveStep(userId: 8, Now, nextStepId: 200);
        state.Reopen(userId: 9, "перерахунок методології", Now);

        Assert.Null(state.CurrentStepId);
    }

    private static ApprovalRoute Route()
    {
        var route = new ApprovalRoute(Code("R1"), Name, projectId: 5);

        // Кроки отримують `Id` від бази; у домені їх роздає фікстура.
        return route;
    }

    private static EcrCode Code(string value) => EcrCode.Create(value);

    private static LocalizedText Name
        => new(new Dictionary<string, string> { ["en"] = "Route" });
}
