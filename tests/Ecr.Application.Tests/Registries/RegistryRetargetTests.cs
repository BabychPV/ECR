// tests/Ecr.Application.Tests/Registries/RegistryRetargetTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
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
/// Зміна й зняття зв'язку наявного поля довідника (<c>ФВ-8.12</c>, порція 1).
/// </summary>
/// <remarks>
/// Мутаційні докази: без перевірки значень у <c>GuardLookupRetargetAsync</c> червоний
/// <see cref="Ціль_не_змінюється_поки_на_неї_посилаються_значення"/>; без <c>PointTo</c> —
/// <see cref="Ціль_змінюється_коли_значень_немає"/>; без перевірки існування цілі —
/// <see cref="Невідома_ціль_відхиляється"/>; без <c>IsDenied</c> у <c>RequireUsableTargetAsync</c> —
/// <see cref="Ретаргет_на_заборонений_довідник_дає_404_як_на_неіснуючий"/> і
/// <see cref="Нове_поле_Lookup_із_забороненою_ціллю_дає_404"/>; без вимоги цілі —
/// <see cref="Ціль_не_знімається_навіть_коли_значень_немає"/>.
/// </remarks>
public sealed class RegistryRetargetTests
{
    private const int OwnerId = 41;
    private const int OldTargetId = 42;
    private const int NewTargetId = 43;
    private const int LinkFieldId = 411;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IRegistryKeyStore _keys = Substitute.For<IRegistryKeyStore>();
    private readonly RegistryDef _owner;
    private readonly RegistryFieldDef _link;

