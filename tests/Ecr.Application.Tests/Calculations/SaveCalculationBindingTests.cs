// tests/Ecr.Application.Tests/Calculations/SaveCalculationBindingTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Прив'язка виходу методології до колонки-приймача
/// (<see cref="SaveCalculationBindingHandler"/>, <c>D-69</c>).
/// </summary>
/// <remarks>
/// ⛔ Обробник не мав ЖОДНОГО тесту — при тому, що він «головний блокер
/// розрахунку» за власним описом. Цей файл заводить його разом із перевіркою
/// типу колонки-приймача (<c>ECR-TMPL-4227</c>).
///
/// ⚠ Шкода тут ІНША, ніж у формул шаблону, і плутати їх не можна. Результат
/// методології лягає в <c>calc.CalculationResult</c>, а не в
/// <c>doc.CellValue</c> (<c>RecalculationJob</c>), тож комірку оператора він
/// НЕ перезаписує. Натомість на одну колонку виникають дві правди: число, яке
/// оператор бачить і правив у гріді (<c>doc.CellValue</c>), і число, з якого
/// будується звіт (<c>ReportDefHandlers.RowSource = "CalculationResults"</c>).
/// Оператор працює з першим, замовник отримує друге — і жоден екран не
/// показує, що вони розійшлися.
/// </remarks>
public sealed class SaveCalculationBindingTests
{
    private const int MethodologyId = 7;
    private const int TableDefId = 3;
    private const int ColumnDefId = 42;

    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public SaveCalculationBindingTests()
    {
        _user.UserId.Returns(9);
        _clock.UtcNow.Returns(new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc));

