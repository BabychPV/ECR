// tests/Ecr.Api.Tests/Security/SecurityHeadersTests.cs

using System.Net;
using System.Net.Http.Json;
using Ecr.Api.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Заголовки безпеки в <b>кожній</b> відповіді (<c>S-22</c>): звичайній,
/// <c>problem+json</c> і статичній.
/// </summary>
/// <remarks>
/// ⛔ Головний тут — не перший тест, а другий. Заголовки, написані звичайним
/// присвоєнням, зникають із <c>problem+json</c>: <c>ExceptionHandlingMiddleware</c>
/// робить <c>Response.Clear()</c>, який стирає геть усі заголовки. Тобто
/// «захист» тихо не діяв би рівно на відповідях про помилку. Тому перевірка
/// йде по відповіді, яка ПРОЙШЛА через <c>Clear()</c>.
///
/// ⚠ Мутаційний доказ на кожен випадок названий у самому тесті.
/// </remarks>
[Collection("SqlServer")]
public sealed class SecurityHeadersTests(SqlServerFixture sql)
{
    private const string Nosniff = "nosniff";
    private const string FrameOptions = "X-Frame-Options";
    private const string ContentTypeOptions = "X-Content-Type-Options";
    private const string Csp = "Content-Security-Policy";
    private const string CspReportOnly = "Content-Security-Policy-Report-Only";
    private const string Hsts = "Strict-Transport-Security";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Звичайна_відповідь_несе_повний_набір_заголовків()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client
            .GetAsync(new Uri("/health/live", UriKind.Relative))
            .ConfigureAwait(true);

        // ⛔ Мутаційний доказ: прибрати `app.UseMiddleware<SecurityHeadersMiddleware>()`
        // з `Program.cs` — падає кожен рядок нижче, бо заголовків немає взагалі.
        Assert.Equal(Nosniff, Single(response, ContentTypeOptions));
        Assert.Equal("DENY", Single(response, FrameOptions));
        Assert.Equal(
            SecurityHeadersMiddleware.ReferrerPolicy,
            Single(response, SecurityHeadersMiddleware.ReferrerPolicyHeader));

        // ⚠ Примусова CSP навмисно НЕ містить `script-src`/`style-src`: довести
        // тестом, що клієнт живий під ними, тут неможливо (потрібен браузерний
        // прогін), а мовчки зламаний застосунок гірший за відсутню директиву.
        var enforced = Single(response, Csp);
        Assert.Contains("frame-ancestors 'none'", enforced, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", enforced, StringComparison.Ordinal);

        // …але й не «вимкнено зовсім»: ризикова частина їде звітною, тобто
        // порушення видно в консолі браузера, а сторінка не гасне.
        var reportOnly = Single(response, CspReportOnly);
        Assert.Contains("default-src 'self'", reportOnly, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", reportOnly, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заголовки_переживають_Response_Clear_у_відповіді_про_помилку()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        // ⚠ Невдалий вхід — найдешевша АНОНІМНА відмова, що проходить увесь
        // конвеєр помилок: `LoginHandler` кидає `AccessDeniedException`,
        // `ExceptionHandlingMiddleware` ловить його і збирає тіло через
        // `Response.Clear()`. Саме цей `Clear()` і є предметом перевірки.
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = "немає-такого-користувача", password = "не-той-пароль" })
            .ConfigureAwait(true);