    public RegistryRetargetTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }
            .Permission("Registry.View").Permission("Registry.EditDefinition").Permission("Registry.Publish")
            .Permission("Template.View").Permission("Calculation.View").Build());

        _owner = new RegistryDef(EcrCode.Create("OWNER"), Text("OWNER"), false);
        SetId(_owner, OwnerId);
        var name = new RegistryFieldDef(OwnerId, EcrCode.Create("NAME"), Text("NAME"), CellDataType.String, 1);
        SetId(name, 410);
        name.MarkKey(true);
        _owner.AddField(name);
        _link = new RegistryFieldDef(OwnerId, EcrCode.Create("LINK"), Text("LINK"), CellDataType.Lookup, 2);
        SetId(_link, LinkFieldId);
        _link.PointTo(OldTargetId);
        _owner.AddField(_link);

        _registries.FindDefinitionAsync("OWNER", Arg.Any<CancellationToken>()).Returns(_owner);
        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>()).Returns([_owner]);
        _registries.FindDefinitionByIdAsync(NewTargetId, Arg.Any<CancellationToken>())
            .Returns(new RegistryDef(EcrCode.Create("NEW"), Text("NEW"), false));
        _registries.FindDefinitionByIdAsync(999, Arg.Any<CancellationToken>()).Returns((RegistryDef?)null);
        _registries.FindFieldChainConsumersAsync(
                Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new UsageResponse(0, []));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    [InlineData(NewTargetId)]
    public async Task Ціль_не_змінюється_поки_через_поле_читають_атрибути_правила_чи_методології(int? target)
    {
        // ⛔ Правило `LINK.Capacity > 0`, формула методології — через ребра cfg.RegistryUse. Значень
        // у полі немає, тож лише ця перевірка тримає ціль.
        UsageItemDto[] consumers =
        [
            new(UsageKinds.RegistryField, "5", "OWNER.CAPACITY_POSITIVE", "/admin/registries/OWNER/definition"),
            new(UsageKinds.MethodologyFormula, "9:E_NOX", "M1 v2.E_NOX", "/admin/methodologies/3/versions"),
        ];
        _registries.FindFieldChainConsumersAsync(
                OwnerId, Arg.Is<IReadOnlyCollection<string>>(codes => codes.Contains("LINK")), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new UsageResponse(2, consumers));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save(target));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REG-0422.lookupRetargetUsedByRules", error.Details!["messageKey"]);
        Assert.Equal("LINK", error.Details["fieldCode"]);
        Assert.Equal("2", error.Details["total"]);
        Assert.Equal("OWNER.CAPACITY_POSITIVE, M1 v2.E_NOX", error.Details["usedBy"]);
        Assert.Equal(consumers, (IReadOnlyList<UsageItemDto>)error.Details["references"]!);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Збереження_без_зміни_цілі_споживачів_не_питає()
    {
        await Save(OldTargetId);

        await _registries.DidNotReceive().FindFieldChainConsumersAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Ціль_змінюється_коли_значень_немає()
    {
        await Save(NewTargetId);

        Assert.Equal(NewTargetId, _link.RefRegistryDefId);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Ціль_не_знімається_навіть_коли_значень_немає()
    {
        // ⛔ ent6 R1: поле Lookup без цілі приймало запис БУДЬ-ЯКОГО довідника (і забороненого).
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save(null));

        Assert.Equal("err.ECR-REG-0422.lookupTargetUnknown", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Ретаргет_на_заборонений_довідник_дає_404_як_на_неіснуючий()
    {
        DenyRegistry(NewTargetId);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => Save(NewTargetId));

        // Та сама відповідь, що й на неіснуючий довідник: заборона не розкриває його існування.
        Assert.Equal("ECR-REG-0404", error.ErrorCode);
        Assert.Equal("err.ECR-REG-0404.registryId", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Нове_поле_Lookup_із_забороненою_ціллю_дає_404()
    {
        DenyRegistry(NewTargetId);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Save(OldTargetId, extra: NewLookup(NewTargetId)));

        Assert.Equal("err.ECR-REG-0404.registryId", error.Details!["messageKey"]);
        Assert.DoesNotContain(_owner.Fields, f => f.Code == "EXTRA");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Нове_поле_Lookup_без_цілі_або_з_невідомою_ціллю_відхиляється()
    {
        foreach (var target in new int?[] { null, 999 })
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Save(OldTargetId, extra: NewLookup(target)));

            Assert.Equal("err.ECR-REG-0422.lookupTargetUnknown", error.Details!["messageKey"]);
        }

        Assert.DoesNotContain(_owner.Fields, f => f.Code == "EXTRA");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Нове_поле_Lookup_із_дозволеною_ціллю_додається()
    {
        await Save(OldTargetId, extra: NewLookup(NewTargetId));

        Assert.Contains(_owner.Fields, f => f.Code == "EXTRA" && f.RefRegistryDefId == NewTargetId);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    [InlineData(NewTargetId)]
    public async Task Ціль_не_змінюється_поки_на_неї_посилаються_значення(int? target)
    {
        // ⛔ Гейт — прямий EXISTS (`FindFieldHoldingReferenceAsync`), а не вибірка записів зі стелею.
        _registries.FindFieldHoldingReferenceAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(LinkFieldId)), Arg.Any<CancellationToken>())
            .Returns(LinkFieldId);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save(target));

        Assert.Equal("err.ECR-REG-0422.lookupRetargetInUse", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Невідома_ціль_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save(999));

        Assert.Equal("err.ECR-REG-0422.lookupTargetUnknown", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Композиція_не_перенацілюється()
    {
        _link.ComposeInto(ParentDeletePolicy.Restrict);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save(NewTargetId));

        Assert.Equal("err.ECR-REG-0422.relationKindImmutable", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Відмова_за_споживачами_не_розкриває_невидимих_правил_формул_і_методологій()
    {
        // ⛔ Мутація: прибрати VisibleConsumersAsync (віддавати consumers.Items) — тест червоніє.
        const int secretId = 77;
        var secret = new RegistryDef(EcrCode.Create("SECRET"), Text("SECRET"), false);
        SetId(secret, secretId);
        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>()).Returns([_owner, secret]);
        var own = new UsageItemDto(UsageKinds.RegistryField, "5", "OWNER.CAPACITY_POSITIVE", "/admin/registries/OWNER/definition");
        UsageItemDto[] consumers =
        [
            own,
            new(UsageKinds.RegistryField, "6", "SECRET.HIDDEN_RULE", "/admin/registries/SECRET/definition"),
            new(UsageKinds.TemplateFormula, "8", "HIDDEN_TABLE#8", null),
            new(UsageKinds.MethodologyFormula, "9:E_NOX", "HIDDEN_METH v2.E_NOX", "/admin/methodologies/3/versions"),
        ];
        _registries.FindFieldChainConsumersAsync(
                OwnerId, Arg.Is<IReadOnlyCollection<string>>(codes => codes.Contains("LINK")), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new UsageResponse(5, consumers));
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }
            .Permission("Registry.View").Permission("Registry.EditDefinition").Permission("Registry.Publish")
            .Deny(ResourceKind.Registry, secretId).Build());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save(NewTargetId));

        Assert.Equal("5", error.Details!["total"]);
        Assert.Equal("3", error.Details["hiddenCount"]);
        Assert.Equal("OWNER.CAPACITY_POSITIVE", error.Details["usedBy"]);
        Assert.Equal([own], (IReadOnlyList<UsageItemDto>)error.Details["references"]!);
        Assert.DoesNotContain("SECRET", error.Message);
        Assert.DoesNotContain("HIDDEN", error.Message);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Ключове_поле_не_перенацілюється()
    {
        // ⛔ Бізнес-ключ незмінний: поле-ключ із посиланням перенацілити не можна навіть без значень.
        var keyLink = new RegistryFieldDef(OwnerId, EcrCode.Create("KEYLINK"), Text("KEYLINK"), CellDataType.Lookup, 3);
        SetId(keyLink, 412);
        keyLink.PointTo(OldTargetId);
        keyLink.MarkKey(true);
        _owner.AddField(keyLink);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save(NewTargetId, linkFieldId: keyLink.Id));

        Assert.Equal("err.ECR-REG-0422.relationKindImmutable", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, keyLink.RefRegistryDefId);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Гейт_ретаргета_читає_значення_всередині_транзакції_і_не_читає_записи()
    {
        // ⛔ Гейт до транзакції пропускав паралельний запис зі старою ціллю; а вибірка записів
        // (`ListEntriesAsync`, стеля 50 000) губила хвіст великого довідника.
        var inTransaction = false;
        var seenInsideTransaction = false;
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                inTransaction = true;
                try
                {
                    await call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1));
                }
                finally
                {
                    inTransaction = false;
                }
            });
        _registries.FindFieldHoldingReferenceAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                seenInsideTransaction = inTransaction;
                return Task.FromResult<int?>(null);
            });

        await Save(NewTargetId);

        Assert.True(seenInsideTransaction);
        await _registries.DidNotReceive().ListEntriesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _registries.DidNotReceive()
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Ретаргет_пишеться_в_журнал_структурних_змін_зі_знімком_до_і_після()
    {
        var audit = Substitute.For<IAuditWriter>();
        StructureChangeRecord? written = null;
        await audit.WriteStructureChangeAsync(
            Arg.Do<StructureChangeRecord>(r => written = r), Arg.Any<CancellationToken>());

        await Save(NewTargetId, audit: audit);

        Assert.NotNull(written);
        Assert.Equal("cfg.RegistryDef", written!.EntityType);
        Assert.Equal(OwnerId, written.EntityId);
        Assert.Equal("SaveDefinition", written.Operation);
        Assert.Equal("ФВ-8.12", written.ChangeReason);
        Assert.Equal(OldTargetId, LinkTargetIn(written.OldJson));
        Assert.Equal(NewTargetId, LinkTargetIn(written.NewJson));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    [InlineData("\"7\"")]
    [InlineData("W/\"7\"")]
    [InlineData("abc")]
    public async Task Чужа_версія_в_If_Match_дає_409_і_нічого_не_пише(string ifMatch)
    {
        // Опис довідника — версія 1 (див. конструктор `RegistryDef`).
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Save(NewTargetId, ifMatch: ifMatch));

        Assert.Equal("ECR-REG-0409", error.ErrorCode);
        Assert.Equal("err.ECR-REG-0409.definitionChanged", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"1\"")]
    [InlineData("1")]
    public async Task Поточна_версія_в_If_Match_або_її_відсутність_пропускає_збереження(string? ifMatch)
    {
        var version = await Save(NewTargetId, ifMatch: ifMatch);

        Assert.Equal(2, version);
        Assert.Equal(NewTargetId, _link.RefRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Версія_що_змінилась_між_читанням_і_блокуванням_рядка_дає_409()
    {
        // ⛔ Справжня гонка: `If-Match` збігся з прочитаним, але до блокування рядка опис
        // встиг закомітити інший запит.
        _registries.LockDefinitionIsStaleAsync(OwnerId, 1, Arg.Any<CancellationToken>()).Returns(true);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Save(NewTargetId, ifMatch: "\"1\""));

        Assert.Equal("err.ECR-REG-0409.definitionChanged", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static int? LinkTargetIn(string? json)
        => System.Text.Json.JsonDocument.Parse(json!).RootElement.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("Code").GetString() == "LINK").GetProperty("RefRegistryDefId") is
            { ValueKind: System.Text.Json.JsonValueKind.Number } n ? n.GetInt32() : null;

    private void DenyRegistry(int registryDefId)

        => _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }
            .Permission("Registry.View").Permission("Registry.EditDefinition").Permission("Registry.Publish")
            .Deny(ResourceKind.Registry, registryDefId).Build());

    private static RegistryFieldSaveDto NewLookup(int? target)
        => new(null, "EXTRA", Text("EXTRA"), "Lookup", 9, false, false, target, null);

    private Task<int> Save(
        int? target, RegistryFieldSaveDto? extra = null, int linkFieldId = LinkFieldId,
        string? ifMatch = null, IAuditWriter? audit = null)
    {
        var fields = _owner.Fields.OrderBy(f => f.Ordinal).Select(f => new RegistryFieldSaveDto(
            f.Id, f.Code, f.NameL10n, f.DataType.ToString(), f.Ordinal, f.IsRequired, f.IsKey,
            f.Id == linkFieldId ? target : f.RefRegistryDefId, f.UnitId)).ToList();
        if (extra is not null)
        {
            fields.Add(extra);
        }

        var handler = new SaveRegistryDefinitionHandler(
            _registries, _uow, audit ?? Substitute.For<IAuditWriter>(), _access, _user, Substitute.For<IClock>(),
            Substitute.For<IUnitCatalog>(), _keys,
            new Ecr.Application.Registries.Keys.RegistryKeyService(_keys, _uow));
        return handler.HandleAsync("OWNER", new SaveRegistryDefinitionDto(fields, [], "ФВ-8.12"), default, ifMatch);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}