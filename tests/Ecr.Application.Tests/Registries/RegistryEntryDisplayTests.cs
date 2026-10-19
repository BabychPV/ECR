// tests/Ecr.Application.Tests/Registries/RegistryEntryDisplayTests.cs
using System.Text.Json;
using Ecr.Application.Common;
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
/// </remarks>
public sealed class RegistryEntryDisplayTests
{
    private const int RegistryDefId = 100;
    private const string Code = "DISPLAY_REG";

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryEntryCache _cache = Substitute.For<IRegistryEntryCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public RegistryEntryDisplayTests()
    {
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 9 }
                .Permission(GetRegistryEntriesHandler.Permission)
                .Build());
        _cache.GetOrAddAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                callInfo.ArgAt<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(1)(default));

        var definition = new RegistryDef(EcrCode.Create(Code), Text("Registry"), false);
        SetId(definition, RegistryDefId);
        _registries.FindDefinitionAsync(Code, Arg.Any<CancellationToken>()).Returns(definition);
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
        // Саме це бачив тестувальник: запис створено, назви немає, перелік показує код.
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
