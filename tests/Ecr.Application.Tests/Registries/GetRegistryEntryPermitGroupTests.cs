// tests/Ecr.Application.Tests/Registries/GetRegistryEntryPermitGroupTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Permits PRMG: «unlisted» - запис, що сидить у комірці колонки-групи, читається за кодом ГРУПИ
/// (<c>GET /registries/PRMG_X/entries/{id}</c>), хоча належить реєстру-члену. Чужий реєстр, реєстр поза складом і реєстр
/// члена під Deny - та сама відповідь «запису немає», що й для неіснуючого (клас S18).
/// </summary>
/// <remarks>
/// Мутація: у <c>GetRegistryEntryHandler</c> повернути <c>entry.RegistryDefId != definition.Id</c> без
/// <c>LookupTargetMatcher</c> - тест «запис типу-члена через групу» червоний.
/// </remarks>
public sealed class GetRegistryEntryPermitGroupTests
{
    private const int GroupId = 900;
    private const int MemberId = 10;
    private const int OutsideId = 11;
    private const long MemberEntry = 2001;
    private const long OutsideEntry = 3001;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly FakePermitGroupResolver _groups = new FakePermitGroupResolver().WithGroup(GroupId, [MemberId]);

    public GetRegistryEntryPermitGroupTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());

        var group = Definition(GroupId, "PRMG_WATER");
        var member = Definition(MemberId, "PRM_WATER_A");
        var field = new RegistryFieldDef(MemberId, EcrCode.Create("LIMIT"), Names("LIMIT"), CellDataType.Decimal, 1);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(field, 501);
        member.AddField(field);
        var outside = Definition(OutsideId, "PRM_AIR");

        foreach (var definition in new[] { group, member, outside })
        {
            _registries.FindDefinitionAsync(definition.Code, Arg.Any<CancellationToken>()).Returns(definition);
            _registries.FindDefinitionByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        }

        _registries.FindEntryAsync(MemberEntry, Arg.Any<CancellationToken>()).Returns(Entry(MemberEntry, MemberId, "N100"));
        _registries.FindEntryAsync(OutsideEntry, Arg.Any<CancellationToken>()).Returns(Entry(OutsideEntry, OutsideId, "A1"));

        var limit = new RegistryValue(MemberEntry, 501);
        limit.Set(CellDataType.Decimal, 12.5m, null);
        _registries.ListValuesAsync(MemberEntry, Arg.Any<CancellationToken>()).Returns([limit]);
        _registries.ListValuesAsync(OutsideEntry, Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Запис_типу_члена_читається_через_код_групи_з_полями_власного_реєстру()
    {
        var read = await Handler().HandleAsync("PRMG_WATER", MemberEntry, CancellationToken.None);

        Assert.Equal(MemberEntry, read.Id);
        Assert.Equal("N100", read.Code);
        Assert.Equal("12.5", read.Values["LIMIT"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Запис_типу_поза_складом_через_групу_не_читається()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync("PRMG_WATER", OutsideEntry, CancellationToken.None));

        Assert.Equal("err.ECR-REG-0404.registryEntry", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Реєстр_члена_під_Deny_через_групу_відповідає_як_неіснуючий_запис()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile(denied: $"Registry:{MemberId}"));

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync("PRMG_WATER", MemberEntry, CancellationToken.None));

        Assert.Equal("err.ECR-REG-0404.registryEntry", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_резолвера_груп_запис_реєстру_члена_через_код_групи_не_читається()
    {
        var bare = new GetRegistryEntryHandler(_registries, _access, _user);

        await Assert.ThrowsAsync<NotFoundException>(
            () => bare.HandleAsync("PRMG_WATER", MemberEntry, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Звичайний_довідник_не_кличе_резолвер_груп()
    {
        _groups.ThrowOnUse = true;

        var read = await Handler().HandleAsync("PRM_WATER_A", MemberEntry, CancellationToken.None);

        Assert.Equal(MemberEntry, read.Id);
        Assert.Equal(0, _groups.Calls);
        Assert.Equal(0, _groups.EnsureFreshCalls);
    }

    private GetRegistryEntryHandler Handler() => new(_registries, _access, _user, _groups);

    private static AccessProfile Profile(string? denied = null) => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(["Registry.View"], StringComparer.Ordinal),
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = denied is null ? new HashSet<string>() : new HashSet<string> { denied },
        RoleIds = new HashSet<int>(),
    };

    private static RegistryDef Definition(int id, string code)
    {
        var definition = new RegistryDef(EcrCode.Create(code), Names(code), isTemporal: true);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, id);
        return definition;
    }

    private static RegistryEntry Entry(long id, int registryDefId, string code)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Names(code));
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Names(string text) => new(new Dictionary<string, string> { ["en"] = text });
}
