// tests/Ecr.Infrastructure.Tests/Persistence/PublishPermitTypeTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Атомарна публікація типу дозволу: N+1 реєстрів однією транзакцією, нова версія, права, спільні довідники
/// лишаються недоторканими (Permits, PM-03).
/// </summary>
/// <remarks>
/// Мутаційні докази (безпека й цілісність даних): (1) прибрати обгортку <c>uow.ExecuteInTransactionAsync</c> у
/// <c>PublishPermitTypeHandler.HandleAsync</c> (викликати <c>PublishAsync</c> напряму) - червоний «Збій_на_другій_таблиці…»:
/// шапка й перша таблиця лишаються опублікованими; (2) прибрати <c>PermissionCheck.RequireAsync</c> - червоний
/// «Редактор_без_Registry_Publish…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishPermitTypeTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string Code(string name) => $"{name}{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повний_цикл_створення_заміни_і_публікації_будує_реєстри_ключі_композицію_і_надбудову()
    {
        var shared = await EnsureSharedRegistryAsync($"SITES_{_tag}");
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var draft = await DraftAsync(h, shared.Code);

        await using var publish = await h.NextRequestAsync();
        var published = await publish.Publish.HandleAsync(Code("WASTE"), new PublishPermitTypeRequest("перший випуск типу", draft.RowVersion), default);

        Assert.Equal(("Published", 1, false), (published.Status, published.Version, published.HasUnpublishedChanges));

        await using var read = await h.NextRequestAsync();
        var type = (await read.Permits.FindByCodeAsync(Code("WASTE"), default))!;
        Assert.Null(type.DraftJson);
        Assert.Equal(PermitTypeStatus.Published, type.Status);

        // Шапка: вісім полів, первинний ключ за номером, темпоральна.
        var header = (await read.Registries.FindDefinitionByIdAsync(type.HeaderRegistryDefId, default))!;
        Assert.Equal(8, header.Fields.Count);
        Assert.Equal(["Permit_Number"], await PrimaryKeyAsync(read, header));

        // Таблиця Limits: композиція на шапку, ключ = PERMIT + позначені стовпці, нетемпоральна.
        var limits = await TableAsync(read, type, "Limits");
        Assert.False(limits.IsTemporal);
        var permit = Assert.Single(limits.Fields, f => f.RelationKind == RegistryRelationKind.Composition);
        Assert.Equal((PermitTypeStatus.Published, header.Id), (type.Status, permit.RefRegistryDefId!.Value));
        Assert.Equal(["PERMIT", "Place", "Period"], await PrimaryKeyAsync(read, limits));

        // Надбудова: точність, область списків; список «лише для типу» - окремий реєстр зі значеннями.
        var metas = await read.Permits.ListFieldMetasAsync(type.Id, default);
        var limit = limits.Fields.Single(f => f.Code == "Limit");
        var limitMeta = metas.Single(m => m.RegistryFieldDefId == limit.Id);
        Assert.Equal(((byte?)12, (byte?)3, "Volumes"), (limitMeta.Precision, limitMeta.Scale, limitMeta.GroupCode));

        var kind = limits.Fields.Single(f => f.Code == "Kind");
        var kindMeta = metas.Single(m => m.RegistryFieldDefId == kind.Id);
        Assert.Equal(PermitListScope.TypeOnly, kindMeta.ListScope);
        var list = await read.Registries.FindDefinitionByIdAsync(kindMeta.ListRegistryDefId!.Value, default);
        Assert.Equal($"PRM_{Code("WASTE")}_Limits_Kind", list!.Code);
        Assert.Equal(["Hazardous", "Safe"], (await read.Registries.ListEntriesAsync(list.Id, default)).OrderBy(e => e.Ordinal).Select(e => e.DisplayL10n.Get("en")));

        var place = limits.Fields.Single(f => f.Code == "Place");
        var placeMeta = metas.Single(m => m.RegistryFieldDefId == place.Id);
        Assert.Equal(("Places", PermitListScope.SamePermitTable), (placeMeta.ListSiblingTableCode, placeMeta.ListScope));
        Assert.Equal((await TableAsync(read, type, "Places")).Id, place.RefRegistryDefId);

        // Читання після публікації - з реєстрів - дає те саме, що чернетка (крім порядку правил/мов).
        var shown = await read.Get.HandleAsync(Code("WASTE"), default);
        var shownLimits = shown.Tables.Single(t => t.Code == "Limits");
        Assert.Equal(["Place", "Kind", "Site", "Period", "Limit"], shownLimits.Columns.Select(c => c.Code));
        Assert.Equal(["Hazardous", "Safe"], shownLimits.Columns.Single(c => c.Code == "Kind").Choices);
        Assert.Equal(shared.Code, shownLimits.Columns.Single(c => c.Code == "Site").ListRegistryCode);
        Assert.Equal(("NonNegative", "Block"), (shownLimits.Rules.Single().Code, shownLimits.Rules.Single().Severity));
        Assert.Equal(["Place", "Period"], shownLimits.Columns.Where(c => c.Key).Select(c => c.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_на_другій_таблиці_відкочує_шапку_першу_таблицю_реєстри_і_стан_типу()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(Code("ROLL"), Name("Rollback"), "Waste"), default);

        // Друга таблиця має одиницю, якої немає: ядро реєстрів відмовить ПІСЛЯ шапки й першої таблиці.
        var tables = new[]
        {
            Table("Aaa", [Col("K", key: true)]),
            Table("Zzz", [Col("K", key: true), Col("V", key: false) with { DataType = "Decimal", UnitId = 2_000_000_000 }]),
        };
        await using var put = await h.NextRequestAsync();
        var draft = await put.Replace.HandleAsync(Code("ROLL"), new ReplacePermitTypeRequest(created.RowVersion, Name("Rollback"), "Waste", tables, []), default);

        await using var publish = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => publish.Publish.HandleAsync(Code("ROLL"), new PublishPermitTypeRequest("спроба з поганою одиницею", draft.RowVersion), default));
        Assert.Equal("err.ECR-REG-0422.unknownUnit", error.Details!["messageKey"]);

        // Нічого не опубліковано: тип - чернетка зі збереженим складом, реєстри таблиць не існують, шапка порожня.
        await using var check = await h.NextRequestAsync();
        var type = (await check.Permits.FindByCodeAsync(Code("ROLL"), default))!;
        Assert.Equal((PermitTypeStatus.Draft, true, 1), (type.Status, type.HasDraft, type.Version));
        Assert.Empty(await check.Permits.ListTablesAsync(type.Id, default));
        Assert.Empty(await check.Permits.ListFieldMetasAsync(type.Id, default));
        Assert.False(await check.Db.RegistryDefs.AnyAsync(d => d.Code == $"PRM_{Code("ROLL")}_Aaa"));
        Assert.False(await check.Db.RegistryDefs.AnyAsync(d => d.Code == $"PRM_{Code("ROLL")}_Zzz"));

        var header = (await check.Registries.FindDefinitionByIdAsync(type.HeaderRegistryDefId, default))!;
        Assert.Empty(header.Fields);
        Assert.Equal(1, header.DefinitionVersion);

        // Після виправлення тип публікується з того ж токена: збій не пошкодив чернетку.
        var fixedTables = new[] { tables[0], tables[1] with { Columns = [Col("K", key: true), Col("V", key: false) with { DataType = "Decimal" }] } };
        var current = await check.Get.HandleAsync(Code("ROLL"), default);
        await using var retry = await h.NextRequestAsync();
        var repaired = await retry.Replace.HandleAsync(Code("ROLL"), new ReplacePermitTypeRequest(current.RowVersion, Name("Rollback"), "Waste", fixedTables, []), default);
        await using var again = await h.NextRequestAsync();
        Assert.Equal("Published", (await again.Publish.HandleAsync(Code("ROLL"), new PublishPermitTypeRequest("виправлена одиниця", repaired.RowVersion), default)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_без_ключа_без_таблиць_і_з_короткою_причиною_відхиляється_422()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(Code("BAD"), Name("Bad"), "Air"), default);

        var noTables = await Assert.ThrowsAsync<BusinessRuleException>(
            () => h.Publish.HandleAsync(Code("BAD"), new PublishPermitTypeRequest("достатньо довга причина", created.RowVersion), default));
        Assert.Equal("err.ECR-PRM-0422.noTables", noTables.Details!["messageKey"]);

        // Чернетка таблиці без ключа (збережена повз PUT) також не публікується.
        await using (var seed = await h.NextRequestAsync())
        {
            var type = (await seed.Permits.FindByCodeAsync(Code("BAD"), default))!;
            var content = new PermitTypeContent(Name("Bad"), "Air", [Table("T", [Col("A", key: false)])], []);
            type.SaveDraft(Ecr.Application.Permits.PermitTypeCodec.Serialize(content), seed.UserId, DateTime.UtcNow);
            await seed.Uow.SaveChangesAsync(default);
        }

        await using var noKey = await h.NextRequestAsync();
        var current = await noKey.Get.HandleAsync(Code("BAD"), default);
        var keyError = await Assert.ThrowsAsync<BusinessRuleException>(
            () => noKey.Publish.HandleAsync(Code("BAD"), new PublishPermitTypeRequest("достатньо довга причина", current.RowVersion), default));
        Assert.Equal("err.ECR-PRM-0422.tableWithoutKey", keyError.Details!["messageKey"]);

        var shortReason = await Assert.ThrowsAsync<BusinessRuleException>(
            () => noKey.Publish.HandleAsync(Code("BAD"), new PublishPermitTypeRequest("коротко", current.RowVersion), default));
        Assert.Equal("err.ECR-PRM-0422.reasonTooShort", shortReason.Details!["messageKey"]);

        await using var check = await h.NextRequestAsync();
        Assert.Equal(PermitTypeStatus.Draft, (await check.Permits.FindByCodeAsync(Code("BAD"), default))!.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застарілий_токен_публікації_дає_ECR_PRM_4093_і_нічого_не_публікується()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(Code("STALE"), Name("Stale"), "Air"), default);
        await using var put = await h.NextRequestAsync();
        await put.Replace.HandleAsync(Code("STALE"), new ReplacePermitTypeRequest(created.RowVersion, Name("Stale"), "Air", [Table("T", [Col("K", true)])], []), default);

        await using var publish = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => publish.Publish.HandleAsync(Code("STALE"), new PublishPermitTypeRequest("публікація зі старим токеном", created.RowVersion), default));

        Assert.Equal("err.ECR-PRM-4093.changed", error.Details!["messageKey"]);
        await using var check = await h.NextRequestAsync();
        Assert.Equal(PermitTypeStatus.Draft, (await check.Permits.FindByCodeAsync(Code("STALE"), default))!.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Редактор_без_Registry_Publish_і_читач_не_публікують_тип()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(Code("PERM"), Name("Perm"), "Air"), default);
        await using var put = await h.NextRequestAsync();
        var draft = await put.Replace.HandleAsync(Code("PERM"), new ReplacePermitTypeRequest(created.RowVersion, Name("Perm"), "Air", [Table("T", [Col("K", true)])], []), default);

        await using var editor = await PermitHarness.CreateAsync(sql, ["Registry.View", "Permit.EditType"]);
        await using var reader = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        // Адміністратор реєстрів (Land) має Registry.EditDefinition/Publish, але типами дозволів не керує.
        await using var registryAdmin = await PermitHarness.CreateAsync(sql, ["Registry.View", "Registry.EditDefinition", "Registry.Publish"]);
        var request = new PublishPermitTypeRequest("публікація без права", draft.RowVersion);

        await Assert.ThrowsAsync<AccessDeniedException>(() => editor.Publish.HandleAsync(Code("PERM"), request, default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => registryAdmin.Publish.HandleAsync(Code("PERM"), request, default));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => registryAdmin.Create.HandleAsync(new CreatePermitTypeRequest(Code("LAND"), Name("Land"), "Air"), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.Publish.HandleAsync(Code("PERM"), request, default));

        await using var check = await h.NextRequestAsync();
        Assert.Equal(PermitTypeStatus.Draft, (await check.Permits.FindByCodeAsync(Code("PERM"), default))!.Status);
        Assert.False(await check.Db.RegistryDefs.AnyAsync(d => d.Code == $"PRM_{Code("PERM")}_T"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нова_версія_додає_стовпець_зберігаючи_поля_ключі_і_значення_списків_а_видалення_стовпця_заборонене()
    {
        var shared = await EnsureSharedRegistryAsync($"SITES_{_tag}");
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var draft = await DraftAsync(h, shared.Code);
        await using var first = await h.NextRequestAsync();
        await first.Publish.HandleAsync(Code("WASTE"), new PublishPermitTypeRequest("перший випуск типу", draft.RowVersion), default);

        // Опублікований тип без відкритої чернетки не змінюється й не публікується повторно.
        await using var closed = await h.NextRequestAsync();
        var current = await closed.Get.HandleAsync(Code("WASTE"), default);
        var noDraft = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => closed.Replace.HandleAsync(Code("WASTE"), new ReplacePermitTypeRequest(current.RowVersion, current.NameL10n, "Waste", current.Tables, current.Rules), default));
        Assert.Equal("err.ECR-PRM-4093.noDraft", noDraft.Details!["messageKey"]);

        FieldIds before;
        await using (var snapshot = await h.NextRequestAsync())
        {
            before = await FieldIdsAsync(snapshot, Code("WASTE"));
        }

        await using var open = await h.NextRequestAsync();
        var opened = await open.OpenVersion.HandleAsync(Code("WASTE"), default);
        Assert.Equal(("Published", 1, (int?)2, true), (opened.Status, opened.Version, opened.DraftVersion, opened.HasUnpublishedChanges));

        await using var twice = await h.NextRequestAsync();
        var exists = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => twice.OpenVersion.HandleAsync(Code("WASTE"), default));
        Assert.Equal("err.ECR-PRM-4093.draftExists", exists.Details!["messageKey"]);

        // Прибрати опублікований стовпець не можна; додати - можна.
        var limits = opened.Tables.Single(t => t.Code == "Limits");
        await using var bad = await h.NextRequestAsync();
        var removed = await Assert.ThrowsAsync<BusinessRuleException>(
            () => bad.Replace.HandleAsync(
                Code("WASTE"),
                new ReplacePermitTypeRequest(opened.RowVersion, opened.NameL10n, "Waste", [opened.Tables[0], limits with { Columns = [.. limits.Columns.Where(c => c.Code != "Limit")] }], []),
                default));
        Assert.Equal("err.ECR-PRM-0422.columnRemoved", removed.Details!["messageKey"]);

        var withNotes = limits with { Columns = [.. limits.Columns, Col("Notes", false)] };
        await using var good = await h.NextRequestAsync();
        var second = await good.Replace.HandleAsync(
            Code("WASTE"), new ReplacePermitTypeRequest(opened.RowVersion, opened.NameL10n, "Waste", [opened.Tables[0], withNotes], []), default);

        await using var publish = await h.NextRequestAsync();
        var republished = await publish.Publish.HandleAsync(Code("WASTE"), new PublishPermitTypeRequest("додано стовпець приміток", second.RowVersion), default);
        Assert.Equal(("Published", 2, false), (republished.Status, republished.Version, republished.HasUnpublishedChanges));

        await using var read = await h.NextRequestAsync();
        var after = await FieldIdsAsync(read, Code("WASTE"));
        Assert.Equal(before.Limits, after.Limits.Where(p => before.Limits.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value));
        Assert.True(after.Limits.ContainsKey("Notes"));
        Assert.Equal(before.ListEntryIds, after.ListEntryIds);
        Assert.Equal(before.PrimaryKey, after.PrimaryKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_типу_не_чіпає_спільний_довідник_на_який_посилається_стовпець()
    {
        // Land: SUBSTANCE_APPLICABLE (Id 50 у бойовій базі) читається стовпцями, але публікація N+1 реєстрів його не змінює.
        var applicable = await EnsureSharedRegistryAsync("SUBSTANCE_APPLICABLE");
        var before = await FingerprintAsync(applicable.Id);

        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var draft = await DraftAsync(h, applicable.Code);
        await using var publish = await h.NextRequestAsync();
        await publish.Publish.HandleAsync(Code("WASTE"), new PublishPermitTypeRequest("посилання на Land-довідник", draft.RowVersion), default);

        Assert.Equal(before, await FingerprintAsync(applicable.Id));
    }

    private async Task<string> FingerprintAsync(int registryId)
    {
        await using var db = sql.CreateContext();
        var def = await db.RegistryDefs.AsNoTracking().Include(d => d.Fields).SingleAsync(d => d.Id == registryId);
        var entries = await db.RegistryEntries.AsNoTracking().CountAsync(e => e.RegistryDefId == registryId);
        return $"{def.Code}|v{def.DefinitionVersion}|r{def.DataRevision}|f{def.Fields.Count}|e{entries}|{def.DataChangedAt:O}";
    }

    private async Task<(int Id, string Code)> EnsureSharedRegistryAsync(string code)
    {
        await using var db = sql.CreateContext();
        var existing = await db.RegistryDefs.AsNoTracking().FirstOrDefaultAsync(d => d.Code == code);
        if (existing is not null)
        {
            return (existing.Id, existing.Code);
        }

        var def = new RegistryDef(EcrCode.Create(code), PermitHarness.Text(code), isTemporal: false);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync();
        var field = new RegistryFieldDef(def.Id, EcrCode.Create("Name"), PermitHarness.Text("Name"), CellDataType.String, 1);
        field.MarkKey(true);
        db.RegistryFieldDefs.Add(field);
        db.RegistryEntries.Add(new RegistryEntry(def.Id, EcrCode.Create("E1"), PermitHarness.Text("Entry 1"), 1, DateTime.UnixEpoch));
        await db.SaveChangesAsync();
        return (def.Id, code);
    }

    /// <summary>Тип із двома таблицями (сестринське посилання, список «лише для типу», спільний довідник, складений ключ, перевірка).</summary>
    private async Task<PermitTypeDto> DraftAsync(PermitHarness h, string sharedCode)
    {
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(Code("WASTE"), Name("Waste"), "Waste"), default);
        var places = Table("Places", [Col("Place", key: true)]);
        var limits = Table(
            "Limits",
            [
                Col("Place", key: true) with { DataType = "Lookup", ListScope = "SamePermitTable", SiblingTableCode = "Places" },
                Col("Kind", key: false) with { DataType = "Lookup", ListScope = "TypeOnly", Choices = ["Hazardous", "Safe"] },
                Col("Site", key: false) with { DataType = "Lookup", ListScope = "SharedRegistry", ListRegistryCode = sharedCode },
                Col("Period", key: true) with { DataType = "Date" },
                Col("Limit", key: false) with { DataType = "Decimal", Precision = 12, Scale = 3, Group = "Volumes" },
            ]) with
        {
            Rules = [new PermitTypeRuleDto("NonNegative", Name("Not negative"), "[Limit] >= 0", "Block")],
        };

        await using var put = await h.NextRequestAsync();
        return await put.Replace.HandleAsync(
            Code("WASTE"), new ReplacePermitTypeRequest(created.RowVersion, Name("Waste"), "Waste", [places, limits], []), default);
    }

    private static async Task<RegistryDef> TableAsync(PermitHarness h, PermitTypeDef type, string tableCode)
    {
        var table = (await h.Permits.ListTablesAsync(type.Id, default)).Single(t => t.Code == tableCode);
        return (await h.Registries.FindDefinitionByIdAsync(table.RegistryDefId, default))!;
    }

    private static async Task<List<string>> PrimaryKeyAsync(PermitHarness h, RegistryDef def)
    {
        var key = (await h.Keys.ListKeysForUpdateAsync(def.Id, default)).Single(k => k.IsPrimary && k.IsActive);
        var codeById = def.Fields.ToDictionary(f => f.Id, f => f.Code);
        return [.. key.Fields.OrderBy(f => f.Ordinal).Select(f => codeById[f.RegistryFieldDefId])];
    }

    private async Task<FieldIds> FieldIdsAsync(PermitHarness h, string typeCode)
    {
        var type = (await h.Permits.FindByCodeAsync(typeCode, default))!;
        var limits = await TableAsync(h, type, "Limits");
        var kindMeta = (await h.Permits.ListFieldMetasAsync(type.Id, default))
            .Single(m => m.RegistryFieldDefId == limits.Fields.Single(f => f.Code == "Kind").Id);
        var entries = await h.Registries.ListEntriesAsync(kindMeta.ListRegistryDefId!.Value, default);
        return new FieldIds(
            limits.Fields.ToDictionary(f => f.Code, f => f.Id),
            [.. entries.OrderBy(e => e.Id).Select(e => e.Id)],
            await PrimaryKeyAsync(h, limits));
    }

    private sealed record FieldIds(Dictionary<string, int> Limits, List<long> ListEntryIds, List<string> PrimaryKey);

    private static PermitTypeTableDto Table(string code, IReadOnlyList<PermitTypeColumnDto> columns)
        => new(code, Name(code), 1, columns, []);

    private static PermitTypeColumnDto Col(string code, bool key)
        => new(code, Name(code), "String", null, key, key, null, null, null, null, null, null, null, 1);

    private static Dictionary<string, string> Name(string en) => new() { ["en"] = en };
}
