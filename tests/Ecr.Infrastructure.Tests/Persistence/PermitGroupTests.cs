// tests/Ecr.Infrastructure.Tests/Persistence/PermitGroupTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Groups;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Групи типів дозволів над живою базою (Permits, PRMG): створення з прихованим реєстром <c>PRMG_</c>, склад (тип у кількох групах),
/// деактивація, видалення невикористаної, відмова для використаної, права, конкуренція, знімок резолвера.
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) у <c>CreatePermitGroupHandler</c> не створювати реєстр - червоніє «Створення_групи…»; (2) у
/// <c>PermitGroupAssembler.MembersAsync</c> прибрати перевірку <c>Published</c> - червоніє «Склад_лише_з_опублікованих…»;
/// (3) у <c>DeletePermitGroupHandler</c> прибрати перевірку використання - червоніє «Використана_група_не_видаляється…»;
/// (4) у <c>RequireAsync</c> змінити право - червоніє «Без_права_Permit_EditType…»; (5) у <c>PermitGroupResolver.Accepts</c>
/// прибрати <c>IsActive</c> - червоніє «Деактивація…»; (6) прибрати <c>RequireRowVersion</c> - червоніє «Застарілий_токен…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitGroupTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string Type(int n) => $"PG{_tag}{n}";

    private string GroupCode => $"GRP{_tag}";

    private sealed class Handlers(PermitHarness h, PermitGroupSnapshotCache cache, IRegistryStore? registries = null)
    {
        public PermitGroupStore Store { get; } = new(h.Db);

        public PermitGroupSnapshotCache Cache { get; } = cache;

        public PermitGroupResolver Resolver => new(Store, Cache);

        private IRegistryStore Reg => registries ?? h.Registries;

        public CreatePermitGroupHandler Create => new(Store, h.Permits, h.Registries, h.Uow, h.Audit, Cache, h.Access, h.User, h.Clock);

        public UpdatePermitGroupHandler Update => new(Store, h.Permits, h.Registries, h.Uow, h.Audit, Cache, h.Access, h.User, h.Clock);

        public ReplacePermitGroupTypesHandler Replace => new(Store, h.Permits, h.Registries, h.Uow, h.Audit, Cache, h.Access, h.User, h.Clock);

        public DeletePermitGroupHandler Delete => new(Store, h.Permits, Reg, h.Uow, h.Audit, Cache, h.Access, h.User, h.Clock);

        public GetPermitGroupHandler Get => new(Store, h.Permits, h.Registries, h.Access, h.User);

        public ListPermitGroupsHandler List => new(Store, h.Permits, h.Registries, h.Access, h.User);
    }

    private static async Task<(PermitHarness H, Handlers G)> NextAsync(PermitHarness previous, PermitGroupSnapshotCache cache, IRegistryStore? registries = null)
    {
        var h = await previous.NextRequestAsync();
        return (h, new Handlers(h, cache, registries));
    }

    private static Dictionary<string, string> Name(string en) => new() { ["en"] = en };

    private static CreatePermitGroupRequest NewGroup(string code, params string[] types)
        => new(code, Name("Water permits"), Name("Water intake, discharge and general"), types);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_групи_заводить_прихований_реєстр_без_полів_і_склад_у_порядку()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, Type(1));
        await PermitFixtures.PublishTypeAsync(h, Type(2));
        var cache = new PermitGroupSnapshotCache();

        await using var create = await h.NextRequestAsync();
        var created = await new Handlers(create, cache).Create.HandleAsync(NewGroup(GroupCode, Type(2), Type(1)), default);

        Assert.Equal(("PRMG_" + GroupCode, true), (created.RegistryCode, created.IsActive));
        Assert.Equal([Type(2), Type(1)], created.Types.Select(t => t.Code));
        Assert.Equal(["PRM_" + Type(2), "PRM_" + Type(1)], created.Types.Select(t => t.HeaderRegistryCode));
        Assert.Equal("Water intake, discharge and general", created.DescriptionL10n["en"]);

        await using var db = sql.CreateContext();
        var registry = await db.RegistryDefs.Include(d => d.Fields).SingleAsync(d => d.Id == created.RegistryDefId);
        Assert.Equal(("PRMG_" + GroupCode, 0, true), (registry.Code, registry.Fields.Count, registry.IsActive));
        Assert.False(await db.RegistryEntries.AnyAsync(e => e.RegistryDefId == created.RegistryDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Резолвер_бачить_склад_і_його_зміну_та_тип_може_бути_у_двох_групах()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var t1 = await PermitFixtures.PublishTypeAsync(h, Type(1));
        var t2 = await PermitFixtures.PublishTypeAsync(h, Type(2));
        var cache = new PermitGroupSnapshotCache();

        await using var a = await h.NextRequestAsync();
        var first = await new Handlers(a, cache).Create.HandleAsync(NewGroup(GroupCode, Type(1), Type(2)), default);
        await using var b = await h.NextRequestAsync();
        var second = await new Handlers(b, cache).Create.HandleAsync(NewGroup(GroupCode + "B", Type(1)), default);

        await using var read = await h.NextRequestAsync();
        var resolver = new Handlers(read, cache).Resolver;
        await resolver.EnsureFreshAsync(default);

        var header1 = (await read.Permits.FindByCodeAsync(Type(1), default))!.HeaderRegistryDefId;
        var header2 = (await read.Permits.FindByCodeAsync(Type(2), default))!.HeaderRegistryDefId;
        Assert.True(resolver.Accepts(first.RegistryDefId, header1));
        Assert.True(resolver.Accepts(first.RegistryDefId, header2));
        Assert.True(resolver.Accepts(second.RegistryDefId, header1));
        Assert.False(resolver.Accepts(second.RegistryDefId, header2));
        Assert.Equal([header1, header2], resolver.ExpandRegistryIds(first.RegistryDefId));
        Assert.Equal([t1.Code, t2.Code], first.Types.Select(t => t.Code));

        // Заміна складу: тип 1 виходить, лишається тип 2; інвалідація локального кешу бачить зміну одразу.
        await using var edit = await h.NextRequestAsync();
        var edited = await new Handlers(edit, cache).Replace.HandleAsync(
            GroupCode, new ReplacePermitGroupTypesRequest([Type(2)], first.RowVersion), default);
        Assert.Equal([Type(2)], edited.Types.Select(t => t.Code));

        await resolver.EnsureFreshAsync(default);
        Assert.False(resolver.Accepts(first.RegistryDefId, header1));
        Assert.True(resolver.Accepts(first.RegistryDefId, header2));
        Assert.True(resolver.Accepts(second.RegistryDefId, header1));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Склад_лише_з_опублікованих_існуючих_типів_без_повторів()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, Type(1));
        await h.Create.HandleAsync(new Ecr.Application.Permits.Dto.CreatePermitTypeRequest(Type(2), Name(Type(2)), "Air"), default);
        var cache = new PermitGroupSnapshotCache();

        async Task<string> KeyOfAsync(string typeCode, params string[] more)
        {
            await using var x = await h.NextRequestAsync();
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(
                () => new Handlers(x, cache).Create.HandleAsync(NewGroup(GroupCode, [typeCode, .. more]), default));
            Assert.Equal("ECR-PRM-0422", ex.ErrorCode);
            return (string)ex.Details!["messageKey"]!;
        }

        Assert.Equal("err.ECR-PRM-0422.permitGroupMemberNotPublished", await KeyOfAsync(Type(2)));
        Assert.Equal("err.ECR-PRM-0422.permitGroupMemberUnknown", await KeyOfAsync("NOPE" + _tag));
        Assert.Equal("err.ECR-PRM-0422.permitGroupMemberDuplicate", await KeyOfAsync(Type(1), Type(1)));

        await using var check = sql.CreateContext();
        Assert.False(await check.PermitGroups.AnyAsync(g => g.Code == GroupCode));
        Assert.False(await check.RegistryDefs.AnyAsync(d => d.Code == "PRMG_" + GroupCode));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Назва_і_код_перевіряються_до_запису()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var cache = new PermitGroupSnapshotCache();

        async Task<string> KeyOfAsync(CreatePermitGroupRequest request)
        {
            await using var x = await h.NextRequestAsync();
            var ex = await Assert.ThrowsAnyAsync<EcrException>(() => new Handlers(x, cache).Create.HandleAsync(request, default));
            return (string)ex.Details!["messageKey"]!;
        }

        Assert.Equal(
            "err.ECR-PRM-0422.permitGroupNameRequired",
            await KeyOfAsync(new CreatePermitGroupRequest(GroupCode, Name("  "), null, null)));
        Assert.Equal(
            "err.ECR-PRM-0422.permitGroupCodeInvalid",
            await KeyOfAsync(new CreatePermitGroupRequest(new string('A', 60), Name("x"), null, null)));
        Assert.Equal(
            "err.ECR-PRM-0422.permitGroupTextTooLong",
            await KeyOfAsync(new CreatePermitGroupRequest(GroupCode, Name("x"), Name(new string('d', 1001)), null)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дубль_коду_групи_і_зайнятий_код_реєстру_дають_4097()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var cache = new PermitGroupSnapshotCache();

        await using var a = await h.NextRequestAsync();
        await new Handlers(a, cache).Create.HandleAsync(NewGroup(GroupCode), default);

        await using var b = await h.NextRequestAsync();
        var duplicate = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new Handlers(b, cache).Create.HandleAsync(NewGroup(GroupCode), default));
        Assert.Equal(("ECR-PRM-4097", "err.ECR-PRM-4097.code"), (duplicate.ErrorCode, duplicate.Details!["messageKey"]));

        // Звичайний довідник із зарезервованим префіксом: група з таким кодом реєстру не створюється.
        await using var c = await h.NextRequestAsync();
        c.Registries.AddDefinition(new RegistryDef(EcrCode.Create("PRMG_" + GroupCode + "X"), PermitHarness.Text("x"), false));
        await c.Uow.SaveChangesAsync(default);

        await using var d = await h.NextRequestAsync();
        var taken = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new Handlers(d, cache).Create.HandleAsync(NewGroup(GroupCode + "X"), default));
        Assert.Equal(("ECR-PRM-4097", "err.ECR-PRM-4097.registryCode"), (taken.ErrorCode, taken.Details!["messageKey"]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Деактивація_вимикає_реєстр_і_нові_значення_але_група_лишається_групою()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, Type(1));
        var cache = new PermitGroupSnapshotCache();

        await using var a = await h.NextRequestAsync();
        var created = await new Handlers(a, cache).Create.HandleAsync(NewGroup(GroupCode, Type(1)), default);

        await using var b = await h.NextRequestAsync();
        var updated = await new Handlers(b, cache).Update.HandleAsync(
            GroupCode, new UpdatePermitGroupRequest(Name("Renamed"), null, false, created.RowVersion), default);
        Assert.Equal((false, "Renamed", 0), (updated.IsActive, updated.NameL10n["en"], updated.DescriptionL10n.Count));

        await using var read = await h.NextRequestAsync();
        var resolver = new Handlers(read, cache).Resolver;
        await resolver.EnsureFreshAsync(default);
        var header = (await read.Permits.FindByCodeAsync(Type(1), default))!.HeaderRegistryDefId;
        Assert.True(resolver.IsGroup(created.RegistryDefId));
        Assert.False(resolver.Accepts(created.RegistryDefId, header));
        Assert.Equal([header], resolver.ExpandRegistryIds(created.RegistryDefId));

        await using var db = sql.CreateContext();
        var registry = await db.RegistryDefs.SingleAsync(d => d.Id == created.RegistryDefId);
        Assert.Equal((false, "Renamed"), (registry.IsActive, registry.NameL10n.Get("en")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застарілий_токен_дає_4099_для_зміни_і_для_складу()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, Type(1));
        var cache = new PermitGroupSnapshotCache();

        await using var a = await h.NextRequestAsync();
        var created = await new Handlers(a, cache).Create.HandleAsync(NewGroup(GroupCode), default);

        await using var b = await h.NextRequestAsync();
        await new Handlers(b, cache).Replace.HandleAsync(
            GroupCode, new ReplacePermitGroupTypesRequest([Type(1)], created.RowVersion), default);

        await using var c = await h.NextRequestAsync();
        var stale = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new Handlers(c, cache).Update.HandleAsync(
                GroupCode, new UpdatePermitGroupRequest(Name("x"), null, true, created.RowVersion), default));
        Assert.Equal(("ECR-PRM-4099", "err.ECR-PRM-4099.changed"), (stale.ErrorCode, stale.Details!["messageKey"]));

        await using var d = await h.NextRequestAsync();
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new Handlers(d, cache).Replace.HandleAsync(
                GroupCode, new ReplacePermitGroupTypesRequest([], created.RowVersion), default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невикористана_група_видаляється_разом_зі_складом_і_реєстром_а_використана_ні()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, Type(1));
        var cache = new PermitGroupSnapshotCache();

        await using var a = await h.NextRequestAsync();
        var used = await new Handlers(a, cache).Create.HandleAsync(NewGroup(GroupCode, Type(1)), default);
        await using var a2 = await h.NextRequestAsync();
        var free = await new Handlers(a2, cache).Create.HandleAsync(NewGroup(GroupCode + "F", Type(1)), default);

        // Використання: колонка шаблону вказує на реєстр групи - підставляємо облік використання.
        var registries = Substitute.For<IRegistryStore>();
        registries.CountTemplateUsageAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<int, RegistryTemplateUsage> { [used.RegistryDefId] = new(2, 1) });

        await using var b = await h.NextRequestAsync();
        var inUse = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new Handlers(b, cache, registries).Delete.HandleAsync(GroupCode, default));
        Assert.Equal(("ECR-PRM-4098", "err.ECR-PRM-4098.inUse"), (inUse.ErrorCode, inUse.Details!["messageKey"]));

        await using var c = await h.NextRequestAsync();
        await new Handlers(c, cache).Delete.HandleAsync(GroupCode + "F", default);

        await using var db = sql.CreateContext();
        Assert.False(await db.PermitGroups.AnyAsync(g => g.Code == GroupCode + "F"));
        Assert.False(await db.PermitGroupMembers.AnyAsync(m => !db.PermitGroups.Any(g => g.Id == m.PermitGroupId)));
        Assert.False(await db.RegistryDefs.AnyAsync(d => d.Id == free.RegistryDefId));
        Assert.True(await db.PermitGroups.AnyAsync(g => g.Code == GroupCode));

        await using var d2 = await h.NextRequestAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => new Handlers(d2, cache).Get.HandleAsync(GroupCode + "F", default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Permit_EditType_змінювати_не_можна_а_читати_з_Registry_View_можна()
    {
        await using var editor = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var cache = new PermitGroupSnapshotCache();
        await using var a = await editor.NextRequestAsync();
        var created = await new Handlers(a, cache).Create.HandleAsync(NewGroup(GroupCode), default);

        await using var reader = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        var g = new Handlers(reader, cache);
        await Assert.ThrowsAsync<AccessDeniedException>(() => g.Create.HandleAsync(NewGroup(GroupCode + "R"), default));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => g.Update.HandleAsync(GroupCode, new UpdatePermitGroupRequest(Name("x"), null, true, created.RowVersion), default));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => g.Replace.HandleAsync(GroupCode, new ReplacePermitGroupTypesRequest([], created.RowVersion), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => g.Delete.HandleAsync(GroupCode, default));

        Assert.Equal(GroupCode, (await g.Get.HandleAsync(GroupCode, default)).Code);
        Assert.Contains(GroupCode, (await g.List.HandleAsync(default)).Select(x => x.Code));

        await using var none = await PermitHarness.CreateAsync(sql, []);
        await Assert.ThrowsAsync<AccessDeniedException>(() => new Handlers(none, cache).Get.HandleAsync(GroupCode, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заборона_S18_на_реєстр_групи_ховає_її_як_неіснуючу()
    {
        await using var editor = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var cache = new PermitGroupSnapshotCache();
        await using var a = await editor.NextRequestAsync();
        var created = await new Handlers(a, cache).Create.HandleAsync(NewGroup(GroupCode), default);

        await using var reader = await PermitHarness.CreateAsync(sql, ["Registry.View", "Permit.EditType"]);
        reader.Access.BuildProfileAsync(reader.UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = reader.UserId }
                .Permission("Registry.View").Permission("Permit.EditType")
                .Deny(ResourceKind.Registry, created.RegistryDefId).Build());
        var g = new Handlers(reader, cache);

        var get = await Assert.ThrowsAsync<NotFoundException>(() => g.Get.HandleAsync(GroupCode, default));
        Assert.Equal("ECR-PRM-0405", get.ErrorCode);
        Assert.DoesNotContain(GroupCode, (await g.List.HandleAsync(default)).Select(x => x.Code));
        await Assert.ThrowsAsync<NotFoundException>(() => g.Delete.HandleAsync(GroupCode, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ревізія_знімка_змінюється_при_створенні_групи_і_зміні_складу()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, Type(1));
        var cache = new PermitGroupSnapshotCache();

        await using var a = await h.NextRequestAsync();
        var store = new PermitGroupStore(a.Db);
        var before = await store.GetSnapshotRevisionAsync(default);

        var created = await new Handlers(a, cache).Create.HandleAsync(NewGroup(GroupCode, Type(1)), default);
        await using var b = await h.NextRequestAsync();
        var afterCreate = await new PermitGroupStore(b.Db).GetSnapshotRevisionAsync(default);
        Assert.NotEqual(before, afterCreate);

        await using var c = await h.NextRequestAsync();
        await new Handlers(c, cache).Replace.HandleAsync(GroupCode, new ReplacePermitGroupTypesRequest([], created.RowVersion), default);
        await using var d = await h.NextRequestAsync();
        var afterReplace = await new PermitGroupStore(d.Db).GetSnapshotRevisionAsync(default);
        Assert.NotEqual(afterCreate, afterReplace);

        // Видалення групи теж міняє ревізію (хеш пар Id+ревізія, а не сума: колізія створення/видалення неможлива).
        await using var e = await h.NextRequestAsync();
        await new Handlers(e, cache).Delete.HandleAsync(GroupCode, default);
        await using var f = await h.NextRequestAsync();
        var afterDelete = await new PermitGroupStore(f.Db).GetSnapshotRevisionAsync(default);
        Assert.NotEqual(afterReplace, afterDelete);
        Assert.NotEqual(afterCreate, afterDelete);
    }
}
