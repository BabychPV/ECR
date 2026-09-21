// tests/Ecr.Application.Tests/Units/UpdateUnitTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Units;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Units;

/// <summary>Зміна одиниці (директива №15, BE-15): правила, яких не видно крізь HTTP.</summary>
public sealed class UpdateUnitTests
{
    private readonly IUnitStore _units = Substitute.For<IUnitStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    public UpdateUnitTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Uom.EditCatalog").Build());
        _units.FindUnitUsageAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new UsageResponse(0, []));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Коефіцієнти_базової_одиниці_не_змінюються_навіть_без_посилань()
    {
        var kg = Make(isBase: true, factor: 1m);
        _units.FindUnitByIdAsync(1, Arg.Any<CancellationToken>()).Returns(kg);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Handler().HandleAsync(
            1, Text("kg"), Text("Kilogram"), 1000m, 0m, UnitVersion.Of(kg), default));

        Assert.Equal("err.ECR-UOM-0409.unitFactorInUse", error.Details!["messageKey"]);
        Assert.Equal(1m, kg.FactorToBase);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Версія_не_залежить_від_масштабу_десяткового()
    {
        // З бази множник приходить як 2.500000000000000000 (decimal(38,18)),
        // з пам'яті — як 2.5: різна версія дала б хибний 409 на наступній правці.
        Assert.Equal(
            UnitVersion.Of(Make(isBase: false, factor: 2.5m)),
            UnitVersion.Of(Make(isBase: false, factor: 2.500000000000000000m)));
        Assert.NotEqual(
            UnitVersion.Of(Make(isBase: false, factor: 2.5m)),
            UnitVersion.Of(Make(isBase: false, factor: 2.6m)));
    }

    private static Unit Make(bool isBase, decimal factor)
        => new(EcrCode.Create("kg"), new LocalizedText(Text("kg")), new LocalizedText(Text("Kilogram")),
               dimensionId: 1, isBase, factor, offsetToBase: 0m);

    private static Dictionary<string, string> Text(string value) => new() { ["en"] = value };

    private UpdateUnitHandler Handler()
        => new(_units, _uow, _access, _user, Substitute.For<IAuditWriter>(), Substitute.For<IClock>());
}
