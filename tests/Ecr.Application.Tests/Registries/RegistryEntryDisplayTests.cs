// tests/Ecr.Application.Tests/Registries/RegistryEntryDisplayTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// AU-14 / P3-6: у переліку записів довідника <c>display</c> дорівнював <c>code</c>, хоч запис
/// створювали з <c>Display=Alpha</c>.
/// </summary>
/// <remarks>
/// Висновок: handler працює за задумом — <c>display</c> береться з назви мовою користувача, далі
/// з <c>en</c>, далі з будь-якої наявної, і лише коли назви немає взагалі — з <c>code</c>. Причина
/// знахідки — форма тіла запиту: <see cref="LocalizedText"/> у JSON — це <c>{"values":{"en":"Alpha"}}</c>,
/// а пласке <c>{"en":"Alpha"}</c> System.Text.Json мовчки перетворює на ПОРОЖНІЙ текст. Обидві
/// половини зафіксовано, щоб зміна будь-якої з них була свідомою.
/// ✎ D14 (рішення координатора 19.10): запис без назви більше НЕ приймається мовчки — upsert без
/// жодної непорожньої мови (зокрема пласка мапа) дає <c>422 ECR-REG-4222 entryNameRequired</c>.
/// </remarks>
public sealed class RegistryEntryDisplayTests
{
    private const int RegistryDefId = 100;
    private const string Code = "DISPLAY_REG";
    private const long EntryId = 501;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryEntryCache _cache = Substitute.For<IRegistryEntryCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly RegistryDef _definition;

