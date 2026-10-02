// tests/Ecr.Api.Tests/NegativePathSweep2Tests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Прохід по відмовах 2 (2.10): нові ендпоінти після <c>b8576884</c> — патч презентації (порядок
/// рядків, підписи), правила сповіщень, проба подій джерела, опис і перемикання довідників.
/// </summary>
/// <remarks>
/// ⛔ Кожен випадок нижче давав 500 (або 200 із зіпсованою версією) на <c>dev/integration</c>
/// <c>d649921</c>: зонд розіслав ~2,1 тис. некоректних запитів по цих операціях під повними правами.
/// Тест перевіряє статус, код і ключ: «не 500» без коду довело б лише, що впало інакше.
/// </remarks>
[Collection("SqlServer")]
public sealed class NegativePathSweep2Tests(SqlServerFixture sql)
{
    private const string Password = "Api-Negative-Path-2-2026!";

    [Theory]
    [InlineData("LabelL10n", "abc")]
    [InlineData("LabelL10n", "[]")]
    [InlineData("LabelL10n", "1")]
    [InlineData("LabelL10n", null)]
    [InlineData("LabelL10n", """{"en":"a","EN":"b"}""")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep-2")]
    public async Task Підпис_рядка_не_JSON_обєктом_дає_422_і_не_ламає_структуру_версії(string field, string? value)
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        using var response = await PatchAsync(client, stand, new[] { new { entityType = "RowDef", entityId = stand.RowDefId, field, value } })
            .ConfigureAwait(true);
        var problem = await AssertRefusalAsync(
            app, response, HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422", "err.ECR-TMPL-0422.presentationValueInvalid").ConfigureAwait(true);
        Assert.Equal("RowDef", problem.GetProperty("entityType").GetString());
        Assert.Equal(field, problem.GetProperty("field").GetString());

        // ⛔ Головне: раніше "abc" зберігався (200), і далі КОЖНЕ читання структури версії падало
        // 500 на розборі підпису — версію не відкривав ні конструктор, ні документ.
        using var structure = await client.GetAsync(
            new Uri($"/api/v1/template-versions/{stand.TemplateVersionId}/structure", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(structure.IsSuccessStatusCode, $"{(int)structure.StatusCode} {await structure.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        // Межа не зсунута: законний підпис зберігається.
        using var ok = await PatchAsync(client, stand, new[] { new { entityType = "RowDef", entityId = stand.RowDefId, field, value = (string?)"""{"en":"Renamed"}""" } })
            .ConfigureAwait(true);
        Assert.True(ok.IsSuccessStatusCode, $"{(int)ok.StatusCode} {await ok.Content.ReadAsStringAsync().ConfigureAwait(true)}");
    }

    [Theory]
    [InlineData("HeaderL10n", null)]
    [InlineData("HeaderL10n", "abc")]
    [InlineData("IsHidden", null)]
    [InlineData("IsHidden", "abc")]
    [InlineData("IsHidden", "2")]
    [InlineData("StyleId", "abc")]
    [InlineData("StyleId", "99999999999")]
    [InlineData("DisplayFormat", "123456789012345678901234567890123456789012345678901")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep-2")]
    public async Task Значення_колонки_неприйнятної_форми_дає_422_а_не_500(string field, string? value)
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        using var response = await PatchAsync(client, stand, new[] { new { entityType = "ColumnDef", entityId = stand.ColumnDefId, field, value } })
            .ConfigureAwait(true);

        var problem = await AssertRefusalAsync(
            app, response, HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422", "err.ECR-TMPL-0422.presentationValueInvalid").ConfigureAwait(true);
        Assert.Equal(field, problem.GetProperty("field").GetString());
    }

    [Theory]
    [InlineData("IsHidden", "1")]
    [InlineData("IsHidden", "false")]
    [InlineData("StyleId", null)]
    [InlineData("DisplayFormat", "12345678901234567890123456789012345678901234567890")]
    [InlineData("HeaderL10n", """{"en":"Tons","ru":"Тонны"}""")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep-2")]
    public async Task Законне_значення_колонки_зберігається(string field, string? value)
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        using var response = await PatchAsync(client, stand, new[] { new { entityType = "ColumnDef", entityId = stand.ColumnDefId, field, value } })
            .ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("""[{"entityType":null,"entityId":1,"field":"Ordinal","value":"1"}]""")]
    [InlineData("""[{"entityType":"RowDef","entityId":1,"field":null,"value":"1"}]""")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep-2")]
    public async Task Зміна_патча_без_типу_чи_поля_дає_422_patchChangeInvalid(string body)
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PatchAsync(
            new Uri($"/api/v1/template-versions/{stand.TemplateVersionId}/presentation", UriKind.Relative), content).ConfigureAwait(true);

        await AssertRefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422", "err.ECR-TMPL-0422.patchChangeInvalid")
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep-2")]
    public async Task Порожня_клітинка_матриці_правил_дає_422_notificationRuleInvalid()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        using var content = new StringContent("""{"rules":[null]}""", Encoding.UTF8, "application/json");
        using var response = await client.PutAsync(new Uri("/api/v1/notifications/rules", UriKind.Relative), content).ConfigureAwait(true);

        await AssertRefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-REQ-0422", "err.ECR-REQ-0422.notificationRuleInvalid")
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep-2")]
    public async Task Порожній_атрибут_проби_подій_дає_422_probeEventsInvalid()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        using var content = new StringContent(
            """{"template":"T","fromUtc":"2026-01-01T00:00:00Z","toUtc":"2026-01-02T00:00:00Z","maxEvents":10,"attributes":[null]}""",
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            new Uri($"/api/v1/data-sources/{stand.DataSourceId}/probe-events", UriKind.Relative), content).ConfigureAwait(true);

        await AssertRefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-REQ-0422", "err.ECR-REQ-0422.probeEventsInvalid")
            .ConfigureAwait(true);
    }

    [Theory]
    [InlineData("definition", "PUT", """{"fields":[null],"rules":[],"reason":"probe"}""")]
    [InlineData("definition", "PUT", """{"fields":[],"rules":[null],"reason":"probe"}""")]
    [InlineData("definition", "PUT", """{"fields":[],"rules":[],"keys":[null],"reason":"probe"}""")]
    [InlineData("definition/draft", "PUT", """{"fields":[null],"rules":[],"reason":"probe","rowVersion":null}""")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep-2")]
    public async Task Порожній_елемент_опису_довідника_дає_422_definitionItemMissing(string path, string method, string body)
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);
        var registry = await CreateRegistryAsync(client).ConfigureAwait(true);

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri($"/api/v1/registries/{registry}/{path}", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request).ConfigureAwait(true);

        await AssertRefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-REG-0422", "err.ECR-REG-0422.definitionItemMissing")
            .ConfigureAwait(true);
    }

    [Theory]
    [InlineData("""{"registryCodes":[null],"sourceKind":"External","reason":"probe"}""")]
    [InlineData("""{"registryCodes":[" "],"sourceKind":"External","reason":"probe"}""")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep-2")]
    public async Task Порожній_код_у_наборі_перемикання_дає_422_switchCodeEmpty(string body)
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PutAsync(new Uri("/api/v1/registries/source-kind", UriKind.Relative), content).ConfigureAwait(true);

        await AssertRefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-REG-0422", "err.ECR-REG-0422.switchCodeEmpty")
            .ConfigureAwait(true);
    }

    [Theory]
    [InlineData("entries/batch", """{"items":[null]}""", "ECR-REQ-0422", "err.ECR-REQ-0422.batchItemInvalid")]
    [InlineData("keys/check", """{"fieldCodes":[null]}""", "ECR-REG-0422", "err.ECR-REG-0422.keyFieldUnknown")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep-2")]
    public async Task Відмова_на_порожній_елемент_не_лишає_незаповненого_плейсхолдера(
        string path, string body, string code, string messageKey)
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);
        var registry = await CreateRegistryAsync(client).ConfigureAwait(true);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri($"/api/v1/registries/{registry}/{path}", UriKind.Relative), content)
            .ConfigureAwait(true);

        // `AssertRefusalAsync` перевіряє й відсутність `{` у тексті: раніше там стояло «{clientRowId}» / «{fieldCode}».
        await AssertRefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, code, messageKey).ConfigureAwait(true);
    }

    private static Task<HttpResponseMessage> PatchAsync<T>(HttpClient client, Stand stand, T changes)
        => client.PatchAsJsonAsync(
            new Uri($"/api/v1/template-versions/{stand.TemplateVersionId}/presentation", UriKind.Relative), changes);

    private static async Task<string> CreateRegistryAsync(HttpClient client)
    {
        var code = $"NP{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/registries", UriKind.Relative),
            new { code, nameL10n = new Dictionary<string, string> { ["en"] = "Negative path" }, isTemporal = false }).ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return code;
    }

    private static async Task<JsonElement> AssertRefusalAsync(
        EcrApiFactory app, HttpResponseMessage response, HttpStatusCode status, string code, string messageKey)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"очікували {(int)status}, отримали {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, problem.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());

        // Ключ знайдено в каталозі, і кожен плейсхолдер заповнено.
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.DoesNotContain("{", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        return problem;
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>
    /// Документ (його версія шаблону — ціль патча), з'єднання PI і користувач з усіма правами
    /// каталогу та грантом <c>Manage</c> на проєкт документа.
    /// </summary>
    private async Task<Stand> ArrangeAsync()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await using var db = Db();

        var userName = $"np2_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"NP2_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Negative path 2" }));
        db.Roles.Add(role);

        var dataSource = new DataSource(
            EcrCode.Create($"Np{Guid.NewGuid():N}"[..12]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Source" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in await db.Permissions.AsNoTracking().Select(p => p.Id).ToListAsync().ConfigureAwait(false))
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Manage));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(
            document.TemplateVersionId, document.RowDefIds[0], document.ColumnDefIds[0], dataSource.Id, userName);
    }

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed record Stand(int TemplateVersionId, int RowDefId, int ColumnDefId, int DataSourceId, string UserName);
}
