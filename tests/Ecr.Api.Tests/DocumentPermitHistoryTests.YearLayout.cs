// tests/Ecr.Api.Tests/DocumentPermitHistoryTests.YearLayout.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// L7c: документ річної розкладки (<c>YearWithMonthColumns</c>, місячний проєкт) — діалог поділу відкривається з ЯКОРЯ
/// (209901), а значення вересня живе в екземплярі-власнику 209909 (<c>MonthRoutedCellStore</c>). Історія з якоря
/// показує одиницю «вересень» з коміркою колонки M9, а поділ з якоря пише суму туди, де її читає вересень.
/// </summary>
/// <remarks>
/// ⛔ B121: розкладка версії повертається до <see cref="TemplateLayout.PerMonthCopies"/> у <c>finally</c>.
/// Мутаційний доказ (локально, не комітити): у <c>PermitHistoryReader.RowDto</c> для місячних таблиць брати
/// <c>c.MonthNumber == unit.First.Month - 1</c> — історія з якоря показує не ту колонку (червоне).
/// </remarks>
public sealed partial class DocumentPermitHistoryTests
{
    private const int YearAnchor = 209901;
    private const int YearSeptember = 209909;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L7c")]
    public async Task Річний_документ_з_якоря_історія_бачить_вересень_а_поділ_пише_суму_у_період_вересня()
    {
        var s = await ArrangeYearAsync().ConfigureAwait(true);
        try
        {
            EcrApiFactory created;
            Environment.SetEnvironmentVariable(RoutingFlag, "false");
            try
            {
                created = new EcrApiFactory(sql);
            }
            finally
            {
                Environment.SetEnvironmentVariable(RoutingFlag, null);
            }

            using var app = created;
            using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

            // Значення вересня — як пише застосунок річний документ: PATCH з якоря, роутер кладе його у 209909.
            var row = await SliceRowAsync(app, client, s, s.AnchorInstanceId).ConfigureAwait(true);
            var patch = await client.PatchAsJsonAsync(
                new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/cells", UriKind.Relative),
                new
                {
                    tableInstanceId = s.AnchorInstanceId,
                    periodKey = YearAnchor,
                    origin = "UserEdit",
                    rows = new[]
                    {
                        new
                        {
                            rowKey = s.RowKey,
                            baseVersion = row.GetProperty("rowVersion").GetString(),
                            cells = new[] { new { columnCode = s.SepColumnCode, value = (object)"12" } },
                        },
                    },
                }).ConfigureAwait(true);
            Assert.True(
                patch.StatusCode == HttpStatusCode.OK,
                $"PATCH: {patch.StatusCode}: {await patch.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
            Assert.Equal([(YearSeptember, 12m)], await StoredYearAsync(s).ConfigureAwait(true));

            // Дозвіл B з 15.09 — тим самим шляхом, що й клієнт.
            var add = await client.PostAsJsonAsync(
                new Uri($"{Base(s.DocumentId)}/rows", UriKind.Relative),
                new
                {
                    tableInstanceId = s.AnchorInstanceId,
                    rowKey = s.RowKey,
                    columnCode = s.PermitColumnCode,
                    registryEntryId = s.PermitBId,
                    validFrom = "2099-09-15",
                }).ConfigureAwait(true);
            Assert.True(
                add.StatusCode == HttpStatusCode.OK,
                $"add: {add.StatusCode}: {await add.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

            // 2. Історія з якоря: рівно одна одиниця — вересень, з коміркою M9 = 12, не поділена.
            var before = await HistoryAsync(app, client, s).ConfigureAwait(true);
            Assert.Equal(1, before.GetProperty("cellsNeedingSplit").GetInt32());
            var unit = Assert.Single(Assert.Single(before.GetProperty("rows").EnumerateArray()).GetProperty("splitMonths").EnumerateArray());
            Assert.Equal("2099-09-01", unit.GetProperty("month").GetString());
            Assert.Equal([s.SepColumnCode], unit.GetProperty("columnCodes").EnumerateArray().Select(c => c.GetString()));
            var spans = unit.GetProperty("parts").EnumerateArray().ToList();
            Assert.Equal(("2099-09-01", "2099-09-14"), (spans[0].GetProperty("dayFrom").GetString(), spans[0].GetProperty("dayTo").GetString()));
            Assert.Equal(("2099-09-15", "2099-09-30"), (spans[1].GetProperty("dayFrom").GetString(), spans[1].GetProperty("dayTo").GetString()));
            var cell = Assert.Single(unit.GetProperty("cells").EnumerateArray());
            Assert.Equal(s.SepColumnCode, cell.GetProperty("columnCode").GetString());
            Assert.Equal(12m, Dec(cell.GetProperty("total")));
            Assert.False(cell.GetProperty("isSplit").GetBoolean());

            // 3. Поділ з якоря: екземпляр якоря, колонка M9, дві частини.
            row = await SliceRowAsync(app, client, s, s.AnchorInstanceId).ConfigureAwait(true);
            var split = await client.PutAsJsonAsync(
                new Uri($"{Base(s.DocumentId)}/splits", UriKind.Relative),
                new
                {
                    periodKey = YearAnchor,
                    items = new[]
                    {
                        new
                        {
                            tableInstanceId = s.AnchorInstanceId,
                            rowKey = s.RowKey,
                            columnCode = s.SepColumnCode,
                            baseVersion = row.GetProperty("rowVersion").GetString(),
                            parts = new decimal?[] { 5.5m, 7.25m },
                        },
                    },
                }).ConfigureAwait(true);
            var splitBody = await split.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(split.StatusCode == HttpStatusCode.OK, $"split: {split.StatusCode}: {splitBody}\n{app.ErrorsText}");

            // (a) історія з якоря: вересень поділено, частини ті самі.
            var after = await HistoryAsync(app, client, s).ConfigureAwait(true);
            Assert.Equal(0, after.GetProperty("cellsNeedingSplit").GetInt32());
            var done = Assert.Single(Assert.Single(after.GetProperty("rows")[0].GetProperty("splitMonths").EnumerateArray())
                .GetProperty("cells").EnumerateArray());
            Assert.Equal(s.SepColumnCode, done.GetProperty("columnCode").GetString());
            Assert.True(done.GetProperty("isSplit").GetBoolean());
            Assert.Equal(12.75m, Dec(done.GetProperty("total")));
            Assert.Equal([5.5m, 7.25m], done.GetProperty("parts").EnumerateArray().Select(Dec).Select(p => p!.Value));

            // (b) вересень читає суму: фізично — лише власник 209909; зріз екземпляра вересня — 12.75.
            Assert.Equal([(YearSeptember, 12.75m)], await StoredYearAsync(s).ConfigureAwait(true));
            var september = await SliceRowAsync(app, client, s, s.SeptemberInstanceId).ConfigureAwait(true);
            Assert.Equal(12.75m, Dec(september.GetProperty("cells").GetProperty(s.SepColumnCode)));

            // (c) річний перегляд (якір) показує те саме.
            var anchor = await SliceRowAsync(app, client, s, s.AnchorInstanceId).ConfigureAwait(true);
            Assert.Equal(12.75m, Dec(anchor.GetProperty("cells").GetProperty(s.SepColumnCode)));
        }
        finally
        {
            await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
            await YearLayoutCleanup.ResetAsync(db, [s.TemplateVersionId]).ConfigureAwait(true);
        }
    }

    private static string Base(long documentId) => $"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/permit-history";

    private static decimal? Dec(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture),
        JsonValueKind.Number => value.GetDecimal(),
        _ => null,
    };

    private static async Task<JsonElement> HistoryAsync(EcrApiFactory app, HttpClient client, YearScenario s)
    {
        using var response = await client.GetAsync(
            new Uri($"{Base(s.DocumentId)}?periodKey={YearAnchor.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative))
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"history: {response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Рядок сценарію в зрізі екземпляра <paramref name="instanceId"/> (<c>GET /documents/{id}/tables/{instance}</c>).</summary>
    private static async Task<JsonElement> SliceRowAsync(EcrApiFactory app, HttpClient client, YearScenario s, long instanceId)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/tables/{instanceId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"slice {instanceId}: {response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == s.RowKey).Clone();
    }

    /// <summary>Фізичні комірки M9 у всіх екземплярах документа: (період, значення).</summary>
    private async Task<List<(int Period, decimal Value)>> StoredYearAsync(YearScenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var rows = await (
                from cell in db.CellValues.AsNoTracking()
                where cell.ColumnDefId == s.SepColumnId && cell.ValueNumeric != null
                join row in db.TableRows.AsNoTracking()
                    on new { P = cell.PeriodKeyValue, R = cell.TableRowId } equals new { P = row.PeriodKeyValue, R = row.Id }
                join instance in db.TableInstances.AsNoTracking()
                    on new { P = row.PeriodKeyValue, I = row.TableInstanceId } equals new { P = instance.PeriodKeyValue, I = instance.Id }
                where instance.DocumentId == s.DocumentId
                orderby cell.PeriodKeyValue
                select new { cell.PeriodKeyValue, Value = cell.ValueNumeric!.Value })
            .ToListAsync().ConfigureAwait(false);
        return [.. rows.Select(r => (r.PeriodKeyValue, r.Value))];
    }

    /// <summary>
    /// Документ 209901 (якір) версії <c>YearWithMonthColumns</c> місячного проєкту: таблиця «місяці в колонках» з M1..M12 і
    /// колонкою дозволу (реєстр дозволів: A — увесь 2099, B — з 15.09.2099); рядок має дозвіл A; календар 1..12 відкритий;
    /// екземпляр вересня 209909 з тим самим рядком.
    /// </summary>
    private async Task<YearScenario> ArrangeYearAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            YearAnchor, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using var db = builder.CreateContext();

        var template = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId).ConfigureAwait(false);
        db.Entry(template).Property(v => v.Layout).CurrentValue = TemplateLayout.YearWithMonthColumns;
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
        for (var m = 1; m <= 12; m++)
        {
            var key = (2099 * 100) + m;
            var period = key == YearAnchor
                ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == YearAnchor).ConfigureAwait(false)
                : db.Periods.Add(new Period(
                    doc.ProjectId, new PeriodKey(key), (byte)m,
                    new DateOnly(2099, m, 1), new DateOnly(2099, m, DateTime.DaysInMonth(2099, m)))).Entity;
            period.AdvanceTo(PeriodState.Open, now);
        }

        var registry = new RegistryDef(EcrCode.Create($"PRM_YL{tag}"), Text("Permit year"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var type = new PermitTypeDef(EcrCode.Create($"YL{tag}"), Text("Water permit year"), PermitCategory.Water, registry.Id);
        type.Publish(null, now);
        db.PermitTypeDefs.Add(type);

        var permitA = new RegistryEntry(registry.Id, EcrCode.Create("YLA"), Text("KZ-YL-A"));
        permitA.SetValidity(new DateOnly(2099, 1, 1), new DateOnly(2100, 1, 1));
        var permitB = new RegistryEntry(registry.Id, EcrCode.Create("YLB"), Text("KZ-YL-B"));
        permitB.SetValidity(new DateOnly(2099, 9, 15), new DateOnly(2100, 1, 1));
        db.RegistryEntries.AddRange(permitA, permitB);

        var permitColumnCode = $"PRM{tag}";
        var permitColumn = new ColumnDef(doc.TableDefId, EcrCode.Create(permitColumnCode), Text("Permit number"), 50, CellDataType.Lookup);
        permitColumn.SetLookup(registry.Id);
        db.ColumnDefs.Add(permitColumn);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Дозвіл рядка — не-місячна колонка, власник якір.
        db.CellValues.Add(new CellValue(
            new CellAddress(doc.PeriodKey, doc.RowIds[0], permitColumn.Id),
            doc.TableDefId, new CellValueData { ValueRegistryEntryId = permitA.Id }));

        var userName = $"prmy_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"PRMY_{Guid.NewGuid():N}"), Text("Permit history year"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var september = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(YearSeptember)).ConfigureAwait(false);

        var rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
            .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        var sepColumnId = doc.MonthColumnDefIds[8];
        var sepColumnCode = await db.ColumnDefs.AsNoTracking().Where(c => c.Id == sepColumnId)
            .Select(c => c.Code).SingleAsync().ConfigureAwait(false);

        return new YearScenario(
            doc.TemplateVersionId, doc.DocumentId, doc.TableInstanceId, september.TableInstanceId, userName, rowKey,
            permitColumnCode, permitB.Id, sepColumnId, sepColumnCode.ToString());
    }

    private sealed record YearScenario(
        int TemplateVersionId, long DocumentId, long AnchorInstanceId, long SeptemberInstanceId, string UserName, string RowKey,
        string PermitColumnCode, long PermitBId, int SepColumnId, string SepColumnCode);
}
