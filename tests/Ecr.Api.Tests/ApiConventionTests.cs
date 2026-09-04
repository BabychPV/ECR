using System.Reflection;
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
    public void Розмір_сторінки_понад_максимум_відхиляється_400()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Курсор_наступної_сторінки_повертає_наступні_елементи_без_пропусків()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Довга_операція_повертає_202_із_ідентифікатором_задачі()
        => Assert.Fail("not implemented");
}
