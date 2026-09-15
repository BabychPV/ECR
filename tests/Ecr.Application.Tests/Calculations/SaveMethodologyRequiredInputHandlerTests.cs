// tests/Ecr.Application.Tests/Calculations/SaveMethodologyRequiredInputHandlerTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// UI-аудит, lane 5 (`Q-337`): «обов'язкові вхідні колонки методології» —
/// без валідації і без попередження про застарілу прив'язку.
/// </summary>
/// <remarks>
/// ⛔ Частина знахідки вже виправлена раніше (`Q-332`, `NumberInput` →
/// `Select searchable`) — цей тест НЕ про те. Тут: (1) сервер має відхиляти
/// `ColumnDefId`, що не відповідає жодній реальній колонці, навіть коли
/// клієнт пропонує лише реальні id через searchable dropdown; (2) вимогу
/// МОЖНА завести ДО того, як з'явилась прив'язка — це не помилка; (3) список
/// вимог повинен позначати, чи має колонка АКТИВНУ прив'язку цієї
/// методології ЗАРАЗ, а не тільки на момент створення вимоги.
/// </remarks>
public sealed class SaveMethodologyRequiredInputHandlerTests
{
    private const int VersionId = 1;
    private const int MethodologyId = 7;
    private const int ColumnDefId = 42;
    private static readonly DateTime Now = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly MethodologyVersion _version =
        new(MethodologyId, "1.0.0.0", CalculationLevel.Configuration, 9, Now);

    public SaveMethodologyRequiredInputHandlerTests()
    {
        _user.UserId.Returns(9);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(SaveMethodologyRequiredInputHandler.Permission)
                .Build());

        _drafts.FindVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_version);
    }

    private SaveMethodologyRequiredInputHandler Handler() => new(_drafts, _bindings, _uow, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колонки_що_не_існує_відхиляється_ECR_TMPL_0404()
    {
        // ⛔ Мутаційний доказ: без перевірки в `SaveMethodologyRequiredInputHandler`
        // (`bindings.FindTableOfColumnAsync`) цей виклик пройшов би успішно —
        // рівно та поведінка, що й до фіксу.
        _bindings.FindTableOfColumnAsync(ColumnDefId, Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(
                VersionId, ColumnDefId, RequiredInputSeverity.Block, hint: null, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колонка_без_жодної_прив_язки_все_одно_приймається_як_попередження_не_блокування()
    {
        // Колонка РЕАЛЬНА (таблиця 3), але прив'язок методології ще немає
        // взагалі — методологію ще проєктують, вимогу можна додати наперед.
        _bindings.FindTableOfColumnAsync(ColumnDefId, Arg.Any<CancellationToken>())
            .Returns((int?)3);
        _bindings.ListAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CalculationBinding>)[]);
        _drafts.FindRequiredInputAsync(VersionId, ColumnDefId, Arg.Any<CancellationToken>())
            .Returns((MethodologyRequiredInput?)null);

        var result = await Handler().HandleAsync(
            VersionId, ColumnDefId, RequiredInputSeverity.Block, hint: null, CancellationToken.None);

        Assert.Equal(ColumnDefId, result.ColumnDefId);
        Assert.False(result.HasActiveBinding);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колонка_з_активною_прив_язкою_позначається_як_прив_язана()
    {
        _bindings.FindTableOfColumnAsync(ColumnDefId, Arg.Any<CancellationToken>())
            .Returns((int?)3);
        _bindings.ListAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CalculationBinding>)
                [new CalculationBinding(3, ColumnDefId, MethodologyId, "OUT1", "{}")]);
        _drafts.FindRequiredInputAsync(VersionId, ColumnDefId, Arg.Any<CancellationToken>())
            .Returns((MethodologyRequiredInput?)null);

        var result = await Handler().HandleAsync(
            VersionId, ColumnDefId, RequiredInputSeverity.Block, hint: null, CancellationToken.None);

        Assert.True(result.HasActiveBinding);
    }
}

/// <summary>
/// UI-аудит, lane 5: список вимог позначає «завислу» вимогу — колонку, чию
/// прив'язку деактивували ПІСЛЯ того, як вимогу вже завели.
/// </summary>
public sealed class ListMethodologyRequiredInputsHandlerTests
{
    private const int VersionId = 1;
    private const int MethodologyId = 7;
    private const int ColumnDefId = 42;
    private static readonly DateTime Now = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly MethodologyVersion _version =
        new(MethodologyId, "1.0.0.0", CalculationLevel.Configuration, 9, Now);

    public ListMethodologyRequiredInputsHandlerTests()
    {
        _user.UserId.Returns(9);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(ListMethodologyRequiredInputsHandler.Permission)
                .Build());

        _drafts.FindVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_version);
    }

    private ListMethodologyRequiredInputsHandler Handler() => new(_drafts, _bindings, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Деактивована_прив_язка_лишає_вимогу_без_позначки_прив_язаної()
    {
        // ⛔ Найважливіший мутаційний доказ лейну 5: без обчислення
        // `HasActiveBinding` за АКТИВНИМИ прив'язками (не за самим фактом
        // існування рядка) деактивована прив'язка виглядала б так само, як і
        // активна, — рівно той дефект з репро аудиту.
        var requiredInput = new MethodologyRequiredInput(
            VersionId, ColumnDefId, RequiredInputSeverity.Block, hint: null);
        _drafts.GetAllRequiredInputsAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyRequiredInput>)[requiredInput]);

        var inactiveBinding = new CalculationBinding(3, ColumnDefId, MethodologyId, "OUT1", "{}");
        inactiveBinding.Update("{}", isActive: false);
        _bindings.ListAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CalculationBinding>)[inactiveBinding]);

        var result = await Handler().HandleAsync(VersionId, CancellationToken.None);

        Assert.Single(result);
        Assert.False(result[0].HasActiveBinding);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Активна_прив_язка_позначає_вимогу_як_прив_язану()
    {
        var requiredInput = new MethodologyRequiredInput(
            VersionId, ColumnDefId, RequiredInputSeverity.Block, hint: null);
        _drafts.GetAllRequiredInputsAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyRequiredInput>)[requiredInput]);

        var activeBinding = new CalculationBinding(3, ColumnDefId, MethodologyId, "OUT1", "{}");
        _bindings.ListAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CalculationBinding>)[activeBinding]);

        var result = await Handler().HandleAsync(VersionId, CancellationToken.None);

        Assert.Single(result);
        Assert.True(result[0].HasActiveBinding);
    }
}
