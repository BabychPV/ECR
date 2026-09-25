// tests/Ecr.Api.Tests/RouteOwnershipNotFoundTests.cs
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
/// Батьківський ідентифікатор у маршруті не декоративний: версія чужої
/// методології й неіснуючий батько — <c>404</c>, а не <c>200</c> (B-07,
/// UX-прохід, четвертий раунд).
/// </summary>
/// <remarks>
/// ⛔ Що відтворили аналітики: <c>GET /methodologies/999999999/versions/4/formulas</c>
/// → <c>200</c> з формулами версії ІНШОЇ методології; публікація, видалення
/// формули й запис константи так само діяли через будь-яку адресу. Поруч —
/// той самий клас «<c>200 []</c> замість <c>404</c>» на гранах ролі, ролях
/// користувача, версіях шаблону, стилях версії і прив'язках методології.
/// </remarks>
[Collection("SqlServer")]
public sealed class RouteOwnershipNotFoundTests(SqlServerFixture sql)
{
    private const int Missing = 999_999_999;

    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Calculation =
    [
        "Calculation.View", "Calculation.EditFormula", "Calculation.EditConstant",
        "Calculation.EditRule", "Calculation.ManageRequiredInputs", "Calculation.Publish",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B-07")]
    public async Task Версія_чужої_методології_за_адресою_іншої_дає_404_і_нічого_не_змінює()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, Calculation).ConfigureAwait(true);

        var own = $"/api/v1/methodologies/{stand.OwnerId}/versions/{stand.DraftId}";
        var foreign = $"/api/v1/methodologies/{stand.StrangerId}/versions/{stand.DraftId}";
        var missing = $"/api/v1/methodologies/{Missing}/versions/{stand.DraftId}";

        // Контроль: за СВОЄЮ адресою читання працює — інакше 404 нижче нічого не доводив би.
        foreach (var part in new[] { "formulas", "constants", "rules", "required-inputs", "outputs", "tests" })
        {
            using var ok = await client.GetAsync(new Uri($"{own}/{part}", UriKind.Relative)).ConfigureAwait(true);
            Assert.True(ok.StatusCode == HttpStatusCode.OK, $"{own}/{part}: {ok.StatusCode} {app.ErrorsText}");

            await AssertNotFoundAsync(client.GetAsync(new Uri($"{foreign}/{part}", UriKind.Relative)), $"GET {part}", app);
            await AssertNotFoundAsync(client.GetAsync(new Uri($"{missing}/{part}", UriKind.Relative)), $"GET {part} (999999999)", app);
        }

