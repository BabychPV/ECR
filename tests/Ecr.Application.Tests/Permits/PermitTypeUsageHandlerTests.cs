// tests/Ecr.Application.Tests/Permits/PermitTypeUsageHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// Права й форма відповіді «використання типу даними» (PM-03b2): як у читання типу - <c>Registry.View</c> і заборона на
/// шапку; кількості не віддаються тому, кому тип не видно.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>GetPermitTypeUsageHandler</c> прибрати перевірку <c>RegistryAccess.IsDenied</c> - червоний
/// «Тип_під_забороною…» (замість 404 віддаються лічильники); замінити <c>Permission</c> на інше право - червоний
/// «Без_права_Registry_View…».
/// </remarks>
public sealed class PermitTypeUsageHandlerTests
{
    private readonly IPermitTypeStore _permits = Substitute.For<IPermitTypeStore>();

    [Fact]
    public async Task Без_права_Registry_View_відхиляється_і_до_сховища_не_звертається()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }.Build());

        await Assert.ThrowsAsync<AccessDeniedException>(() => Handler(access).HandleAsync("AIR", default));

        await _permits.DidNotReceiveWithAnyArgs().FindByCodeAsync(default!, default);
    }

    [Fact]
    public async Task Тип_під_забороною_і_невідомий_тип_відповідають_однаково_404_без_підрахунку()
    {
        _permits.FindByCodeAsync("MISSING", Arg.Any<CancellationToken>()).Returns((PermitTypeDef?)null);
        var denied = Type("DENIED");
        _permits.FindByCodeAsync("DENIED", Arg.Any<CancellationToken>()).Returns(denied);
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 9 }.Permission("Registry.View").Deny(ResourceKind.Registry, denied.HeaderRegistryDefId).Build());

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Handler(access).HandleAsync("MISSING", default));
        var hidden = await Assert.ThrowsAsync<NotFoundException>(() => Handler(access).HandleAsync("DENIED", default));

        Assert.Equal("ECR-PRM-0404", missing.ErrorCode);
        Assert.Equal(missing.ErrorCode, hidden.ErrorCode);
        await _permits.DidNotReceiveWithAnyArgs().CountPermitsAsync(default!, default);
    }

    [Fact]
    public async Task Відповідь_містить_лише_кількості_за_кодами_опублікованих_таблиць()
    {
        var type = Type("AIR");
        _permits.FindByCodeAsync("AIR", Arg.Any<CancellationToken>()).Returns(type);
        _permits.ListTablesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new PermitTypeTable(1, EcrCode.Create("EMIS"), new LocalizedText(Name("Emissions")), 1, 201),
            new PermitTypeTable(1, EcrCode.Create("FUEL"), new LocalizedText(Name("Fuel")), 2, 202),
        ]);
        _permits.CountPermitsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [type.HeaderRegistryDefId] = 7, [201] = 40 });

        var usage = await Handler(Viewer()).HandleAsync("AIR", default);

        Assert.Equal(7, usage.InstanceCount);
        Assert.Equal([("EMIS", 40), ("FUEL", 0)], usage.Tables.Select(t => (t.Code, t.RowCount)));
        // Усі реєстри типу - одним викликом (шапка першою).
        await _permits.Received(1).CountPermitsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { type.HeaderRegistryDefId, 201, 202 })),
            Arg.Any<CancellationToken>());
    }

    private GetPermitTypeUsageHandler Handler(IAccessDecisionService access)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        return new GetPermitTypeUsageHandler(_permits, access, user);
    }

    private static IAccessDecisionService Viewer()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.View").Build());
        return access;
    }

    private static PermitTypeDef Type(string code)
        => new(EcrCode.Create(code), new LocalizedText(Name(code)), PermitCategory.Air, 100);

    private static Dictionary<string, string> Name(string en) => new() { ["en"] = en };
}
