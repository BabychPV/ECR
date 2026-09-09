using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>§6.3 директиви — проєкт, період, доступ: S-10..S-12.</summary>
[Collection("SqlServer")]
public sealed class ProjectAndPeriodScenarios(SqlServerFixture sql)
{
    /// <summary>
    /// S-10. Проєкт із обов'язковим IANA-поясом (<c>Asia/Almaty</c>), активація, період.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-10")]
    public async Task Проєкт_з_IANA_поясом_активація_період()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "S10", ["Project.Manage", "Document.View", "Template.Edit"]);

        var projectId = await CreateProjectAsync(admin.Client, "S10", "Asia/Almaty");
        admin = await ActivateProjectAsync(admin, projectId);

        // Проєкт активний і має календар періодів. Грант Manage вже видано
        // самим створенням проєкту — повторний GrantAsync тут упав би на
        // «роль уже має грант(и)».
        var periods = await admin.Client.GetAsync(new Uri($"/api/v1/projects/{projectId}/periods", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, periods.StatusCode);

        var calendar = await periods.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(calendar.GetProperty("periods").GetArrayLength() > 0, "календар не побудував жодного періоду");
    }

    /// <summary>
    /// S-11. Період стає відкритим без очікування години: <c>GET</c> періодів
    /// віддає <c>Open</c> одразу після активації.
    /// </summary>
    /// <remarks>
    /// ⛔ Сценарій БІЛЬШЕ НЕ ОПИТУЄ і не чекає жодної секунди — і це сама суть
    /// його назви. Раніше він крутив цикл на 20 с у надії, що
    /// <c>PeriodStateJob</c> устигне; ЗАМІР показував, що не встигає й за 90 с,
    /// бо задача йде на ГОДИННОМУ розкладі, а `Period.AdvanceTo` кликала лише
    /// вона. `W8` (директива №09 п.1) зробив перехід частиною самої активації:
    /// відповідь на <c>POST …/activate</c> уже означає, що періоди в належному
    /// стані.
    ///
    /// ⚠ Тому перевірка тепер СИЛЬНІША, а не просто «зелена»: очікування
    /// прибране, і будь-яке повернення до фонового переходу знову зробить її
    /// червоною негайно, а не «іноді».
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-11")]
    public async Task Період_стає_відкритим_без_очікування_години()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "S11", ["Project.Manage", "Document.View", "Template.Edit"]);

        var projectId = await CreateProjectAsync(admin.Client, "S11", "Asia/Almaty");
        admin = await ActivateProjectAsync(admin, projectId);

        // Грант Manage вже видано самим створенням проєкту.
        var response = await admin.Client.GetAsync(
            new Uri($"/api/v1/projects/{projectId}/periods", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var calendar = await response.Content.ReadFromJsonAsync<JsonElement>();
        var periods = calendar.GetProperty("periods").EnumerateArray().ToList();

        Assert.True(
            periods.Exists(p => string.Equals(p.GetProperty("state").GetString(), "Open", StringComparison.Ordinal)),
            $"одразу після активації проєкту {projectId} жоден період не в стані Open: {app.ErrorsText}");

        // ⚠ І поточний період призначений: на нього спирається кожен екран,
        // який відкриває документ «за поточний період». Доти прапорець
        // `isCurrent` не стояв на жодному періоді до першого прогону задачі.
        Assert.True(
            periods.Exists(p => p.GetProperty("isCurrent").GetBoolean()),
            $"жоден період проєкту {projectId} не позначений поточним одразу після активації.");
    }

    /// <summary>
    /// S-12. Правило доступу до періоду діє: оператор без гранта — не бачить
    /// проєкт, з грантом — бачить.
    /// </summary>
    /// <remarks>
    /// ⚠ Замінник для «403 без гранта»: реальна поведінка
    /// <c>GET /api/v1/projects</c> — ФІЛЬТРАЦІЯ, а не відмова
    /// (`ProjectsController.List`: «перелік проєктів, до яких немає доступу,
    /// це вже розвідка структури підприємства», підтверджено
    /// `tools/e2e-stand.ps1`: «Без гранта перелік порожній для всіх»).
    /// Директива в §3.2 прямо наказує довіряти реальній поведінці, а не
    /// очікуванню в тексті. Пряму перевірку 403 на комірці (`CellsController.GetSlice`)
    /// довести не можна: вона вимагає реального <c>TableInstanceId</c>, а
    /// шляху опублікувати версію з таблицями через API немає (S-04..S-09).
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-12")]
    public async Task Правило_доступу_оператор_без_гранта_і_з_грантом()
    {
        using var app = new EcrApiFactory(sql);
        var owner = await Provisioning.AdministratorAsync(app, "S12Owner", ["Project.Manage", "Document.View", "Template.Edit"]);
        var projectId = await CreateProjectAsync(owner.Client, "S12", "Asia/Almaty");

        var operatorAdmin = await Provisioning.AdministratorAsync(app, "S12Op", ["Document.View"]);

        var before = await operatorAdmin.Client.GetAsync(new Uri("/api/v1/projects", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var beforeItems = (await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.DoesNotContain(
            beforeItems.EnumerateArray(),
            p => p.GetProperty("id").GetInt32() == projectId);

        await Provisioning.GrantAsync(app, operatorAdmin.RoleId, "Project", projectId, "Read");
        operatorAdmin = await Provisioning.ReauthenticateAsync(app, operatorAdmin);

        var after = await operatorAdmin.Client.GetAsync(new Uri("/api/v1/projects", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var afterItems = (await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.Contains(
            afterItems.EnumerateArray(),
            p => p.GetProperty("id").GetInt32() == projectId);
    }

    /// <summary>
    /// Творець одразу активує ВЛАСНИЙ щойно створений проєкт, без стороннього
    /// гранта.
    /// </summary>
    /// <remarks>
    /// ⛔ Побічна знахідка при Q-179: до цього фіксу `CreateProjectHandler`
    /// не видавав творцю ЖОДНОГО гранта на щойно створений проєкт —
    /// `Activate` (вимагає `GrantLevel.Manage` на конкретний `projectId`
    /// після Q-179) відмовляв би творцю власного проєкту, доки хтось не
    /// видав би грант окремим кроком. «Якщо є право створити проєкт — є
    /// право ним володіти» (рішення людини).
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-10")]
    public async Task Творець_одразу_активує_власний_проєкт_без_стороннього_гранта()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "S10own", ["Project.Manage", "Document.View", "Template.Edit"]);

        var projectId = await CreateProjectAsync(admin.Client, "S10own", "Asia/Almaty");

        // ⛔ ТІЄЮ САМОЮ сесією, без GrantAsync і без ReauthenticateAsync:
        // грант на власність видає сам CreateProjectHandler.
        var buildCalendar = await admin.Client.GetAsync(
            new Uri($"/api/v1/projects/{projectId}/periods", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, buildCalendar.StatusCode);

        var activate = await admin.Client.PostAsync(
            new Uri($"/api/v1/projects/{projectId}/activate", UriKind.Relative), content: null);
        Assert.Equal(
            HttpStatusCode.NoContent,
            activate.StatusCode);
    }

    /// <summary>Створює проєкт із IANA-поясом і повертає його ідентифікатор.</summary>
    internal static async Task<int> CreateProjectAsync(HttpClient client, string prefix, string timeZoneId)
    {
        var policiesResponse = await client.GetAsync(new Uri("/api/v1/projects/period-policies", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, policiesResponse.StatusCode);
        var policies = await policiesResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(policies.GetArrayLength() > 0, "seed не завів жодної doc.PeriodPolicy — ECR-Standard відсутня");
        var policyId = policies[0].GetProperty("id").GetInt32();

        // ⚠ `TemplateVersionId` типізований як `int?` (опційний, ФВ-1.2), але
        // РЕАЛЬНА поведінка інша: без версії обробник відмовляє з
        // `ECR-TMPL-0404` («Проєкт неможливо створити без версії шаблону»).
        // Довіряємо тому, що робить застосунок (§3.2 директиви), а не типу в
        // контракті, і даємо йому чернеткову версію — так само, як зробив би
        // клієнт, отримавши цю відмову вперше.
        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(client, prefix);

        var code = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var create = await client.PostAsJsonAsync(
            new Uri("/api/v1/projects", UriKind.Relative),
            new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} project" },
                timeZoneId,
                periodKind = "Monthly",
                year = DateTime.UtcNow.Year,
                templateVersionId = versionId,
                periodPolicyId = policyId,
            });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetInt32();
    }

    /// <summary>Активує проєкт — спершу будує календар періодів, потім активує.</summary>
    /// <remarks>
    /// ⚠ Реальна поведінка: <c>POST …/activate</c> відмовляє з
    /// <c>ECR-PRJ-0422</c> («немає періодів»), якщо календар ще не
    /// побудований. Побудова — побічний ефект самого <c>GET …/periods</c>
    /// (`ProjectsController.Periods`: «Календар добудовується перед
    /// читанням... Виклик ідемпотентний»), тому цей крок явно виконується
    /// ПЕРЕД активацією, а не покладається на активацію саму собою.
    ///
    /// ⛔ Q-179 (аудит фази 2, авторизація). `Activate` вимагає грант
    /// `Manage` на КОНКРЕТНИЙ проєкт, не лише глобальне `Project.Manage`.
    /// До побічної знахідки при Q-179 щойно створений проєкт такого гранта
    /// не мав НІ В КОГО, і цей метод видавав його ролі виконавця тут явно.
    /// Після фікса `CreateProjectHandler` сам видає грант творцю тією ж
    /// транзакцією, що й створення (і скидає лише його кешований профіль,
    /// не крутячи `SecurityStamp` — сесія лишається дійсною), тож `admin`
    /// (він же завжди творець у кожному виклику цього методу) уже має
    /// грант і дійсну сесію одразу після `CreateProjectAsync`. Явний
    /// `GrantAsync` тут падав би на власній «роль уже має грант(и)».
    /// </remarks>
    internal static async Task<Provisioning.Administrator> ActivateProjectAsync(
        Provisioning.Administrator admin, int projectId)
    {
        var buildCalendar = await admin.Client.GetAsync(
            new Uri($"/api/v1/projects/{projectId}/periods", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, buildCalendar.StatusCode);

        var activate = await admin.Client.PostAsync(
            new Uri($"/api/v1/projects/{projectId}/activate", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);

        return admin;
    }
}
