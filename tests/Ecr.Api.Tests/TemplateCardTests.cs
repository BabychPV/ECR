// tests/Ecr.Api.Tests/TemplateCardTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Картка шаблону: <c>GET /api/v1/templates/{id}</c>, <c>PUT</c> (назва),
/// <c>POST …/archive</c> і <c>…/restore</c> (директива №15, <c>BE-26</c>).
/// </summary>
/// <remarks>
/// ⛔ Доказ саме на HTTP, а не на обробнику. Відмову «шаблон уже архівований»
/// кидає домен голим <c>DomainException</c>, і в <c>409</c> її перетворює не
/// арм на код, а правило «суфікс <c>-0409</c>»
/// (<c>ExceptionHandlingMiddleware.Map</c>). Обробникового тесту досить, щоб
/// повірити, ніби клієнт бачить конфлікт, — а він бачив би <c>422</c> «дані
/// невірні» там, де вводити нічого.
///
/// ⚠ Усі числа лічильника перевіряються на ДВОХ шаблонах одночасно: лічильник,
/// перевірений на одному, лишається зеленим і тоді, коли він рахує всю базу.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateCardTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 7, 0, 0, DateTimeKind.Utc);

    /// <summary>Права з обробників, а не літералами: розійтися нема з чим.</summary>
    private static string ViewPermission => Ecr.Application.Templates.GetTemplateCardHandler.Permission;

    private static string EditPermission => Ecr.Application.Templates.SetTemplateArchivedHandler.Permission;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Картка_рахує_ЛИШЕ_свої_версії_проєкти_й_документи()
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ №1. Прибрати `versionIds.Contains(...)` у
        // `TemplateVersionStore.FindCardAsync` — і червоним стає рівно цей
        // тест: сусідній шаблон, заведений поруч (а в спільній тестовій базі їх
        // сотні), домішує свої проєкти й документи, і адміністратор бачить
        // «документів 4 812» на шаблоні, де їх два.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewPermission).ConfigureAwait(true);

        var mine = await ArrangeAsync(versions: 2, published: 1, projects: 1, documentsPerProject: 2)
            .ConfigureAwait(true);

        // Сусід із ЗОВСІМ іншими числами: якби фільтр зник, вони потрапили б у
        // відповідь і зламали б кожне твердження нижче.
        _ = await ArrangeAsync(versions: 3, published: 3, projects: 2, documentsPerProject: 5)
            .ConfigureAwait(true);

        var card = await CardAsync(client, mine.TemplateId).ConfigureAwait(true);

        Assert.Equal(mine.Code, card.GetProperty("code").GetString());
        Assert.True(card.GetProperty("isActive").GetBoolean());

        var dependents = card.GetProperty("dependents");
        Assert.Equal(2, dependents.GetProperty("versions").GetInt32());
        Assert.Equal(1, dependents.GetProperty("publishedVersions").GetInt32());
        Assert.Equal(1, dependents.GetProperty("projects").GetInt32());
        Assert.Equal(2, dependents.GetProperty("documents").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Шаблон_без_залежних_дає_нулі_а_не_порожнечу()
    {
        // ⚠ Нуль — це відповідь, а не відсутність відповіді. Картка, яка на
        // порожньому шаблоні не несе `dependents`, змусила б клієнт вигадати
        // власне «—», і різниця між «нікому не потрібен» і «ще не порахували»
        // зникла б рівно там, де рішення ухвалює людина.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewPermission).ConfigureAwait(true);

        var empty = await ArrangeAsync(versions: 0, published: 0, projects: 0, documentsPerProject: 0)
            .ConfigureAwait(true);

        var dependents = (await CardAsync(client, empty.TemplateId).ConfigureAwait(true))
            .GetProperty("dependents");

        Assert.Equal(0, dependents.GetProperty("versions").GetInt32());
        Assert.Equal(0, dependents.GetProperty("publishedVersions").GetInt32());
        Assert.Equal(0, dependents.GetProperty("projects").GetInt32());
        Assert.Equal(0, dependents.GetProperty("documents").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Архівування_лишає_документи_на_місці_а_повторне_дає_409()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewPermission, EditPermission).ConfigureAwait(true);

        var stand = await ArrangeAsync(versions: 1, published: 1, projects: 1, documentsPerProject: 3)
            .ConfigureAwait(true);

        var archived = await PostAsync(client, stand.TemplateId, "archive").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var card = await JsonAsync(archived).ConfigureAwait(true);
        Assert.False(card.GetProperty("isActive").GetBoolean());

        // ⛔ Наявні документи не зачіпаються — це і є зміст архівування
        // (рішення людини на `Q15-05`: документ назавжди на своїй версії).
        // Лічильник у ТІЙ САМІЙ відповіді це й показує.
        Assert.Equal(3, card.GetProperty("dependents").GetProperty("documents").GetInt32());
        Assert.Equal(3, await DocumentCountAsync(stand.ProjectIds[0]).ConfigureAwait(true));

        // Подія в журналі безпеки — з лічильником, бо через рік питання буде не
        // «хто», а «скільки роботи це зачепило».
        var details = await SecurityEventAsync(
            Ecr.Application.Templates.SetTemplateArchivedHandler.ArchivedEventType,
            stand.TemplateId).ConfigureAwait(true);

        Assert.NotNull(details);
        Assert.Contains("\"documents\":3", details, StringComparison.Ordinal);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ №2 (частина перша). Прибрати `if (!IsActive)` у
        // `Template.Archive` — і повторне архівування відповідає `200`, а в
        // журналі безпеки з'являється другий запис про подію, якої не було.
        var again = await PostAsync(client, stand.TemplateId, "archive").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("ECR-TMPL-0409", await ErrorCodeAsync(again).ConfigureAwait(true));

        // Повернення в обіг — щоб архівування не було дверима в один бік.
        var restored = await PostAsync(client, stand.TemplateId, "restore").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.True((await JsonAsync(restored).ConfigureAwait(true)).GetProperty("isActive").GetBoolean());

        // І назад: шаблон в обігу повертати нема звідки.
        var restoredTwice = await PostAsync(client, stand.TemplateId, "restore").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, restoredTwice.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Перейменування_міняє_назву_і_не_чіпає_коду()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewPermission, EditPermission).ConfigureAwait(true);

        var stand = await ArrangeAsync(versions: 1, published: 0, projects: 0, documentsPerProject: 0)
            .ConfigureAwait(true);

        // ⚠ У тілі є `code` — і сервер мусить його ЗІГНОРУВАТИ, бо в запиті
        // такого поля немає взагалі. Інакше бізнес-ключ, на який посилаються
        // проєкти, змінювався б формою редагування назви.
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/templates/{stand.TemplateId}", UriKind.Relative),
            new { nameL10n = new Dictionary<string, string> { ["en"] = "Renamed" }, code = "HIJACKED" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var card = await JsonAsync(response).ConfigureAwait(true);
        Assert.Equal("Renamed", card.GetProperty("nameL10n").GetProperty("values").GetProperty("en").GetString());
        Assert.Equal(stand.Code, card.GetProperty("code").GetString());

        // Порожня назва — це відсутність назви, і вона відхиляється доменом.
        var empty = await client.PutAsJsonAsync(
            new Uri($"/api/v1/templates/{stand.TemplateId}", UriKind.Relative),
            new { nameL10n = new Dictionary<string, string> { ["en"] = "   " } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("ECR-TMPL-0422", await ErrorCodeAsync(empty).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Без_права_Template_Edit_архівування_дає_403_і_нічого_не_міняє()
    {
        // ⚠ Користувач із правом ПЕРЕГЛЯДУ, а не безправний: інакше тест
        // доводив би лише те, що маршрут закритий для стороннього, і не
        // розрізняв би `Template.View` та `Template.Edit`.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewPermission).ConfigureAwait(true);

        var stand = await ArrangeAsync(versions: 1, published: 0, projects: 0, documentsPerProject: 0)
            .ConfigureAwait(true);

        var response = await PostAsync(client, stand.TemplateId, "archive").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Стан не змінився. Без цього твердження тест лишався б зеленим і на
        // системі, яка спершу архівує, а потім згадує перевірити право.
        Assert.True((await CardAsync(client, stand.TemplateId).ConfigureAwait(true))
            .GetProperty("isActive").GetBoolean());
    }

    /// <summary>Що саме заведено для одного тесту.</summary>
    private sealed record Stand(int TemplateId, string Code, IReadOnlyList<int> ProjectIds);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, int templateId, string action)
        => client.PostAsync(
            new Uri($"/api/v1/templates/{templateId}/{action}", UriKind.Relative), content: null);

    private static async Task<JsonElement> CardAsync(HttpClient client, int templateId)
    {
        var response = await client
            .GetAsync(new Uri($"/api/v1/templates/{templateId}", UriKind.Relative))
            .ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await JsonAsync(response).ConfigureAwait(false);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))
            .RootElement;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
        => (await JsonAsync(response).ConfigureAwait(false)).GetProperty("errorCode").GetString();

    /// <summary>Скільки документів справді лежить у проєкті — з бази, не з відповіді.</summary>
    private async Task<int> DocumentCountAsync(int projectId)
    {
        await using var db = new EcrDbContext(Options());

        return await db.Documents.AsNoTracking().CountAsync(d => d.ProjectId == projectId)
            .ConfigureAwait(false);
    }

    /// <summary><c>DetailsJson</c> події журналу безпеки; <c>null</c> — події немає.</summary>
    private async Task<string?> SecurityEventAsync(string eventType, int templateId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DetailsJson FROM aud.SecurityEvent "
            + "WHERE EventType = @e AND DetailsJson LIKE @like;";
        command.Parameters.AddWithValue("@e", eventType);
        command.Parameters.AddWithValue(
            "@like",
            $"%\"templateId\":{templateId.ToString(CultureInfo.InvariantCulture)},%");

        return await command.ExecuteScalarAsync().ConfigureAwait(false) as string;
    }

    /// <summary>
    /// Шаблон із заданим числом версій, проєктів і документів у кожному.
    /// </summary>
    /// <remarks>
    /// ⚠ Усе заводиться прямо в базі, а не через API: ендпоінтів для
    /// «опублікувати версію, завести проєкт і три документи» тут довелося б
    /// пройти шість, і тест доводив би їх, а не лічильник.
    /// </remarks>
    private async Task<Stand> ArrangeAsync(int versions, int published, int projects, int documentsPerProject)
    {
        await using var db = new EcrDbContext(Options());
        var tag = $"{Guid.NewGuid():N}"[..10];

        var template = new Template(
            EcrCode.Create($"T{tag}"), Name("Template card"), createdByUserId: 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var versionIds = new List<int>();
        for (var i = 0; i < versions; i++)
        {
            var version = new TemplateVersion(
                template.Id, $"1.0.0.{i.ToString(CultureInfo.InvariantCulture)}", createdByUserId: 1, Now);

            if (i < published)
            {
                version.Publish(publishedByUserId: 1, Now);
            }

            db.TemplateVersions.Add(version);
            await db.SaveChangesAsync().ConfigureAwait(false);
            versionIds.Add(version.Id);
        }

        var projectIds = new List<int>();
        for (var i = 0; i < projects; i++)
        {
            var project = new Project(
                EcrCode.Create($"P{tag}{i.ToString(CultureInfo.InvariantCulture)}"), Name("Template card"),
                new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                templateVersionId: versionIds[i % versionIds.Count], PeriodKind.Monthly,
                periodPolicyId: 1, "Asia/Almaty");

            db.Projects.Add(project);
            await db.SaveChangesAsync().ConfigureAwait(false);
            projectIds.Add(project.Id);

            for (var d = 0; d < documentsPerProject; d++)
            {
                db.Documents.Add(new Document(
                    project.Id,
                    $"{tag}-{i.ToString(CultureInfo.InvariantCulture)}-{d.ToString(CultureInfo.InvariantCulture)}",
                    createdByUserId: 1,
                    Now));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new Stand(template.Id, template.Code, projectIds);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Клієнт із чинним сеансом і названими правами.</summary>
    private Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
        => SystemHealthControllerTests.SignedInAsync(sql, app, permissions);
}
