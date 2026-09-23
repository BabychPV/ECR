// tests/Ecr.Application.Tests/Registries/RegistryValueAuditTests.cs
using System.Text.Json;
using Ecr.Application.Common;
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
/// Зміна значення поля запису довідника лишає слід в <c>aud.SecurityEvent</c>.
/// </summary>
/// <remarks>
/// ⛔ Прогалина: зміна ОПИСУ довідника пише <c>aud.StructureChange</c>
/// (<c>SaveRegistryDefinitionHandler</c>), зміна комірки документа — власний
/// <c>aud.CellChange</c>, а зміна ЗНАЧЕННЯ поля запису довідника
/// (<c>UpsertRegistryEntryHandler.ApplyValuesAsync</c> → <c>RegistryValue.Set</c>)
/// не лишала жодного сліду — старе значення перезаписувалося мовчки.
/// <para>
/// ⚠ Без міграції: <c>aud.SecurityEvent</c> уже приймає довільні події через
/// <c>EventType</c>/<c>DetailsJson</c> (<c>DocumentKeyChanged</c>,
/// <c>UserLocked</c> тощо) — той самий журнал, нова подія
/// <c>RegistryValueChanged</c>.
/// </para>
/// </remarks>
public sealed class RegistryValueAuditTests
{
    private const int RegistryDefId = 7;
    private const long EntryId = 101;