        // Спершу — що це справді відповідь, яка пройшла через `Response.Clear()`.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        // ⛔ Мутаційний доказ: замінити `Response.OnStarting(...)` у
        // `SecurityHeadersMiddleware` на звичайне присвоєння перед `next` —
        // обидва рядки падають, бо `Response.Clear()` обробника помилок стер
        // заголовки, а перший тест лишається зеленим і нічого не помічає.
        Assert.Equal(Nosniff, Single(response, ContentTypeOptions));
        Assert.Equal("DENY", Single(response, FrameOptions));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Статичний_файл_теж_несе_заголовки()
    {
        // ⛔ Корінь веба — ВЛАСНИЙ тимчасовий каталог прогону (`UseWebRoot`, як у
        // `SpaFallbackTests`), а не `src/Ecr.Api/wwwroot` дерева. Доти файл
        // клався в спільну теку робочої копії, яку ділять: сусідній набір
        // (`StaticAssetsAndCompressionTests`), паралельні процеси тієї самої DLL
        // (`verify-all.ps1 -ApiParallel`) і всі сесії, що працюють у цьому
        // чекауті (неігнорований `wwwroot` видно в `git status`, і `git clean`/
        // `git stash -u` сусіда прибирає його посеред прогону). Будь-що з цього
        // давало `чанк: NotFound` без жодного стосунку до заголовків — CL-1.
        // Своя тека знімає й двохостовий прийом: каталог існує ДО старту хоста.
        var webRoot = Directory.CreateTempSubdirectory("ecr-security-headers-").FullName;
        var assets = Directory.CreateDirectory(Path.Combine(webRoot, "assets")).FullName;
        const string name = "SecurityHeaders-chunk.js";

        await File.WriteAllTextAsync(Path.Combine(assets, name), "export const ok = 1;").ConfigureAwait(true);

        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = app.WithWebHostBuilder(b => b.UseWebRoot(webRoot)).CreateClient();

            using var response = await client
                .GetAsync(new Uri($"/assets/{name}", UriKind.Relative))
                .ConfigureAwait(true);

            // ⚠ Спершу — що файл узагалі віддано. Інакше твердження нижче були б
            // про заголовки відповіді `404`, і тест був би зелений із порожнечі.
            Assert.True(response.IsSuccessStatusCode, $"чанк: {response.StatusCode}");
            Assert.Equal("export const ok = 1;", await response.Content.ReadAsStringAsync().ConfigureAwait(true));

            // ⛔ Мутаційний доказ: перенести `UseMiddleware<SecurityHeadersMiddleware>()`
            // ПІСЛЯ `UseStaticFiles` — цей рядок падає, бо статику віддає
            // middleware, до якого ми вже не дійшли.
            Assert.Equal(Nosniff, Single(response, ContentTypeOptions));
        }
        finally
        {
            try
            {
                Directory.Delete(webRoot, recursive: true);
            }
            catch (IOException)
            {
                // Тимчасовий каталог прибере ОС (на Windows файл щойно відкривав
                // антивірус чи індексатор). Падати на прибиранні означало б
                // червоніти там, де перевірка вже відповіла.
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task HSTS_ставиться_лише_на_HTTPS_запиті()
    {
        using var app = new EcrApiFactory(sql);

        using var http = app.CreateClient();
        using var https = app.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") });

        var overHttp = await http
            .GetAsync(new Uri("/health/live", UriKind.Relative))
            .ConfigureAwait(true);
        var overHttps = await https
            .GetAsync(new Uri("/health/live", UriKind.Relative))
            .ConfigureAwait(true);

        // ⛔ Мутаційний доказ: зробити HSTS безумовним — падає цей рядок.
        // А він і є суттю рішення: інсталятор сьогодні піднімає `http://`
        // (`R-01`), і `Strict-Transport-Security`, відданий з того ж імені
        // хоста, замкнув би браузери користувачів на HTTPS, якого немає.
        Assert.False(
            overHttp.Headers.Contains(Hsts),
            $"HTTP-відповідь не має нести HSTS, а несе: {Single(overHttp, Hsts)}");

        // ⛔ Зворотний бік: прибрати гілку `if (isHttps)` цілком — падає цей.
        Assert.Equal(SecurityHeadersMiddleware.StrictTransportSecurity, Single(overHttps, Hsts));
    }

    /// <summary>Значення заголовка одним рядком; порожній — якщо його немає.</summary>
    private static string Single(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;
}
