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
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public SaveCalculationBindingTests()
    {
        _user.UserId.Returns(9);

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
    }

    private SaveCalculationBindingHandler Handler() => new(_bindings, _drafts, _uow, _access, _user);

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
}
