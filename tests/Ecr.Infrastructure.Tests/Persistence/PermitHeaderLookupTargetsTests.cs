// tests/Ecr.Infrastructure.Tests/Persistence/PermitHeaderLookupTargetsTests.cs
using Ecr.Application.Common;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Реєстри ШАПОК опублікованих типів дозволів як ціль Lookup (<c>GET /registries?includePermitHeaders=true</c>), живий SQL.
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) у <c>ListRegistriesHandler</c> прибрати умову <c>includePermitHeaders</c> (завжди знімати з
/// прихованих) - червоний «за_замовчуванням_PRM_лишаються_прихованими»; (2) у <c>ListPublishedHeaderRegistryIdsAsync</c>
/// прибрати фільтр <c>Published</c> - червоний «чернетка_типу_не_пропонується»; (3) повернути <c>hidden</c> без перетину
/// з шапками, але лишити <c>RegistryAccess.IsDenied</c> вище - заборонений довідник лишається прихованим.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitHeaderLookupTargetsTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task За_згодою_у_переліку_є_шапка_опублікованого_типу_а_таблиця_і_список_TypeOnly_лишаються_прихованими()
    {
        var ids = await SeedAsync(publish: true);

        var targets = await ListAsync(includePermitHeaders: true);

        Assert.Contains(targets, d => d.Id == ids.Header);
        Assert.DoesNotContain(targets, d => d.Id == ids.Table);
        Assert.DoesNotContain(targets, d => d.Id == ids.TypeOnlyList);
        Assert.Contains(targets, d => d.Id == ids.Shared);
        Assert.Equal($"PRM_T{_tag}", targets.Single(d => d.Id == ids.Header).Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task За_замовчуванням_PRM_лишаються_прихованими()
    {
        var ids = await SeedAsync(publish: true);

        var byDefault = await ListAsync(includePermitHeaders: false);
        var legacyOverload = await ListLegacyAsync();

        Assert.DoesNotContain(byDefault, d => d.Id == ids.Header);
        Assert.DoesNotContain(legacyOverload, d => d.Id == ids.Header);
        Assert.DoesNotContain(byDefault, d => d.Code.StartsWith($"PRM_T{_tag}", StringComparison.Ordinal));
        Assert.Contains(byDefault, d => d.Id == ids.Shared);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чернетка_типу_не_пропонується_навіть_за_згодою()
    {
        var ids = await SeedAsync(publish: false);

        var targets = await ListAsync(includePermitHeaders: true);

        Assert.DoesNotContain(targets, d => d.Id == ids.Header);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Користувач_без_права_Registry_View_не_отримує_перелік_а_із_забороною_на_шапку_не_бачить_її()
    {
        var ids = await SeedAsync(publish: true);

        var denied = await ListAsync(includePermitHeaders: true, deny: ids.Header);
        Assert.DoesNotContain(denied, d => d.Id == ids.Header);
        Assert.Contains(denied, d => d.Id == ids.Shared);

        // Без права Registry.View перелік не віддається взагалі, прапорець права не обходить.
        await Assert.ThrowsAsync<Ecr.Application.Errors.AccessDeniedException>(() => ListAsync(includePermitHeaders: true, withViewPermission: false));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сховище_віддає_лише_шапки_опублікованих_типів()
    {
        var published = await SeedAsync(publish: true);
        var draft = await SeedAsync(publish: false, suffix: "D");

        await using var db = sql.CreateContext();
        var headers = await new PermitTypeStore(db).ListPublishedHeaderRegistryIdsAsync(CancellationToken.None);

        Assert.Contains(published.Header, headers);
        Assert.DoesNotContain(draft.Header, headers);
        Assert.DoesNotContain(published.Table, headers);
        Assert.DoesNotContain(published.TypeOnlyList, headers);
    }

    private async Task<(int Header, int Table, int TypeOnlyList, int Shared)> SeedAsync(bool publish, string suffix = "")
    {
        await using var db = sql.CreateContext();
        var tag = _tag + suffix;

        var header = new RegistryDef(EcrCode.Create($"PRM_T{tag}"), Text("Header"), isTemporal: true);
        var table = new RegistryDef(EcrCode.Create($"PRM_T{tag}_TBL"), Text("Table"), isTemporal: false);
        var list = new RegistryDef(EcrCode.Create($"PRM_T{tag}_TBL_KIND"), Text("List"), isTemporal: false);
        var shared = new RegistryDef(EcrCode.Create($"SHARED_{tag}"), Text("Shared"), isTemporal: false);
        db.RegistryDefs.AddRange(header, table, list, shared);
        await db.SaveChangesAsync();

        var type = new PermitTypeDef(EcrCode.Create($"T{tag}"), Text("Type"), PermitCategory.Air, header.Id);
        db.PermitTypeDefs.Add(type);
        await db.SaveChangesAsync();

        db.PermitTypeTables.Add(new PermitTypeTable(type.Id, EcrCode.Create("TBL"), Text("Table"), 1, table.Id));

        var kind = new RegistryFieldDef(table.Id, EcrCode.Create("Kind"), Text("Kind"), CellDataType.Lookup, 1);
        kind.PointTo(list.Id);
        db.RegistryFieldDefs.Add(kind);
        await db.SaveChangesAsync();

        db.PermitFieldMetas.Add(new PermitFieldMeta(kind.Id, type.Id, null, null, null, PermitListScope.TypeOnly, list.Id, null));
        await db.SaveChangesAsync();

        if (publish)
        {
            type.Publish(1, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        return (header.Id, table.Id, list.Id, shared.Id);
    }

    private ListRegistriesHandler Handler(Ecr.Infrastructure.Persistence.EcrDbContext db, int? deny, bool withViewPermission)
    {
        var builder = new AccessBuilder { UserId = 9 };
        if (withViewPermission)
        {
            builder = builder.Permission("Registry.View");
        }

        if (deny is { } registryId)
        {
            builder = builder.Deny(ResourceKind.Registry, registryId);
        }

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc));

        return new ListRegistriesHandler(new RegistryStore(db), access, user, clock, new PermitTypeStore(db));
    }

    private async Task<IReadOnlyList<RegistryDefDto>> ListAsync(
        bool includePermitHeaders, int? deny = null, bool withViewPermission = true)
    {
        await using var db = sql.CreateContext();

        return await Handler(db, deny, withViewPermission).HandleAsync(includePermitHeaders, CancellationToken.None);
    }

    private async Task<IReadOnlyList<RegistryDefDto>> ListLegacyAsync()
    {
        await using var db = sql.CreateContext();

        return await Handler(db, null, true).HandleAsync(CancellationToken.None);
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
