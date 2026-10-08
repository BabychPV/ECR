// tests/Ecr.Api.Tests/LookupRegistryReferenceApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// RC16-2 (P3): колонка <c>Lookup</c> і поле шапки <c>Lookup</c> з неіснуючим (чи неактивним) довідником відмовляються
/// на ЗБЕРЕЖЕННІ (<c>422</c> з ключем, а не мовчки/<c>500</c>) і на ПУБЛІКАЦІЇ версії шаблону.
/// </summary>
/// <remarks>
/// ⚠ У <c>cfg.ColumnDef.LookupRegistryDefId</c> зовнішнього ключа немає (є лише індекс), тож колонка з
/// <c>999999</c> зберігалася й публікувалася мовчки; у <c>cfg.HeaderFieldDef</c> ключ є (<c>FK_HeaderFieldDef_Registry</c>) -
/// там описка давала голий <c>500</c> на 547.
/// Мутації: прибрати <c>RequireKnownLookupRegistryAsync</c> з <c>SaveColumnDefHandler</c>/<c>SaveHeaderFieldDefHandler</c> -
/// червоні випадки збереження; прибрати <c>PublishChecks.CheckLookupRegistries</c> - червоні випадки публікації.
/// </remarks>
[Collection("SqlServer")]
public sealed class LookupRegistryReferenceApiTests(SqlServerFixture sql)
{
    private const int MissingRegistryId = 999_999;

    private static readonly DateTime Now = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонка_Lookup_з_неіснуючим_довідником_422_з_ключем_і_нічого_не_пишеться()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Edit");
        var draft = await ArrangeAsync();

        var response = await PutColumnAsync(client, draft, "LK_MISSING", MissingRegistryId);

        var problem = await RejectedAsync(response);
        Assert.Equal("err.ECR-TMPL-0422.lookupRegistryUnknown", problem.GetProperty("messageKey").GetString());
        Assert.Contains("LK_MISSING", problem.ToString(), StringComparison.Ordinal);
        Assert.Contains("999999", problem.ToString(), StringComparison.Ordinal);

