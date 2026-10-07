// tests/Ecr.Application.Tests/Sources/FieldMapUnitCompatibilityTests.cs
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
/// D-4 приймальної №8: одиниці мапінгу різної розмірності (MJ → kg) відхиляються при створенні й
/// прийнятті нової одиниці джерела; інтеграл за часом, однакові й невідомі одиниці - ні.
/// </summary>
public sealed class FieldMapUnitCompatibilityTests
{
    private const int Kg = 1;
    private const int Tonne = 2;
    private const int Mj = 3;

    private static readonly UnitCatalogSnapshot Catalog = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
        {
            ["kg"] = new UnitRef(Kg, "kg", 1),
            ["t"] = new UnitRef(Tonne, "t", 1),
            ["MJ"] = new UnitRef(Mj, "MJ", 2),
        },
        new Dictionary<string, int>(StringComparer.Ordinal));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "D-4")]
    [InlineData(Mj, Kg, AggregationKind.Sum, true)]
    [InlineData(Mj, Kg, null, true)]
    [InlineData(Kg, Tonne, AggregationKind.Sum, false)]
    [InlineData(Kg, Kg, AggregationKind.Sum, false)]
    [InlineData(Mj, Kg, AggregationKind.TimeIntegral, false)]
    [InlineData(null, Kg, AggregationKind.Sum, false)]
    [InlineData(Mj, null, AggregationKind.Sum, false)]
    [InlineData(999, Kg, AggregationKind.Sum, false)]
    public void Несумісність_лише_для_двох_відомих_одиниць_різної_розмірності_поза_інтегралом(
        int? source, int? target, AggregationKind? aggregation, bool expected)
        => Assert.Equal(expected, FieldMapUnitCompatibility.IsIncompatible(Catalog, source, target, aggregation));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "D-4")]
    public async Task Створення_мапінгу_MJ_у_kg_відхиляється_422_з_ключем_і_нічого_не_пише_а_kg_у_t_заводиться()
    {
        var sources = Substitute.For<ICollectionStore>();
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        var uow = Substitute.For<IUnitOfWork>();
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(Catalog);

        user.UserId.Returns(9);
        uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Integration.Manage").Build());
        sources.FindSourceEntityAsync(5, Arg.Any<CancellationToken>())
            .Returns(new SourceEntity(1, "AF01", RegistrySourceKind.External));
        sources.ColumnDefExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        sources.FindProjectIdsUsingColumnAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        sources.UnitExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        sources.AddFieldMapAsync(Arg.Any<EntityFieldMap>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<EntityFieldMap>());

        var handler = new CreateEntityFieldMapHandler(
            sources, access, user, uow, Substitute.For<IAuditWriter>(), Substitute.For<IClock>(), units);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => handler.HandleAsync(
            5,
            new CreateEntityFieldMapCommand("Heat", FieldTargetKind.Column, 100, null, Mj, Kg, null, null),
            CancellationToken.None));

        Assert.Equal(ErrorCodes.UnitDimensionMismatch, ex.ErrorCode);
        Assert.Equal(FieldMapUnitCompatibility.MismatchKey, ex.Details!["messageKey"]);
        Assert.Equal("MJ", ex.Details["from"]);
        Assert.Equal("kg", ex.Details["to"]);
        await sources.DidNotReceiveWithAnyArgs().AddFieldMapAsync(default!, default);

        // Контроль: сумісна пара проходить.
        await handler.HandleAsync(
            5,
            new CreateEntityFieldMapCommand("Mass", FieldTargetKind.Column, 100, null, Kg, Tonne, null, null),
            CancellationToken.None);
        await sources.Received(1).AddFieldMapAsync(Arg.Any<EntityFieldMap>(), Arg.Any<CancellationToken>());
    }
}
