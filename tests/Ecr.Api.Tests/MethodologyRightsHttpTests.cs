// tests/Ecr.Api.Tests/MethodologyRightsHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// HTTP-відмова за правом на КОЖНОЇ дії контролера методологій (ФВ-6.14, ФВ-9.x): без права —
/// <c>403 ECR-AUTH-0403</c> з назвою права, однаково для наявних і неіснуючих ідентифікаторів
/// (не 404 — існування методології й версії не розкривається), і нічого не змінюється.
/// </summary>
/// <remarks>
/// ⛔ Раніше 403 на дії авторингу (створення, формули, константи, правила, виходи, тести, режими,
/// прив'язки, публікація) перевіряли лише обробникові тести; на HTTP відмову за правом ніхто не
/// гнав: <c>RouteOwnershipNotFoundTests</c> бере користувача З УСІМА правами й доводить 404, а
/// <c>NegativePathSweepTests</c> — 422. Порядок серверний: право → належність версії → стан.
///
/// ⚠ Таблиця дій нижче — повна за контролером (крім <c>GET /methodologies/{id}/…</c> без права
/// читання, що в тому ж проході як «читання»). Нова дія контролера без рядка тут — привід дописати.
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>scope.RequireAsync(…)</c> / <c>PermissionCheck</c> з будь-якої
/// дії — червоніє її рядок (відповідь 200/404 замість 403).
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyRightsHttpTests(SqlServerFixture sql)
{
    private const int Missing = 999_999_999;

    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Дія контролера: назва для звіту, метод, шлях із підстановками, тіло й право, якого вона вимагає.</summary>
    private sealed record Op(string Name, HttpMethod Method, string Path, object? Body, string Permission);

    /// <summary>Дії запису й публікації: без свого права — 403, навіть із <c>Calculation.View</c>.</summary>
    private static readonly Op[] Writes =
    [
        new("POST /methodologies", HttpMethod.Post, "/api/v1/methodologies",
            new { code = "HNRIGHTS", nameL10n = new Dictionary<string, string> { ["en"] = "Rights" }, kind = 0, group = (string?)null },
            "Calculation.EditFormula"),
        new("POST versions", HttpMethod.Post, "/api/v1/methodologies/{m}/versions",
            new { versionNumber = "2.0", copyFromVersionId = (int?)null, level = "Configuration" },
            "Calculation.EditFormula"),
        new("PUT formula", HttpMethod.Put, "/api/v1/methodologies/{m}/versions/{v}/formulas/tons",
            new { expression = "1", resultType = 0, outputUnitId = (int?)null, argumentsCsv = (string?)null },
            "Calculation.EditFormula"),
        new("DELETE formula", HttpMethod.Delete, "/api/v1/methodologies/{m}/versions/{v}/formulas/tons", null,
            "Calculation.EditFormula"),
        new("PUT constant", HttpMethod.Put, "/api/v1/methodologies/{m}/versions/{v}/constants/k1",
            new { kind = 0, value = 5m, unitId = (int?)null },
            "Calculation.EditConstant"),
        new("PUT rule", HttpMethod.Put, "/api/v1/methodologies/{m}/versions/{v}/rules/r1",
            new { matchJson = "{}", priority = 1, isActive = true },
            "Calculation.EditRule"),
        new("PUT category-rule", HttpMethod.Put, "/api/v1/methodologies/{m}/versions/{v}/category-rule",
            new { expression = "'A'" },
            "Calculation.EditRule"),
        new("DELETE category-rule", HttpMethod.Delete, "/api/v1/methodologies/{m}/versions/{v}/category-rule", null,
            "Calculation.EditRule"),
        new("PUT required-input", HttpMethod.Put, "/api/v1/methodologies/{m}/versions/{v}/required-inputs/{col}",
            new { severity = 0, hintL10n = (object?)null },
            "Calculation.ManageRequiredInputs"),
        new("PUT output", HttpMethod.Put, "/api/v1/methodologies/{m}/versions/{v}/outputs/extra",
            new { unitId = 1, ordinal = 2 },
            "Calculation.EditFormula"),
        new("PUT test", HttpMethod.Put, "/api/v1/methodologies/{m}/versions/{v}/tests/case9",
            new { inputJson = """{"periodKey":{"value":0},"arguments":[]}""", expectedJson = """{"tons":1}""", tolerance = 0m },
            "Calculation.EditFormula"),
        new("PUT modes", HttpMethod.Put, "/api/v1/methodologies/{m}/versions/{v}/modes",
            new { numericMode = 1, calendarMode = 0, traceLevel = 0 },
            "Calculation.EditFormula"),
        new("PUT binding", HttpMethod.Put, "/api/v1/methodologies/{m}/bindings/{col}/tons",
            new { matchJson = "{}", isActive = true },
            "Calculation.EditRule"),
        new("POST publish", HttpMethod.Post, "/api/v1/methodologies/{m}/versions/{v}/publish",
            new { changeReason = "без права", effectiveFrom = "2026-01-01" },
            "Calculation.Publish"),
    ];

    /// <summary>Дії читання й симуляції: без <c>Calculation.View</c> — 403.</summary>
    private static readonly Op[] Reads =
    [
        new("GET /methodologies", HttpMethod.Get, "/api/v1/methodologies", null, "Calculation.View"),
        new("GET versions", HttpMethod.Get, "/api/v1/methodologies/{m}/versions", null, "Calculation.View"),
        new("GET formulas", HttpMethod.Get, "/api/v1/methodologies/{m}/versions/{v}/formulas", null, "Calculation.View"),
        new("GET constants", HttpMethod.Get, "/api/v1/methodologies/{m}/versions/{v}/constants", null, "Calculation.View"),
        new("GET constant usage", HttpMethod.Get, "/api/v1/methodologies/{m}/versions/{v}/constants/k1/usage", null, "Calculation.View"),
        new("GET rules", HttpMethod.Get, "/api/v1/methodologies/{m}/versions/{v}/rules", null, "Calculation.View"),
        new("GET category-rule", HttpMethod.Get, "/api/v1/methodologies/{m}/versions/{v}/category-rule", null, "Calculation.View"),
        new("GET required-inputs", HttpMethod.Get, "/api/v1/methodologies/{m}/versions/{v}/required-inputs", null, "Calculation.View"),
        new("GET outputs", HttpMethod.Get, "/api/v1/methodologies/{m}/versions/{v}/outputs", null, "Calculation.View"),
        new("GET tests", HttpMethod.Get, "/api/v1/methodologies/{m}/versions/{v}/tests", null, "Calculation.View"),
        new("GET bindings", HttpMethod.Get, "/api/v1/methodologies/{m}/bindings", null, "Calculation.View"),
        new("POST simulate", HttpMethod.Post, "/api/v1/methodologies/{m}/simulate",
            new { methodologyVersionId = 0, periodKey = 202601 }, "Calculation.View"),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Користувач_лише_з_Calculation_View_не_може_жодної_дії_запису_403_і_нічого_не_змінюється()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var viewer = await SystemHealthControllerTests.SignedInAsync(sql, app, "Calculation.View").ConfigureAwait(true);

        // Контроль: читання з тим самим правом працює, інакше 403 нижче доводив би лише «усе заборонено».
        using var control = await viewer.GetAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.DraftId}/formulas", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.True(control.StatusCode == HttpStatusCode.OK, $"{(int)control.StatusCode} {app.ErrorsText}");

        var failures = await RunAsync(app, viewer, Writes, stand).ConfigureAwait(true);
        Assert.True(failures.Count == 0, string.Join('\n', failures));

        await using var db = Db();
        Assert.Equal(1, await db.MethodologyVersions.CountAsync(v => v.MethodologyId == stand.MethodologyId).ConfigureAwait(true));
        Assert.Equal("@Fuel * 2", (await db.MethodologyFormulas.AsNoTracking().SingleAsync(f => f.MethodologyVersionId == stand.DraftId).ConfigureAwait(true)).Expression);
        Assert.Equal(1m, (await db.MethodologyConstants.AsNoTracking().SingleAsync(c => c.MethodologyVersionId == stand.DraftId).ConfigureAwait(true)).Value);
        Assert.Equal(0, await db.MethodologyRules.CountAsync(r => r.MethodologyVersionId == stand.DraftId).ConfigureAwait(true));
        Assert.Equal(1, await db.MethodologyOutputs.CountAsync(o => o.MethodologyVersionId == stand.DraftId).ConfigureAwait(true));
        Assert.Equal(1, await db.MethodologyTestCases.CountAsync(t => t.MethodologyVersionId == stand.DraftId).ConfigureAwait(true));
        Assert.False(await db.Methodologies.AnyAsync(m => m.Code == EcrCode.Create("HNRIGHTS")).ConfigureAwait(true));

        var version = await db.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == stand.DraftId).ConfigureAwait(true);
        Assert.Equal(TemplateVersionStatus.Draft, version.Status);
        Assert.Equal(NumericMode.Legacy, version.NumericMode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Користувач_без_жодного_права_не_читає_і_не_симулює_методології_403()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var nobody = await SystemHealthControllerTests.SignedInAsync(sql, app).ConfigureAwait(true);

        var failures = await RunAsync(app, nobody, Reads, stand).ConfigureAwait(true);

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Користувач_без_жодного_права_не_виконує_і_дій_запису_403()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var nobody = await SystemHealthControllerTests.SignedInAsync(sql, app).ConfigureAwait(true);

        var failures = await RunAsync(app, nobody, Writes, stand).ConfigureAwait(true);

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// Кожну дію — двічі: з наявними ідентифікаторами й з неіснуючими. Обидві відповіді мусять бути
    /// <c>403 ECR-AUTH-0403</c> з назвою права, і однаковими за формою.
    /// </summary>
    private static async Task<List<string>> RunAsync(EcrApiFactory app, HttpClient client, Op[] actions, Stand stand)
    {
        var failures = new List<string>();

        foreach (var action in actions)
        {
            var existing = await SendAsync(client, action, stand.MethodologyId, stand.DraftId, stand.ColumnId)
                .ConfigureAwait(false);
            var missing = await SendAsync(client, action, Missing, Missing, Missing).ConfigureAwait(false);

            foreach (var (label, shape) in new[] { ("наявні id", existing), ("неіснуючі id", missing) })
            {
                if (shape.Status != HttpStatusCode.Forbidden
                    || shape.ErrorCode != "ECR-AUTH-0403"
                    || !shape.Body.Contains(action.Permission, StringComparison.Ordinal))
                {
                    failures.Add($"{action.Name} ({label}): очікували 403 ECR-AUTH-0403 з «{action.Permission}», "
                        + $"отримали {(int)shape.Status} {shape.ErrorCode}: {Trim(shape.Body)}");
                }
            }

            if (existing.MessageKey != missing.MessageKey)
            {
                failures.Add($"{action.Name}: ключ відмови розрізняє наявні й неіснуючі id "
                    + $"({existing.MessageKey} ≠ {missing.MessageKey})");
            }
        }

        if (failures.Count > 0)
        {
            failures.Add(app.ErrorsText);
        }

        return failures;
    }

    private sealed record Shape(HttpStatusCode Status, string? ErrorCode, string? MessageKey, string Body);

    private static async Task<Shape> SendAsync(HttpClient client, Op action, int methodology, int version, int column)
    {
        var path = action.Path
            .Replace("{m}", methodology.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{v}", version.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{col}", column.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        using var request = new HttpRequestMessage(action.Method, new Uri(path, UriKind.Relative));
        if (action.Body is not null)
        {
            // Симуляція несе версію в тілі: наявна — та сама, що в адресі.
            var body = action.Name == "POST simulate"
                ? new { methodologyVersionId = version, periodKey = 202601 }
                : action.Body;
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        string? errorCode = null;
        string? messageKey = null;
        if (text.Length > 0 && text[0] == '{')
        {
            using var json = JsonDocument.Parse(text);
            errorCode = json.RootElement.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
            messageKey = json.RootElement.TryGetProperty("messageKey", out var k) ? k.GetString() : null;
        }

        return new Shape(response.StatusCode, errorCode, messageKey, text);
    }

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private sealed record Stand(int MethodologyId, int DraftId, int ColumnId);

    /// <summary>Методологія з чернеткою: формула, константа, вихід і тест; колонка документа для прив'язок.</summary>
    private async Task<Stand> ArrangeAsync()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await using var db = Db();

        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);

        var methodology = new Methodology(
            EcrCode.Create($"MR{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Rights" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var draft = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(draft);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.MethodologyFormulas.Add(draft.AddFormula(EcrCode.Create("tons"), "@Fuel * 2", FormulaResultType.Number, unit));
        db.MethodologyConstants.Add(draft.AddNumericConstant(EcrCode.Create("k1"), 1m, unit));
        db.MethodologyOutputs.Add(draft.AddOutput(EcrCode.Create("tons"), unit, 1));
        db.MethodologyTestCases.Add(draft.AddTestCase("case1", "{}", """{"tons":1}""", 0m));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(methodology.Id, draft.Id, document.ColumnDefIds[0]);
    }

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
