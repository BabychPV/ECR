using System.Text.Json;
using Ecr.Application.PublicApi;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Межа розкриття анонімного екрана входу (<c>BE-07</c>, рішення <c>D15-14</c>).
/// </summary>
/// <remarks>
/// ⛔ Це не «тести ендпоінта». Ендпоінт тривіальний; небезпечна в ньому рівно
/// одна властивість — ВІН АНОНІМНИЙ. Усе, що він віддає, читає кожен, хто
/// дістався порту, без пароля, без сліду в аудиті й без обмеження частоти
/// (див. <c>Обмежувач_частоти_цього_маршруту_НЕ_покриває</c> нижче). Тому
/// перевіряється не «чи є поле», а «чи не з'явилося зайвого» — тест форми
/// відповіді, який падає, коли майбутня правка допише сюди зручне поле.
/// </remarks>
[Collection("SqlServer")]
public sealed class PublicBootstrapTests(SqlServerFixture sql)
{
    private static readonly Uri Route = new("/api/v1/public/bootstrap", UriKind.Relative);

    /// <summary>
    /// Імена полів, яких у цій відповіді бути не може — ні зараз, ні потім.
    /// </summary>
    /// <remarks>
    /// ⚠ Порівняння за ПІДРЯДКОМ і без урахування регістру: нове поле назвуть
    /// <c>remainingAttempts</c>, <c>lockoutMinutes</c> або <c>passwordPolicy</c>,
    /// а не дослівно одним зі слів переліку. Сторож на точний збіг пропустив би
    /// кожен із цих трьох.
    /// </remarks>
    private static readonly string[] ForbiddenFragments =
    [
        "user",      // userName, users, userCount — будь-яке називання облікових записів
        "login",     // logins, lastLogin
        "attempt",   // remainingAttempts, failedAttempts
        "polic",     // passwordPolicy, policy (укр./англ. форми)
        "lock",      // lockout, lockedUntil
        "password",
        "domain",    // ім'я домену — внутрішня деталь розгортання
        "host",
        "machine",
        "server",
        "connection",
        "path",      // шлях логів, шлях до бази
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Анонімний_запит_віддає_200_і_не_видає_жодної_кукі()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Очікували 200 без автентифікації, отримали {(int)response.StatusCode}. {app.ErrorsText}");

        // ⛔ Жодної `Set-Cookie`. Анонімний ендпоінт, який видає cookie, — це
        // сеанс, створений тим, хто себе не назвав: далі його можна лише
        // приймати за когось або ігнорувати, і обидва варіанти гірші за
        // відсутність cookie.
        Assert.DoesNotContain(
            response.Headers,
            header => string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відповідь_не_містить_жодного_поля_про_логіни_спроби_чи_політику()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(Route));

        var offenders = Names(document.RootElement)
            .Where(name => ForbiddenFragments.Any(
                fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // ⚠ Перелік у повідомленні, а не `Assert.Empty`: сторож або мовчить,
        // або має назвати КОЖНЕ поле — інакше той, хто його зачепив, побачить
        // одне ім'я з трьох і полагодить третину.
        Assert.True(
            offenders.Count == 0,
            "Анонімна відповідь називає те, чого називати не має:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відповідь_однакова_для_наявного_і_неіснуючого_логіна()
    {
        // ⛔ ГОЛОВНИЙ тест пункту. «Лишилось 2 спроби» для існуючого логіна
        // проти «лишилось 2 спроби» для неіснуючого — це оракул перебору
        // логінів (`D15-14`). Тут перевіряється сильніше твердження: відповідь
        // не змінюється, ЩО Б клієнт про логін не повідомив.
        //
        // ⚠ Ім'я підкладається рядком запиту, хоча дія його не оголошує — саме
        // тому це доказ, а не тавтологія. Той, хто завтра додасть
        // `[FromQuery] string? userName` і гілку «а для цього покажемо більше»,
        // зробить цю пару відповідей різною і побачить червоне.
        //
        // ⚠ `svc-integration` існує в КОЖНІЙ розгорнутій базі: його заводить
        // `09-seed.sql` як автора змін від матеріалізації AF (`D-118`). Брати
        // користувача, створеного сусіднім тестом, тут не можна — перевірка
        // залежала б від порядку прогону в спільній базі, а не від коду.
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var existing = await client.GetStringAsync(
            new Uri("/api/v1/public/bootstrap?userName=svc-integration", UriKind.Relative));

        var absent = await client.GetStringAsync(
            new Uri("/api/v1/public/bootstrap?userName=zzz-not-a-user-9f3c", UriKind.Relative));

        Assert.Equal(existing, absent);

        // ⚠ І та сама відповідь, що й зовсім без параметра: інакше «однакові між
        // собою» могло б означати «обидві однаково спотворені».
        Assert.Equal(await client.GetStringAsync(Route), existing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прапорці_входу_описують_схеми_які_справді_зареєстровані()
    {
        // ⚠ Фікстура піднімає застосунок із `ECR_Auth__EnableNegotiate=false`
        // (`EcrApiFactory`), тобто доменний вхід у ЦЬОМУ хості неможливий —
        // і відповідь мусить це визнавати. Прапорець-константа `true` (найгірша
        // з ймовірних реалізацій: «кнопка ж усе одно є») падає саме тут.
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(Route));
        var root = document.RootElement;

        Assert.False(
            root.GetProperty("windowsSignInEnabled").GetBoolean(),
            "Negotiate у цьому хості не зареєстровано, а відповідь обіцяє доменний вхід.");

        Assert.True(
            root.GetProperty("localSignInEnabled").GetBoolean(),
            "Схема cookie зареєстрована завжди — без неї не працює жоден вхід.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Мови_беруться_з_реєстру_і_несуть_лише_три_поля()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(Route));

        var languages = document.RootElement.GetProperty("languages").EnumerateArray().ToList();

        // Реєстр сіду — `en`/`ru`/`kz`; порожній перелік означав би, що екран
        // входу не має з чого зібрати перемикач.
        Assert.NotEmpty(languages);
        Assert.Contains(languages, item => item.GetProperty("code").GetString() == "en");
        Assert.Contains(languages, item => item.GetProperty("isDefault").GetBoolean());

        // ⛔ Рівно три поля. Реєстр мов — таблиця з правами на запис, датами й
        // порядковим номером; віддати її «як є» означало б показати анонімові
        // внутрішню будову таблиці замість переліку мов.
        var extra = languages
            .SelectMany(item => item.EnumerateObject().Select(property => property.Name))
            .Where(name => name is not ("code" or "nameNative" or "isDefault"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(extra);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData("1.0.0+9f3c1abdeadbeef", "1.0.0")]
    [InlineData("2.4.1.7+build.42", "2.4.1.7")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.2.0-rc.1+sha.abc", "1.2.0")]
    [InlineData("release/PK2-main", GetPublicBootstrapHandler.UnknownVersion)]
    [InlineData("", GetPublicBootstrapHandler.UnknownVersion)]
    [InlineData(null, GetPublicBootstrapHandler.UnknownVersion)]
    public void Версія_віддається_без_метаданих_збірки(string? informational, string expected)
    {
        // ⛔ Хеш коміту й ім'я гілки — інвентаризація майданчика, а не версія.
        // Перевіряється БІЛИЙ список (числовий префікс), тому тест ловить і ті
        // форми метаданих, яких я не передбачив.
        Assert.Equal(expected, GetPublicBootstrapHandler.MarketingVersion(informational));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Обмежувач_частоти_цього_маршруту_НЕ_покриває()
    {
        // ⛔ Тест стверджує НЕПРИЄМНУ правду, а не бажане. Директива №15 у
        // пункті BE-07 обіцяє «обмежувач частоти покриває маршрут» — це
        // неправда: `LoginRateLimiting` розділяє за префіксом
        // `/api/v1/login` і всьому іншому видає `GetNoLimiter`.
        //
        // ⚠ Чому це записано тестом, а не рядком у звіті: рядок у звіті ніхто
        // не перечитає, а тест почервоніє тієї миті, коли префікс змінять, — і
        // тоді цей коментар прочитають разом із ним. Розширення межі на цей
        // маршрут вимагає правки `LoginRateLimiting.cs`, яка зараз у польоті в
        // іншій гілці (#368), тому робиться окремо і після неї.
        //
        // ⚠ Ризик від непокритого маршруту тут невеликий і названий: відповідь
        // не має жодного PBKDF2 (саме він робить `login/local` дорогим), а її
        // єдине звернення до бази — читання реєстру мов через кешований
        // каталог. Це не привід лишати так назавжди, це привід не змішувати
        // дві зміни в одному PR.
        Assert.False(
            "/api/v1/public/bootstrap".StartsWith(
                Ecr.Api.Security.LoginRateLimiting.LoginPathPrefix, StringComparison.OrdinalIgnoreCase),
            "Префікс обмежувача тепер накриває /api/v1/public — перепиши цей тест і прибери застереження.");
    }

    /// <summary>Імена всіх властивостей JSON, на будь-якій глибині.</summary>
    private static IEnumerable<string> Names(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;

                    foreach (var nested in Names(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Names(item))
                    {
                        yield return nested;
                    }
                }

                break;

            default:
                break;
        }
    }
}
