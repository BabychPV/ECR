// tests/Ecr.Api.Tests/LanguagesControllerTests.cs
using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// AN-40 / L9-13: <c>GET /api/v1/languages</c> несе <c>hasTranslations</c> — перемикач мови більше не тягне повні
/// каталоги всіх мов, щоб вирішити, які показувати.
/// </summary>
/// <remarks>
/// Мутація (лише локально): у <c>LanguagesController.List</c> ставити <c>HasTranslations</c> лише мові за
/// замовчуванням — у ru/kz поле зникає з відповіді, червоніє цей тест.
/// </remarks>
[Collection("SqlServer")]
public sealed class LanguagesControllerTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L9-13")]
    public async Task Перелік_мов_несе_ознаку_перекладу_з_бази()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, []);

        var response = await client.GetAsync(new Uri("/api/v1/languages", UriKind.Relative));

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");
        var languages = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .EnumerateArray()
            .ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("hasTranslations"));

        // Засіяні ru і kz перекладені; мова за замовчуванням — завжди «з перекладом» (вона й є текст інтерфейсу).
        Assert.Equal(JsonValueKind.True, languages["en"].ValueKind);
        Assert.Equal(JsonValueKind.True, languages["ru"].ValueKind);
        Assert.Equal(JsonValueKind.True, languages["kz"].ValueKind);
    }
}
