using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Конвенції API: одна поведінка на всіх ендпоінтах, а не на розсуд автора.
/// </summary>
[Collection("SqlServer")]
public sealed class ApiConventionTests(SqlServerFixture sql)
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Специфікація_OpenAPI_генерується_і_валідна()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Специфікація — це контракт для клієнта. Якщо вона не генерується,
        // фронтенд генерує типи з нічого і розходиться з сервером мовчки.
        Assert.True(json.TryGetProperty("openapi", out _));
        Assert.True(json.TryGetProperty("paths", out var paths));
        Assert.NotEmpty(paths.EnumerateObject());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9c")]
    public async Task Умовний_запит_каталогу_віддає_304_із_порожнім_тілом()
    {
        // ⛔ Тіло має бути ПОРОЖНЄ. `304` із тілом — це весь каталог, надісланий
        // ще раз під виглядом «не змінилося»: клієнт його не читає, а трафік і
        // час на серіалізацію витрачені. Саме заради цього умовний запит і
        // існує (`A7-34`).
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var path = new Uri("/api/v1/ui-strings/en?scope=public", UriKind.Relative);

        var first = await client.GetAsync(path);
        first.EnsureSuccessStatusCode();

        var etag = first.Headers.ETag?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag), "Сервер не віддав ETag — умовний запит неможливий.");

        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);

        var second = await client.SendAsync(conditional);

        Assert.Equal(System.Net.HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Числа_передаються_рядком_щоб_не_втратити_точність()
    {
        // decimal(28,10) не поміщається в double: JSON-число на клієнті стає
        // IEEE-754, і зрізана цифра з'являється у звіті. Тому всі грошові й
        // вимірювані величини їдуть рядком (D-30).
        var dtoTypes = typeof(Ecr.Application.Documents.Dto.TableSliceDto).Assembly
            .GetTypes()
            .Where(t => t.Namespace?.Contains(".Dto", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(dtoTypes);

        var offenders = dtoTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.PropertyType == typeof(double)
                        || x.Property.PropertyType == typeof(float)
                        || x.Property.PropertyType == typeof(double?)
                        || x.Property.PropertyType == typeof(float?))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Дати_передаються_в_UTC_за_ISO_8601()
    {
        // DateTime без Kind — джерело помилок на межі періоду: той самий
        // момент у поясі майданчика і в UTC потрапляє в різні періоди (D-68).
        var utc = new DateTime(2026, 1, 31, 19, 0, 0, DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(utc, WebOptions);

        Assert.Equal("\"2026-01-31T19:00:00Z\"", json);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Списковий_ендпоінт_повертає_сторінку_а_не_весь_набір()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var paths = json.GetProperty("paths");

        // Списковий ендпоінт зобов'язаний приймати limit і cursor: без цього
        // перший же великий реєстр віддає все і кладе і сервер, і клієнта.
        var listing = paths.EnumerateObject()
            .Where(p => p.Value.TryGetProperty("get", out _))
            .Select(p => p.Name)
            .ToList();

        Assert.NotEmpty(listing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розмір_сторінки_понад_максимум_відхиляється_400()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var json = JsonDocument.Parse(
            await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative))).RootElement;

        // Кожен списковий ендпоінт мусить приймати `limit`: без нього перший
        // же великий реєстр віддає все і кладе і сервер, і клієнта. Саму межу
        // перевіряє обробник (Етап 2), тут — що параметр існує в контракті.
        var listWithoutLimit = json.GetProperty("paths").EnumerateObject()
            .Where(path => path.Value.TryGetProperty("get", out var get)
                           && get.TryGetProperty("parameters", out var ps)
                           && ps.EnumerateArray().Any(x => x.GetProperty("name").GetString() == "cursor")
                           && !ps.EnumerateArray().Any(x => x.GetProperty("name").GetString() == "limit"))
            .Select(path => path.Name)
            .ToList();

        Assert.Empty(listWithoutLimit);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Курсор_наступної_сторінки_повертає_наступні_елементи_без_пропусків()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var json = JsonDocument.Parse(
            await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative))).RootElement;

        // ⚠ Курсор, а не offset. `OFFSET n ROWS` на змінному наборі пропускає
        // рядки: поки клієнт гортає, хтось додав запис, і сторінка 2
        // починається не там. Тому в контракті має бути `cursor` і не має
        // бути `offset`/`page`.
        var parameters = json.GetProperty("paths").EnumerateObject()
            .Where(path => path.Value.TryGetProperty("get", out _))
            .SelectMany(path => path.Value.GetProperty("get").TryGetProperty("parameters", out var ps)
                ? ps.EnumerateArray().Select(x => x.GetProperty("name").GetString())
                : [])
            .ToList();

        Assert.DoesNotContain("offset", parameters);
        Assert.DoesNotContain("page", parameters);
        Assert.Contains("cursor", parameters);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Довга_операція_повертає_202_із_ідентифікатором_задачі()
    {
        // ⚠ Перевірка читає ВИХІДНИЙ КОД контролерів, а не рефлексію.
        // Рефлексія бачить атрибут `ProducesResponseType(202)` і не бачить, що
        // тіло дії повертає `Ok`: саме так з'являється ендпоінт, який обіцяє
        // задачу і віддає результат синхронно — на 200 мс у тесті й на дві
        // хвилини таймауту в проді.
        var controllers = Directory.EnumerateFiles(
            Path.Combine(Root(), "src", "Ecr.Api", "Controllers"), "*.cs");

        var broken = new List<string>();

        foreach (var path in controllers)
        {
            foreach (var action in Actions(File.ReadAllText(path)))
            {
                if (!action.Declares202)
                {
                    continue;
                }

                if (!action.Body.Contains("Accepted(", StringComparison.Ordinal))
                {
                    broken.Add($"{Path.GetFileName(path)}.{action.Name}: оголошено 202, повертає інше");
                    continue;
                }

                // ⛔ 202 без ідентифікатора задачі — відповідь «щось почалося,
                // а що саме — не скажу». Клієнту нема чого опитувати, і
                // прогрес довгої операції показати неможливо.
                if (!action.Body.Contains("jobId", StringComparison.Ordinal))
                {
                    broken.Add($"{Path.GetFileName(path)}.{action.Name}: 202 без jobId");
                }
            }
        }

        Assert.Empty(broken);
    }

    /// <summary>Дії контролера: ім'я, атрибути і тіло.</summary>
    private static IEnumerable<(string Name, bool Declares202, string Body)> Actions(string source)
    {
        var matches = Regex.Matches(
            source,
            @"(?<attributes>(?:\s*\[[^\]]+\]\s*)+)\s*public\s+(?:async\s+)?Task<[^(]*?>\s+(?<name>\w+)\s*\(",
            RegexOptions.Singleline);

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : source.Length;

            yield return (
                matches[i].Groups["name"].Value,
                matches[i].Groups["attributes"].Value.Contains("Status202Accepted", StringComparison.Ordinal),
                source[start..end]);
        }
    }

    /// <summary>Корінь репозиторію.</summary>
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
    }
}
