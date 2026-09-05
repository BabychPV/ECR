// tests/Ecr.Api.Tests/CellsControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Правка комірок ЧЕРЕЗ HTTP — межа, якої не перетинає жоден інший тест.
/// </summary>
/// <remarks>
/// ⛔ Тест з'явився після `A7-01`. Обробник розбирав значення комірки
/// правильно, клієнт надсилав правильно, а між ними стояв
/// <c>System.Text.Json</c>: число з тіла запиту приходило як
/// <see cref="JsonElement"/>, і жодна перевірка типу на нього не спрацьовувала.
/// Прикладні тести передавали <c>decimal</c> напряму й перетину не бачили;
/// клієнтські ходили в замокнений <c>fetch</c> і теж.
///
/// ⚠ Тут перевіряється ЗВ'ЯЗУВАННЯ і форма відповіді, а не запис у базу:
/// повний документ із аркушем, таблицею і рядком — це фікстура на кілька
/// сотень рядків, і вона нічого не додала б до того, що вже перевіряють
/// інтеграційні тести сховища. Значення в іншому: що тіло, яке надсилає
/// браузер, доходить до обробника не спотвореним.
///
/// ⛔ Запити йдуть ПІД КОРИСТУВАЧЕМ. Анонімний запит зупиняє автентифікація —
/// з порожнім тілом, ще до конвеєра помилок; тест на такому запиті виглядає
/// зеленим і не перевіряє нічого.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellsControllerTests(SqlServerFixture sql)
{
    private const string Password = "Api-Cells-Probe-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тіло_правки_комірок_звязується_а_не_відхиляється_як_некоректне()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        // Форма — дослівно та, яку надсилає сітка: число, рядок, булеве і
        // явна порожнеча в одному батчі.
        var body = new
        {
            tableInstanceId = 1L,
            periodKey = 202601,
            origin = "Manual",
            rows = new[]
            {
                new
                {
                    rowKey = "R1",
                    baseVersion = (string?)null,
                    cells = new object[]
                    {
                        new { columnCode = "C_NUM", value = (object)12.5m },
                        new { columnCode = "C_TXT", value = (object)"текст" },
                        new { columnCode = "C_BOOL", value = (object)true },
                        new { columnCode = "C_EMPTY", value = (object?)null, isEmpty = true },
                    },
                },
            },
        };

        var response = await client
            .PatchAsJsonAsync(new Uri("/api/v1/documents/1/cells", UriKind.Relative), body)
            .ConfigureAwait(true);

        // ⛔ 400 означав би, що контракт розійшовся: поле назване інакше, тип
        // не той, обов'язкове поле відсутнє. Саме такий 400 отримувала форма
        // входу (`A7-09`) — на кожну спробу і без жодного сліду.
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);

        // Документа з таким номером немає, і це правильна відповідь: вона про
        // ДАНІ, а не про форму запиту.
        Assert.Contains(
            response.StatusCode,
            new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.UnprocessableEntity });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тіло_помилки_несе_код_плоско_і_рівно_один_раз()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri("/api/v1/documents/999999", UriKind.Relative))
            .ConfigureAwait(true);

        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.False(string.IsNullOrWhiteSpace(text), $"Порожнє тіло на {response.StatusCode}: {app.ErrorsText}");

        var json = JsonDocument.Parse(text).RootElement;

        // ⛔ `errorCode` лежить у КОРЕНІ (RFC 9457 §3.2). До `A7-15` типізовані
        // властивості серіалізувалися разом зі словником розширень: код
        // виходив двічі, а поруч стояло поле `extensions2`, якого немає в
        // жодному контракті — і саме за такою формою генератор клієнтських
        // типів описав би помилку неправильно.
        Assert.True(json.TryGetProperty("errorCode", out var code));
        Assert.False(string.IsNullOrWhiteSpace(code.GetString()));
        Assert.False(json.TryGetProperty("extensions2", out _));

        // Дубль не видно через `TryGetProperty` — його видно лише в тексті.
        Assert.Equal(1, CountOf(text, "\"errorCode\""));
        Assert.Equal(1, CountOf(text, "\"correlationId\""));
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        var at = text.IndexOf(token, StringComparison.Ordinal);

        while (at >= 0)
        {
            count++;
            at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>Клієнт із чинним сеансом локального користувача.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"cells_{Guid.NewGuid():N}"[..20];

        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

        await using (var db = new EcrDbContext(options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }
}
