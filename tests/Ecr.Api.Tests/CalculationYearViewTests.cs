using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
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
/// <c>GET /api/v1/documents/{id}/calculation-results/year</c> (CALC-RESULTS.md) на справжньому SQL і HTTP: зведення
/// місяців, лише актуальний прогін, застарілий місяць, місяць без прогону, відсутнє число, сума за рік лише для
/// адитивної одиниці (n/12), схований аркуш, 403 без <c>Calculation.View</c>, 422 на рік поза межами, 202 експорту.
/// </summary>
[Collection("SqlServer")]
public sealed class CalculationYearViewTests(SqlServerFixture sql)
{
    private const string Password = "Calc-Year-View-R!42";
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string[] NoCalcPermissions = ["Document.View"];
    private static readonly string[] FullPermissions = ["Document.View", "Calculation.View", "Document.Export"];
    private static readonly string[] OneRow = ["x"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Рік_зводить_місяці_лише_з_актуальних_прогонів_зі_станами_і_сумою_адитивних()
    {
        var s = await ArrangeAsync(how: "none").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var (status, body) = await YearAsync(client, s.DocumentId, 2026).ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        var view = JsonDocument.Parse(body).RootElement;

        var months = view.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal(12, months.Count);
        Assert.Equal("current", months[0].GetProperty("runState").GetString());
        Assert.Equal("outOfDate", months[1].GetProperty("runState").GetString());
        Assert.Equal("noRun", months[2].GetProperty("runState").GetString());
        Assert.Equal("open", months[2].GetProperty("periodState").GetString());
        Assert.Equal("notOpened", months[3].GetProperty("runState").GetString());
        Assert.False(view.GetProperty("freshnessHidden").GetBoolean());

        // Контроль до тесту Deny: без Deny вихід схованого аркуша B видно — інакше «не бачить» нижче нічого не доводить.
        Assert.Contains(s.OutHidden, body, StringComparison.Ordinal);

        // R1 · E_A (t): січень 1.5, лютий 2.5 актуального прогону (100 неактуального — не потрапляє); сума 4 за 2/12.
        var r1 = Row(view, s.RowKey1, s.OutAdditive);
        Assert.Equal([1.5m, 2.5m], Values(r1).Take(2).Select(v => v!.Value));
        Assert.All(Values(r1).Skip(2), v => Assert.Null(v));
        Assert.Equal("vvnnnnnnnnnn", r1.GetProperty("states").GetString());
        Assert.Equal(4m, Dec(r1.GetProperty("yearTotal")));
        Assert.Equal(2, r1.GetProperty("monthsIncluded").GetInt32());
        Assert.DoesNotContain("100", r1.GetProperty("values").GetRawText(), StringComparison.Ordinal);

        // R2 · E_A: лише лютий; у січні прогін був, а числа немає — «a».
        var r2 = Row(view, s.RowKey2, s.OutAdditive);
        Assert.Equal("avnnnnnnnnnn", r2.GetProperty("states").GetString());
        Assert.Equal(4m, Dec(r2.GetProperty("yearTotal")));
        Assert.Equal(1, r2.GetProperty("monthsIncluded").GetInt32());

        // Неадитивна одиниця: числа є, суми немає.
        var rate = Row(view, s.RowKey1, s.OutRate);
        Assert.Equal(7m, Values(rate)[0]);
        Assert.Equal(JsonValueKind.Null, rate.GetProperty("yearTotal").ValueKind);
        var rateUnit = view.GetProperty("units")[rate.GetProperty("unit").GetInt32()];
        Assert.False(rateUnit.GetProperty("additive").GetBoolean());

        // Формула шаблону (IsCalculated) — окремий вихід без методології, з підписом рядка шаблону.
        var formula = view.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("column").ValueKind == JsonValueKind.Number);
        Assert.Equal(JsonValueKind.Null, formula.GetProperty("methodology").ValueKind);
        Assert.Equal(9m, Values(formula)[1]);
        var eq = view.GetProperty("equipment")[formula.GetProperty("equipment").GetInt32()];
        Assert.Equal("Row 1", eq.GetProperty("labelL10n").GetProperty("values").GetProperty("en").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Читач_із_Deny_на_аркуш_не_бачить_його_чисел_і_застарілості()
    {
        var s = await ArrangeAsync(how: "deny").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var (status, body) = await YearAsync(client, s.DocumentId, 2026).ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

        Assert.DoesNotContain(s.OutHidden, body, StringComparison.Ordinal);
        Assert.Contains(s.OutAdditive, body, StringComparison.Ordinal);
        var view = JsonDocument.Parse(body).RootElement;
        Assert.True(view.GetProperty("freshnessHidden").GetBoolean());
        Assert.DoesNotContain(
            view.GetProperty("months").EnumerateArray(), m => m.GetProperty("runState").GetString() == "outOfDate");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Без_Calculation_View_403_рік_поза_межами_422_експорт_202()
    {
        var s = await ArrangeAsync(how: "none").ConfigureAwait(true);
        var plain = await ArrangeAsync(how: "noCalc").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        using var noCalc = await SignedInAsync(app, plain.UserName).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, (await YearAsync(noCalc, plain.DocumentId, 2026).ConfigureAwait(true)).Status);
        Assert.Equal((HttpStatusCode)422, (await YearAsync(client, s.DocumentId, 1999).ConfigureAwait(true)).Status);

        using var export = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year/export", UriKind.Relative),
            new { year = 2026, layout = "long", rowIds = OneRow,language = "en" }).ConfigureAwait(true);
        var exportBody = await export.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(export.StatusCode == HttpStatusCode.Accepted, $"{export.StatusCode}: {exportBody}\n{app.ErrorsText}");
        Assert.False(string.IsNullOrEmpty(JsonDocument.Parse(exportBody).RootElement.GetProperty("jobId").GetString()));

        using var badLayout = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year/export", UriKind.Relative),
            new { year = 2026, layout = "cube" }).ConfigureAwait(true);
        Assert.Equal((HttpStatusCode)422, badLayout.StatusCode);
    }

