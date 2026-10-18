// tests/Ecr.Infrastructure.Tests/Persistence/PermitTypeStoreTests.cs
using Ecr.Application.Common;
using Ecr.Application.Registries;
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
/// Сховище типів дозволів і приховування їхніх реєстрів із загального переліку «Довідники» (Permits, PM-03).
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати <c>.Where(d =&gt; !hidden.Contains(d.Id))</c> у <c>ListRegistriesHandler</c> —
/// червоний тест «Приховані_реєстри_типу…» (шапка, таблиця й список «лише для типу» знову в переліку).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitTypeStoreTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Приховані_реєстри_типу_не_потрапляють_у_загальний_перелік_а_спільні_лишаються()
    {
        var ids = await SeedTypeAsync();

        var hiddenList = await ListAsync(withPermitStore: true);
        var plainList = await ListAsync(withPermitStore: false);

        // Шапка, таблиця й список «лише для типу» зникли; спільний довідник, на який вказує поле, — на місці.
        Assert.DoesNotContain(hiddenList, d => d.Id == ids.Header);
        Assert.DoesNotContain(hiddenList, d => d.Id == ids.Table);
        Assert.DoesNotContain(hiddenList, d => d.Id == ids.TypeOnlyList);
        Assert.Contains(hiddenList, d => d.Id == ids.Shared);

        // Контроль: без порту ті самі реєстри в переліку є, тож зникнення — саме від виключення, а не від сіду.
        Assert.Contains(plainList, d => d.Id == ids.Header);
        Assert.Contains(plainList, d => d.Id == ids.Table);
        Assert.Contains(plainList, d => d.Id == ids.TypeOnlyList);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Список_прихованих_реєстрів_береться_з_шапок_таблиць_і_лише_зі_списків_TypeOnly()
    {
        var ids = await SeedTypeAsync();

        await using var db = sql.CreateContext();
        var hidden = await new PermitTypeStore(db).ListHiddenRegistryIdsAsync(CancellationToken.None);

        Assert.Contains(ids.Header, hidden);
        Assert.Contains(ids.Table, hidden);
        Assert.Contains(ids.TypeOnlyList, hidden);
        Assert.DoesNotContain(ids.Shared, hidden);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кількість_пермітів_рахує_лише_живі_записи_шапки()
    {
        var ids = await SeedTypeAsync();

        await using (var db = sql.CreateContext())
        {
            var live = new RegistryEntry(ids.Header, EcrCode.Create($"P1_{_tag}"), Text("P1"), 1, DateTime.UnixEpoch);
            var deleted = new RegistryEntry(ids.Header, EcrCode.Create($"P2_{_tag}"), Text("P2"), 1, DateTime.UnixEpoch);
            deleted.SoftDelete();
            db.RegistryEntries.AddRange(live, deleted);
            await db.SaveChangesAsync();
        }

        await using var read = sql.CreateContext();
        var counts = await new PermitTypeStore(read).CountPermitsAsync([ids.Header, ids.Table], CancellationToken.None);

        Assert.Equal(1, counts[ids.Header]);
        Assert.False(counts.ContainsKey(ids.Table));
    }

    private async Task<(int Header, int Table, int TypeOnlyList, int Shared)> SeedTypeAsync()
    {
        await using var db = sql.CreateContext();

        var header = new RegistryDef(EcrCode.Create($"PRM_T{_tag}"), Text("Header"), isTemporal: true);
        var table = new RegistryDef(EcrCode.Create($"PRM_T{_tag}_TBL"), Text("Table"), isTemporal: false);
        var list = new RegistryDef(EcrCode.Create($"PRM_T{_tag}_TBL_KIND"), Text("List"), isTemporal: false);
        var shared = new RegistryDef(EcrCode.Create($"SHARED_{_tag}"), Text("Shared"), isTemporal: false);
        db.RegistryDefs.AddRange(header, table, list, shared);
        await db.SaveChangesAsync();

        var type = new PermitTypeDef(EcrCode.Create($"T{_tag}"), Text("Type"), PermitCategory.Air, header.Id);
        db.PermitTypeDefs.Add(type);
        await db.SaveChangesAsync();

        db.PermitTypeTables.Add(new PermitTypeTable(type.Id, EcrCode.Create("TBL"), Text("Table"), 1, table.Id));

        var kind = new RegistryFieldDef(table.Id, EcrCode.Create("Kind"), Text("Kind"), CellDataType.Lookup, 1);
        kind.PointTo(list.Id);
        var site = new RegistryFieldDef(table.Id, EcrCode.Create("Site"), Text("Site"), CellDataType.Lookup, 2);
        site.PointTo(shared.Id);
        db.RegistryFieldDefs.AddRange(kind, site);
        await db.SaveChangesAsync();

        db.PermitFieldMetas.AddRange(
            new PermitFieldMeta(kind.Id, type.Id, null, null, null, PermitListScope.TypeOnly, list.Id, null),
            new PermitFieldMeta(site.Id, type.Id, null, null, null, PermitListScope.SharedRegistry, shared.Id, null));
        await db.SaveChangesAsync();

        return (header.Id, table.Id, list.Id, shared.Id);
    }

    private async Task<IReadOnlyList<Ecr.Application.Registries.Dto.RegistryDefDto>> ListAsync(bool withPermitStore)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.View").Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc));

        await using var db = sql.CreateContext();

        return await new ListRegistriesHandler(
                new RegistryStore(db), access, user, clock, withPermitStore ? new PermitTypeStore(db) : null)
            .HandleAsync(CancellationToken.None);
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
