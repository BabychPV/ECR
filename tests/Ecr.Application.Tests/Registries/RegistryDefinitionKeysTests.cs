// tests/Ecr.Application.Tests/Registries/RegistryDefinitionKeysTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Application.Registries.Keys;
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
/// Ключі й режим коду в описі довідника — правила, що відмовляють ДО бази (RT-11, <c>D-151</c>,
/// <c>D-153</c>, <c>D-157</c>, FEATURE-REGISTRY-TABLES §3.5, §4.1). Наповнення рядків ключів і
/// <c>409 existingDuplicates</c> — у <c>RegistryDefinitionKeysHttpTests</c> на справжній базі.
/// </summary>
public sealed class RegistryDefinitionKeysTests
{
    private const int RegistryId = 7;
    private static readonly DateTime Now = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryKeyStore _keys = Substitute.For<IRegistryKeyStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();

    private readonly RegistryDef _registry;
    private readonly List<RegistryKeyDef> _existing = [];

    public RegistryDefinitionKeysTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }
            .Permission("Registry.View")
            .Permission("Registry.EditDefinition")
            .Permission("Registry.Publish")
            .Build());

        // Довідник БЕЗ жодного IsKey-поля: бізнес-ключ йому дає лише первинний ключ (§3.5).
        _registry = new RegistryDef(EcrCode.Create("COMPONENT"), Text("COMPONENT"), isTemporal: false);
        SetId(_registry, RegistryId);
        AddField(71, "NAME", CellDataType.String, required: true);
        AddField(72, "FORMULA", CellDataType.String, required: false);
        AddField(73, "MW", CellDataType.Formula, required: false);

        _registries.FindDefinitionAsync("COMPONENT", Arg.Any<CancellationToken>()).Returns(_registry);
        _registries.ListRulesAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(Array.Empty<RegistryRuleDef>());
        _keys.ListKeysForUpdateAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(_ => _existing.ToList());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Первинний_ключ_замінює_ключове_поле()
    {
        // Без первинного ключа — та сама відмова noKeyField, що й доти.
        var bare = await Assert.ThrowsAsync<BusinessRuleException>(() => Save([]));
        Assert.Equal("err.ECR-REG-0422.noKeyField", bare.Details!["messageKey"]);

        await Save([Key("PK", ["NAME"], isPrimary: true)]);

        _keys.Received(1).AddKey(Arg.Is<RegistryKeyDef>(k => k.Code == "PK" && k.IsPrimary && k.CreatedByUserId == 9));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.15")]
    [InlineData("GHOST", false, "err.ECR-REG-0422.keyFieldUnknown")]
    [InlineData("MW", false, "err.ECR-REG-0422.keyFieldTypeNotAllowed")]
    [InlineData("FORMULA", true, "err.ECR-REG-0422.keyFieldNotRequired")]
    [InlineData("NAME,NAME", false, "err.ECR-REG-0422.keyFieldRepeated")]
    [InlineData("", false, "err.ECR-REG-0422.keyFieldCount")]
    public async Task Склад_нового_ключа_перевіряється_до_бази(string fields, bool isPrimary, string messageKey)
    {
        // ⛔ Конструктор `RegistryKeyDef` на тих самих умовах кидає ArgumentException — тобто 500.
        // Людина мусить отримати 422 з ключем ДО нього.
        string[] codes = fields.Length == 0 ? [] : fields.Split(',');

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save([Key("PK", ["NAME"], isPrimary: true), Key("ALT", codes, isPrimary)]));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal(messageKey, error.Details!["messageKey"]);
        _keys.DidNotReceive().AddKey(Arg.Any<RegistryKeyDef>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Два_активні_первинні_ключі_відхиляються()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save([Key("PK", ["NAME"], isPrimary: true), Key("PK2", ["NAME"], isPrimary: true)]));

        Assert.Equal("err.ECR-REG-0422.primaryKeyTwice", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Склад_наявного_ключа_не_змінюється_а_відсутній_вимикається()
    {
        var pk = Existing(81, "PK", isPrimary: true, "NAME");
        var alt = Existing(82, "BY_FORMULA", isPrimary: false, "FORMULA");

        // Інша первинність — відмова: зміна перебудувала б хеш кожного запису.
        var changed = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save([Key("PK", ["NAME"], isPrimary: false, id: 81)]));
        Assert.Equal("err.ECR-REG-0422.keyImmutable", changed.Details!["messageKey"]);

        // Той самий PK з новою назвою, BY_FORMULA відсутній — вимикається, а не зникає.
        await Save([Key("PK", ["NAME"], isPrimary: true, id: 81) with { NameL10n = Text("Primary") }]);

        Assert.Equal("Primary", pk.NameL10n.Get("en"));
        Assert.True(pk.IsActive);
        Assert.False(alt.IsActive);
        _keys.DidNotReceive().AddKey(Arg.Any<RegistryKeyDef>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Код_нового_ключа_не_повторює_наявний()
    {
        var pk = Existing(81, "PK", isPrimary: true, "NAME");
        pk.SetActive(false);

        // Вимкнений ключ теж тримає свій код (UQ_RegistryKeyDef) — повтор дав би 500 на індексі.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save([Key("pk", ["NAME"], isPrimary: true)]));

        Assert.Equal("err.ECR-REG-0422.keyCodeTaken", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Режим_коду_змінюється_лише_без_записів()
    {
        _registries.ListEntriesAsync(RegistryId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RegistryEntry>>([new RegistryEntry(RegistryId, EcrCode.Create("CO2"), Text("CO2"))]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save([Key("PK", ["NAME"], isPrimary: true)], RegistryCodeMode.Auto));

        Assert.Equal("err.ECR-REG-0422.codeModeImmutable", error.Details!["messageKey"]);
        Assert.Equal(RegistryCodeMode.Manual, _registry.CodeMode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Перевірка_ключа_на_невідомому_полі_422()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new CheckRegistryKeyHandler(_registries, _keys, _access, _user)
                .HandleAsync("COMPONENT", new RegistryKeyCheckRequest(["GHOST"]), default));

        Assert.Equal("err.ECR-REG-0422.keyFieldUnknown", error.Details!["messageKey"]);
        Assert.Equal("GHOST", error.Details["fieldCode"]);
    }

    private Task<int> Save(IReadOnlyList<RegistryKeySaveDto> keys, RegistryCodeMode? codeMode = null)
        => new SaveRegistryDefinitionHandler(
                _registries, _uow, _audit, _access, _user, _clock, _units, _keys, new RegistryKeyService(_keys, _uow))
            .HandleAsync(
                "COMPONENT",
                new SaveRegistryDefinitionDto(
                    [.. _registry.Fields.Select(f => new RegistryFieldSaveDto(
                        f.Id, f.Code, f.NameL10n, f.DataType.ToString(), f.Ordinal, f.IsRequired, f.IsKey,
                        f.RefRegistryDefId, f.UnitId))],
                    [],
                    "RT-11",
                    keys,
                    codeMode),
                default);

    private static RegistryKeySaveDto Key(string code, string[] fields, bool isPrimary, int? id = null)
        => new(id, code, Text(code), fields, isPrimary, IgnoreCase: true, IsActive: true);

    private RegistryKeyDef Existing(int id, string code, bool isPrimary, params string[] fields)
    {
        var key = new RegistryKeyDef(
            RegistryId, EcrCode.Create(code), Text(code),
            [.. fields.Select(f => _registry.Fields.Single(x => x.Code == f))],
            isPrimary, ignoreCase: true, 9, Now);
        SetId(key, id);
        _existing.Add(key);
        return key;
    }

    private void AddField(int id, string code, CellDataType type, bool required)
    {
        var field = new RegistryFieldDef(RegistryId, EcrCode.Create(code), Text(code), type, id);
        SetId(field, id);
        field.Update(Text(code), id, required);
        _registry.AddField(field);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