        // Запис через чужу адресу — 404, і вміст версії не змінився.
        await AssertNotFoundAsync(client.PutAsJsonAsync(
            new Uri($"{foreign}/formulas/tons", UriKind.Relative),
            new { expression = "@Fuel * 100", resultType = 0, outputUnitId = stand.UnitId, argumentsCsv = (string?)null }),
            "PUT formula", app);
        await AssertNotFoundAsync(client.PutAsJsonAsync(
            new Uri($"{foreign}/constants/k1", UriKind.Relative),
            new { kind = 0, value = 42m, unitId = stand.UnitId }),
            "PUT constant", app);
        await AssertNotFoundAsync(client.PutAsJsonAsync(
            new Uri($"{foreign}/rules/r1", UriKind.Relative),
            new { matchJson = "{}", priority = 1, isActive = true }),
            "PUT rule", app);
        await AssertNotFoundAsync(client.PutAsJsonAsync(
            new Uri($"{foreign}/outputs/extra", UriKind.Relative),
            new { unitId = stand.UnitId, ordinal = 2 }),
            "PUT output", app);
        await AssertNotFoundAsync(client.PutAsJsonAsync(
            new Uri($"{foreign}/tests/case2", UriKind.Relative),
            new { inputJson = "{}", expectedJson = """{"tons":1}""", tolerance = 0m }),
            "PUT test", app);
        await AssertNotFoundAsync(client.PutAsJsonAsync(
            new Uri($"{foreign}/modes", UriKind.Relative),
            new { numericMode = 1, calendarMode = 0, traceLevel = 0 }),
            "PUT modes", app);
        await AssertNotFoundAsync(client.PutAsJsonAsync(
            new Uri($"{foreign}/required-inputs/{stand.ColumnId}", UriKind.Relative),
            new { severity = 0, hintL10n = (object?)null }),
            "PUT required input", app);
        await AssertNotFoundAsync(client.DeleteAsync(
            new Uri($"{foreign}/formulas/tons", UriKind.Relative)),
            "DELETE formula", app);
        await AssertNotFoundAsync(client.PostAsJsonAsync(
            new Uri($"{foreign}/publish", UriKind.Relative),
            new { changeReason = "через чужу адресу", effectiveFrom = "2026-01-01" }),
            "POST publish", app);
        await AssertNotFoundAsync(client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.StrangerId}/simulate", UriKind.Relative),
            new { methodologyVersionId = stand.DraftId, periodKey = 202601 }),
            "POST simulate", app);

        await using var db = Db();
        var formula = await db.MethodologyFormulas.AsNoTracking()
            .SingleAsync(f => f.MethodologyVersionId == stand.DraftId).ConfigureAwait(true);
        Assert.Equal("@Fuel * 2", formula.Expression);

        var constant = await db.MethodologyConstants.AsNoTracking()
            .SingleAsync(c => c.MethodologyVersionId == stand.DraftId).ConfigureAwait(true);
        Assert.Equal(1m, constant.Value);

        var version = await db.MethodologyVersions.AsNoTracking()
            .SingleAsync(v => v.Id == stand.DraftId).ConfigureAwait(true);
        Assert.Equal(TemplateVersionStatus.Draft, version.Status);
        Assert.Equal(NumericMode.Legacy, version.NumericMode);
        Assert.Equal(0, await db.MethodologyRules.CountAsync(r => r.MethodologyVersionId == stand.DraftId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B-07")]
    public async Task Порожній_перелік_неіснуючого_батька_дає_404_а_не_200_з_порожнечею()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app,
            [.. Calculation, "Template.View", "Security.ManageRoles", "Security.ManageUsers"]).ConfigureAwait(true);

        // Контроль: наявний батько з порожнім переліком — законні `200`.
        foreach (var url in new[]
                 {
                     $"/api/v1/methodologies/{stand.StrangerId}/bindings",
                     $"/api/v1/templates/{stand.TemplateId}/versions",
                     $"/api/v1/template-versions/{stand.TemplateVersionId}/styles",
                 })
        {
            using var ok = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(true);
            Assert.True(ok.StatusCode == HttpStatusCode.OK, $"{url}: {ok.StatusCode} {app.ErrorsText}");
        }

        await AssertNotFoundAsync(client.GetAsync(new Uri($"/api/v1/methodologies/{Missing}/bindings", UriKind.Relative)), "bindings", app, "ECR-CALC-0404", $"Methodology {Missing} does not exist.");
        await AssertNotFoundAsync(client.GetAsync(new Uri($"/api/v1/templates/{Missing}/versions", UriKind.Relative)), "template versions", app, "ECR-TMPL-0404", $"Template {Missing} was not found.");
        await AssertNotFoundAsync(client.GetAsync(new Uri($"/api/v1/template-versions/{Missing}/styles", UriKind.Relative)), "styles", app, "ECR-TMPL-0404", $"Template version {Missing} was not found.");
        await AssertNotFoundAsync(client.GetAsync(new Uri($"/api/v1/roles/{Missing}/grants", UriKind.Relative)), "role grants", app, "ECR-SEC-0404", $"Role {Missing} does not exist.");
        await AssertNotFoundAsync(client.GetAsync(new Uri($"/api/v1/users/{Missing}/roles", UriKind.Relative)), "user roles", app, "ECR-SEC-0404", $"User {Missing} does not exist.");
    }

    private static async Task AssertNotFoundAsync(
        Task<HttpResponseMessage> call, string what, EcrApiFactory app,
        string code = "ECR-CALC-0404", string? detail = null)
    {
        using var response = await call.ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"{what}: очікували 404, отримали {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(code, problem.GetProperty("errorCode").GetString());

        if (detail is not null)
        {
            Assert.Equal(detail, problem.GetProperty("detail").GetString());
        }
    }

    private sealed record Stand(
        int OwnerId, int StrangerId, int DraftId, int UnitId, int ColumnId, int TemplateId, int TemplateVersionId);

    /// <summary>Дві методології; у першої чернетка з формулою, константою, виходом і тестом.</summary>
    private async Task<Stand> ArrangeAsync()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await using var db = Db();

        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);

        var owner = new Methodology(
            EcrCode.Create($"RO{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Owner" }));
        var stranger = new Methodology(
            EcrCode.Create($"RS{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Stranger" }));
        db.Methodologies.AddRange(owner, stranger);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var draft = new MethodologyVersion(owner.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(draft);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.MethodologyFormulas.Add(draft.AddFormula(EcrCode.Create("tons"), "@Fuel * 2", FormulaResultType.Number, unit));
        db.MethodologyConstants.Add(draft.AddNumericConstant(EcrCode.Create("k1"), 1m, unit));
        db.MethodologyOutputs.Add(draft.AddOutput(EcrCode.Create("tons"), unit, 1));
        db.MethodologyTestCases.Add(draft.AddTestCase("case1", "{}", """{"tons":1}""", 0m));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(
            owner.Id, stranger.Id, draft.Id, unit, document.ColumnDefIds[0], document.TemplateId, document.TemplateVersionId);
    }

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