    // ── допоміжне ────────────────────────────────────────────────────────────────────────

    private static JsonElement Row(JsonElement view, string rowKey, string output)
    {
        var equipment = view.GetProperty("equipment").EnumerateArray().ToList();
        return view.GetProperty("rows").EnumerateArray().Single(r =>
            r.GetProperty("outputCode").GetString() == output
            && equipment[r.GetProperty("equipment").GetInt32()].GetProperty("rowKey").GetString() == rowKey);
    }

    private static List<decimal?> Values(JsonElement row)
        => [.. row.GetProperty("values").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? (decimal?)null : Dec(v))];

    /// <summary>Десяткове API — рядком без втрати знаків (D-30), не JSON-числом.</summary>
    private static decimal Dec(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? decimal.Parse(value.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
            : value.GetDecimal();

    private static async Task<(HttpStatusCode Status, string Body)> YearAsync(HttpClient client, long documentId, int year)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year?year={year.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private sealed record Scenario(
        long DocumentId, string UserName, string RowKey1, string RowKey2, string OutAdditive, string OutRate, string OutHidden);

    /// <summary>
    /// Документ на 2026: січень (відкритий, актуальний прогін проєкту), лютий (відкритий, неактуальний прогін зі
    /// 100 і актуальний — 2.5/4; вхід змінено після прогону → застарілий; формула шаблону 9), березень (відкритий,
    /// без прогону), квітень (періоду немає). Схований аркуш B — вихід OUT_H, прив'язаний до його колонки.
    /// </summary>
    /// <param name="how"><c>none</c> — звичайна роль; <c>deny</c> — Deny на аркуш B; <c>noCalc</c> — без Calculation.View.</param>
    private async Task<Scenario> ArrangeAsync(string how)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var b = await builder.BuildAsync().ConfigureAwait(false);
        await builder.OpenPeriodAsync(b).ConfigureAwait(false);
        var feb = await builder.AddPeriodInstanceAsync(b, PeriodKey.Create(2026, 2)).ConfigureAwait(false);
        await builder.AddPeriodAsync(b, PeriodKey.Create(2026, 3)).ConfigureAwait(false);

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var userName = $"cyv{Guid.NewGuid():N}"[..20];
        var outAdditive = $"E_A_{tag}";
        var outRate = $"RATE_{tag}";
        var outHidden = $"OUTH_{tag}";
        var rowKeys = await RowKeysAsync(b).ConfigureAwait(false);

        await using var db = Context();
        foreach (var key in new[] { 202602, 202603 })
        {
            var period = await db.Periods.SingleAsync(p => p.ProjectId == b.ProjectId && p.PeriodKeyValue == key).ConfigureAwait(false);
            period.AdvanceTo(PeriodState.Open, Now);
        }

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create($"CYVB{tag}"), Name($"Hidden {tag}"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var tableB = new TableDef(
            sheetB.Id, EcrCode.Create($"CYTB{tag}"), Name($"Hidden table {tag}"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheetB.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);
        var columnB = new ColumnDef(tableB.Id, EcrCode.Create($"CYCB{tag}"), Name("Col B"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(columnB);

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Calc year role"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in how == "noCalc" ? NoCalcPermissions : FullPermissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Write));
        if (how == "deny")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, sheetB.Id, GrantLevel.Read, isDeny: true));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));

        var methodology = new Methodology(EcrCode.Create($"CY{tag}"), Name("Combustion"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var version = new MethodologyVersion(methodology.Id, "1.2", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        db.CalculationBindings.Add(new CalculationBinding(b.TableDefId, b.ColumnDefIds[1], methodology.Id, outAdditive, "{}"));
        db.CalculationBindings.Add(new CalculationBinding(b.TableDefId, b.ColumnDefIds[1], methodology.Id, outRate, "{}"));
        db.CalculationBindings.Add(new CalculationBinding(tableB.Id, columnB.Id, methodology.Id, outHidden, "{}"));

        var jan = Run(b.ProjectId, 202601, current: true);
        var febOld = Run(b.ProjectId, 202602, current: false);
        var febNew = Run(b.ProjectId, 202602, current: true);
        db.CalculationRuns.AddRange(jan, febOld, febNew);

        // Формула шаблону: R1 · колонка 3, лютий.
        db.CellValues.Add(new CellValue(
            new CellAddress(PeriodKey.Create(2026, 2), feb.RowIds[0], b.ColumnDefIds[2]), b.TableDefId,
            new CellValueData { ValueNumeric = 9m, IsCalculated = true }));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tonne = await UnitAsync(db, "Mass").ConfigureAwait(false);
        var rate = await UnitAsync(db, "MassFlow").ConfigureAwait(false);
        var results = new (CalculationRun Run, int Period, long Instance, string Row, string Output, decimal Value, int Unit)[]
        {
            (jan, 202601, b.TableInstanceId, rowKeys[0], outAdditive, 1.5m, tonne),
            (jan, 202601, b.TableInstanceId, rowKeys[0], outRate, 7m, rate),
            (jan, 202601, b.TableInstanceId, rowKeys[0], outHidden, 3m, tonne),
            (febOld, 202602, feb.TableInstanceId, rowKeys[0], outAdditive, 100m, tonne),
            (febNew, 202602, feb.TableInstanceId, rowKeys[0], outAdditive, 2.5m, tonne),
            (febNew, 202602, feb.TableInstanceId, rowKeys[1], outAdditive, 4m, tonne),
        };
        foreach (var r in results)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId, TableInstanceId)
                VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {r.Run.Id}, {version.Id}, {r.Period}, {b.DocumentId},
                        {r.Row}, {r.Output}, CAST({r.Value} AS decimal(34,16)), {r.Unit}, {r.Instance})
                """).ConfigureAwait(false);
        }

        // Лютий: вхід змінено після прогону.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES ({Now.AddHours(1)}, 202602, {b.DocumentId}, {feb.RowIds[0]}, {rowKeys[0]}, {b.ColumnDefIds[1]}, N'1', N'2', {user.Id}, N'UserEdit', 0)
            """).ConfigureAwait(false);

        return new Scenario(b.DocumentId, userName, rowKeys[0], rowKeys[1], outAdditive, outRate, outHidden);
    }

    private static CalculationRun Run(int projectId, int period, bool current)
    {
        var run = new CalculationRun(projectId, period, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
        if (current)
        {
            run.MakeCurrent();
        }
        else
        {
            run.Supersede();
        }

        return run;
    }

    private static async Task<int> UnitAsync(EcrDbContext db, string dimension)
        => await (from u in db.Units.AsNoTracking()
                  join d in db.Dimensions.AsNoTracking() on u.DimensionId equals d.Id
                  where d.Code == dimension
                  orderby u.Id
                  select u.Id).FirstAsync().ConfigureAwait(false);

    private async Task<List<string>> RowKeysAsync(TestDocument b)
    {
        await using var db = Context();
        return await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == b.PeriodKey.Value && r.TableInstanceId == b.TableInstanceId)
            .OrderBy(r => r.Ordinal)
            .Select(r => r.RowKeyValue)
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
