using System.Net.Http.Headers;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Стиснення відповідей (<c>RD-01</c>) і заголовки кешу SPA (<c>DAT-08</c>,
/// серверна половина) — <c>DIRECTIVE-14-ARCH.md</c> §3.1, §3.4.
/// </summary>
/// <remarks>
/// ⛔ <b>Заголовки кешу — це не про швидкість, а про білий екран.</b> Усі
/// сторінки клієнта — ліниві чанки з хешем у назві. Після оновлення MSI
/// відкрита вкладка просить чанк, якого на диску вже немає: <c>import()</c>
/// відхиляється, і застосунок зникає — невідрізнимо від дефекту продукту.
/// Закешований <c>index.html</c> і є тією вкладкою, що вічно просить старий
/// чанк, тому в нього <c>no-cache</c>, а в <c>/assets/*</c> — навпаки,
/// <c>immutable</c> на рік: ім'я вже містить хеш вмісту.
///
/// ⛔ <b>Стиснення не було взагалі</b> (<c>AddResponseCompression</c> — нуль
/// збігів у <c>src</c>), а зріз 500×60 — це 0.8–3 МБ JSON.
///
/// ⚠ Чому тести ходять саме по <c>/health/live</c> і по підкладених файлах, а
/// не по зрізу: зріз вимагає документа, прав і гранта, тобто перевіряв би
/// півсистеми заради двох заголовків. Предмет тут — конвеєр, а не дані.
/// </remarks>
[Collection("SqlServer")]
public sealed class StaticAssetsAndCompressionTests(SqlServerFixture sql)
{
    /// <summary>Довге тіло: коротке відповідь стискати не стане й правильно зробить.</summary>
    private const int Repeats = 400;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відповідь_стискається_коли_клієнт_це_вміє()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        // ⚠ `/health/ready` віддає JSON із переліком перевірок — тіло, достатнє
        // для порога стиснення, і доступне анонімно (моніторинг, `АРХ-7`).
        var response = await client
            .GetAsync(new Uri("/health/ready", UriKind.Relative))
            .ConfigureAwait(true);

        // ⛔ Мутаційний доказ: прибрати `UseResponseCompression()` — цей рядок
        // падає, бо `Content-Encoding` порожній.
        Assert.Contains(
            "br",
            response.Content.Headers.ContentEncoding,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клієнт_без_підтримки_стиснення_дістає_звичайне_тіло()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        // ⚠ Зворотний бік: стиснення не має нав'язуватися тому, хто його не
        // просив, — інакше «оптимізація» ламає найпростіших споживачів
        // (`curl` у скриптах розгортання, `deploy-ecr.ps1`).
        var response = await client
            .GetAsync(new Uri("/health/ready", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чанк_з_хешем_у_назві_кешується_назавжди_а_оболонка_ні()
    {
        // ⛔ Файли підкладаються в `wwwroot` цього ж прогону: у dev-дереві його
        // немає (клієнт збирає інсталятор), тож без цього кроку перевірка
        // заголовків нічого не перевіряла б — `UseStaticFiles` просто не
        // знайшов би файлу, і тест був би зелений із порожнього місця.
        //
        // ⚠ Каталог береться з ПІДНЯТОГО застосунку, а не з
        // `AppContext.BaseDirectory`. Перша спроба була саме такою і дала
        // `чанк: NotFound`: `WebApplicationFactory` ставить корінь вмісту в
        // каталог проєкту `Ecr.Api`, а не в теку збірки тестів, тож файли
        // лягали туди, де їх ніхто не шукав.
        //
        // ⚠ Провайдер статики прив'язується до `WebRootPath` на СТАРТІ, тому
        // каталог має існувати до підняття того хоста, який ми питаємо: перший
        // хост — лише щоб дізнатися шлях, другий — щоб перевірити заголовки.
        string contentRoot;

        using (var probe = new EcrApiFactory(sql))
        {
            contentRoot = ((Microsoft.AspNetCore.Hosting.IWebHostEnvironment)
                probe.Services.GetService(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment))!)
                .ContentRootPath;
        }

        var root = Path.Combine(contentRoot, "wwwroot");
        var assets = Path.Combine(root, "assets");

        Directory.CreateDirectory(assets);

        var shell = Path.Combine(root, "index.html");
        var chunk = Path.Combine(assets, "DocumentPage-a1b2c3d4.js");

        await File.WriteAllTextAsync(shell, new string('x', Repeats)).ConfigureAwait(true);
        await File.WriteAllTextAsync(chunk, new string('y', Repeats)).ConfigureAwait(true);

        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = app.CreateClient();

            var chunkResponse = await client
                .GetAsync(new Uri("/assets/DocumentPage-a1b2c3d4.js", UriKind.Relative))
                .ConfigureAwait(true);

            var shellResponse = await client
                .GetAsync(new Uri("/index.html", UriKind.Relative))
                .ConfigureAwait(true);

            // ⚠ Спершу — що файли взагалі віддано. Інакше обидва твердження
            // нижче були б про заголовки відповіді `404`.
            Assert.True(chunkResponse.IsSuccessStatusCode, $"чанк: {chunkResponse.StatusCode}");
            Assert.True(shellResponse.IsSuccessStatusCode, $"оболонка: {shellResponse.StatusCode}");

            var chunkCache = chunkResponse.Headers.CacheControl?.ToString() ?? string.Empty;

            // ⛔ Мутаційний доказ: прибрати `OnPrepareResponse` — обидва
            // твердження падають, бо заголовка немає взагалі.
            Assert.Contains("immutable", chunkCache, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("max-age=31536000", chunkCache, StringComparison.OrdinalIgnoreCase);

            // ⛔ А це головне: оболонка НЕ кешується. Саме закешований
            // `index.html` після оновлення просить чанк, якого вже немає.
            Assert.True(
                shellResponse.Headers.CacheControl?.NoCache,
                $"оболонка мусить бути no-cache, а має: {shellResponse.Headers.CacheControl}");
        }
        finally
        {
            File.Delete(shell);
            File.Delete(chunk);
        }
    }
}
