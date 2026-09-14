// tests/Ecr.Application.Tests/Units/CreateUnitTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Units;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Units;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Units;

/// <summary>
/// Заведення нової одиниці довідника <c>uom.Unit</c> (UI-аудит, lane 4: жоден
/// обліковий запис, включно з повноправним адміністратором, не мав шляху
/// додати одиницю виміру — той самий клас дефекту, що вже виправлений для
/// довідників, `Q-200`).
/// </summary>
public sealed class CreateUnitTests
{
    private readonly IUnitStore _units = Substitute.For<IUnitStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    public CreateUnitTests()
    {
        _user.UserId.Returns(9);
        Allow("Uom.EditCatalog");

        _units.FindUnitByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Unit?)null);
        _units.DimensionExistsAsync(Arg.Any<byte>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Заводить_нову_похідну_одиницю()
    {
        var created = await Handler().HandleAsync(
            "lb",
            new Dictionary<string, string> { ["en"] = "lb" },
            new Dictionary<string, string> { ["en"] = "Pound" },
            dimensionId: 1,
            factorToBase: 0.45359237m,
            offsetToBase: 0m,
            default);

        Assert.Equal("lb", created.Code);
        Assert.Equal(1, created.DimensionId);
        Assert.Equal(0.45359237m, created.FactorToBase);
        Assert.False(created.IsBase);

        _units.Received(1).AddUnit(Arg.Is<Unit>(u => u.Code == "lb"));
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Дублікат_коду_відхиляється_і_нічого_не_зберігається()
    {
        var clash = new Unit(
            Ecr.Domain.ValueObjects.EcrCode.Create("lb"),
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "lb" }),
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Pound" }),
            dimensionId: 1,
            isBase: false,
            factorToBase: 0.45359237m,
            offsetToBase: 0m);

        _units.FindUnitByCodeAsync("lb", Arg.Any<CancellationToken>()).Returns(clash);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            "lb",
            new Dictionary<string, string> { ["en"] = "lb" },
            new Dictionary<string, string> { ["en"] = "Pound" },
            dimensionId: 1,
            factorToBase: 0.45359237m,
            offsetToBase: 0m,
            default));

        Assert.Equal("ECR-UOM-4091", error.ErrorCode);
        Assert.NotNull(error.Details);
        Assert.Equal("err.ECR-UOM-4091", error.Details!["messageKey"]);
        Assert.Equal("lb", error.Details["code"]);

        _units.DidNotReceive().AddUnit(Arg.Any<Unit>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Неіснуюча_розмірність_відхиляється()
    {
        _units.DimensionExistsAsync(99, Arg.Any<CancellationToken>()).Returns(false);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => Handler().HandleAsync(
            "lb",
            new Dictionary<string, string> { ["en"] = "lb" },
            new Dictionary<string, string> { ["en"] = "Pound" },
            dimensionId: 99,
            factorToBase: 1m,
            offsetToBase: 0m,
            default));

        Assert.Equal("ECR-UOM-4041", error.ErrorCode);

        _units.DidNotReceive().AddUnit(Arg.Any<Unit>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_права_Uom_EditCatalog_одиниця_не_заводиться()
    {
        // ⛔ D-134: мутація, що прибирає перевірку права, має ловитися саме
        // цим тестом — і рівно тому право задане ЗАМІНОЮ дозволеного набору,
        // а не додаванням до нього.
        Allow("Registry.View");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            "lb",
            new Dictionary<string, string> { ["en"] = "lb" },
            new Dictionary<string, string> { ["en"] = "Pound" },
            dimensionId: 1,
            factorToBase: 1m,
            offsetToBase: 0m,
            default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);

        _units.DidNotReceive().AddUnit(Arg.Any<Unit>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Недопустимий_код_відхиляється()
    {
        var error = await Assert.ThrowsAsync<DomainException>(() => Handler().HandleAsync(
            "l b",
            new Dictionary<string, string> { ["en"] = "lb" },
            new Dictionary<string, string> { ["en"] = "Pound" },
            dimensionId: 1,
            factorToBase: 1m,
            offsetToBase: 0m,
            default));

        Assert.Equal("ECR-CFG-0422", error.ErrorCode);
    }

    private CreateUnitHandler Handler() => new(_units, _uow, _access, _user);

    private void Allow(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = 9 };
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }
}
