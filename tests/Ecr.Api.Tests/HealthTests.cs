using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Health має пояснювати стан, а не лише повідомляти «живий».
/// </summary>
/// <remarks>
/// ⚠ Позначені <c>Integration</c>, хоча в `06d` цієї позначки не було
/// (`Q-053`): підняти застосунок без бази неможливо — послідовність старту
/// першим кроком чекає на з'єднання і без нього не стартує навмисно.
/// </remarks>
[Collection("SqlServer")]
public sealed class HealthTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Health_db_повідомляє_режим_редакції()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var data = await ReadDbDataAsync(client);

        // АРХ-7 п. 5: адміністратор має бачити, у якому режимі працює система.
        // «Healthy» без цього не пояснює, чому нічна операція поводиться інакше.
        Assert.True(data.TryGetProperty("effectiveMode", out var mode));
        Assert.False(string.IsNullOrWhiteSpace(mode.GetString()));
        Assert.True(data.TryGetProperty("edition", out var edition));
        Assert.Contains("Edition", edition.GetString()!, StringComparison.OrdinalIgnoreCase);

        // І що саме в цьому режимі недоступне — інакше режим це просто слово.
        Assert.True(data.TryGetProperty("limitations", out var limitations));
        Assert.NotEmpty(limitations.EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Health_db_повідомляє_стан_RCSI()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var data = await ReadDbDataAsync(client);

        // ⚠ Регресія на Q-052: у скелеті стояло
        // DATABASEPROPERTYEX(...,'IsReadCommittedSnapshotOn'), а такої
        // властивості не існує — вона повертає NULL, і health довіку
        // повідомляв би, що RCSI вимкнено.
        Assert.True(data.TryGetProperty("rcsi", out var rcsi));
        Assert.True(rcsi.GetBoolean(), "Фікстура вмикає RCSI скриптом 06-rcsi.sql.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Health_db_повідомляє_запас_партицій()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var data = await ReadDbDataAsync(client);

        // Менше двох попереду — Degraded, і дізнатися про це треба на старті,
        // а не вночі, коли архівація впреться у відсутню межу.
        Assert.True(data.TryGetProperty("partitionsAhead", out var ahead));
        Assert.True(ahead.GetInt32() >= 0);
        Assert.True(data.TryGetProperty("filegroups", out var filegroups));
        Assert.Contains(
            filegroups.EnumerateArray().Select(x => x.GetString()),
            fg => string.Equals(fg, "DATA_HOT", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Health_live_не_звертається_до_БД()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} — {app.ErrorsText}");

        // ⚠ /health/live опитує оркестратор. Якби він ходив у базу, повільна
        // або недоступна база спричиняла б перезапуск процесу, який працює, —
        // тобто рівно те, чого liveness-проба має уникати.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("db", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> ReadDbDataAsync(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/health/db", UriKind.Relative));
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("checks")[0].GetProperty("data");
    }
}
