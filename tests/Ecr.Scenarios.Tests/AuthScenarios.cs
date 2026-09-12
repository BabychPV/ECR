using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>§6.1 директиви — розгортання і вхід: S-01, S-02.</summary>
[Collection("SqlServer")]
public sealed class AuthScenarios(SqlServerFixture sql)
{
    /// <summary>
    /// S-01. Чиста база, схема <c>Validate</c>, bootstrap-адміністратор
    /// входить і змінює одноразовий пароль.
    /// </summary>
    /// <remarks>
    /// ⚠ Bootstrap-обліковий запис у системі рівно один (`ФВ-6.18`, унікальний
    /// індекс), а всі 28 сценаріїв цієї збірки ділять ОДНУ базу
    /// (<see cref="SqlServerFixture"/> перестворює її раз на збірку, не на
    /// тест). Тому <see cref="Provisioning.BootstrapAdministratorAsync"/>
    /// спершу доводить bootstrap до автентифікованого стану, який би сценарій
    /// не зайшов першим — а ЦЕЙ сценарій одразу після того сам, явно і без
    /// жодної умови, виконує послідовність «вхід → зміна пароля → повторний
    /// вхід новим паролем» і перевіряє КОЖЕН крок конкретним кодом відповіді.
    /// Зміна пароля (`POST /auth/change-password`) доступна незалежно від
    /// того, чи стоїть `MustChangePassword` (`SecurityController.ChangePassword`),
    /// тому цей доказ дійсний для bootstrap незалежно від порядку прогону
    /// інших сценаріїв.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-01")]
    public async Task Bootstrap_адміністратор_входить_і_змінює_одноразовий_пароль()
    {
        using var app = new EcrApiFactory(sql);

        // Доводить bootstrap до автентифікованого стану (перший вхід, і за
        // потреби — обов'язкова зміна одноразового пароля, ФВ-6.18).
        var client = await Provisioning.BootstrapAdministratorAsync(app);
        var currentPassword = Provisioning.CurrentBootstrapPassword();

        // Доказ 1: bootstrap автентифікований і бачить власний профіль — 200.
        var me = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // Доказ 2: зміна пароля — явний виклик, явний код.
        var newPassword = $"S01-{Guid.NewGuid():N}Aa1!";
        var change = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/change-password", UriKind.Relative),
            new { currentPassword, newPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // Доказ 3: повторний вхід НОВИМ паролем — 200. Стара cookie після
        // зміни пароля недійсна (SecurityStamp, ФВ-6.7), тому клієнт свіжий.
        using var relogin = app.CreateClient();
        var reloginResponse = await relogin.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = "bootstrap", password = newPassword });
        Assert.Equal(HttpStatusCode.OK, reloginResponse.StatusCode);

        // Наступні сценарії цього прогону мають знати чинний пароль bootstrap.
        Provisioning.AdvanceBootstrapPassword(newPassword);
    }

    /// <summary>
    /// S-02. Кожне право, яким гейтоване меню, існує в seed.
    /// </summary>
    /// <remarks>
    /// ⚠ Статична перевірка файл-проти-файла (директива §6.1): звіряє
    /// перелік прав, якими гейтовані пункти меню
    /// (<c>src/Ecr.Web/src/app/routes.ts</c>), з переліком прав у
    /// seed (<c>src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql</c>).
    /// Це читання файлів, а не бізнес-логіка — дозволено директивою явно.
    ///
    /// ⛔ Джерело — <c>routes.ts</c>, а НЕ <c>AppLayout.tsx</c> (навігаційна
    /// архітектура, PR 1/8, Q-276). До цієї картки навбар тримав власний
    /// масив `Items` із рядковими `permission: '...'` прямо в
    /// `AppLayout.tsx` — сценарій читав саме той текст. Тепер навбар
    /// (`navRoutes`) і типізований реєстр маршрутів — одне джерело, і
    /// рядковий літерал `permission: '...'` живе РІВНО в `routes.ts`
    /// (`handle.permission`). Читати й далі `AppLayout.tsx` означало б, що
    /// цей сценарій мовчки осліп би (порожній `menuPermissions`, звідси і
    /// `Assert.NotEmpty` нижче) з тим самим PR, який прибрав звідти
    /// рядкові літерали, — рівно той відмовний режим, від якого застерігає
    /// коментар до `Assert.NotEmpty`.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-02")]
    public void Кожне_право_яким_гейтоване_меню_існує_в_seed()
    {
        var root = RepoRoot();
        var layoutPath = Path.Combine(root, "src", "Ecr.Web", "src", "app", "routes.ts");
        var seedPath = Path.Combine(root, "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql");

        Assert.True(File.Exists(layoutPath), $"Не знайдено {layoutPath}");
        Assert.True(File.Exists(seedPath), $"Не знайдено {seedPath}");

        var layoutText = File.ReadAllText(layoutPath);
        var menuPermissions = Regex.Matches(layoutText, @"permission:\s*'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(menuPermissions);

        var seedText = File.ReadAllText(seedPath);
        var blockStart = seedText.IndexOf("MERGE sec.Permission AS t", StringComparison.Ordinal);
        Assert.True(blockStart >= 0, $"У {seedPath} немає блока MERGE sec.Permission — seed змінив форму, звірку треба переглянути.");
        var blockEnd = seedText.IndexOf("\nGO", blockStart, StringComparison.Ordinal);
        var permissionBlock = seedText[blockStart..blockEnd];

        // Кожен рядок каталогу прав — `(N'Code', N'Group', 0-чи-1)`.
        var seedPermissions = Regex.Matches(permissionBlock, @"N'([A-Za-z0-9_.]+)'\s*,\s*N'[A-Za-z]+'\s*,\s*[01]")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(seedPermissions);

        var missing = menuPermissions.Where(p => !seedPermissions.Contains(p)).ToList();

        // ⛔ Це і є доказ сценарію: якщо тут щось є — меню гейтоване правом,
        // якого користувач НІКОЛИ не отримає, бо seed його не заводить.
        // AppLayout.tsx:45 гейтує '/admin/periods' правом 'Period.Manage',
        // якого в 09-seed.sql немає — заведені лише 'Period.Configure' і
        // 'Period.Reopen'. Пункт меню періодів не з'явиться НІКОМУ.
        Assert.True(
            missing.Count == 0,
            "Меню гейтоване правами, яких немає в seed: " + string.Join(", ", missing) +
            $". Перевір {layoutPath} проти {seedPath}.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Корінь репозиторію (Ecr.sln) не знайдено від {AppContext.BaseDirectory}.");
    }
}
