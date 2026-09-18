using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// `MI-01`: кільце ключів DataProtection спільне для інстансів і переживає
/// рестарт (`D-32`).
/// </summary>
/// <remarks>
/// ⛔ Предмет. До цього в репозиторії не було жодного <c>AddDataProtection</c>.
/// Наслідків два, і другий гірший за перший: cookie, видана одним інстансом,
/// для другого — шум (тобто «≥ 2 інстанси» не виконується вже на вході), а під
/// сервісною обліковкою БЕЗ ЗАВАНТАЖЕНОГО ПРОФІЛЮ ключі взагалі ефемерні —
/// кожен рестарт служби розлогінює всіх навіть на одному інстансі.
///
/// ⚠ Чому дефект прожив так довго: тест, який піднімає ОДИН хост, цього не
/// бачить у принципі. Тому тут два <see cref="EcrApiFactory"/> на ОДНІЙ базі, і
/// cookie переноситься між ними руками.
///
/// ⛔ І чесно про межу цього набору. Два хости в одному процесі мають однаковий
/// content root, а саме він за замовчуванням і є ознакою застосунку
/// (<c>ApplicationDiscriminator</c>). Тому поведінковий тест «cookie перейшла»
/// НЕ впав би, якби прибрати самий лише <c>SetApplicationName</c>: обидва хости
/// й без нього домовилися б про однакову ознаку. За цей виклик відповідає
/// окрема перевірка нижче — і вона названа структурною, а не видана за
/// поведінкову.
/// </remarks>
[Collection("SqlServer")]
public sealed class SharedDataProtectionKeyRingTests(SqlServerFixture sql)
{
    private const string Password = "Key-Ring-Across-Nodes-2026!";

