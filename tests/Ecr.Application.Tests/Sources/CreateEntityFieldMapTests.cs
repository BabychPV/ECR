// tests/Ecr.Application.Tests/Sources/CreateEntityFieldMapTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Заведення мапінгу поля джерела (Прогалина 1 директиви паритету зі старою
/// системою).
/// </summary>
/// <remarks>
/// ⛔ До <see cref="CreateEntityFieldMapHandler"/> <see cref="EntityFieldMap"/>
/// заводився лише двома статичними фабриками домену, і обидві викликалися
/// виключно з тестів — жодного шляху АПІ не було. Тести тут доводять саме
/// ЦЕЙ шлях: право, існування сутностей-цілей і сам запис.
/// </remarks>
public sealed class CreateEntityFieldMapTests
{
    private const int SourceEntityId = 5;

    private readonly ICollectionStore _sources = Substitute.For<ICollectionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public CreateEntityFieldMapTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Integration.Manage").Build());

        _sources.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>())
            .Returns(new SourceEntity(1, "AF01", RegistrySourceKind.External));

        _sources.ColumnDefExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _sources.RegistryFieldDefExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _sources.UnitExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        // ⚠ Повертає той самий об'єкт: обробник читає з нього Id/поля щойно
        // створеного мапінгу для DTO відповіді, так само як інші стори
        // повертають агрегат, з яким щойно працювали.
        _sources.AddFieldMapAsync(Arg.Any<EntityFieldMap>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<EntityFieldMap>());
    }

    private CreateEntityFieldMapHandler Handler() => new(_sources, _access, _user);

    private static CreateEntityFieldMapCommand ColumnCommand(
        string sourceField = "Flare_01_CO",
        int? targetColumnDefId = 100,
        int? sourceUnitId = null,
        int? targetUnitId = null,
        string? targetRowKey = null,
        AggregationKind? aggregation = null)
        => new(sourceField, FieldTargetKind.Column, targetColumnDefId, null, sourceUnitId, targetUnitId, targetRowKey, aggregation);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Мапінг_на_колонку_заводиться_і_повертається()
    {
        var dto = await Handler().HandleAsync(SourceEntityId, ColumnCommand(), CancellationToken.None);

        Assert.Equal("Flare_01_CO", dto.SourceField);
        Assert.Equal(FieldTargetKind.Column, dto.TargetKind);
        Assert.Equal(100, dto.TargetColumnDefId);
        Assert.Null(dto.TargetRegistryFieldDefId);
        Assert.True(dto.IsActive);

        await _sources.Received(1).AddFieldMapAsync(
            Arg.Is<EntityFieldMap>(m =>
                m.SourceEntityId == SourceEntityId
                && m.SourceField == "Flare_01_CO"
                && m.TargetColumnDefId == 100),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Мапінг_на_поле_реєстру_заводиться()
    {
        var command = new CreateEntityFieldMapCommand(
            "Permit_Limit", FieldTargetKind.RegistryField, null, 200, null, null, null, null);

        var dto = await Handler().HandleAsync(SourceEntityId, command, CancellationToken.None);

        Assert.Equal(FieldTargetKind.RegistryField, dto.TargetKind);
        Assert.Equal(200, dto.TargetRegistryFieldDefId);
        Assert.Null(dto.TargetColumnDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Одиниці_межі_ставляться_коли_названі()
    {
        var dto = await Handler().HandleAsync(
            SourceEntityId, ColumnCommand(sourceUnitId: 11, targetUnitId: 12), CancellationToken.None);

        Assert.Equal(11, dto.SourceUnitId);
        Assert.Equal(12, dto.TargetUnitId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Матеріалізація_з_рядком_і_агрегацією_ставиться()
    {
        var dto = await Handler().HandleAsync(
            SourceEntityId,
            ColumnCommand(targetRowKey: "Flare_01", aggregation: AggregationKind.Sum),
            CancellationToken.None);

        Assert.Equal("Flare_01", dto.TargetRowKey);
        Assert.Equal(AggregationKind.Sum, dto.Aggregation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ECR-INT-0422")]
    public async Task Рядок_без_агрегації_відхиляється_доменом()
    {
        // ⛔ Мутаційний доказ: домен (EntityFieldMap.SetMaterialization) сам
        // відхиляє рядок-адресат без способу згортання — обробник цю
        // перевірку НЕ дублює, а лише пробрасує обидва значення разом. Тест
        // ловить регресію, якби хтось "спростив" виклик до одного параметра.
        var command = ColumnCommand(targetRowKey: "Flare_01", aggregation: null);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Handler().HandleAsync(SourceEntityId, command, CancellationToken.None));

        Assert.Equal("ECR-INT-0422", ex.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Право_перевіряється_першим()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(SourceEntityId, ColumnCommand(), CancellationToken.None));

        await _sources.DidNotReceive().FindSourceEntityAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ECR-INT-0404")]
    public async Task Неіснуюча_сутність_джерела_відхиляється()
    {
        _sources.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>())
            .Returns((SourceEntity?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(SourceEntityId, ColumnCommand(), CancellationToken.None));

        Assert.Equal(ErrorCodes.SourceEntityNotFound, ex.ErrorCode);
        Assert.Equal("err.ECR-INT-0404.sourceEntity", ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ECR-INT-0405")]
    public async Task Неіснуюча_колонка_ціль_відхиляється()
    {
        _sources.ColumnDefExistsAsync(100, Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(SourceEntityId, ColumnCommand(), CancellationToken.None));

        Assert.Equal(ErrorCodes.EntityFieldMapTargetNotFound, ex.ErrorCode);
        Assert.Equal("err.ECR-INT-0405.column", ex.Details!["messageKey"]);
        Assert.Equal("100", ex.Details!["columnDefId"]);

        // ⛔ Запис не мав статися: перевірка цілі йде ДО AddFieldMapAsync, а не
        // «спробувати й відкотити» — сховище не має транзакції для одного INSERT.
        await _sources.DidNotReceive().AddFieldMapAsync(Arg.Any<EntityFieldMap>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ECR-INT-0405")]
    public async Task Неіснуюче_поле_реєстру_ціль_відхиляється()
    {
        _sources.RegistryFieldDefExistsAsync(200, Arg.Any<CancellationToken>()).Returns(false);

        var command = new CreateEntityFieldMapCommand(
            "Permit_Limit", FieldTargetKind.RegistryField, null, 200, null, null, null, null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(SourceEntityId, command, CancellationToken.None));

        Assert.Equal(ErrorCodes.EntityFieldMapTargetNotFound, ex.ErrorCode);
        Assert.Equal("err.ECR-INT-0405.registryField", ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ECR-UOM-0404")]
    public async Task Неіснуюча_одиниця_відхиляється()
    {
        _sources.UnitExistsAsync(11, Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(
                SourceEntityId, ColumnCommand(sourceUnitId: 11), CancellationToken.None));

        Assert.Equal(ErrorCodes.UnitNotFound, ex.ErrorCode);
        Assert.Equal("err.ECR-UOM-0404.unitId", ex.Details!["messageKey"]);
        Assert.Equal("11", ex.Details!["id"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ECR-REQ-0422")]
    public async Task Колонка_без_targetColumnDefId_відхиляється()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(SourceEntityId, ColumnCommand(targetColumnDefId: null), CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, ex.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.entityFieldMapColumnRequired", ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ECR-REQ-0422")]
    public async Task Мапінг_на_колонку_з_targetRegistryFieldDefId_відхиляється()
    {
        var command = new CreateEntityFieldMapCommand(
            "Flare_01_CO", FieldTargetKind.Column, 100, 200, null, null, null, null);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(SourceEntityId, command, CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, ex.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.entityFieldMapColumnExtraField", ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ECR-REQ-0422")]
    public async Task Порожнє_поле_джерела_відхиляється()
    {
        // ⛔ Мутаційний доказ (`err.ECR-REQ-0422.entityFieldMapSourceField`):
        // прибери `messageKey` з кидка в `CreateEntityFieldMapHandler.HandleAsync`
        // — це твердження, і лише воно, червоніє; ErrorCode лишається тим самим.
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(SourceEntityId, ColumnCommand(sourceField: "  "), CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, ex.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.entityFieldMapSourceField", ex.Details!["messageKey"]);
    }
}
