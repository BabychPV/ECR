using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents.CalculationYear;
using Ecr.Application.Security;
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
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Рік_зводить_місяці_лише_з_актуальних_прогонів_зі_станами_і_сумою_адитивних()
    {
        var s = await ArrangeAsync(how: "none").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var year = await YearAsync(client, s.DocumentId, 2026).ConfigureAwait(true);
        Assert.True(year.Status == HttpStatusCode.OK, $"{year.Status}: {year.Body}\n{app.ErrorsText}");
        var summary = year.Summary;

        var months = summary.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal(12, months.Count);
        Assert.Equal("current", months[0].GetProperty("runState").GetString());
        Assert.Equal("outOfDate", months[1].GetProperty("runState").GetString());
        Assert.Equal("noRun", months[2].GetProperty("runState").GetString());
        Assert.Equal("open", months[2].GetProperty("periodState").GetString());
        Assert.Equal("notOpened", months[3].GetProperty("runState").GetString());
        Assert.False(summary.GetProperty("freshnessHidden").GetBoolean());

        // Зведення без жодного числа: значення — лише на сторінках таблиць.
        Assert.DoesNotContain("\"states\"", year.SummaryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"yearTotal\"", year.SummaryBody, StringComparison.Ordinal);
        Assert.Equal(year.Pages.Sum(p => p.Page.GetProperty("rows").GetArrayLength()), summary.GetProperty("totalRows").GetInt32());

        // Контроль до тестів Deny: без Deny усе, що аркуш B дає (вихід методології, формула шаблону, назви аркуша й
        // таблиці, підпис рядка), видно — інакше «не бачить» нижче нічого не доводить.
        foreach (var secret in s.SheetBSecrets)
        {
            Assert.Contains(secret, year.Body, StringComparison.Ordinal);
        }

        Assert.Contains(TableKeys(summary), k => k == s.TableBKey);

        // Число до D-401 (без екземпляра) — у єдиній таблиці місяця з таким рядком.
        var legacy = Row(year, s.DynKey, s.OutAdditive);
        Assert.Equal(s.TableBCode, TableOf(summary, legacy.Table).GetProperty("tableCode").GetString());

        var export = await ExportTextAsync(app, s).ConfigureAwait(true);
        foreach (var secret in s.SheetBSecrets)
        {
            Assert.Contains(secret, export, StringComparison.Ordinal);
        }

        // R1 · E_A (t): січень 1.5, лютий 2.5 актуального прогону (100 неактуального — не потрапляє); сума 4 за 2/12.
        var r1 = Row(year, s.RowKey1, s.OutAdditive).Row;
        Assert.Equal([1.5m, 2.5m], Values(r1).Take(2).Select(v => v!.Value));
        Assert.All(Values(r1).Skip(2), v => Assert.Null(v));
        Assert.Equal("vvnnnnnnnnnn", r1.GetProperty("states").GetString());
        Assert.Equal(4m, Dec(r1.GetProperty("yearTotal")));
        Assert.Equal(2, r1.GetProperty("monthsIncluded").GetInt32());
        Assert.DoesNotContain("100", r1.GetProperty("values").GetRawText(), StringComparison.Ordinal);

        // Значення без хвостових нулів масштабу decimal(34,16): «1.5», а не «1.5000000000000000».
        Assert.Equal("1.5", r1.GetProperty("values")[0].GetString());

        // R2 · E_A: лише лютий; у січні прогін був, а числа немає — «a».
        var r2 = Row(year, s.RowKey2, s.OutAdditive).Row;
        Assert.Equal("avnnnnnnnnnn", r2.GetProperty("states").GetString());
        Assert.Equal(4m, Dec(r2.GetProperty("yearTotal")));
        Assert.Equal(1, r2.GetProperty("monthsIncluded").GetInt32());

        // Неадитивна одиниця: числа є, суми немає.
        var (ratePage, rate, _) = Row(year, s.RowKey1, s.OutRate);
        Assert.Equal(7m, Values(rate)[0]);
        Assert.Equal(JsonValueKind.Null, rate.GetProperty("yearTotal").ValueKind);
        var rateUnit = ratePage.GetProperty("units")[rate.GetProperty("unit").GetInt32()];
        Assert.False(rateUnit.GetProperty("additive").GetBoolean());

        // Формула шаблону (IsCalculated) — окремий вихід без методології, з підписом рядка шаблону.
        var (formulaPage, formula) = year.Pages
            .SelectMany(p => p.Page.GetProperty("rows").EnumerateArray().Select(r => (p.Page, Row: r)))
            .Single(x => x.Row.GetProperty("column").ValueKind == JsonValueKind.Number
                         && x.Row.GetProperty("outputCode").GetString() != s.FormulaCode);
        Assert.Equal(JsonValueKind.Null, formula.GetProperty("methodology").ValueKind);
        Assert.Equal(9m, Values(formula)[1]);
        var eq = formulaPage.GetProperty("equipment")[formula.GetProperty("equipment").GetInt32()];
        Assert.Equal("Row 1", eq.GetProperty("labelL10n").GetProperty("values").GetProperty("en").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Фільтри_зведення_і_сторінок_одне_правило_сторінки_без_перекриття()
    {
        var s = await ArrangeAsync(how: "none").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var root = $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year";

        // Пошук за підписом рядка таблиці B (лише в ній): зведення рахує 2 виходи (OUTH і число до D-401) у таблиці B, 0 деінде.
        var found = JsonDocument.Parse(await GetOkAsync(app, client, $"{root}/summary?year=2026&q={Uri.EscapeDataString(s.LabelName.ToLowerInvariant())}").ConfigureAwait(true)).RootElement;
        var matched = found.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("key").GetString()!, t => t.GetProperty("matched").GetInt32());
        Assert.Equal(matched.Values.Sum(), found.GetProperty("matchedRows").GetInt32());
        Assert.True(matched[s.TableBKey] >= 2, string.Join(", ", matched));
        Assert.All(matched.Where(kv => kv.Key != s.TableBKey), kv => Assert.Equal(0, kv.Value));
        var rows = JsonDocument.Parse(await GetOkAsync(app, client, $"{root}/rows?year=2026&table={s.TableBKey}&q={Uri.EscapeDataString(s.LabelName)}").ConfigureAwait(true)).RootElement;
        Assert.Equal(matched[s.TableBKey], rows.GetProperty("total").GetInt32());

        // Методологія «formula» — лише формули шаблону.
        var formulas = JsonDocument.Parse(await GetOkAsync(app, client, $"{root}/summary?year=2026&methodology=formula").ConfigureAwait(true)).RootElement;
        Assert.Equal(2, formulas.GetProperty("matchedRows").GetInt32());

        // Сторінки по 1: послідовні, без перекриття, разом — уся таблиця; ідентифікатори виходів ті самі, що й у повній сторінці.
        var all = JsonDocument.Parse(await GetOkAsync(app, client, $"{root}/rows?year=2026&table={s.TableAKey}").ConfigureAwait(true)).RootElement;
        var ids = all.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        var paged = new List<string?>();
        for (var offset = 0; offset < ids.Count; offset++)
        {
            var one = JsonDocument.Parse(await GetOkAsync(app, client, $"{root}/rows?year=2026&table={s.TableAKey}&offset={offset}&limit=1").ConfigureAwait(true)).RootElement;
            Assert.Equal(ids.Count, one.GetProperty("total").GetInt32());
            paged.AddRange(one.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        }

        Assert.Equal(ids, paged);
        Assert.True(ids.Count >= 3, string.Join(", ", ids));
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

        var year = await YearAsync(client, s.DocumentId, 2026).ConfigureAwait(true);
        Assert.True(year.Status == HttpStatusCode.OK, $"{year.Status}: {year.Body}\n{app.ErrorsText}");

        // Той самий arrange, що й у тесті без Deny (там кожен із цих рядків є): вихід, формула, аркуш, таблиця, підпис.
        foreach (var secret in s.SheetBSecrets)
        {
            Assert.DoesNotContain(secret, year.Body, StringComparison.Ordinal);
        }

        Assert.Contains(s.OutAdditive, year.Body, StringComparison.Ordinal);
        var summary = year.Summary;
        Assert.True(summary.GetProperty("freshnessHidden").GetBoolean());
        Assert.DoesNotContain(
            summary.GetProperty("months").EnumerateArray(), m => m.GetProperty("runState").GetString() == "outOfDate");
        Assert.All(
            summary.GetProperty("months").EnumerateArray(),
            m => Assert.Equal(JsonValueKind.Null, m.GetProperty("calculatedAt").ValueKind));

        // ⛔ Таблиці схованого аркуша у зведенні немає, а її сторінка — 200 без жодного рядка (як таблиця без виходів),
        // і в ній теж нічого з аркуша B — ні з фільтром, ні без.
        Assert.DoesNotContain(TableKeys(summary), k => k == s.TableBKey);
        var root = $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year";
        foreach (var query in new[] { "", $"&q={Uri.EscapeDataString(s.LabelName)}", "&methodology=formula" })
        {
            var hidden = await GetOkAsync(app, client, $"{root}/rows?year=2026&table={s.TableBKey}{query}").ConfigureAwait(true);
            var page = JsonDocument.Parse(hidden).RootElement;
            Assert.Equal(0, page.GetProperty("total").GetInt32());
            Assert.Equal(0, page.GetProperty("rows").GetArrayLength());
            foreach (var secret in s.SheetBSecrets)
            {
                Assert.DoesNotContain(secret, hidden, StringComparison.Ordinal);
            }
        }

        // Стан місяця на сторінці видимої таблиці — той самий, що у зведенні (рахується по всьому документу):
        // у березні прогону немає — «n», у січні прогін є, а числа R2 немає — «a».
        Assert.Equal("avnnnnnnnnnn", Row(year, s.RowKey2, s.OutAdditive).Row.GetProperty("states").GetString());

        // Число до D-401 з ключем рядка схованої таблиці лишається, але без таблиці (а не в схованій).
        var legacy = Row(year, s.DynKey, s.OutAdditive);
        Assert.Equal(0, TableOf(summary, legacy.Table).GetProperty("tableDefId").GetInt32());

        // Книга експорту будується тим самим будівником для профілю замовника — і теж без аркуша B.
        var export = await ExportTextAsync(app, s).ConfigureAwait(true);
        Assert.Contains(s.OutAdditive, export, StringComparison.Ordinal);
        foreach (var secret in s.SheetBSecrets)
        {
            Assert.DoesNotContain(secret, export, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("denyColumn")]
    [InlineData("denyFormula")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Deny_на_колонку_видимої_таблиці_ховає_лише_числа_цієї_колонки(string how)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var year = await YearAsync(client, s.DocumentId, 2026).ConfigureAwait(true);
        Assert.True(year.Status == HttpStatusCode.OK, $"{year.Status}: {year.Body}\n{app.ErrorsText}");

        // Таблиця B видима (інші її колонки читаються): її назва й підпис рядка лишаються.
        Assert.Contains(s.TableBName, year.Body, StringComparison.Ordinal);
        Assert.Contains(s.LabelName, year.Body, StringComparison.Ordinal);
        var (hidden, shown) = how == "denyColumn" ? (s.OutHidden, s.FormulaCode) : (s.FormulaCode, s.OutHidden);
        Assert.DoesNotContain(hidden, year.Body, StringComparison.Ordinal);
        Assert.Contains(shown, year.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Deny_звужений_періодом_ховає_числа_й_підпис_рядка_лише_за_ці_місяці()
    {
        var s = await ArrangeAsync(how: "denyJan").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var year = await YearAsync(client, s.DocumentId, 2026).ConfigureAwait(true);
        Assert.True(year.Status == HttpStatusCode.OK, $"{year.Status}: {year.Body}\n{app.ErrorsText}");

        // Лютий аркуша B видно, січень — ні (число й підпис рядка, що є лише в січні).
        var outH = Row(year, s.DynKey, s.OutHidden).Row;
        Assert.Null(Values(outH)[0]);
        Assert.Equal(5m, Values(outH)[1]);
        Assert.DoesNotContain(s.LabelName, year.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(s.FormulaCode, year.Body, StringComparison.Ordinal);

        // Пошук за підписом, схованим у січні, не знаходить рядка (підпис не читається — і в зведенні, і на сторінці).
        var root = $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year";
        var found = await GetOkAsync(app, client, $"{root}/summary?year=2026&q={Uri.EscapeDataString(s.LabelName)}").ConfigureAwait(true);
        Assert.Equal(0, JsonDocument.Parse(found).RootElement.GetProperty("matchedRows").GetInt32());

        var export = await ExportTextAsync(app, s).ConfigureAwait(true);
        Assert.Contains(s.OutHidden, export, StringComparison.Ordinal);
        Assert.DoesNotContain(s.LabelName, export, StringComparison.Ordinal);
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

        // Сторінка: невідомий ключ таблиці чи розмір поза 1…1000 — 422, а не 500 і не порожньо.
        var root = $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year";
        foreach (var bad in new[] { "&table=abc", "&table=1~", $"&table={s.TableAKey}&limit=0", $"&table={s.TableAKey}&limit=1001", $"&table={s.TableAKey}&offset=-1" })
        {
            using var response = await client.GetAsync(new Uri($"{root}/rows?year=2026{bad}", UriKind.Relative)).ConfigureAwait(true);
            Assert.True(response.StatusCode == (HttpStatusCode)422, $"{bad}: {response.StatusCode}");
        }

        using (var noCalcRows = await noCalc.GetAsync(new Uri($"/api/v1/documents/{plain.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year/rows?year=2026&table=1", UriKind.Relative)).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noCalcRows.StatusCode);
        }

        using var export = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year/export", UriKind.Relative),
            new { year = 2026, layout = "long", filtered = true, problemsOnly = true, language = "en" }).ConfigureAwait(true);
        var exportBody = await export.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(export.StatusCode == HttpStatusCode.Accepted, $"{export.StatusCode}: {exportBody}\n{app.ErrorsText}");
        Assert.False(string.IsNullOrEmpty(JsonDocument.Parse(exportBody).RootElement.GetProperty("jobId").GetString()));

        using var badLayout = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year/export", UriKind.Relative),
            new { year = 2026, layout = "cube" }).ConfigureAwait(true);
        Assert.Equal((HttpStatusCode)422, badLayout.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Рік_без_жодного_прогону_зведення_і_сторінка_200_а_не_500()
    {
        // Документ із таблицями й формулою, але без прогонів: зведення агрегує формули, числа — порожні (e2e 12.11).
        var s = await ArrangeAsync(how: "noRuns").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var root = $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year";

        var summary = JsonDocument.Parse(await GetOkAsync(app, client, $"{root}/summary?year=2026").ConfigureAwait(true)).RootElement;
        foreach (var key in TableKeys(summary))
        {
            await GetOkAsync(app, client, $"{root}/rows?year=2026&table={key}&limit=200").ConfigureAwait(true);
        }

        Assert.DoesNotContain(s.OutAdditive, summary.GetRawText(), StringComparison.Ordinal);
    }

    // ── допоміжне ────────────────────────────────────────────────────────────────────────

    /// <summary>Вихід за ключем рядка й кодом серед усіх сторінок: (сторінка, рядок, ключ таблиці).</summary>
    private static (JsonElement Page, JsonElement Row, string Table) Row(YearResponse year, string rowKey, string output)
        => year.Pages
            .SelectMany(p =>
            {
                var equipment = p.Page.GetProperty("equipment").EnumerateArray().ToList();
                return p.Page.GetProperty("rows").EnumerateArray()
                    .Where(r => r.GetProperty("outputCode").GetString() == output
                                && equipment[r.GetProperty("equipment").GetInt32()].GetProperty("rowKey").GetString() == rowKey)
                    .Select(r => (p.Page, r, p.Table));
            })
            .Single();

    private static IEnumerable<string?> TableKeys(JsonElement summary)
        => summary.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("key").GetString());

    private static JsonElement TableOf(JsonElement summary, string key)
        => summary.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("key").GetString() == key);

    private static async Task<string> GetOkAsync(EcrApiFactory app, HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode}: {body}\n{app.ErrorsText}");
        return body;
    }

    /// <summary>Зведення і всі сторінки всіх його таблиць (по 1000) — те, що екран бачить, розгорнувши все.</summary>
    private sealed record YearResponse(
        HttpStatusCode Status, string SummaryBody, JsonElement Summary, IReadOnlyList<(string Table, JsonElement Page)> Pages, string Body);

    private static List<decimal?> Values(JsonElement row)
        => [.. row.GetProperty("values").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? (decimal?)null : Dec(v))];

    /// <summary>Десяткове API — рядком без втрати знаків (D-30), не JSON-числом.</summary>
    private static decimal Dec(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? decimal.Parse(value.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
            : value.GetDecimal();

    private static async Task<YearResponse> YearAsync(HttpClient client, long documentId, int year)
    {
        var root = $"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year";
        var y = year.ToString(CultureInfo.InvariantCulture);
        using var response = await client.GetAsync(new Uri($"{root}/summary?year={y}", UriKind.Relative)).ConfigureAwait(false);
        var summaryBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new YearResponse(response.StatusCode, summaryBody, default, [], summaryBody);
        }

        var summary = JsonDocument.Parse(summaryBody).RootElement;
        var pages = new List<(string, JsonElement)>();
        var bodies = new List<string> { summaryBody };
        foreach (var key in TableKeys(summary))
        {
            using var page = await client.GetAsync(new Uri($"{root}/rows?year={y}&table={key}&limit=1000", UriKind.Relative)).ConfigureAwait(false);
            var body = await page.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(page.StatusCode == HttpStatusCode.OK, $"{key}: {page.StatusCode}: {body}");
            bodies.Add(body);
            pages.Add((key!, JsonDocument.Parse(body).RootElement));
        }

        return new YearResponse(response.StatusCode, summaryBody, summary, pages, string.Join('\n', bodies));
    }

    /// <summary>Книга експорту для профілю замовника — тим самим шляхом, що й задача експорту (без черги задач).</summary>
    private static async Task<string> ExportTextAsync(EcrApiFactory app, Scenario s)
    {
        using var scope = app.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAccessDecisionService>();
        var builder = scope.ServiceProvider.GetRequiredService<CalculationYearViewBuilder>();
        var profile = await access.BuildProfileAsync(s.UserId, CancellationToken.None).ConfigureAwait(false);
        var view = await builder.BuildAsync(profile, s.DocumentId, 2026, CancellationToken.None).ConfigureAwait(false);
        var sheets = CalculationYearWorkbook.Sheets(view, new CalculationYearExportSpec(2026, ExportCalculationYearHandler.Long, null), "en");
        return string.Join('\n', sheets.SelectMany(w => w.Rows).SelectMany(r => r).Select(v => v ?? ""));
    }

    private sealed record Scenario(
        long DocumentId, int UserId, string UserName, string RowKey1, string RowKey2, string OutAdditive, string OutRate,
        string OutHidden, string DynKey, string SheetBName, string TableBName, string TableBCode, string LabelName, string FormulaCode,
        string TableAKey, string TableBKey)
    {
        /// <summary>Усе, що аркуш B дає в огляд: вихід методології, формула шаблону, аркуш, таблиця, підпис рядка.</summary>
        public string[] SheetBSecrets => [OutHidden, FormulaCode, SheetBName, TableBName, LabelName];
    }

    /// <summary>
    /// Документ на 2026: січень (відкритий, актуальний прогін проєкту), лютий (відкритий, неактуальний прогін зі
    /// 100 і актуальний — 2.5/4; вхід змінено після прогону → застарілий; формула шаблону 9), березень (відкритий,
    /// без прогону), квітень (періоду немає). Аркуш B — таблиця B з динамічним рядком (екземпляри січня й лютого):
    /// вихід OUTH, прив'язаний до колонки B (січень 3, лютий 5), формула шаблону в колонці F (січень), підпис рядка
    /// в колонці назви (лише січень) і число до D-401 без екземпляра з ключем цього рядка (січень).
    /// </summary>
    /// <param name="how">
    /// <c>none</c> — звичайна роль; <c>deny</c> — Deny на аркуш B; <c>denyColumn</c> — Deny на колонку B;
    /// <c>denyFormula</c> — Deny на колонку F; <c>denyJan</c> — Deny на аркуш B у ролі, звуженій січнем;
    /// <c>noCalc</c> — без Calculation.View.
    /// </param>
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
        var sheetBName = $"Hidden {tag}";
        var tableBName = $"Hidden table {tag}";
        var tableBCode = $"CYTB{tag}";
        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create($"CYVB{tag}"), Name(sheetBName), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var tableB = new TableDef(
            sheetB.Id, EcrCode.Create(tableBCode), Name(tableBName), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheetB.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);
        var formulaCode = $"CYFB{tag}"; // і код, і заголовок колонки формули: книга експорту пише заголовок
        var nameB = new ColumnDef(tableB.Id, EcrCode.Create($"CYNB{tag}"), Name("Name B"), 1, CellDataType.String);
        var columnB = new ColumnDef(tableB.Id, EcrCode.Create($"CYCB{tag}"), Name("Col B"), 2, CellDataType.Decimal);
        var formulaB = new ColumnDef(tableB.Id, EcrCode.Create(formulaCode), Name(formulaCode), 3, CellDataType.Decimal);
        db.ColumnDefs.AddRange(nameB, columnB, formulaB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Екземпляри таблиці B (січень, лютий) з одним динамічним рядком (без RowDef — підпис із колонки назви).
        var dynKey = $"DYN_{tag}";
        var labelName = $"Pump {tag}";
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instB = new Dictionary<int, (long Instance, long Row)>();
        foreach (var key in new[] { 202601, 202602 })
        {
            var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
            var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
            db.TableInstances.Add(new TableInstance(new PeriodKey(key), instanceId, b.DocumentId, tableB.Id, Now));
            db.TableRows.Add(new TableRow(new PeriodKey(key), rowId, instanceId, RowKey.Create(dynKey), 1, Now));
            instB[key] = (instanceId, rowId);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        db.CellValues.Add(new CellValue(
            new CellAddress(PeriodKey.Create(2026, 1), instB[202601].Row, nameB.Id), tableB.Id,
            new CellValueData { ValueString = labelName }));
        db.CellValues.Add(new CellValue(
            new CellAddress(PeriodKey.Create(2026, 1), instB[202601].Row, formulaB.Id), tableB.Id,
            new CellValueData { ValueNumeric = 6.5m, IsCalculated = true }));

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
        switch (how)
        {
            case "deny":
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, sheetB.Id, GrantLevel.Read, isDeny: true));
                break;
            case "denyColumn":
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Column, columnB.Id, GrantLevel.Read, isDeny: true));
                break;
            case "denyFormula":
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Column, formulaB.Id, GrantLevel.Read, isDeny: true));
                break;
            case "denyJan":
                // Друга роль, звужена січнем: Deny на аркуш B діє лише в січні (основна роль — без обмежень).
                var janRole = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Calc year Jan deny"));
                db.Roles.Add(janRole);
                await db.SaveChangesAsync().ConfigureAwait(false);
                db.ResourceGrants.Add(new ResourceGrant(janRole.Id, ResourceKind.Sheet, sheetB.Id, GrantLevel.Read, isDeny: true));
                var janAssignment = new RoleAssignment(janRole.Id, user.Id, principalSid: null);
                janAssignment.SetScope(RoleAssignmentScope.Create(
                    [b.ProjectId], periodFrom: PeriodKey.Create(2026, 1), periodTo: PeriodKey.Create(2026, 1)));
                db.RoleAssignments.Add(janAssignment);
                break;
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
        if (how != "noRuns")
        {
            db.CalculationRuns.AddRange(jan, febOld, febNew);
        }

        // Формула шаблону: R1 · колонка 3, лютий.
        db.CellValues.Add(new CellValue(
            new CellAddress(PeriodKey.Create(2026, 2), feb.RowIds[0], b.ColumnDefIds[2]), b.TableDefId,
            new CellValueData { ValueNumeric = 9m, IsCalculated = true }));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var scenario = new Scenario(
            b.DocumentId, user.Id, userName, rowKeys[0], rowKeys[1], outAdditive, outRate, outHidden, dynKey,
            sheetBName, tableBName, tableBCode, labelName, formulaCode,
            b.TableDefId.ToString(CultureInfo.InvariantCulture), tableB.Id.ToString(CultureInfo.InvariantCulture));
        if (how == "noRuns")
        {
            return scenario;
        }

        var tonne = await UnitAsync(db, "Mass").ConfigureAwait(false);
        var rate = await UnitAsync(db, "MassFlow").ConfigureAwait(false);
        var results = new (CalculationRun Run, int Period, long Instance, string Row, string Output, decimal Value, int Unit)[]
        {
            (jan, 202601, b.TableInstanceId, rowKeys[0], outAdditive, 1.5m, tonne),
            (jan, 202601, b.TableInstanceId, rowKeys[0], outRate, 7m, rate),
            (jan, 202601, instB[202601].Instance, dynKey, outHidden, 3m, tonne),
            (febNew, 202602, instB[202602].Instance, dynKey, outHidden, 5m, tonne),
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

        // Число до D-401: без екземпляра таблиці, ключ рядка — лише в таблиці B.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId, TableInstanceId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {jan.Id}, {version.Id}, 202601, {b.DocumentId},
                    {dynKey}, {outAdditive}, CAST(11 AS decimal(34,16)), {tonne}, NULL)
            """).ConfigureAwait(false);

        // Лютий: вхід змінено після прогону.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES ({Now.AddHours(1)}, 202602, {b.DocumentId}, {feb.RowIds[0]}, {rowKeys[0]}, {b.ColumnDefIds[1]}, N'1', N'2', {user.Id}, N'UserEdit', 0)
            """).ConfigureAwait(false);

        return scenario;
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