    /// <summary>Клієнт без власного сховища cookie: сеанс носить сам тест.</summary>
    /// <remarks>
    /// ⚠ <c>HandleCookies = false</c> обов'язкове. З типовим обробником cookie
    /// осідає всередині <see cref="HttpClient"/> першого хоста, і «перенесення
    /// між інстансами» звелося б до того, що другий хост її просто не побачив
    /// би — тест був би зеленим і беззмістовним.
    /// </remarks>
    private static readonly WebApplicationFactoryClientOptions NoCookieJar = new() { HandleCookies = false };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-32")]
    public async Task Cookie_діє_на_вузлі_розгорнутому_з_іншого_каталогу()
    {
        // ⛔ Це і є справжня перевірка «двох інстансів». Дослівна форма зі
        // стандарту доказу — два `WebApplicationFactory` на одній базі — тут не
        // працює, і це перевірено мутацією, а не вгадано: на коді БЕЗ спільного
        // кільця ключів вона лишалася зеленою. Два хости з одного чекауту
        // ділять content root, а він за замовчуванням і є ознакою застосунку,
        // тобто вони домовляються про ключі без нашої участі.
        //
        // ⚠ Друга машина відрізняється саме каталогом установки. Без сталого
        // `SetApplicationName` ключ із бази цьому вузлу не підійде — ланцюжок
        // призначення інший, і користувач отримає вхід «через раз».
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        using var first = new EcrApiFactory(sql);
        var cookie = await SignInAsync(first, name).ConfigureAwait(true);

        using var secondNode = new SecondNodeApiFactory(sql);
        using var client = secondNode.CreateClient(NoCookieJar);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/me", UriKind.Relative));
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request).ConfigureAwait(true);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Вузол з іншого каталогу відповів {response.StatusCode}: {secondNode.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-32")]
    public async Task Рестарт_хоста_не_робить_видану_cookie_недійсною()
    {
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        string cookie;
        using (var before = new EcrApiFactory(sql))
        {
            cookie = await SignInAsync(before, name).ConfigureAwait(true);
        }

        await using var db = CreateContext();
        var keysBefore = await db.DataProtectionKeys.CountAsync().ConfigureAwait(true);

        // ⛔ Хост, що видав cookie, знищений — разом зі своїм кільцем ключів у
        // пам'яті. Це і є рестарт служби; під обліковкою без профілю саме тут
        // ключі й зникали безслідно.
        using var after = new EcrApiFactory(sql);

        using var response = await GetMeAsync(after, cookie).ConfigureAwait(true);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Після рестарту хоста cookie дала {response.StatusCode} — тобто рестарт служби "
            + $"розлогінює всіх: {after.ErrorsText}");

        // ⚠ Самого лише `200` мало, і це теж перевірено мутацією: на одній
        // машині під одним обліковим записом піднятий заново хост знайшов би
        // ключі й у профілі. Друге твердження прив'язує успіх до бази: ключ у
        // ній був ДО рестарту, і новий хост узяв саме його, а не намалював
        // собі свіже кільце.
        var keysAfter = await db.DataProtectionKeys.CountAsync().ConfigureAwait(true);

        Assert.True(
            keysBefore >= 1,
            "До рестарту в sec.DataProtectionKey не було жодного ключа: кільце не в базі.");
        Assert.Equal(keysBefore, keysAfter);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-32")]
    public async Task Ключі_лежать_у_базі_а_не_в_профілі_облікового_запису()
    {
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        await using (var db = CreateContext())
        {
            // Стан «до» фіксується, бо база спільна для всього набору: інший
            // тест міг уже змусити кільце створити ключ.
            var before = await db.DataProtectionKeys.CountAsync().ConfigureAwait(true);

            using var app = new EcrApiFactory(sql);
            await SignInAsync(app, name).ConfigureAwait(true);

            // ⛔ Ця перевірка — єдина, що падає, якщо прибрати
            // `PersistKeysToDbContext`. Поведінковий тест вище лишився б
            // зеленим: два хости в одному процесі ділять профіль облікового
            // запису, тобто й типове файлове кільце теж.
            var after = await db.DataProtectionKeys.CountAsync().ConfigureAwait(true);
            Assert.True(
                after >= 1,
                "У sec.DataProtectionKey жодного ключа: кільце лягло в профіль облікового запису, "
                + "і під сервісною обліковкою без профілю воно ефемерне.");
            Assert.True(after >= before, "Кількість ключів не може зменшуватися.");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-32")]
    public async Task Ознака_застосунку_стала_а_сховище_ключів_це_база()
    {
        // ⛔ Структурна перевірка, і названа так навмисно. Вона стоїть тут не
        // замість поведінкової, а поруч із нею: у межах одного процесу content
        // root в обох хостів однаковий, тому «cookie перейшла» нічого не каже
        // про `SetApplicationName`. На двох машинах каже — і платить за це
        // мовчазним виходом усіх користувачів.
        using var app = new EcrApiFactory(sql);
        using var scope = app.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<DataProtectionOptions>>().Value;
        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        Assert.Equal("Ecr", options.ApplicationDiscriminator);

        // ⚠ Друга половина твердження, без якої перша нічого не варта: типова
        // ознака — це шлях, за яким запущено процес. Служба з `C:\Program
        // Files\ECR` і той самий код, піднятий із чекауту, мають РІЗНУ типову
        // ознаку й спільною таблицею ключів не рятуються.
        Assert.NotEqual(environment.ContentRootPath, options.ApplicationDiscriminator);

        var keyManagement = scope.ServiceProvider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        Assert.IsType<EntityFrameworkCoreXmlRepository<EcrDbContext>>(keyManagement.XmlRepository);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.9")]
    public async Task Health_db_називає_незахищені_ключі_коли_сертифіката_немає()
    {
        // ⛔ `D14-08`: тимчасове рішення не має права стати невидимим постійним.
        // У тестовому стенді сертифіката немає — отже режим саме той, про який
        // директива каже «і з попередженням».
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var cookie = await SignInAsync(app, name).ConfigureAwait(true);

        using var client = app.CreateClient(NoCookieJar);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/db", UriKind.Relative));
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));

        var limitations = json.RootElement.GetProperty("checks")
            .EnumerateArray()
            .Single(c => string.Equals(c.GetProperty("name").GetString(), "db", StringComparison.Ordinal))
            .GetProperty("data")
            .GetProperty("limitations")
            .EnumerateArray()
            .Select(v => v.GetString() ?? string.Empty)
            .ToList();

        Assert.Contains(
            limitations,
            text => text.Contains("sec.DataProtectionKey", StringComparison.Ordinal));
    }

    /// <summary>Локальний користувач із відомим паролем у спільній базі.</summary>
    private async Task<string> ArrangeLocalUserAsync()
    {
        var name = $"dpk_{Guid.NewGuid():N}"[..20];

        await using var db = CreateContext();

        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));

        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }

    /// <summary>Входить і віддає cookie сеансу рядком — так, як її носить браузер.</summary>
    private static async Task<string> SignInAsync(EcrApiFactory app, string userName)
    {
        using var client = app.CreateClient(NoCookieJar);

        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        // ⚠ Береться саме заголовок відповіді: інший спосіб дістати cookie
        // означав би дістати її з контейнера першого хоста, тобто з місця,
        // якого в другого інстанса немає.
        var setCookie = login.Headers.GetValues("Set-Cookie").First();
        return setCookie.Split(';')[0];
    }

    private static async Task<HttpResponseMessage> GetMeAsync(EcrApiFactory app, string cookie)
    {
        using var client = app.CreateClient(NoCookieJar);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/me", UriKind.Relative));
        request.Headers.Add("Cookie", cookie);

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);
}