        // Транзакція виконує операцію, як справжня: інакше журнал усередині неї не видно.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(SaveCalculationBindingHandler.Permission)
                .Build());

        _drafts.FindAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns(new Methodology(
                EcrCode.Create("M1"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "M1" })));

        _bindings.FindAsync(ColumnDefId, MethodologyId, "OUT1", Arg.Any<CancellationToken>())
            .Returns((CalculationBinding?)null);

        // Єдина версія методології оголошує рівно вихід OUT1 (F-09).
        var version = new MethodologyVersion(
            MethodologyId, "1.0", CalculationLevel.Configuration, 9,
            new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, 70);
        _drafts.GetAllVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyVersion>)[version]);
        _methodologies.GetOutputsAsync(70, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyOutput>)[new MethodologyOutput(70, EcrCode.Create("OUT1"), 5)]);
    }

    private SaveCalculationBindingHandler Handler() => new(_bindings, _drafts, _methodologies, _uow, _access, _user, _audit, _clock);

    /// <remarks>
    /// F-09 (четвертий раунд UX): <c>"not json"</c> зберігався з <c>200</c>, а
    /// <c>MethodologyRuleMatcher</c> вважає битий предикат таким, що не
    /// збігається ні з чим. Мутація: прибрати <c>RequireValidBindingPredicate</c>
    /// — прив'язка йде на збереження.
    /// </remarks>
    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"42\":{\"nested\":1}}")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Зламаний_предикат_відхиляється_ключем(string matchJson)
    {
        Column(CellDataType.Calculated);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(
                MethodologyId, ColumnDefId, "OUT1", matchJson, isActive: true, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0422.bindingMatchInvalid", error.Details!["messageKey"]);
        Assert.Equal("OUT1", error.Details!["outputCode"]);
        _bindings.DidNotReceiveWithAnyArgs().Add(null!);
    }

    /// <remarks>
    /// F-09: неіснуючий вихід <c>NO_SUCH_OUT</c> приймався з <c>200</c>.
    /// Мутація: прибрати <c>RequireDeclaredOutputAsync</c> — прив'язка йде на
    /// збереження.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Неоголошений_вихід_відхиляється_ключем()
    {
        Column(CellDataType.Calculated);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(
                MethodologyId, ColumnDefId, "NO_SUCH_OUT", "{}", isActive: true, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0422.bindingUnknownOutput", error.Details!["messageKey"]);
        Assert.Equal("NO_SUCH_OUT", error.Details!["outputCode"]);
        _bindings.DidNotReceiveWithAnyArgs().Add(null!);
    }

    private void Column(CellDataType dataType)
        => _bindings.FindColumnAsync(ColumnDefId, Arg.Any<CancellationToken>())
            .Returns(new BoundColumnRef(TableDefId, "Manual", dataType));

    private Task<Ecr.Application.Calculations.Dto.CalculationBindingDto> Save()
        => Handler().HandleAsync(
            MethodologyId, ColumnDefId, "OUT1", "{}", isActive: true, CancellationToken.None);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Прив_язка_на_колонку_ручного_вводу_відхиляється()
    {
        // ⛔ Колонка типу `Decimal` не обчислювана: `EditRules` дає
        // `ColumnIsComputed = false` і пускає оператора правити її руками. Його
        // число живе в `doc.CellValue`, число методології — в
        // `calc.CalculationResult`, і у звіт іде друге. Оператор про це не
        // дізнається ніяк: розбіжність не показує жоден екран.
        Column(CellDataType.Decimal);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Save);

        Assert.Equal(ErrorCodes.ComputationOnManualColumn, error.ErrorCode);
        Assert.Contains("Manual", error.Message, StringComparison.Ordinal);
        Assert.Contains("Decimal", error.Message, StringComparison.Ordinal);

        // Нічого не записано: відмова йде ДО черги на вставку.
        _bindings.DidNotReceiveWithAnyArgs().Add(null!);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Прив_язка_на_колонку_Calculated_приймається()
    {
        Column(CellDataType.Calculated);

        var saved = await Save();

        Assert.Equal(ColumnDefId, saved.ColumnDefId);
        Assert.Equal(TableDefId, saved.TableDefId);
        _bindings.ReceivedWithAnyArgs(1).Add(null!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колонки_що_не_існує_відхиляється_404_а_не_422()
    {
        // ⚠ Порядок відмов важить: «колонки немає» і «колонка не та» — різні
        // відповіді, і плутати їх не можна. Неіснуючу колонку перевірка типу не
        // має перетворювати на 422 «не обчислювана».
        _bindings.FindColumnAsync(ColumnDefId, Arg.Any<CancellationToken>())
            .Returns((BoundColumnRef?)null);

        var error = await Assert.ThrowsAsync<NotFoundException>(Save);

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0404.column", error.Details!["messageKey"]);
        Assert.Equal(ColumnDefId.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Details!["columnDefId"]);
    }

    /// <summary>
    /// Зміна прив'язки ОПУБЛІКОВАНОЇ методології лягає в журнал структурних
    /// змін із автором і станом до/після (F-10, UX-прохід, четвертий раунд).
    /// </summary>
    /// <remarks>
    /// ⛔ Доти будь-хто з <c>Calculation.EditRule</c> міг перевести вихід уже
    /// опублікованої методології в іншу колонку — і журнал не знав про це
    /// нічого: ні хто, ні що було до того.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "F-10")]
    public async Task Зміна_прив_язки_опублікованої_методології_пишеться_в_журнал_з_автором_і_станом_до()
    {
        Column(CellDataType.Calculated);
        PublishedMethodology();

        var existing = new CalculationBinding(TableDefId, ColumnDefId, MethodologyId, "OUT1", "{}");
        _bindings.FindAsync(ColumnDefId, MethodologyId, "OUT1", Arg.Any<CancellationToken>()).Returns(existing);

        await Handler().HandleAsync(
            MethodologyId, ColumnDefId, "OUT1", """{"kind":"stack"}""", isActive: false, CancellationToken.None);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "cfg.CalculationBinding"
                && r.Operation == "Update"
                && r.ChangedByUserId == 9
                && r.OldJson!.Contains("\"matchJson\":\"{}\"", StringComparison.Ordinal)
                && r.OldJson.Contains("\"isActive\":true", StringComparison.Ordinal)
                && r.NewJson!.Contains("\"isActive\":false", StringComparison.Ordinal)
                && r.NewJson.Contains("\"publishedMethodology\":true", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "F-10")]
    public async Task Нова_прив_язка_теж_журналюється_а_повтор_без_змін_ні()
    {
        Column(CellDataType.Calculated);

        await Save();

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "cfg.CalculationBinding"
                && r.Operation == "Create"
                && r.OldJson == null
                && r.NewJson!.Contains("\"publishedMethodology\":false", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        _audit.ClearReceivedCalls();
        _uow.ClearReceivedCalls();

        // Той самий PUT удруге — прив'язка вже така сама: ні запису, ні журналу.
        var same = new CalculationBinding(TableDefId, ColumnDefId, MethodologyId, "OUT1", "{}");
        _bindings.FindAsync(ColumnDefId, MethodologyId, "OUT1", Arg.Any<CancellationToken>()).Returns(same);

        await Save();

        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    /// <summary>Методологія з опублікованою версією 1.0.</summary>
    private void PublishedMethodology()
    {
        var methodology = new Methodology(
            EcrCode.Create("M1"), new LocalizedText(new Dictionary<string, string> { ["en"] = "M1" }));
        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, _clock.UtcNow);
        methodology.AddVersion(version);
        version.Publish(publishedByUserId: 2, "first", new DateOnly(2026, 1, 1), testsPassed: true, _clock.UtcNow);

        _drafts.FindAsync(MethodologyId, Arg.Any<CancellationToken>()).Returns(methodology);
    }
}
