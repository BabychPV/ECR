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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Форма_звіту_збігається_зі_спільним_зразком_який_читає_клієнт()
    {
        // ⛔ Це межа «сервер → клієнт», на якій ДВІЧІ жив `A7-04`: клієнт
        // читав `entries` словником, сервер писав `checks` масивом, дашборд
        // здоров'я відкривався порожнім — і виглядав точно як здорова система
        // без перевірок. `Object.entries(undefined ?? {})` не падає.
        //
        // ⚠ `/health/*` — middleware, а не контролер: його немає в OpenAPI, і
        // згенерувати клієнтський тип нема з чого. Тому форму тримає спільний
        // зразок, який читають ОБИДВА боки: цей тест і `health.test.tsx`.
        // Розійтися нишком вони більше не можуть.
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var live = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var sample = JsonDocument.Parse(
            await File.ReadAllTextAsync(TestFixtures.Path("health-response.json"))).RootElement;

        // 1. Верхній рівень: ті самі поля, з тими самими типами.
        Assert.Equal(JsonValueKind.String, live.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Number, live.GetProperty("totalDurationMs").ValueKind);
        Assert.Equal(JsonValueKind.Array, live.GetProperty("checks").ValueKind);

        // 2. Кожна перевірка має рівно ті поля, що й у зразку. Саме тут
        //    ловиться перейменування `checks` → `entries` і навпаки.
        var expected = sample.GetProperty("checks")[0]
            .EnumerateObject()
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var check in live.GetProperty("checks").EnumerateArray())
        {
            var actual = check.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(expected, actual);
        }

        // 3. Перевірки знаходяться ЗА ІМЕНЕМ, не за позицією: порядок задає
        //    контейнер, і покластися на нього означає зламатися від наступної
        //    зареєстрованої перевірки.
        var names = live.GetProperty("checks")
            .EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("db", names);
        Assert.Contains("jobs", names);
        Assert.Contains("sources", names);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перевірки_задач_і_джерел_не_відповідають_заглушкою()
    {
        // ⛔ Обидві перевірки від ЕТАПУ 5 повертали `Degraded` із текстом
        // «з'явиться на Етапі 5» — незалежно ні від чого. Етап 5 закритий,
        // задач сім, збір працює, а `/health/ready` світився жовтим ЗАВЖДИ.
        //
        // ⚠ Моніторинг, який роками показує те саме, навчають ігнорувати —
        // і справжню деградацію після цього не помічає ніхто. Перевірка, яка
        // ніколи не змінює відповіді, не перевіряє нічого.
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        foreach (var name in new[] { "jobs", "sources" })
        {
            var check = report.GetProperty("checks")
                .EnumerateArray()
                .Single(c => string.Equals(c.GetProperty("name").GetString(), name, StringComparison.Ordinal));

            var description = check.GetProperty("description").GetString() ?? string.Empty;

            Assert.DoesNotContain("Етапі 5", description, StringComparison.Ordinal);
            Assert.NotEmpty(check.GetProperty("data").EnumerateObject());

            // Дані перевірки більше не «stage = 5», а щось вимірюване.
            Assert.False(check.GetProperty("data").TryGetProperty("stage", out _));
        }
    }

    private static async Task<JsonElement> ReadDbDataAsync(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/health/db", UriKind.Relative));
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // ⚠ ЗА ІМЕНЕМ, а не за позицією: порядок перевірок задає контейнер,
        // і покластися на нього означає зламатися від наступної зареєстрованої.
        return json.RootElement.GetProperty("checks")
            .EnumerateArray()
            .Single(c => string.Equals(c.GetProperty("name").GetString(), "db", StringComparison.Ordinal))
            .GetProperty("data");
    }
}
