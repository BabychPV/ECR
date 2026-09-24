// tests/Ecr.Application.Tests/Registries/RegistryEntryEditTests.cs
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
/// Правка запису довідника не затирає незмінене (X-03, R-04, четвертий раунд UX).
/// </summary>
/// <remarks>
/// ⛔ Форма правки підставляла назву лише під <c>en</c> і лишала поля порожніми,
/// а <c>UpsertRegistryEntryHandler</c> писав назву рівно тією, що приїхала:
/// кожне збереження стирало переклади. Тут — обидві половини фіксу: читання
/// запису цілком (<see cref="GetRegistryEntryHandler"/>) і злиття назви на
/// записі.
/// </remarks>
public sealed class RegistryEntryEditTests
{
    private const int RegistryDefId = 7;
    private const long EntryId = 101;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    public RegistryEntryEditTests()
    {
        _clock.UtcNow.Returns(new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(Profile("Registry.EditData", "Registry.View"));
    }

    private static AccessProfile Profile(params string[] permissions) => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(permissions, StringComparer.Ordinal),
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(),
        RoleIds = new HashSet<int>(),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Правка_лише_англійської_назви_лишає_інші_переклади()
    {
        var definition = Definition();
        var entry = Entry(EntryId, definition.Id, "CO2", Names(("en", "Carbon dioxide"), ("ru", "Диоксид углерода"), ("kz", "Көмірқышқыл газы")));
        Arrange(definition, entry, []);

        await Upsert().HandleAsync(
            new RegistryEntryUpsertDto(
                EntryId, definition.Id, "CO2", Names(("en", "Carbon dioxide (CO2)")), null,
                new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.Equal("Carbon dioxide (CO2)", entry.DisplayL10n.Values["en"]);
        Assert.Equal("Диоксид углерода", entry.DisplayL10n.Values["ru"]);
        Assert.Equal("Көмірқышқыл газы", entry.DisplayL10n.Values["kz"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Порожній_текст_мови_свідомо_прибирає_переклад()
    {
        var definition = Definition();
        var entry = Entry(EntryId, definition.Id, "CO2", Names(("en", "Carbon dioxide"), ("ru", "Диоксид углерода")));
        Arrange(definition, entry, []);

        await Upsert().HandleAsync(
            new RegistryEntryUpsertDto(
                EntryId, definition.Id, "CO2", Names(("en", "Carbon dioxide"), ("ru", "")), null,
                new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.False(entry.DisplayL10n.Values.ContainsKey("ru"));
        Assert.Equal("Carbon dioxide", entry.DisplayL10n.Values["en"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Читання_запису_віддає_всі_мови_назви_і_значення_полів_текстом()
    {
        var definition = Definition();
        var limit = Field(definition.Id, "LIMIT", CellDataType.Decimal, 1, 501);
        var since = Field(definition.Id, "SINCE", CellDataType.Date, 2, 502);
        var active = Field(definition.Id, "ACTIVE", CellDataType.Bool, 3, 503);
        var parent = Field(definition.Id, "GROUP", CellDataType.Lookup, 4, 504);
        var note = Field(definition.Id, "NOTE", CellDataType.String, 5, 505);
        definition.AddField(limit);
        definition.AddField(since);
        definition.AddField(active);
        definition.AddField(parent);
        definition.AddField(note);

        var entry = Entry(EntryId, definition.Id, "CO2", Names(("en", "Carbon dioxide"), ("ru", "Диоксид углерода")));

        var limitValue = new RegistryValue(EntryId, limit.Id);
        limitValue.Set(CellDataType.Decimal, 12.5000000000m, null);
        var sinceValue = new RegistryValue(EntryId, since.Id);
        sinceValue.Set(CellDataType.Date, new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc), null);
        var activeValue = new RegistryValue(EntryId, active.Id);
        activeValue.Set(CellDataType.Bool, true, null);
        var parentValue = new RegistryValue(EntryId, parent.Id);
        parentValue.Set(CellDataType.Lookup, 42L, null);

        // ⚠ Значення поля, якого в описі вже немає, — не віддається.
        var orphan = new RegistryValue(EntryId, 999);
        orphan.Set(CellDataType.String, "stale", null);

        Arrange(definition, entry, [limitValue, sinceValue, activeValue, parentValue, orphan]);

        var read = await Get().HandleAsync("PERMITS", EntryId, CancellationToken.None);

        Assert.Equal("Carbon dioxide", read.DisplayL10n.Values["en"]);
        Assert.Equal("Диоксид углерода", read.DisplayL10n.Values["ru"]);
        Assert.Equal("12.5", read.Values["LIMIT"]);
        Assert.Equal("2026-01-31", read.Values["SINCE"]);
        Assert.Equal("true", read.Values["ACTIVE"]);
        Assert.Equal("42", read.Values["GROUP"]);
        Assert.False(read.Values.ContainsKey("NOTE"));
        Assert.Equal(4, read.Values.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Запис_чужого_довідника_не_читається()
    {
        var definition = Definition();
        var foreign = Entry(EntryId, RegistryDefId + 1, "OTHER", Names(("en", "Other")));
        Arrange(definition, foreign, []);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Get().HandleAsync("PERMITS", EntryId, CancellationToken.None));

        Assert.Equal("err.ECR-REG-0404.registryEntry", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_права_перегляду_запис_не_читається()
    {
        var definition = Definition();
        Arrange(definition, Entry(EntryId, definition.Id, "CO2", Names(("en", "CO2"))), []);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Get().HandleAsync("PERMITS", EntryId, CancellationToken.None));
    }

    private UpsertRegistryEntryHandler Upsert() => new(_registries, _uow, _audit, _access, _user, _clock);

    private GetRegistryEntryHandler Get() => new(_registries, _access, _user);

    private void Arrange(RegistryDef definition, RegistryEntry entry, IReadOnlyList<RegistryValue> values)
    {
        _registries.FindDefinitionByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        _registries.FindDefinitionAsync(definition.Code, Arg.Any<CancellationToken>()).Returns(definition);
        _registries.FindEntryAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(entry);
        _registries.ListValuesAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(values);
    }

    private static RegistryDef Definition()
    {
        var definition = new RegistryDef(EcrCode.Create("PERMITS"), Names(("en", "Permits")), isTemporal: false);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, RegistryDefId);
        return definition;
    }

    private static RegistryFieldDef Field(int registryDefId, string code, CellDataType dataType, int ordinal, int id)
    {
        var field = new RegistryFieldDef(registryDefId, EcrCode.Create(code), Names(("en", code)), dataType, ordinal);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(field, id);
        return field;
    }

    private static RegistryEntry Entry(long id, int registryDefId, string code, LocalizedText display)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), display);
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Names(params (string Language, string Text)[] names)
        => new(names.ToDictionary(n => n.Language, n => n.Text));
}