    private static readonly DateTime Now = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    public RegistryValueAuditTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _user.CorrelationId.Returns("corr-1");

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Registry.EditData" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(),
        RoleIds = new HashSet<int>(),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Зміна_значення_поля_пише_подію_зі_старим_і_новим_значенням()
    {
        var definition = Definition();
        var limitField = Field(definition.Id, "LIMIT", CellDataType.String, ordinal: 1, id: 501);
        definition.AddField(limitField);

        var entry = Entry(EntryId, definition.Id, "PERMIT_A");
        var existingValue = new RegistryValue(EntryId, limitField.Id);
        existingValue.Set(CellDataType.String, "100", null);

        Arrange(definition, entry, [existingValue]);

        SecurityEventRecord? captured = null;
        _audit.WriteSecurityEventAsync(Arg.Do<SecurityEventRecord>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new UpsertRegistryEntryHandler(_registries, _uow, _audit, _access, _user, _clock);

        await handler.HandleAsync(
            new RegistryEntryUpsertDto(
                Id: EntryId, RegistryDefId: definition.Id, Code: "PERMIT_A",
                Display: Text("Дозвіл A"),
                ParentEntryId: null,
                Values: new Dictionary<string, object?> { ["LIMIT"] = "150" }),
            CancellationToken.None).ConfigureAwait(true);

        await _audit.Received(1).WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>())
            .ConfigureAwait(true);

        Assert.NotNull(captured);
        Assert.Equal(UpsertRegistryEntryHandler.ValueChangedEventType, captured!.EventType);
        Assert.Equal(9, captured.ChangedByUserId);
        Assert.Equal("corr-1", captured.CorrelationId);

        var details = JsonDocument.Parse(captured.DetailsJson!).RootElement;
        Assert.Equal(definition.Id, details.GetProperty("registryDefId").GetInt32());
        Assert.Equal(EntryId, details.GetProperty("entryId").GetInt64());

        var change = Assert.Single(details.GetProperty("changes").EnumerateArray());
        Assert.Equal("LIMIT", change.GetProperty("field").GetString());
        Assert.Equal("100", change.GetProperty("oldValue").GetString());
        Assert.Equal("150", change.GetProperty("newValue").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Повторне_збереження_тим_самим_значенням_не_пише_подію()
    {
        var definition = Definition();
        var limitField = Field(definition.Id, "LIMIT", CellDataType.String, ordinal: 1, id: 501);
        definition.AddField(limitField);

        var entry = Entry(EntryId, definition.Id, "PERMIT_A");
        var existingValue = new RegistryValue(EntryId, limitField.Id);
        existingValue.Set(CellDataType.String, "100", null);

        Arrange(definition, entry, [existingValue]);

        var handler = new UpsertRegistryEntryHandler(_registries, _uow, _audit, _access, _user, _clock);

        await handler.HandleAsync(
            new RegistryEntryUpsertDto(
                Id: EntryId, RegistryDefId: definition.Id, Code: "PERMIT_A",
                Display: Text("Дозвіл A"),
                ParentEntryId: null,

                // ⚠ Те саме значення, що вже лежить у existingValue — жодної
                // фактичної зміни. Ревізія довідника все одно рухається
                // (окрема поведінка, не предмет цього тесту), а от журнал
                // аудиту — ні: подія без реальної зміни була б шумом, який
                // ніхто не зможе відрізнити від справжньої правки.
                Values: new Dictionary<string, object?> { ["LIMIT"] = "100" }),
            CancellationToken.None).ConfigureAwait(true);

        await _audit.DidNotReceive()
            .WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>())
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Зміна_кількох_полів_одним_запитом_дає_одну_подію_з_переліком_змін()
    {
        var definition = Definition();
        var limitField = Field(definition.Id, "LIMIT", CellDataType.String, ordinal: 1, id: 501);
        var activeField = Field(definition.Id, "ACTIVE", CellDataType.Bool, ordinal: 2, id: 502);
        definition.AddField(limitField);
        definition.AddField(activeField);

        var entry = Entry(EntryId, definition.Id, "PERMIT_A");

        var limitValue = new RegistryValue(EntryId, limitField.Id);
        limitValue.Set(CellDataType.String, "100", null);

        var activeValue = new RegistryValue(EntryId, activeField.Id);
        activeValue.Set(CellDataType.Bool, true, null);

        Arrange(definition, entry, [limitValue, activeValue]);

        SecurityEventRecord? captured = null;
        _audit.WriteSecurityEventAsync(Arg.Do<SecurityEventRecord>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new UpsertRegistryEntryHandler(_registries, _uow, _audit, _access, _user, _clock);

        await handler.HandleAsync(
            new RegistryEntryUpsertDto(
                Id: EntryId, RegistryDefId: definition.Id, Code: "PERMIT_A",
                Display: Text("Дозвіл A"),
                ParentEntryId: null,
                Values: new Dictionary<string, object?> { ["LIMIT"] = "150", ["ACTIVE"] = false }),
            CancellationToken.None).ConfigureAwait(true);

        // ⛔ Головне твердження цього тесту: ОДНА подія на весь виклик
        // upsert, а не одна на кожне змінене поле.
        await _audit.Received(1).WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>())
            .ConfigureAwait(true);

        var details = JsonDocument.Parse(captured!.DetailsJson!).RootElement;
        var changes = details.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(2, changes.Count);

        var byField = changes.ToDictionary(c => c.GetProperty("field").GetString()!);
        Assert.Equal("100", byField["LIMIT"].GetProperty("oldValue").GetString());
        Assert.Equal("150", byField["LIMIT"].GetProperty("newValue").GetString());
        Assert.True(byField["ACTIVE"].GetProperty("oldValue").GetBoolean());
        Assert.False(byField["ACTIVE"].GetProperty("newValue").GetBoolean());
    }

    private void Arrange(RegistryDef definition, RegistryEntry entry, IReadOnlyList<RegistryValue> values)
    {
        _registries.FindDefinitionByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
        _registries.FindEntryAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(entry);
        _registries.ListValuesAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(values);
    }

    /// <summary>Опис довідника з призначеним <c>Id</c> — його дає база.</summary>
    private static RegistryDef Definition()
    {
        var definition = new RegistryDef(EcrCode.Create("PERMITS"), Text("Permits"), isTemporal: false);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, RegistryDefId);
        return definition;
    }

    /// <summary>Поле довідника з призначеним <c>Id</c> — його дає база.</summary>
    private static RegistryFieldDef Field(int registryDefId, string code, CellDataType dataType, int ordinal, int id)
    {
        var field = new RegistryFieldDef(registryDefId, EcrCode.Create(code), Text(code), dataType, ordinal);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(field, id);
        return field;
    }

    /// <summary>Запис довідника з призначеним <c>Id</c> — його дає база.</summary>
    private static RegistryEntry Entry(long id, int registryDefId, string code)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Text(code));
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
