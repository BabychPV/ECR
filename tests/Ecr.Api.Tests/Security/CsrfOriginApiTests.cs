// tests/Ecr.Api.Tests/Security/CsrfOriginApiTests.cs

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// <see cref="Ecr.Api.Security.CsrfOriginMiddleware"/> у справжньому конвеєрі
/// (<c>L1-04</c>): чужий <c>Origin</c> — <c>403 ECR-AUTH-0403</c> з причиною з
/// каталогу, той самий запит без позначок браузера — як і раніше.
/// </summary>
/// <remarks>
/// ⚠ Вхід — найдешевший анонімний небезпечний шлях: предмет тут конвеєр, а не
/// дані. Невідоме ім'я дає звичайну відмову входу, і саме з нею порівнюється.
///
/// ⛔ Мутаційний доказ (лише локально): прибрати рядок
/// <c>UseMiddleware&lt;CsrfOriginMiddleware&gt;</c> із <c>Program.cs</c> —
/// червоніє перша перевірка (приходить відмова входу, а не 403); прибрати
/// <c>err.ECR-AUTH-0403.csrfOrigin</c> із сіду — червоніє перевірка подробиці.
/// </remarks>
[Collection("SqlServer")]
public sealed class CsrfOriginApiTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "L1-04")]
    public async Task Небезпечний_метод_з_чужим_Origin_403_без_Origin_як_раніше()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        using var forged = await LoginAsync(client, ("Origin", "https://evil.example")).ConfigureAwait(true);
        var body = await forged.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(forged.StatusCode == HttpStatusCode.Forbidden, $"чужий Origin: {forged.StatusCode}: {body}\n{app.ErrorsText}");
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-AUTH-0403", json.GetProperty("errorCode").GetString());
        Assert.Contains("another website", json.GetProperty("detail").GetString(), StringComparison.Ordinal);

        using var sameSite = await LoginAsync(client, ("Sec-Fetch-Site", "same-site")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, sameSite.StatusCode);

        // Без позначок і зі своїм origin — та сама відмова входу, що й до фіксу.
        using var plain = await LoginAsync(client).ConfigureAwait(true);
        using var own = await LoginAsync(client, ("Sec-Fetch-Site", "same-origin"), ("Origin", "http://localhost")).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.Forbidden, plain.StatusCode);
        Assert.Equal(plain.StatusCode, own.StatusCode);
        Assert.NotEqual(
            "ECR-AUTH-0403",
            JsonDocument.Parse(await own.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("errorCode").GetString());

        // Безпечний метод з чужим Origin не перевіряється.
        using var get = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        get.Headers.Add("Origin", "https://evil.example");
        get.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var live = await client.SendAsync(get).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/login/local", UriKind.Relative))
        {
            Content = JsonContent.Create(new { userName = "немає-такого-користувача-csrf", password = "Wrong-Password-2026!" }),
        };
        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }
}
