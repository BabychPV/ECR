// tests/Ecr.Infrastructure.Tests/Persistence/PermitGroupPickerTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Permits.Groups;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Піклер групи типів дозволів, деталь запису під кодом групи, перелік довідників і сторож «реєстр керується групою»
/// (Permits, PRMG) - на живій базі.
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) у <c>GroupEntriesAsync</c> брати <c>members</c> без фільтра заборони - червоніє «Піклер_не_віддає_чужий…»;
/// (2) у ключі кешу прибрати набір членів - той самий тест (кеш віддає склад іншого читача); (3) у <c>RequireDefinitionAsync</c> писача
/// прибрати <c>RequireNotManaged</c> - червоніє «Запис_у_прихований_реєстр_групи…»; (4) у <c>ListRegistriesHandler</c> не додавати
/// групи до прихованих - червоніє «Перелік_довідників…»; (5) у <c>GetRegistryEntryHandler</c> прибрати перевірку заборони -
/// червоніє «Деталь_запису…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitGroupPickerTests(SqlServerFixture sql)
{
    private static readonly DateOnly AsOf = new(2026, 6, 30);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string Type(int n) => $"PK{_tag}{n}";

    private string GroupCode => $"PKG{_tag}";

    private static readonly IMemoryCache Memory = new MemoryCache(new MemoryCacheOptions());

    private static GetRegistryEntriesHandler Entries(PermitHarness h, PermitGroupSnapshotCache snapshot)
    {
        var store = new PermitGroupStore(h.Db);
        return new GetRegistryEntriesHandler(
            h.Registries, new RegistryResolver(), new RegistryEntryCache(Memory), h.Access, h.User,
            null, null, store, new PermitGroupResolver(store, snapshot), h.Permits);
    }

    private async Task<(int GroupRegistryId, long P1, long P2, long P3, long P4, int Header1, int Header2)> SeedAsync(
        PermitHarness h, PermitGroupSnapshotCache snapshot)
    {
        await PermitFixtures.PublishTypeAsync(h, Type(1));
        await PermitFixtures.PublishTypeAsync(h, Type(2));

        await using var create = await h.NextRequestAsync();
        var group = await new CreatePermitGroupHandler(
                new PermitGroupStore(create.Db), create.Permits, create.Registries, create.Uow, create.Audit, snapshot,
                create.Access, create.User, create.Clock)
            .HandleAsync(new CreatePermitGroupRequest(GroupCode, new() { ["en"] = "Water" }, new() { ["en"] = "Water permits" }, [Type(1), Type(2)]), default);

        // Той самий номер у двох типах (унікальний лише в межах типу) - обидва в піклері.
        async Task<long> PermitAsync(string type, string number, DateOnly from, DateOnly to, bool active)
        {
            await using var r = await h.NextRequestAsync();
            var created = await r.CreatePermit.HandleAsync(new CreatePermitRequest(type, number, null, null, from, to, null), default);
            if (active)
            {
                await PermitFixtures.ActivateDirectAsync(h, type, created.Id);
            }

            return created.Id;
        }

        var p1 = await PermitAsync(Type(1), "N-1", new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), active: true);
        var p2 = await PermitAsync(Type(2), "N-1", new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), active: true);
        var p3 = await PermitAsync(Type(1), "N-2", new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), active: false);
        var p4 = await PermitAsync(Type(2), "N-3", new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1), active: true);

        await using var read = await h.NextRequestAsync();
        return (
            group.RegistryDefId, p1, p2, p3, p4,
            (await read.Permits.FindByCodeAsync(Type(1), default))!.HeaderRegistryDefId,
            (await read.Permits.FindByCodeAsync(Type(2), default))!.HeaderRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Піклер_віддає_чинні_активні_дозволи_усіх_типів_складу_з_типом_у_порядку_складу()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var snapshot = new PermitGroupSnapshotCache();
        var seed = await SeedAsync(h, snapshot);

        await using var r = await h.NextRequestAsync();
        var list = await Entries(r, snapshot).HandleAsync("PRMG_" + GroupCode, AsOf, null, default);

        // Чернетка (P3) і прострочений (P4) не показуються; той самий номер у двох типах - два записи з різним типом.
        Assert.Equal([seed.P1, seed.P2], list.Select(e => e.Id));
        // Display - номер дозволу (Code - службовий код запису).
        Assert.Equal(["N-1", "N-1"], list.Select(e => e.Display));
        Assert.Equal([Type(1), Type(2)], list.Select(e => e.TypeCode));
        Assert.All(list, e => Assert.NotNull(e.TypeName));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Піклер_не_віддає_чужий_склад_читачеві_із_забороною_на_реєстр_типу_і_кеш_не_протікає()
    {
        await using var editor = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var snapshot = new PermitGroupSnapshotCache();
        var seed = await SeedAsync(editor, snapshot);

        // Повний читач першим наповнює кеш.
        await using var full = await editor.NextRequestAsync();
        Assert.Equal(2, (await Entries(full, snapshot).HandleAsync("PRMG_" + GroupCode, AsOf, null, default)).Count);

        // Читач із забороною на реєстр шапки другого типу бачить лише перший; кеш повного читача йому не віддається.
        await using var denied = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        denied.Access.BuildProfileAsync(denied.UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = denied.UserId }.Permission("Registry.View").Deny(ResourceKind.Registry, seed.Header2).Build());

        var list = await Entries(denied, snapshot).HandleAsync("PRMG_" + GroupCode, AsOf, null, default);
        Assert.Equal([seed.P1], list.Select(e => e.Id));

        // І навпаки: повний читач далі бачить обидва (кеш відфільтрованого не потрапив до нього).
        await using var again = await editor.NextRequestAsync();
        Assert.Equal(2, (await Entries(again, snapshot).HandleAsync("PRMG_" + GroupCode, AsOf, null, default)).Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Деактивована_група_віддає_порожній_перелік_а_без_asOf_піклер_відмовляє()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var snapshot = new PermitGroupSnapshotCache();
        await SeedAsync(h, snapshot);

        await using var a = await h.NextRequestAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Entries(a, snapshot).HandleAsync("PRMG_" + GroupCode, default, null, default));
        Assert.Equal("err.ECR-REQ-0422.asOfRequired", ex.Details!["messageKey"]);

        await using var b = await h.NextRequestAsync();
        var current = await new GetPermitGroupHandler(new PermitGroupStore(b.Db), b.Permits, b.Registries, b.Access, b.User)
            .HandleAsync(GroupCode, default);
        await using var c = await h.NextRequestAsync();
        await new UpdatePermitGroupHandler(
                new PermitGroupStore(c.Db), c.Permits, c.Registries, c.Uow, c.Audit, snapshot, c.Access, c.User, c.Clock)
            .HandleAsync(GroupCode, new UpdatePermitGroupRequest(new() { ["en"] = "Water" }, null, false, current.RowVersion), default);

        await using var d = await h.NextRequestAsync();
        Assert.Empty(await Entries(d, snapshot).HandleAsync("PRMG_" + GroupCode, AsOf, null, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Деталь_запису_під_кодом_групи_віддається_лише_для_члена_без_заборони_інакше_404_як_для_неіснуючого()
    {
        await using var editor = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var snapshot = new PermitGroupSnapshotCache();
        var seed = await SeedAsync(editor, snapshot);

        GetRegistryEntryHandler Detail(PermitHarness x)
            => new(x.Registries, x.Access, x.User, new PermitGroupResolver(new PermitGroupStore(x.Db), snapshot));

        await using var ok = await editor.NextRequestAsync();
        Assert.Equal(seed.P1, (await Detail(ok).HandleAsync("PRMG_" + GroupCode, seed.P1, default)).Id);

        // Дозвіл типу ПОЗА складом групи (інша група/тип) - 404, така сама відповідь, як для неіснуючого Id.
        await PermitFixtures.PublishTypeAsync(editor, Type(3));
        await using var other = await editor.NextRequestAsync();
        var foreign = (await other.CreatePermit.HandleAsync(new CreatePermitRequest(Type(3), "N-9", null, null, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), null), default)).Id;

        await using var miss = await editor.NextRequestAsync();
        var notMember = await Assert.ThrowsAsync<NotFoundException>(() => Detail(miss).HandleAsync("PRMG_" + GroupCode, foreign, default));
        await using var missing = await editor.NextRequestAsync();
        var absent = await Assert.ThrowsAsync<NotFoundException>(() => Detail(missing).HandleAsync("PRMG_" + GroupCode, long.MaxValue - 1, default));
        Assert.Equal((absent.ErrorCode, absent.Details!["messageKey"]), (notMember.ErrorCode, notMember.Details!["messageKey"]));

        // Заборона на РЕАЛЬНИЙ реєстр члена - теж 404.
        await using var denied = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        denied.Access.BuildProfileAsync(denied.UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = denied.UserId }.Permission("Registry.View").Deny(ResourceKind.Registry, seed.Header2).Build());
        await Assert.ThrowsAsync<NotFoundException>(() => Detail(denied).HandleAsync("PRMG_" + GroupCode, seed.P2, default));
        Assert.Equal(seed.P1, (await Detail(denied).HandleAsync("PRMG_" + GroupCode, seed.P1, default)).Id);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_довідників_ховає_групу_без_прапора_і_віддає_її_з_видом_і_описом_з_прапором()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var snapshot = new PermitGroupSnapshotCache();
        await SeedAsync(h, snapshot);

        ListRegistriesHandler List(PermitHarness x)
            => new(x.Registries, x.Access, x.User, x.Clock, x.Permits, new PermitGroupStore(x.Db));

        await using var a = await h.NextRequestAsync();
        Assert.DoesNotContain(await List(a).HandleAsync(false, default), d => d.Code.StartsWith("PRMG_", StringComparison.Ordinal));

        await using var b = await h.NextRequestAsync();
        var withFlag = await List(b).HandleAsync(true, default);
        var group = Assert.Single(withFlag, d => d.Code == "PRMG_" + GroupCode);
        Assert.Equal((RegistryDefKind.PermitGroup, "Water permits", true), (group.Kind, group.DescriptionL10n!.Get("en"), group.AsOfRequired));
        Assert.Equal(RegistryDefKind.PermitHeader, withFlag.Single(d => d.Code == "PRM_" + Type(1)).Kind);
        Assert.All(withFlag.Where(d => !d.Code.StartsWith("PRM", StringComparison.Ordinal)), d => Assert.Equal(RegistryDefKind.Dictionary, d.Kind));

        // Заборона S18 на реєстр групи ховає її й з прапором.
        await using var denied = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        denied.Access.BuildProfileAsync(denied.UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = denied.UserId }.Permission("Registry.View").Deny(ResourceKind.Registry, group.Id).Build());
        Assert.DoesNotContain(await List(denied).HandleAsync(true, default), d => d.Code == "PRMG_" + GroupCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_у_прихований_реєстр_групи_і_його_опис_заборонені_а_префікс_зарезервований()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var snapshot = new PermitGroupSnapshotCache();
        var seed = await SeedAsync(h, snapshot);

        await using var w = await h.NextRequestAsync();
        var write = await Assert.ThrowsAsync<BusinessRuleException>(() => w.Writer.WriteAsync(
            new RegistryEntryWriteBatch(seed.GroupRegistryId, [new RegistryEntryWrite("X-1", new Dictionary<string, object?>())]), default));
        Assert.Equal("err.ECR-PRM-0422.permitGroupRegistryManaged", write.Details!["messageKey"]);

        await using var u = await h.NextRequestAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => u.Writer.UpdateAsync(
            new RegistryEntryUpdateBatch(seed.GroupRegistryId, [new RegistryEntryUpdate(seed.P1, new Dictionary<string, object?>())]), default));

        await using var c = await PermitHarness.CreateAsync(sql, [.. PermitHarness.EverythingWithData, "Registry.EditDefinition"]);
        var create = await Assert.ThrowsAsync<BusinessRuleException>(() => new CreateRegistryHandler(
                c.Registries, c.Uow, c.Access, c.User)
            .HandleAsync("PRMG_OTHER" + _tag, new Dictionary<string, string> { ["en"] = "x" }, false, default));
        Assert.Equal(("ECR-PRM-4097", "err.ECR-PRM-4097.registryCode"), (create.ErrorCode, create.Details!["messageKey"]));

        await using var db = sql.CreateContext();
        Assert.False(db.RegistryEntries.Any(e => e.RegistryDefId == seed.GroupRegistryId));
    }
}
