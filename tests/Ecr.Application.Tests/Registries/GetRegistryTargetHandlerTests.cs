using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// <c>GET /registries/by-id/{id}</c> (Permits PRMG): ПОРЯДОК «спершу читаємо довідник, потім перевіряємо право» (відповідь і час не
/// розрізняють існування) і Deny на прихований реєстр групи (404 як для неіснуючого, вид не витікає).
/// </summary>
/// <remarks>
/// Мутації: переставити <c>FindDefinitionByIdAsync</c> після <c>RequireAsync</c> - червоніє «Довідник_читається_першим…»;
/// прибрати перевірку заборони (або читати <c>Kind</c> до неї) - червоніє «Заборона_на_реєстр_групи…».
/// </remarks>
public sealed class GetRegistryTargetHandlerTests
{
    private const int GroupId = 900;
    private const int Missing = 987_654;

    private sealed class Rig
    {
        public Rig(AccessProfile profile)
        {
            Calls = [];
            Registries = Substitute.For<IRegistryStore>();
            Access = Substitute.For<IAccessDecisionService>();
            Groups = Substitute.For<IPermitGroupStore>();
            Permits = Substitute.For<IPermitTypeStore>();

            var group = new RegistryDef(EcrCode.Create("PRMG_WATER"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), false);
            typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(group, GroupId);

            Registries.FindDefinitionByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Calls.Add("find");
                return Task.FromResult<RegistryDef?>(call.Arg<int>() == GroupId ? group : null);
            });
            Access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Calls.Add("profile");
                return Task.FromResult(profile);
            });
            Groups.ListBriefAsync(Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IReadOnlyList<PermitGroupBrief>>([new PermitGroupBrief(GroupId, true, new LocalizedText(new Dictionary<string, string> { ["en"] = "Water permits" }))]));

            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(profile.UserId);
            Handler = new GetRegistryTargetHandler(Registries, Access, user, Permits, Groups);
        }

        public List<string> Calls { get; }

        public IRegistryStore Registries { get; }

        public IAccessDecisionService Access { get; }

        public IPermitGroupStore Groups { get; }

        public IPermitTypeStore Permits { get; }

        public GetRegistryTargetHandler Handler { get; }
    }

    private static AccessProfile Profile(Func<AccessBuilder, AccessBuilder> configure)
        => configure(new AccessBuilder { UserId = 7 }).Build();

    [Theory]
    [InlineData(GroupId)]
    [InlineData(Missing)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Довідник_читається_першим_а_право_перевіряється_після_і_для_існуючого_і_для_неіснуючого(int id)
    {
        var rig = new Rig(Profile(b => b.Permission("Registry.View")));

        try
        {
            await rig.Handler.HandleAsync(id, default);
        }
        catch (NotFoundException)
        {
        }

        Assert.Equal(["find", "profile"], rig.Calls);
    }

    [Theory]
    [InlineData(GroupId)]
    [InlineData(Missing)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_права_і_гранта_спершу_читається_довідник_і_відповідь_403_однакова(int id)
    {
        var rig = new Rig(Profile(b => b));

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => rig.Handler.HandleAsync(id, default));

        Assert.Equal("find", rig.Calls[0]);
        Assert.NotNull(ex);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Заборона_на_реєстр_групи_дає_ту_саму_404_що_неіснуючий_і_вид_та_опис_не_читаються()
    {
        var rig = new Rig(Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, GroupId)));

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => rig.Handler.HandleAsync(GroupId, default));
        var absent = await Assert.ThrowsAsync<NotFoundException>(() => rig.Handler.HandleAsync(Missing, default));

        Assert.Equal((absent.ErrorCode, absent.Details!["messageKey"]), (denied.ErrorCode, denied.Details!["messageKey"]));
        Assert.Equal(absent.Details.Keys.Order(), denied.Details.Keys.Order());
        Assert.DoesNotContain("PRMG_WATER", denied.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(denied.Details.Values, v => v?.ToString()?.Contains("PRMG_WATER", StringComparison.Ordinal) == true);

        // Вид і опис групи (PermitGroup) читаються ПІСЛЯ доступу - при заборонах до них не доходить.
        await rig.Groups.DidNotReceive().ListBriefAsync(Arg.Any<CancellationToken>());
        await rig.Permits.DidNotReceive().ListPublishedHeaderRegistryIdsAsync(Arg.Any<CancellationToken>());
    }
}