        await using var db = Context();
        Assert.False(await db.ColumnDefs.AnyAsync(c => c.TableDefId == draft.TableId && c.Code == "LK_MISSING"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонка_Lookup_з_наявним_довідником_зберігається()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Edit");
        var draft = await ArrangeAsync();

        var response = await PutColumnAsync(client, draft, "LK_OK", draft.RegistryId);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поле_шапки_Lookup_з_неіснуючим_довідником_422_з_ключем_а_не_500()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Edit");
        var draft = await ArrangeAsync();

        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/header-fields/HF_MISSING", UriKind.Relative),
            new
            {
                labelL10n = new Dictionary<string, string> { ["en"] = "Missing" },
                ordinal = (int?)null,
                dataType = "Lookup",
                isRequired = false,
                lookupRegistryDefId = (int?)MissingRegistryId,
            });

        var problem = await RejectedAsync(response);
        Assert.Equal("err.ECR-TMPL-0422.headerFieldLookupRegistryUnknown", problem.GetProperty("messageKey").GetString());
        Assert.Contains("HF_MISSING", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_відмовляє_коли_колонка_Lookup_вказує_на_неіснуючий_довідник()
    {
        var draft = await ArrangeAsync();
        await using (var db = Context())
        {
            var column = new ColumnDef(draft.TableId, EcrCode.Create("LK_GONE"), Name("LK_GONE"), 5, CellDataType.Lookup);
            column.SetLookup(MissingRegistryId, null);
            db.ColumnDefs.Add(column);
            await db.SaveChangesAsync();
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish");

        var problem = await RejectedAsync(await PublishAsync(client, draft));

        Assert.Equal("err.ECR-TMPL-0422.lookupRegistryUnknown", problem.GetProperty("messageKey").GetString());
        Assert.Contains("LK_GONE", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_відмовляє_коли_довідник_колонки_Lookup_неактивний()
    {
        var draft = await ArrangeAsync();
        await using (var db = Context())
        {
            var column = new ColumnDef(draft.TableId, EcrCode.Create("LK_OFF"), Name("LK_OFF"), 5, CellDataType.Lookup);
            column.SetLookup(draft.RegistryId, null);
            db.ColumnDefs.Add(column);
            await db.SaveChangesAsync();

            await db.RegistryDefs.Where(r => r.Id == draft.RegistryId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, false));
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish");

        var problem = await RejectedAsync(await PublishAsync(client, draft));

        Assert.Equal("err.ECR-TMPL-0422.lookupRegistryUnknown", problem.GetProperty("messageKey").GetString());
        Assert.Contains("LK_OFF", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_відмовляє_коли_довідник_поля_шапки_Lookup_неактивний()
    {
        var draft = await ArrangeAsync();
        await using (var db = Context())
        {
            var field = new HeaderFieldDef(draft.VersionId, EcrCode.Create("HF_OFF"), Name("HF_OFF"), 1, CellDataType.Lookup);
            field.SetLookup(draft.RegistryId);
            db.HeaderFieldDefs.Add(field);
            await db.SaveChangesAsync();

            await db.RegistryDefs.Where(r => r.Id == draft.RegistryId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, false));
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish");

        var problem = await RejectedAsync(await PublishAsync(client, draft));

        Assert.Equal("err.ECR-TMPL-0422.headerFieldLookupRegistryUnknown", problem.GetProperty("messageKey").GetString());
        Assert.Contains("HF_OFF", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_проходить_коли_довідник_колонки_Lookup_активний()
    {
        var draft = await ArrangeAsync();
        await using (var db = Context())
        {
            var column = new ColumnDef(draft.TableId, EcrCode.Create("LK_FINE"), Name("LK_FINE"), 5, CellDataType.Lookup);
            column.SetLookup(draft.RegistryId, null);
            db.ColumnDefs.Add(column);
            await db.SaveChangesAsync();
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish");

        var response = await PublishAsync(client, draft);

        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    // ── Нерозкриття: заборонений довідник відповідає ТАК САМО, як неіснуючий ──────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонка_на_заборонений_довідник_відповідає_так_само_як_на_неіснуючий()
    {
        var draft = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        var (client, userId) = await SignedInWithIdAsync(app);
        await DenyRegistryAsync(userId, draft.RegistryId);

        var denied = await NormalizedAsync(await PutColumnAsync(client, draft, "LK_SAME", draft.RegistryId), draft.RegistryId);
        var missing = await NormalizedAsync(await PutColumnAsync(client, draft, "LK_SAME", MissingRegistryId), MissingRegistryId);

        Assert.StartsWith("422 ", denied, StringComparison.Ordinal);
        Assert.Equal(missing, denied);
        client.Dispose();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поле_шапки_на_заборонений_довідник_відповідає_так_само_як_на_неіснуючий()
    {
        var draft = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        var (client, userId) = await SignedInWithIdAsync(app);
        await DenyRegistryAsync(userId, draft.RegistryId);

        var denied = await NormalizedAsync(await PutHeaderFieldAsync(client, draft, "HF_SAME", draft.RegistryId), draft.RegistryId);
        var missing = await NormalizedAsync(await PutHeaderFieldAsync(client, draft, "HF_SAME", MissingRegistryId), MissingRegistryId);

        Assert.StartsWith("422 ", denied, StringComparison.Ordinal);
        Assert.Equal(missing, denied);
        client.Dispose();
    }

    // ── Правка без зміни довідника не перевіряє ціль заново ──────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перейменування_колонки_без_зміни_довідника_проходить_коли_довідник_деактивовано()
    {
        var draft = await ArrangeAsync();
        await AddLookupColumnAsync(draft, "LK_REN", draft.RegistryId);
        await DeactivateAsync(draft.RegistryId);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Edit");

        var response = await PutColumnAsync(client, draft, "LK_REN", draft.RegistryId, label: "Renamed", ordinal: 7);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перейменування_колонки_без_зміни_довідника_проходить_коли_довідник_заборонено_автору()
    {
        var draft = await ArrangeAsync();
        await AddLookupColumnAsync(draft, "LK_DEN", draft.RegistryId);
        using var app = new EcrApiFactory(sql);
        var (client, userId) = await SignedInWithIdAsync(app);
        await DenyRegistryAsync(userId, draft.RegistryId);

        var response = await PutColumnAsync(client, draft, "LK_DEN", draft.RegistryId, label: "Renamed");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        client.Dispose();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_довідника_наявної_колонки_на_неіснуючий_422()
    {
        var draft = await ArrangeAsync();
        await AddLookupColumnAsync(draft, "LK_RETARGET", draft.RegistryId);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Edit");

        var problem = await RejectedAsync(await PutColumnAsync(client, draft, "LK_RETARGET", MissingRegistryId));

        Assert.Equal("err.ECR-TMPL-0422.lookupRegistryUnknown", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перейменування_поля_шапки_без_зміни_довідника_проходить_коли_довідник_деактивовано()
    {
        var draft = await ArrangeAsync();
        await AddLookupHeaderFieldAsync(draft, "HF_REN");
        await DeactivateAsync(draft.RegistryId);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Edit");

        var response = await PutHeaderFieldAsync(client, draft, "HF_REN", draft.RegistryId, label: "Renamed");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перейменування_поля_шапки_без_зміни_довідника_проходить_коли_довідник_заборонено_автору()
    {
        var draft = await ArrangeAsync();
        await AddLookupHeaderFieldAsync(draft, "HF_DEN");
        using var app = new EcrApiFactory(sql);
        var (client, userId) = await SignedInWithIdAsync(app);
        await DenyRegistryAsync(userId, draft.RegistryId);

        var response = await PutHeaderFieldAsync(client, draft, "HF_DEN", draft.RegistryId, label: "Renamed");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        client.Dispose();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_довідника_наявного_поля_шапки_на_неіснуючий_422_а_нове_поле_теж()
    {
        var draft = await ArrangeAsync();
        await AddLookupHeaderFieldAsync(draft, "HF_RETARGET");
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Edit");

        var retarget = await RejectedAsync(await PutHeaderFieldAsync(client, draft, "HF_RETARGET", MissingRegistryId));
        Assert.Equal("err.ECR-TMPL-0422.headerFieldLookupRegistryUnknown", retarget.GetProperty("messageKey").GetString());

        var created = await RejectedAsync(await PutHeaderFieldAsync(client, draft, "HF_NEW", MissingRegistryId));
        Assert.Equal("err.ECR-TMPL-0422.headerFieldLookupRegistryUnknown", created.GetProperty("messageKey").GetString());
    }

    // ── Опора ─────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> PutHeaderFieldAsync(
        HttpClient client, Draft draft, string code, int registryId, string? label = null)
        => client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/header-fields/{code}", UriKind.Relative),
            new
            {
                labelL10n = new Dictionary<string, string> { ["en"] = label ?? code },
                ordinal = (int?)null,
                dataType = "Lookup",
                isRequired = false,
                lookupRegistryDefId = (int?)registryId,
            });

    /// <summary>Тіло відповіді без мінливого (instance, correlationId) і з id довідника, зведеним до заглушки.</summary>
    private static async Task<string> NormalizedAsync(HttpResponseMessage response, int registryId)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
            node.Remove("instance");
            node.Remove("correlationId");
            node.Remove("traceId");
            if (node.ContainsKey("registryDefId"))
            {
                node["registryDefId"] = "<id>";
            }

            var id = System.Text.RegularExpressions.Regex.Escape(registryId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (node["detail"] is { } detail)
            {
                node["detail"] = System.Text.RegularExpressions.Regex.Replace(detail.ToString(), $@"(?<!\d){id}(?!\d)", "<id>");
            }

            return $"{(int)response.StatusCode} {node.ToJsonString()}";
        }
    }

    private async Task<(HttpClient Client, int UserId)> SignedInWithIdAsync(EcrApiFactory app)
    {
        const string password = "Api-Lookup-Registry-2026!";
        var name = $"lkreg_{Guid.NewGuid():N}"[..20];
        int userId;

        await using (var db = Context())
        {
            var user = new Ecr.Domain.Entities.Security.User(name, name, AuthProvider.Local);
            user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash(password));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;

            var role = new Ecr.Domain.Entities.Security.Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("lookup guard"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var permission in new[] { "Template.View", "Template.Edit" })
            {
                db.RolePermissions.Add(new Ecr.Domain.Entities.Security.RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new Ecr.Domain.Entities.Security.RoleAssignment(role.Id, userId, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return (client, userId);
    }

    private async Task DenyRegistryAsync(int userId, int registryId)
    {
        // Заборона вішається на РОЛЬ (grantee - роль), роль призначається автору.
        await using var db = Context();
        var denier = new Ecr.Domain.Entities.Security.Role(EcrCode.Create($"D{Guid.NewGuid():N}"[..12]), Name("registry deny"));
        db.Roles.Add(denier);
        await db.SaveChangesAsync();

        db.ResourceGrants.Add(new Ecr.Domain.Entities.Security.ResourceGrant(
            denier.Id, ResourceKind.Registry, registryId, GrantLevel.Read, isDeny: true));
        db.RoleAssignments.Add(new Ecr.Domain.Entities.Security.RoleAssignment(denier.Id, userId, principalSid: null));
        await db.SaveChangesAsync();
    }

    private async Task DeactivateAsync(int registryId)
    {
        await using var db = Context();
        await db.RegistryDefs.Where(r => r.Id == registryId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, false));
    }

    private async Task AddLookupColumnAsync(Draft draft, string code, int registryId)
    {
        await using var db = Context();
        var column = new ColumnDef(draft.TableId, EcrCode.Create(code), Name(code), 5, CellDataType.Lookup);
        column.SetLookup(registryId, null);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();
    }

    private async Task AddLookupHeaderFieldAsync(Draft draft, string code)
    {
        await using var db = Context();
        var field = new HeaderFieldDef(draft.VersionId, EcrCode.Create(code), Name(code), 1, CellDataType.Lookup);
        field.SetLookup(draft.RegistryId);
        db.HeaderFieldDefs.Add(field);
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Draft draft)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/publish", UriKind.Relative),
            new { reason = "RC16-2" });

    private static Task<HttpResponseMessage> PutColumnAsync(
        HttpClient client, Draft draft, string code, int registryId, string? label = null, int? ordinal = null)
        => client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/tables/{draft.TableId}/columns/{code}", UriKind.Relative),
            new
            {
                headerL10n = new Dictionary<string, string> { ["en"] = label ?? code },
                ordinal,
                dataType = "Lookup",
                isRequired = false, isReadOnly = false, isHidden = false,
                precision = (byte?)null, scale = (byte?)null,
                defaultValue = (string?)null, displayFormat = (string?)null, styleId = (int?)null,
                lookupRegistryDefId = (int?)registryId, lookupFilter = (string?)null,
                unitId = (int?)null,
            });

    private static async Task<JsonElement> RejectedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.UnprocessableEntity,
                $"Очікувалась відмова 422, а прийшло {(int)response.StatusCode}: {body}");

            var problem = JsonDocument.Parse(body).RootElement.Clone();
            Assert.Equal("ECR-TMPL-0422", problem.GetProperty("errorCode").GetString());
            return problem;
        }
    }

    /// <summary>Чернетка з фіксованою таблицею (один рядок) і живим довідником.</summary>
    private async Task<Draft> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var registry = new RegistryDef(EcrCode.Create($"RLK{tag}"), Name("RC16-2 registry"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var template = new Template(EcrCode.Create($"LK{tag}"), Name("RC16-2"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        db.ColumnDefs.Add(new ColumnDef(table.Id, EcrCode.Create("CDEC"), Name("CDEC"), 1, CellDataType.Decimal));
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item));
        await db.SaveChangesAsync();

        return new Draft(version.Id, table.Id, registry.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Draft(int VersionId, int TableId, int RegistryId);
}
