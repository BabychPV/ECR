using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Контракт зафіксований знімком (<c>D-136</c>).
/// </summary>
/// <remarks>
/// ⛔ `A7-34`, `A7-35` і `A7-36` — не три помилки, а три прояви одного:
/// **клієнт мав власні рукописні типи відповідей, і ніщо не звіряло їх із тим,
/// що сервер справді віддає.** Розбіжності не видно в жодному з двох файлів
/// окремо — лише МІЖ ними, а між ними не дивився ніхто.
///
/// Знімок робить розбіжність подією збірки: змінив код відповіді — тест
/// червоний. Оновлення знімка стає свідомим комітом, який видно в рев'ю, а не
/// мовчазним розходженням у прозі.
/// </remarks>
[Collection("SqlServer")]
public sealed class OpenApiSnapshotTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.6")]
    public async Task Живий_документ_OpenAPI_збігається_зі_знімком()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        var live = OpenApiSnapshot.Normalize(await response.Content.ReadAsStringAsync());

        // ⛔ Оновлення знімка — ЯВНА дія: `ECR_UPDATE_SNAPSHOT=1`. Тест, який
        // переписує знімок сам, не сторож, а прикраса: він зеленітиме на
        // будь-якій зміні контракту, зокрема на випадковій.
        //
        // ⚠ Навіть із прапорцем тест ПАДАЄ — щоб оновлення не пройшло повз
        // рев'ю разом із зеленою збіркою.
        if (Environment.GetEnvironmentVariable("ECR_UPDATE_SNAPSHOT") == "1")
        {
            OpenApiSnapshot.Write(live);
            Assert.Fail(
                $"Знімок перезаписано: {OpenApiSnapshot.Path()}. " +
                "Перевір діф і закоміть окремо — це зміна контракту.");
        }

        var stored = OpenApiSnapshot.Read();

        if (!string.Equals(live, stored, StringComparison.Ordinal))
        {
            // ⚠ У повідомленні — перші розбіжні рядки, а не «файли різні».
            // Інакше єдиний спосіб зрозуміти, що змінилося, — записати живий
            // документ у файл руками, а це роблять раз і більше не роблять.
            Assert.Fail(Difference(stored, live));
        }
    }

    /// <summary>Перші розбіжні рядки з контекстом.</summary>
    private static string Difference(string expected, string actual)
    {
        var left = expected.Split('\n');
        var right = actual.Split('\n');

        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i] : "<кінець файла>";
            var b = i < right.Length ? right[i] : "<кінець файла>";

            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                continue;
            }

            return $"""
                Документ OpenAPI розійшовся зі знімком у рядку {i + 1}.

                  знімок: {a}
                  сервер: {b}

                Якщо зміна свідома — оновити contracts/openapi.snapshot.json
                окремим комітом: запустити цей тест із ECR_UPDATE_SNAPSHOT=1.
                Якщо ні — це зміна контракту, якої ніхто не планував.
                """;
        }

        return $"Довжина різна: знімок {left.Length} рядків, сервер {right.Length}.";
    }
}
