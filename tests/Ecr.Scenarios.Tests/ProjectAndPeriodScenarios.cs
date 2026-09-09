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

    /// <summary>
    /// T6/#36. <c>PeriodKind.Custom</c> — раніше недосяжний через API:
    /// обробник створення не приймав кількості періодів узагалі, а календар
    /// (<c>GET …/periods</c>) завжди рахував customCount як <c>0</c>, тобто
    /// відмовляв <c>ECR-PRD-4224</c> для БУДЬ-ЯКОГО Custom-проєкту.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "T6-36")]
    public async Task Custom_періодичність_будує_календар_із_заданою_кількістю_періодів()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "T636", ["Project.Manage", "Document.View", "Template.Edit"]);

        var projectId = await CreateProjectAsync(
            admin.Client, "T636", "Asia/Almaty", periodKind: "Custom", customPeriodCount: 6);

        var periods = await admin.Client.GetAsync(new Uri($"/api/v1/projects/{projectId}/periods", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, periods.StatusCode);

        var calendar = await periods.Content.ReadFromJsonAsync<JsonElement>();
        var items = calendar.GetProperty("periods").EnumerateArray().ToList();

        // Шість періодів, послідовно занумерованих 1..6: раніше `Custom` не
        // проходив узагалі (0 доступних раніше `customCount` завжди давав
        // `ECR-PRD-4224` на першому ж `GET …/periods`). Точне покриття року
        // датами перевіряють швидші доменні тести (`SequenceRangeTests`,
        // `BuildPeriodCalendarTests`) — тут важливо, що ланцюжок
        // API → домен → база довозить саме те число, яке ввів користувач.
        Assert.Equal(6, items.Count);
        Assert.Equal(
            [1, 2, 3, 4, 5, 6],
            items.Select(p => p.GetProperty("sequence").GetInt32()).Order());
    }

    /// <summary>
    /// T6/#36 — D-134. Кількість, що НЕ ділить рік нарівно, відхиляється при
    /// створенні, а не мовчки дає зламаний календар пізніше.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "T6-36")]
    public async Task D_134_Custom_кількість_5_відхиляється_при_створенні()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "T636bad", ["Project.Manage", "Document.View", "Template.Edit"]);

        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(admin.Client, "T636bad");
        var policyId = await FirstPeriodPolicyIdAsync(admin.Client);

        var create = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/projects", UriKind.Relative),
            new
            {
                code = $"T636bad_{Guid.NewGuid():N}"[..20],
                nameL10n = new Dictionary<string, string> { ["en"] = "T636bad project" },
                timeZoneId = "Asia/Almaty",
                periodKind = "Custom",
                year = DateTime.UtcNow.Year,
                templateVersionId = versionId,
                periodPolicyId = policyId,
                customPeriodCount = 5,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ECR-PRD-4224", body.GetProperty("errorCode").GetString());
    }

    /// <summary>
    /// T6/#37. CRUD політик періодів: до цього обробника завести чи змінити
    /// політику можна було лише сідингом або рукою DBA.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "T6-37")]
    public async Task Політика_періодів_створюється_і_редагується()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "T637", ["Project.Manage"]);

        var code = $"T637_{Guid.NewGuid():N}"[..20];
        var create = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/projects/period-policies", UriKind.Relative),
            new { code, openOffsetDays = 0, graceOffsetDays = 15, hardCloseOffsetDays = 45, yearGraceOffsetDays = 45 });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var policyId = created.GetProperty("id").GetInt32();

        var update = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/projects/period-policies/{policyId}", UriKind.Relative),
            new { openOffsetDays = 0, graceOffsetDays = 20, hardCloseOffsetDays = 90, yearGraceOffsetDays = 120 });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(90, updated.GetProperty("hardCloseOffsetDays").GetInt32());
        Assert.Equal(120, updated.GetProperty("yearGraceOffsetDays").GetInt32());

        // D-134: грейс довший за жорстке закриття — «неможливе значення».
        var invalid = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/projects/period-policies/{policyId}", UriKind.Relative),
            new { openOffsetDays = 0, graceOffsetDays = 100, hardCloseOffsetDays = 45, yearGraceOffsetDays = 45 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var invalidBody = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ECR-PRD-4225", invalidBody.GetProperty("errorCode").GetString());
    }

    /// <summary>
    /// T6/#52. Пояс майданчика змінюється, поки жоден період не відкрився, і
    /// відмовляється, щойно перший період вийшов зі <c>Scheduled</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "T6-52")]
    public async Task D_134_Зміна_поясу_дозволена_в_чернетці_і_заборонена_після_активації()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "T652", ["Project.Manage", "Document.View", "Template.Edit"]);

        var projectId = await CreateProjectAsync(admin.Client, "T652", "Asia/Almaty");

        var changeInDraft = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/projects/{projectId}/timezone", UriKind.Relative),
            new { timeZoneId = "Asia/Aqtau" });
        Assert.Equal(HttpStatusCode.NoContent, changeInDraft.StatusCode);

        admin = await ActivateProjectAsync(admin, projectId);

        var changeAfterActivation = await admin.Client.PutAsJsonAsync(
            new Uri($"/api/v1/projects/{projectId}/timezone", UriKind.Relative),
            new { timeZoneId = "UTC" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, changeAfterActivation.StatusCode);
        var body = await changeAfterActivation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ECR-PRD-0409", body.GetProperty("errorCode").GetString());
    }

    /// <summary>Перша політика періодів, доступна для вибору (seed завжди має ECR-Standard).</summary>
    private static async Task<int> FirstPeriodPolicyIdAsync(HttpClient client)
    {
        var policiesResponse = await client.GetAsync(new Uri("/api/v1/projects/period-policies", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, policiesResponse.StatusCode);
        var policies = await policiesResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(policies.GetArrayLength() > 0, "seed не завів жодної doc.PeriodPolicy — ECR-Standard відсутня");
        return policies[0].GetProperty("id").GetInt32();
    }

    /// <summary>Створює проєкт із IANA-поясом і повертає його ідентифікатор.</summary>
    internal static async Task<int> CreateProjectAsync(
        HttpClient client, string prefix, string timeZoneId,
        string periodKind = "Monthly", int? customPeriodCount = null)
    {
        var policyId = await FirstPeriodPolicyIdAsync(client);

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
                periodKind,
                year = DateTime.UtcNow.Year,
                templateVersionId = versionId,
                periodPolicyId = policyId,
                customPeriodCount,
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