    public RegistryEntryDisplayTests()
    {
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _clock.UtcNow.Returns(new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc));
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 9 }
                .Permission(GetRegistryEntriesHandler.Permission)
                .Permission(UpsertRegistryEntryHandler.Permission)
                .Build());
        _cache.GetOrAddAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                callInfo.ArgAt<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(1)(default));

        var definition = new RegistryDef(EcrCode.Create(Code), Text("Registry"), false);
        SetId(definition, RegistryDefId);
        _definition = definition;
        _registries.FindDefinitionAsync(Code, Arg.Any<CancellationToken>()).Returns(definition);
        _registries.FindDefinitionByIdAsync(RegistryDefId, Arg.Any<CancellationToken>()).Returns(definition);
    }

    public static TheoryData<string> NamelessDisplays => new()
    {
        """{}""",
        """{"values":{}}""",
        """{"values":{"en":""}}""",
        """{"values":{"en":"   ","ru":"\t"}}""",
        // Пласка мовна мапа (Ж-8 п. 2) — для LocalizedText це порожній текст.
        """{"en":"Alpha"}""",
    };

    [Theory]
    [MemberData(nameof(NamelessDisplays))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "D14")]
    public async Task Новий_запис_без_назви_жодною_мовою_відхиляється_422_ECR_REG_4222(string displayJson)
    {
        var display = JsonSerializer.Deserialize<LocalizedText>(displayJson, WebJson)!;

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Upsert().HandleAsync(
            new RegistryEntryUpsertDto(null, RegistryDefId, "E1", display, null, new Dictionary<string, object?>()),
            CancellationToken.None));

        Assert.Equal("ECR-REG-4222", error.ErrorCode);
        Assert.Equal(Ecr.Domain.Errors.ErrorCodes.RegistryEntryNameRequired, error.ErrorCode);
        Assert.Equal("err.ECR-REG-4222.entryNameRequired", error.Details!["messageKey"]);
        Assert.Equal(Code, error.Details["registryCode"]);
        Assert.Equal("E1", error.Details["code"]);
        // Відмова ДО створення: код навіть не перевірявся на зайнятість, запис не додано.
        await _registries.DidNotReceive().FindEntryByCodeAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "D14")]
    public async Task Новий_запис_з_назвою_хоча_б_однією_мовою_створюється()
    {
        var id = await Upsert().HandleAsync(
            new RegistryEntryUpsertDto(null, RegistryDefId, "E1", Text("Alpha"), null, new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.Equal(0L, id);
        await _registries.Received(1).FindEntryByCodeAsync(RegistryDefId, "E1", Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "D14")]
    public async Task Правка_що_стирає_всі_мови_назви_відхиляється_і_назва_лишається()
    {
        var entry = Existing(new LocalizedText(new Dictionary<string, string> { ["en"] = "Alpha", ["ru"] = "Альфа" }));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Upsert().HandleAsync(
            new RegistryEntryUpsertDto(
                EntryId, RegistryDefId, "E1",
                new LocalizedText(new Dictionary<string, string> { ["en"] = "", ["ru"] = " " }),
                null, new Dictionary<string, object?>()),
            CancellationToken.None));

        Assert.Equal("ECR-REG-4222", error.ErrorCode);
        Assert.Equal("err.ECR-REG-4222.entryNameRequired", error.Details!["messageKey"]);
        Assert.Equal("Alpha", entry.DisplayL10n.Get("en"));
        Assert.Equal("Альфа", entry.DisplayL10n.Get("ru"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "D14")]
    public async Task Правка_без_назви_в_запиті_лишає_назву_і_не_відхиляється_навіть_у_давнього_запису_без_назви()
    {
        var named = Existing(Text("Alpha"));
        await Upsert().HandleAsync(
            new RegistryEntryUpsertDto(EntryId, RegistryDefId, "E1", new LocalizedText(), null, new Dictionary<string, object?>()),
            CancellationToken.None);
        Assert.Equal("Alpha", named.DisplayL10n.Get("en"));

        // Давні дані без назви: правка значень не блокується відмовою про назву, якої запит не чіпав.
        var legacy = Existing(new LocalizedText());
        await Upsert().HandleAsync(
            new RegistryEntryUpsertDto(EntryId, RegistryDefId, "E1", new LocalizedText(), null, new Dictionary<string, object?>()),
            CancellationToken.None);
        Assert.Empty(legacy.DisplayL10n.Values);
    }

    private UpsertRegistryEntryHandler Upsert()
        => new(_registries, _access, _user, new RegistryEntryWriter(_registries, _uow, _audit, _user, _clock));

    private RegistryEntry Existing(LocalizedText name)
    {
        var entry = new RegistryEntry(RegistryDefId, EcrCode.Create("E1"), name);
        SetId(entry, EntryId);
        _registries.FindEntryAsync(EntryId, Arg.Any<CancellationToken>()).Returns(entry);
        _registries.ListValuesAsync(EntryId, Arg.Any<CancellationToken>()).Returns(new List<RegistryValue>());
        return entry;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "AU-14")]
    public async Task Display_у_переліку_береться_з_назви_а_не_з_коду()
    {
        var display = await ListDisplayAsync("E1", Text("Alpha"));

        Assert.Equal("Alpha", display);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "AU-14")]
    public async Task Display_без_перекладу_мовою_користувача_береться_з_en_а_без_назви_взагалі_з_коду()
    {
        _user.Language.Returns("kz");
        Assert.Equal("Alpha", await ListDisplayAsync("E1", Text("Alpha")));

        Assert.Equal("E1", await ListDisplayAsync("E1", new LocalizedText()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "AU-14")]
    public void Тіло_запиту_LocalizedText_має_форму_values_а_пласка_мовна_мапа_мовчки_порожня()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var wrapped = JsonSerializer.Deserialize<LocalizedText>("""{"values":{"en":"Alpha"}}""", options)!;
        var flat = JsonSerializer.Deserialize<LocalizedText>("""{"en":"Alpha"}""", options)!;

        Assert.Equal("Alpha", wrapped.Get("en"));
        // Саме це бачив тестувальник: назви немає. ✎ D14: такий upsert тепер 422 ECR-REG-4222 (тест вище).
        Assert.Null(flat.Get("en"));
        Assert.Empty(flat.Values);

        var upsert = JsonSerializer.Deserialize<RegistryEntryUpsertDto>(
            """{"registryDefId":100,"code":"E1","display":{"values":{"en":"Alpha"}},"values":{}}""", options)!;
        Assert.Equal("Alpha", upsert.Display.Get("en"));
    }

    private async Task<string> ListDisplayAsync(string entryCode, LocalizedText name)
    {
        var entry = new RegistryEntry(RegistryDefId, EcrCode.Create(entryCode), name);
        SetId(entry, 1L);
        _registries.ListEntriesAsync(RegistryDefId, Arg.Any<CancellationToken>())
            .Returns(new List<RegistryEntry> { entry });

        var result = await new GetRegistryEntriesHandler(_registries, new RegistryResolver(), _cache, _access, _user)
            .HandleAsync(Code, default, parentEntryId: null, default);

        return Assert.Single(result).Display;
    }

    private static void SetId<T>(Entity<T> entity, T id) where T : IEquatable<T>
        => typeof(Entity<T>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Text(string en)
        => new(new Dictionary<string, string> { ["en"] = en });
}
