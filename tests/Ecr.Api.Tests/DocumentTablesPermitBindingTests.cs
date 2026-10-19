using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// PM-11b: <c>GET /documents/{id}/tables</c> несе в кожній таблиці ознаку <c>permitBinding</c> (код, режим, тип) - за
/// власною версією шаблону документа, під одним правом <c>Document.View</c>, без жодних даних дозволу.
/// </summary>
/// <remarks>
/// Мутаційні докази (запущені): (1) у <c>GetDocumentTablesHandler</c> віддавати <c>null</c> замість ознаки - червоний
/// перший тест; (2) прибрати <c>b.IsActive</c> із <c>PermitBindingStore.ListActiveSummariesAsync</c> - червоніє рядок про
/// вимкнену прив'язку; (3) у запиті прибрати <c>Join</c> із типами (відповідь без коду типу) - червоніє перевірка коду
/// типу. Лічильник: ТЕПЛИЙ запит (екземпляри вже створені прогрівом), лише команди потоку запиту - як у
/// <c>PatchCellsQueryCountTests</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentTablesPermitBindingTests(SqlServerFixture sql)
{
    private const string Password = "Doc-Tables-Pm11b-2026!";

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прив_язана_таблиця_має_permitBinding_а_решта_null_і_читач_лише_з_Document_View_не_бачить_даних_дозволу()
    {
        using var app = new EcrApiFactory(sql);
        using var admin = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Permit.EditType", "Permit.PublishType", "Template.View", "Template.Edit").ConfigureAwait(true);
        await PublishTypeAsync(admin).ConfigureAwait(true);
        var stand = await ArrangeAsync(extraTables: 2).ConfigureAwait(true);

        await BindAsync(admin, stand, stand.Document.TableDefId, "A", isActive: true).ConfigureAwait(true);
        await BindAsync(admin, stand, stand.Extra[0], "I", isActive: false).ConfigureAwait(true);

        using var reader = await ReaderAsync(app, stand.Document.ProjectId).ConfigureAwait(true);
        var tables = await TablesAsync(reader, stand).ConfigureAwait(true);

        Assert.Equal(3, tables.Count);
        var bound = tables.Single(t => t.GetProperty("tableDefId").GetInt32() == stand.Document.TableDefId);
        var info = bound.GetProperty("permitBinding");
        Assert.Equal(($"A{_tag}", "Rows", $"AIR{_tag}"), (info.GetProperty("code").GetString(), info.GetProperty("mode").GetString(), info.GetProperty("permitTypeCode").GetString()));

        // Лише ознака: ні номерів, ні лімітів, ні рядків дозволу.
        Assert.Equal(["code", "mode", "permitTypeCode"], info.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        // Вимкнена прив'язка й таблиця без прив'язки - null.
        foreach (var other in tables.Where(t => t.GetProperty("tableDefId").GetInt32() != stand.Document.TableDefId))
        {
            Assert.Equal(JsonValueKind.Null, other.GetProperty("permitBinding").ValueKind);
        }

        // Те, що лишається за іншими правами, читачеві далі закрите.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(new Uri($"/api/v1/template-versions/{stand.Document.TemplateVersionId}/permit-bindings", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(
            new Uri($"/api/v1/template-versions/{stand.Document.TemplateVersionId}/permit-bindings/A{_tag}/preview?facilityName=X&periodEnd=2026-01-31", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_таблиць_документа_читає_прив_язки_одним_запитом_і_число_запитів_не_росте_з_числом_таблиць()
    {
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300, revisionWindow: TimeSpan.FromMinutes(5));
        app.Server.PreserveExecutionContext = true;
        using var admin = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Permit.EditType", "Permit.PublishType", "Template.View", "Template.Edit").ConfigureAwait(true);
        await PublishTypeAsync(admin).ConfigureAwait(true);

        var small = await ArrangeAsync(extraTables: 1).ConfigureAwait(true);
        await BindAsync(admin, small, small.Document.TableDefId, "S", isActive: true).ConfigureAwait(true);
        var large = await ArrangeAsync(extraTables: 5).ConfigureAwait(true);
        await BindAsync(admin, large, large.Document.TableDefId, "L", isActive: true).ConfigureAwait(true);
        var none = await ArrangeAsync(extraTables: 2).ConfigureAwait(true);

        var measuredSmall = await MeasureAsync(app, small).ConfigureAwait(true);
        var measuredLarge = await MeasureAsync(app, large).ConfigureAwait(true);
        var measuredNone = await MeasureAsync(app, none).ConfigureAwait(true);

        Assert.Equal((2, 6, 3), (measuredSmall.Tables, measuredLarge.Tables, measuredNone.Tables));

        // Рівно одне звернення до прив'язок - і з прив'язкою, і без неї (охоронця окремо немає: запит і є охоронець).
        Assert.True(
            (measuredSmall.BindingCommands, measuredLarge.BindingCommands, measuredNone.BindingCommands) == (1, 1, 1),
            $"Звернень до PermitSheetBinding: {measuredSmall.BindingCommands}/{measuredLarge.BindingCommands}/{measuredNone.BindingCommands}.\n{measuredLarge.Dump}");

        // Не росте з числом таблиць (2 → 6) і не залежить від наявності прив'язок.
        Assert.True(
            measuredSmall.Total == measuredLarge.Total && measuredLarge.Total == measuredNone.Total,
            $"Усього звернень: {measuredSmall.Total} (2 табл.), {measuredLarge.Total} (6 табл.), {measuredNone.Total} (3 табл., без прив'язок).\n{measuredLarge.Dump}");
    }

    /// <summary>Прогрів (створює екземпляри), потім замір теплого запиту.</summary>
    private async Task<(int Tables, int Total, int BindingCommands, string Dump)> MeasureAsync(EcrApiFactory app, Stand stand)
    {
        using var reader = await ReaderAsync(app, stand.Document.ProjectId).ConfigureAwait(true);
        await TablesAsync(reader, stand).ConfigureAwait(true);

        var texts = new ConcurrentQueue<string>();
        using var counter = new SqlClientCommandCounter(new CommandTally(), command =>
        {
            if (Measuring.Value is not { Value: true })
            {
                return false;
            }

            texts.Enqueue(command.CommandText ?? string.Empty);
            return true;
        });

        counter.Tally.Reset();
        var flag = new StrongBox<bool>(true);
        Measuring.Value = flag;
        List<JsonElement> tables;
        try
        {
            tables = await TablesAsync(reader, stand).ConfigureAwait(true);
        }
        finally
        {
            flag.Value = false;
            Measuring.Value = null;
        }

        counter.AssertObserved();
        var all = texts.ToArray();
        var dump = string.Join('\n', all.Select((t, i) =>
        {
            var flat = string.Join(' ', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return $"{i + 1,2}. {(flat.Length > 160 ? flat[..160] : flat)}";
        }));

        return (tables.Count, all.Length, all.Count(t => t.Contains("PermitSheetBinding", StringComparison.Ordinal)), dump);
    }

    private static async Task<List<JsonElement>> TablesAsync(HttpClient client, Stand stand)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/v1/documents/{stand.Document.DocumentId}/tables?periodKey={stand.Document.PeriodKey.Value}", UriKind.Relative))
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");

        return [.. JsonDocument.Parse(body).RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    /// <summary>Документ з прив'язуваною таблицею (динамічна, ліміт - лише читання) і <paramref name="extraTables"/> зайвими.</summary>
    private async Task<Stand> ArrangeAsync(int extraTables)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 0, rowMode: TableRowMode.Dynamic).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var limit = await db.ColumnDefs.SingleAsync(c => c.Id == doc.ColumnDefIds[1]).ConfigureAwait(false);
        limit.SetReadOnly(true);
        limit.SetNumericFormat(12, 3);
        db.HeaderFieldDefs.Add(new HeaderFieldDef(
            doc.TemplateVersionId, EcrCode.Create("Facility"), Name("Facility"), 1, CellDataType.String));

        // Без складу аркушів документа `EnsureTableInstancesAsync` нічого не створює.
        db.DocumentSheets.Add(new Ecr.Domain.Entities.Documents.DocumentSheet(doc.DocumentId, doc.SheetDefId));

        var extra = new List<TableDef>();
        for (var i = 1; i <= extraTables; i++)
        {
            var table = new TableDef(
                doc.SheetDefId, EcrCode.Create($"X{i}_{_tag}{doc.TableDefId}"), Name($"X{i}"), 1 + i,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
            db.TableDefs.Add(table);
            extra.Add(table);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        foreach (var table in extra)
        {
            db.ColumnDefs.Add(new ColumnDef(table.Id, EcrCode.Create($"K{table.Id}_{_tag}"), Name("K"), 1, CellDataType.String));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new Stand(doc, [.. extra.Select(t => t.Id)]);
    }

    private async Task BindAsync(HttpClient admin, Stand stand, int tableDefId, string prefix, bool isActive)
    {
        // Колонки: ключ і ліміт беруться з колонок таблиці з будівника; для зайвої таблиці - лише ключ достатній не завжди,
        // тому всі прив'язки тесту ставляться на таблиці з будівника або на таблицю з ключем і лімітом.
        var table = tableDefId == stand.Document.TableDefId
            ? (stand.Document.ColumnDefIds[0], stand.Document.ColumnDefIds[1])
            : await ExtraColumnsAsync(tableDefId).ConfigureAwait(false);

        var response = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{stand.Document.TemplateVersionId}/permit-bindings/{prefix}{_tag}", UriKind.Relative),
            new
            {
                sheetTableDefId = tableDefId,
                permitTypeCode = $"AIR{_tag}",
                permitTableCode = "Emissions",
                mode = "Rows",
                selectionRule = "ByFacilityPeriodEnd",
                headerFieldDefId = (int?)null,
                isActive,
                columns = new object[]
                {
                    new { role = "RowKey", permitFieldCode = "Substance", sheetColumnDefId = table.Item1 },
                    new { role = "Limit", permitFieldCode = "Limit", sheetColumnDefId = table.Item2 },
                },
            });
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
    }

    /// <summary>Додає зайвій таблиці ключ і ліміт (лише читання) для прив'язки й повертає їхні Id.</summary>
    private async Task<(int Key, int Limit)> ExtraColumnsAsync(int tableDefId)
    {
        await using var db = Context();
        var key = new ColumnDef(tableDefId, EcrCode.Create($"PK{tableDefId}_{_tag}"), Name("PK"), 5, CellDataType.String);
        var limit = new ColumnDef(tableDefId, EcrCode.Create($"PL{tableDefId}_{_tag}"), Name("PL"), 6, CellDataType.Decimal);
        limit.SetReadOnly(true);
        limit.SetNumericFormat(12, 3);
        db.ColumnDefs.AddRange(key, limit);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (key.Id, limit.Id);
    }

    private async Task PublishTypeAsync(HttpClient client)
    {
        var code = $"AIR{_tag}";
        var created = await client.PostAsJsonAsync(
            new Uri("/api/v1/permit-types", UriKind.Relative), new { code, nameL10n = new { en = "Air" }, category = "Air" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rowVersion = (await JsonAsync(created).ConfigureAwait(true)).GetProperty("rowVersion").GetString();

        object Col(string name, string type, bool key, int? precision = null, int? scale = null) => new
        {
            code = name, nameL10n = new { en = name }, dataType = type, unitId = (int?)null, required = key, key,
            group = (string?)null, precision, scale, listScope = (string?)null, listRegistryCode = (string?)null,
            siblingTableCode = (string?)null, choices = (string[]?)null, order = 1,
        };

        var replaced = await client.PutAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}", UriKind.Relative),
            new
            {
                rowVersion,
                nameL10n = new { en = "Air" },
                category = "Air",
                tables = new[]
                {
                    new
                    {
                        code = "Emissions", nameL10n = new { en = "Emissions" }, order = 1,
                        columns = new[] { Col("Substance", "String", true), Col("Limit", "Decimal", false, 12, 3) },
                        rules = Array.Empty<object>(),
                    },
                },
                rules = Array.Empty<object>(),
            });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var draft = (await JsonAsync(replaced).ConfigureAwait(true)).GetProperty("rowVersion").GetString();

        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "випуск для PM-11b", rowVersion = draft });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    /// <summary>Користувач лише з <c>Document.View</c> (без <c>Template.View</c> і <c>Registry.View</c>) і читанням проєкту.</summary>
    private async Task<HttpClient> ReaderAsync(EcrApiFactory app, int projectId)
    {
        var name = $"pm11b_{Guid.NewGuid():N}"[..20];
        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("PM-11b reader"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Read));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private sealed record Stand(TestDocument Document, IReadOnlyList<int> Extra);
}
