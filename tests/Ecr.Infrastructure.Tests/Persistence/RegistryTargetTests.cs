// tests/Ecr.Infrastructure.Tests/Persistence/RegistryTargetTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Groups;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>GET /registries/by-id/{id}</c> (Permits PRMG, «unlisted»): вид і опис цілі, права/грант, S18 без оракула існування.
/// </summary>
/// <remarks>
/// Мутації: у <c>GetRegistryTargetHandler</c> прибрати <c>RegistryAccess.RequireAsync</c> - червоніють тести прав і S18; повертати
/// <c>NotFound(code)</c> замість <c>NotFound(id)</c> - червоніє «Заборонений_і_неіснуючий…» (код у тексті); не ставити
/// <c>Kind</c> групи - червоніє «Група_шапка_і_звичайний…».
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryTargetTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static GetRegistryTargetHandler Handler(PermitHarness h)
        => new(h.Registries, h.Access, h.User, h.Permits, new PermitGroupStore(h.Db));

    private async Task<(int GroupRegistryId, int HeaderRegistryId, int PlainRegistryId, string GroupCode)> SeedAsync(PermitHarness h)
    {
        var type = $"RT{_tag}";
        await PermitFixtures.PublishTypeAsync(h, type);
        var snapshot = new PermitGroupSnapshotCache();

        await using var a = await h.NextRequestAsync();
        var group = await new CreatePermitGroupHandler(
                new PermitGroupStore(a.Db), a.Permits, a.Registries, a.Uow, a.Audit, snapshot, a.Access, a.User, a.Clock)
            .HandleAsync(new CreatePermitGroupRequest($"RTG{_tag}", new() { ["en"] = "Water" }, new() { ["en"] = "Water permits" }, [type]), default);

        await using var b = await h.NextRequestAsync();
        var header = (await b.Permits.FindByCodeAsync(type, default))!.HeaderRegistryDefId;

        await using var c = await h.NextRequestAsync();
        var plain = new Ecr.Domain.Entities.Configuration.RegistryDef(
            Ecr.Domain.ValueObjects.EcrCode.Create($"RTP{_tag}"), PermitHarness.Text("Plain"), false);
        c.Registries.AddDefinition(plain);
        await c.Uow.SaveChangesAsync(default);

        return (group.RegistryDefId, header, plain.Id, group.Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Група_шапка_і_звичайний_довідник_віддають_вид_опис_і_активність()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var seed = await SeedAsync(h);

        await using var r = await h.NextRequestAsync();
        var group = await Handler(r).HandleAsync(seed.GroupRegistryId, default);
        Assert.Equal((RegistryDefKind.PermitGroup, "PRMG_" + seed.GroupCode, "Water permits", true), (group.Kind, group.Code, group.DescriptionL10n!.Get("en"), group.IsActive));

        var header = await Handler(r).HandleAsync(seed.HeaderRegistryId, default);
        Assert.Equal((RegistryDefKind.PermitHeader, (string?)null), (header.Kind, header.DescriptionL10n?.Get("en")));

        var plain = await Handler(r).HandleAsync(seed.PlainRegistryId, default);
        Assert.Equal((RegistryDefKind.Dictionary, $"RTP{_tag}"), (plain.Kind, plain.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заборонений_і_неіснуючий_довідник_дають_однакову_404_без_коду_реєстру()
    {
        await using var editor = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var seed = await SeedAsync(editor);
        const int missing = 987_654_321;

        await using var reader = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        reader.Access.BuildProfileAsync(reader.UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = reader.UserId }.Permission("Registry.View").Deny(ResourceKind.Registry, seed.PlainRegistryId).Build());

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => Handler(reader).HandleAsync(seed.PlainRegistryId, default));
        var absent = await Assert.ThrowsAsync<NotFoundException>(() => Handler(reader).HandleAsync(missing, default));

        Assert.Equal((absent.ErrorCode, absent.Details!["messageKey"]), (denied.ErrorCode, denied.Details!["messageKey"]));
        Assert.Equal(absent.Details.Keys.Order(), denied.Details.Keys.Order());
        Assert.DoesNotContain($"RTP{_tag}", denied.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(denied.Details.Values.Select(v => v?.ToString()), v => v is not null && v.Contains($"RTP{_tag}", StringComparison.Ordinal));
        Assert.Equal(seed.PlainRegistryId.ToString(System.Globalization.CultureInfo.InvariantCulture), denied.Details["registryDefId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_і_гранта_403_однаково_для_існуючого_і_неіснуючого_а_грант_Read_відкриває_лише_свій_довідник()
    {
        await using var editor = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var seed = await SeedAsync(editor);

        await using var none = await PermitHarness.CreateAsync(sql, []);
        var existing = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler(none).HandleAsync(seed.PlainRegistryId, default));
        var absent = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler(none).HandleAsync(987_654_321, default));
        Assert.Equal(absent.ErrorCode, existing.ErrorCode);

        await using var granted = await PermitHarness.CreateAsync(sql, []);
        granted.Access.BuildProfileAsync(granted.UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = granted.UserId }.Grant(ResourceKind.Registry, seed.PlainRegistryId, GrantLevel.Read).Build());
        Assert.Equal(seed.PlainRegistryId, (await Handler(granted).HandleAsync(seed.PlainRegistryId, default)).Id);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Handler(granted).HandleAsync(seed.GroupRegistryId, default));
    }
}
