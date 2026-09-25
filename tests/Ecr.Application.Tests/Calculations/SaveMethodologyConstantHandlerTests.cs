// tests/Ecr.Application.Tests/Calculations/SaveMethodologyConstantHandlerTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Аудит-пас 4: числова константа без значення/одиниці кидала
/// <c>BusinessRuleException</c> без <c>Details["messageKey"]</c> — сире
/// українське речення доходило до клієнта незалежно від мови інтерфейсу
/// (той самий клас дефекту, що Q-303/Q-304).
/// </summary>
public sealed class SaveMethodologyConstantHandlerTests
{
    private const int VersionId = 1;
    private static readonly DateTime Now = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public SaveMethodologyConstantHandlerTests()
    {
        _user.UserId.Returns(9);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(SaveMethodologyConstantHandler.Permission)
                .Build());

        var version = new MethodologyVersion(1, "1.0.0.0", CalculationLevel.Configuration, 9, Now);
        _drafts.FindVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(version);
        _drafts.GetConstantsByCodeAsync(VersionId, "K1", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyConstant>)[]);

        // Довідник знає рівно одиницю 5 — ту, що в запитах нижче.
        _units.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitCatalogSnapshot(
                new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["t"] = new(5, "t", 1) },
                new Dictionary<string, int>(StringComparer.Ordinal)));
    }

    private SaveMethodologyConstantHandler Handler() => new(_drafts, _registries, _units, _uow, _access, _user);

    /// <remarks>
    /// B-01 (четвертий раунд UX): неіснуюча одиниця доходила до бази й падала
    /// 500 на <c>FK_MC_Unit</c>. Мутація: прибрати виклик
    /// <c>MethodologyUnitChecks.RequireKnownAsync</c> з обробника — відмови немає,
    /// константа йде на збереження.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Неіснуюча_одиниця_відхиляється_ключем_до_збереження()
    {
        var request = new SaveMethodologyConstant(
            ConstantKind.Numeric, Value: 1m, UnitId: 999_999, TextValue: null,
            ValidFrom: null, ValidTo: null, Category: null, SubstanceEntryId: null, Source: null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "K1", request, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0422.unknownUnit", error.Details!["messageKey"]);
        Assert.Equal("999999", error.Details!["unitId"]);
        Assert.Equal("K1", error.Details!["code"]);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Число_без_значення_несе_ключ_каталогу()
    {
        var request = new SaveMethodologyConstant(
            ConstantKind.Numeric, Value: null, UnitId: 5, TextValue: null,
            ValidFrom: null, ValidTo: null, Category: null, SubstanceEntryId: null, Source: null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "K1", request, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.NotNull(error.Details);
        Assert.Equal("err.ECR-CALC-0422.constantNoValue", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.1")]
    public async Task Число_без_одиниці_несе_ключ_каталогу()
    {
        var request = new SaveMethodologyConstant(
            ConstantKind.Numeric, Value: 100m, UnitId: null, TextValue: null,
            ValidFrom: null, ValidTo: null, Category: null, SubstanceEntryId: null, Source: null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "K1", request, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.NotNull(error.Details);
        Assert.Equal("err.ECR-CALC-0422.constantNoUnit", error.Details!["messageKey"]);
    }
}
