// tests/Ecr.Api.Tests/PermitBindingPreviewReadScopeTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
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
/// ⛔ S-1 (аудит 2026-11-10): перегляд прив'язки дозволу з документом
/// (<c>GET …/permit-bindings/{code}/preview?documentId=&amp;periodKey=</c>) накладає фактичні комірки документа лише в
/// межах читання користувача — ті самі, що й зріз таблиці: заборона на таблицю чи колонку (S6, ФВ-6.6), роль, звужена
/// періодами (D-214), і прихована колонка (<c>ColumnDef.IsHidden</c>) факту не віддають.
/// </summary>
/// <remarks>
/// Диференційні тести на живому HTTP-шляху з реальними грантами: той самий документ, той самий запит, відрізняється
/// лише грант (або область ролі, або ознака прихованої колонки). Контроль у кожному тесті — дозволений читач бачить
/// факт. Доказ без мутації фільтрів доступу: до фіксу обробник межі читання не питав, і кожен тест падав на рядку
/// «факт схований» (значення 7.25 у відповіді).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitBindingPreviewReadScopeTests(SqlServerFixture sql)
{
    private const string Password = "Permit-Preview-S1-2026!";
    private const decimal Fact = 7.25m;
    private const decimal Limit = 12.5m;

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Theory]
    [InlineData(ResourceKind.Column)]
    [InlineData(ResourceKind.Table)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Заборона_на_таблицю_чи_колонку_ховає_факт_у_перегляді_а_дозволений_читач_його_бачить(ResourceKind denied)
    {
        using var app = new EcrApiFactory(sql);
        var stand = await ArrangeAsync(app).ConfigureAwait(true);
        var deniedId = denied == ResourceKind.Column ? stand.Doc.ColumnDefIds[2] : stand.Doc.TableDefId;

        using var allowed = await ReaderAsync(app, stand, deny: null).ConfigureAwait(true);
        AssertFactVisible(await PreviewAsync(allowed, app, stand).ConfigureAwait(true), stand);

        using var restricted = await ReaderAsync(app, stand, deny: (denied, deniedId)).ConfigureAwait(true);
        AssertFactHidden(await PreviewAsync(restricted, app, stand).ConfigureAwait(true), stand);

        // Обмеження те саме, що бачить зріз: схована таблиця там — 404.
        Assert.Equal(HttpStatusCode.OK, await SliceStatusAsync(allowed, stand).ConfigureAwait(true));
        if (denied == ResourceKind.Table)
        {
            Assert.Equal(HttpStatusCode.NotFound, await SliceStatusAsync(restricted, stand).ConfigureAwait(true));
        }
    }

    /// <summary>D-214: роль, звужена періодами, не бачить даних періоду поза проміжком — і в перегляді прив'язки теж.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Роль_звужена_іншим_періодом_не_бачить_факту_у_перегляді_а_свій_період_бачить()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await ArrangeAsync(app).ConfigureAwait(true);

        using var inRange = await NarrowedReaderAsync(app, stand, from: 202601).ConfigureAwait(true);
        AssertFactVisible(await PreviewAsync(inRange, app, stand).ConfigureAwait(true), stand);

        // Документ за 202601 цій ролі видно (у проєкті є 202602), а дані 202601 — ні.
        using var outOfRange = await NarrowedReaderAsync(app, stand, from: 202602).ConfigureAwait(true);
        AssertFactHidden(await PreviewAsync(outOfRange, app, stand).ConfigureAwait(true), stand);

        Assert.Equal(HttpStatusCode.OK, await SliceStatusAsync(inRange, stand).ConfigureAwait(true));
        Assert.Equal(HttpStatusCode.NotFound, await SliceStatusAsync(outOfRange, stand).ConfigureAwait(true));
    }

    private static async Task<HttpStatusCode> SliceStatusAsync(HttpClient client, Stand stand)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{stand.Doc.DocumentId}/tables/{stand.Doc.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false);
        return response.StatusCode;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прихована_колонка_не_накладається_у_перегляді()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await ArrangeAsync(app).ConfigureAwait(true);
        using var reader = await ReaderAsync(app, stand, deny: null).ConfigureAwait(true);

        AssertFactVisible(await PreviewAsync(reader, app, stand).ConfigureAwait(true), stand);

        // Той самий читач, той самий запит; змінилась лише ознака прихованої колонки.
        await using (var db = Context())
        {
            var fact = await db.ColumnDefs.SingleAsync(c => c.Id == stand.Doc.ColumnDefIds[2]).ConfigureAwait(true);
            fact.SetHidden(true);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        AssertFactHidden(await PreviewAsync(reader, app, stand).ConfigureAwait(true), stand);
    }

    private static void AssertFactVisible(JsonElement row, Stand stand)
    {
        Assert.Equal(Limit, Number(Cell(row, stand.LimitCode)));
        Assert.True(Cell(row, stand.FactCode) is not null, $"факт мав бути видимий: {row}");
        Assert.Equal(Fact, Number(Cell(row, stand.FactCode)));
    }

    private static void AssertFactHidden(JsonElement row, Stand stand)
    {
        // Запланове значення з дозволу лишається: перегляд без фактів читач бачити вправі.
        Assert.Equal(Limit, Number(Cell(row, stand.LimitCode)));
        Assert.True(Cell(row, stand.FactCode) is null, $"факт поза межами читання потрапив у перегляд: {row}");
        Assert.DoesNotContain("7.25", row.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>Число з відповіді: серіалізатор API віддає decimal і числом, і рядком.</summary>
    private static decimal? Number(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDecimal(),
        { ValueKind: JsonValueKind.String } s => decimal.Parse(s.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    private static JsonElement? Cell(JsonElement row, string columnCode)
        => row.GetProperty("cells").EnumerateObject()
            .Where(p => string.Equals(p.Name, columnCode, StringComparison.OrdinalIgnoreCase))
            .Select(p => (JsonElement?)p.Value)
            .FirstOrDefault();

    /// <summary>Перегляд з документом за його періодом; повертає рядок дозволу (він один).</summary>
    private static async Task<JsonElement> PreviewAsync(HttpClient client, EcrApiFactory app, Stand stand)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/template-versions/{stand.Doc.TemplateVersionId}/permit-bindings/{stand.Binding}/preview" +
            $"?documentId={stand.Doc.DocumentId}&periodKey={stand.Doc.PeriodKey.Value}&facilityName=Site%20A",
            UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var rows = JsonDocument.Parse(body).RootElement.GetProperty("rows");
        return Assert.Single(rows.EnumerateArray()
            .Where(r => r.GetProperty("rowKey").GetString() == stand.RowKey)
            .Select(r => r.Clone()));
    }

    /// <summary>
    /// Документ за 202601 (у проєкті ще 202602), прив'язка <c>Rows</c> за об'єктом, дозвіл «Site A» з одним рядком і в
    /// екземплярі документа — рядок цього дозволу з фактом <see cref="Fact"/> у незіставленій колонці.
    /// </summary>
    private async Task<Stand> ArrangeAsync(EcrApiFactory app)
    {
        using var admin = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Permit.EditType", "Permit.PublishType", "Template.View", "Template.Edit").ConfigureAwait(false);
        await PublishTypeAsync(admin).ConfigureAwait(false);

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 0, rowMode: TableRowMode.Dynamic).ConfigureAwait(false);
        await builder.OpenPeriodAsync(doc).ConfigureAwait(false);

        await using (var db = Context())
        {
            var limit = await db.ColumnDefs.SingleAsync(c => c.Id == doc.ColumnDefIds[1]).ConfigureAwait(false);
            limit.SetReadOnly(true);
            limit.SetNumericFormat(12, 3);
            db.HeaderFieldDefs.Add(new HeaderFieldDef(
                doc.TemplateVersionId, EcrCode.Create("Facility"), Name("Facility"), 1, CellDataType.String));
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            db.Periods.Add(new Period(doc.ProjectId, new PeriodKey(202602), 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var binding = $"B{_tag}";
        var put = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{doc.TemplateVersionId}/permit-bindings/{binding}", UriKind.Relative),
            new
            {
                sheetTableDefId = doc.TableDefId,
                permitTypeCode = $"AIR{_tag}",
                permitTableCode = "Emissions",
                mode = "Rows",
                selectionRule = "ByFacilityPeriodEnd",
                headerFieldDefId = (int?)null,
                isActive = true,
                columns = new object[]
                {
                    new { role = "RowKey", permitFieldCode = "Substance", sheetColumnDefId = doc.ColumnDefIds[0] },
                    new { role = "Limit", permitFieldCode = "Limit", sheetColumnDefId = doc.ColumnDefIds[1] },
                },
            }).ConfigureAwait(false);
        Assert.True(put.StatusCode == HttpStatusCode.OK, $"{put.StatusCode}: {await put.Content.ReadAsStringAsync().ConfigureAwait(false)}");

        var rowEntryId = await SeedPermitAsync().ConfigureAwait(false);
        var rowKey = $"P{rowEntryId}";
        var rowId = await new BulkCellLoader(sql.ConnectionString, 1000).ReserveIdsAsync("doc.TableRowSeq", 1, default).ConfigureAwait(false);

        await using (var db = Context())
        {
            db.TableRows.Add(new TableRow(doc.PeriodKey, rowId, doc.TableInstanceId, RowKey.Create(rowKey), 1,
                new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)));
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.CellValues.Add(new CellValue(
                new CellAddress(doc.PeriodKey, rowId, doc.ColumnDefIds[2]), doc.TableDefId, new CellValueData { ValueNumeric = Fact }));
            await db.SaveChangesAsync().ConfigureAwait(false);

            var codes = await db.ColumnDefs.Where(c => c.TableDefId == doc.TableDefId)
                .ToDictionaryAsync(c => c.Id, c => c.Code).ConfigureAwait(false);
            return new Stand(doc, binding, rowKey, codes[doc.ColumnDefIds[1]], codes[doc.ColumnDefIds[2]]);
        }
    }

    /// <summary>Дозвіл «Site A» (чинний з 2026-01-01) з одним рядком NOx / <see cref="Limit"/>; повертає Id рядка.</summary>
    private async Task<long> SeedPermitAsync()
    {
        await using var db = Context();
        var code = $"AIR{_tag}";
        var type = await db.PermitTypeDefs.SingleAsync(t => t.Code == code).ConfigureAwait(false);
        var table = await db.PermitTypeTables.SingleAsync(t => t.PermitTypeDefId == type.Id && t.Code == "Emissions").ConfigureAwait(false);
        var headerFields = await db.Set<RegistryFieldDef>().Where(f => f.RegistryDefId == type.HeaderRegistryDefId)
            .ToDictionaryAsync(f => f.Code, f => f.Id).ConfigureAwait(false);
        var rowFields = await db.Set<RegistryFieldDef>().Where(f => f.RegistryDefId == table.RegistryDefId)
            .ToDictionaryAsync(f => f.Code, f => f.Id).ConfigureAwait(false);

        var permit = new RegistryEntry(type.HeaderRegistryDefId, EcrCode.Create($"N{_tag}"), Name($"N{_tag}"), 1, DateTime.UnixEpoch);
        permit.SetValidity(new DateOnly(2026, 1, 1), null);
        db.RegistryEntries.Add(permit);
        await db.SaveChangesAsync().ConfigureAwait(false);
        Value(db, permit.Id, headerFields["Permit_Number"], CellDataType.String, $"N{_tag}");
        Value(db, permit.Id, headerFields["Location"], CellDataType.String, "Site A");

        var row = new RegistryEntry(table.RegistryDefId, EcrCode.Create($"N{_tag}_1"), Name("NOx"), 1, DateTime.UnixEpoch);
        row.SetOrdinal(1);
        db.RegistryEntries.Add(row);
        await db.SaveChangesAsync().ConfigureAwait(false);
        Value(db, row.Id, rowFields["PERMIT"], CellDataType.Lookup, permit.Id);
        Value(db, row.Id, rowFields["Substance"], CellDataType.String, "NOx");
        Value(db, row.Id, rowFields["Limit"], CellDataType.Decimal, Limit);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return row.Id;
    }

    private static void Value(EcrDbContext db, long entryId, int fieldId, CellDataType type, object? value)
    {
        var stored = new RegistryValue(entryId, fieldId);
        stored.Set(type, value, null);
        db.RegistryValues.Add(stored);
    }

    /// <summary>
    /// Читач: <c>Template.View</c>, <c>Registry.View</c>, <c>Document.View</c>, грант <c>Read</c> на проєкт і, якщо
    /// задано, явна заборона на ресурс.
    /// </summary>
    private async Task<HttpClient> ReaderAsync(EcrApiFactory app, Stand stand, (ResourceKind Kind, int Id)? deny)
    {
        var name = $"s1pv_{Guid.NewGuid():N}"[..20];
        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("S-1 reader"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in new[] { "Template.View", "Registry.View", "Document.View" })
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, stand.Doc.ProjectId, GrantLevel.Read));
            if (deny is { } d)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, d.Kind, d.Id, GrantLevel.Read, isDeny: true));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return await LoginAsync(app, name).ConfigureAwait(false);
    }

    /// <summary>
    /// Читач із роллю, звуженою періодами з <paramref name="from"/> (D-214): <c>Document.View</c> і грант <c>Read</c> на
    /// проєкт — у звуженій ролі; <c>Template.View</c> і <c>Registry.View</c> — окремою роллю без грантів.
    /// </summary>
    private async Task<HttpClient> NarrowedReaderAsync(EcrApiFactory app, Stand stand, int from)
    {
        var name = $"s1pd_{Guid.NewGuid():N}"[..20];
        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var catalog = new Role(EcrCode.Create($"C{Guid.NewGuid():N}"[..12]), Name("S-1 catalog"));
            var narrowed = new Role(EcrCode.Create($"N{Guid.NewGuid():N}"[..12]), Name("S-1 narrowed"));
            db.Roles.AddRange(catalog, narrowed);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(catalog.Id, "Template.View"));
            db.RolePermissions.Add(new RolePermission(catalog.Id, "Registry.View"));
            db.RolePermissions.Add(new RolePermission(narrowed.Id, "Document.View"));
            db.ResourceGrants.Add(new ResourceGrant(narrowed.Id, ResourceKind.Project, stand.Doc.ProjectId, GrantLevel.Read));
            db.RoleAssignments.Add(new RoleAssignment(catalog.Id, user.Id, principalSid: null));
            var assignment = new RoleAssignment(narrowed.Id, user.Id, principalSid: null);
            db.RoleAssignments.Add(assignment);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var scope = RoleAssignmentScope.Create([stand.Doc.ProjectId], null, new PeriodKey(from), null).ToJson();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {assignment.Id}").ConfigureAwait(false);
        }

        return await LoginAsync(app, name).ConfigureAwait(false);
    }

    private static async Task<HttpClient> LoginAsync(EcrApiFactory app, string name)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task PublishTypeAsync(HttpClient client)
    {
        var code = $"AIR{_tag}";
        var created = await client.PostAsJsonAsync(
            new Uri("/api/v1/permit-types", UriKind.Relative), new { code, nameL10n = new { en = "Air" }, category = "Air" }).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rowVersion = (await JsonAsync(created).ConfigureAwait(false)).GetProperty("rowVersion").GetString();

        object Col(string column, string type, bool key, int? precision = null, int? scale = null) => new
        {
            code = column, nameL10n = new { en = column }, dataType = type, unitId = (int?)null, required = key, key,
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
            }).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var draft = (await JsonAsync(replaced).ConfigureAwait(false)).GetProperty("rowVersion").GetString();

        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "випуск для S-1", rowVersion = draft }).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private sealed record Stand(TestDocument Doc, string Binding, string RowKey, string LimitCode, string FactCode);
}
