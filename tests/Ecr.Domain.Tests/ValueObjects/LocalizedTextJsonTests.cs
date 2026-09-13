using System.Text.Json;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// <see cref="LocalizedText"/> як поле в тілі запиту (`RegistryFieldSaveDto.NameL10n`,
/// `RegistryRuleSaveDto.MessageL10n`) — контролери довідників приймають її напряму
/// через MVC-серіалізатор, не лише через <see cref="LocalizedText.FromJson"/>.
/// </summary>
/// <remarks>
/// ⛔ Q-301 (знайдено під час живої перевірки Q-296): `PUT
/// /api/v1/registries/{code}/definition` повертав `500` —
/// `System.InvalidOperationException` при побудові контракту серіалізації
/// `LocalizedText`. `[JsonConstructor]`-параметр `values` мав тип
/// <c>Dictionary&lt;string, string&gt;</c>, а публічна властивість
/// <see cref="LocalizedText.Values"/> — <c>IReadOnlyDictionary&lt;string, string&gt;</c>:
/// System.Text.Json зіставляє параметр конструктора з публічною властивістю за
/// іменем (без урахування регістру) і вимагає ТОЧНОГО збігу типів, інакше кидає
/// виняток при першому використанні типу — не лише коли значення справді
/// присутнє в JSON.
/// </remarks>
public sealed class LocalizedTextJsonTests
{
    // JsonSerializerDefaults.Web — те саме, що ConfigureHttpJsonOptions/AddJsonOptions
    // застосовують у Program.cs (camelCase, регістронезалежне зіставлення).
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Десеріалізація_з_тіла_запиту_не_кидає_виняток()
    {
        const string json = """{"values":{"en":"Hello","ru":"Привіт"}}""";

        var text = JsonSerializer.Deserialize<LocalizedText>(json, Options);

        Assert.NotNull(text);
        Assert.Equal("Hello", text!.Get("en"));
        Assert.Equal("Привіт", text.Get("ru"));
    }

    [Fact]
    public void Серіалізація_і_десеріалізація_проходять_туди_й_назад()
    {
        var original = LocalizedText.FromJson("""{"en":"A","kz":"Ä"}""");

        var json = JsonSerializer.Serialize(original, Options);
        var roundTripped = JsonSerializer.Deserialize<LocalizedText>(json, Options);

        Assert.Equal("A", roundTripped!.Get("en"));
        Assert.Equal("Ä", roundTripped.Get("kz"));
    }

    [Fact]
    public void Порожнє_тіло_дає_порожній_LocalizedText()
    {
        var text = JsonSerializer.Deserialize<LocalizedText>("{}", Options);

        Assert.NotNull(text);
        Assert.Null(text!.Get("en"));
    }
}
