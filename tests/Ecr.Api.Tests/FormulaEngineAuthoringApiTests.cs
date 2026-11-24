using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ P-1124-1 (людина 24.11: «має бути тільки наш новий редакор»): з вимкненим формульним авторингом — як у продукті
/// (<c>appsettings.json</c>) — API не заводить нових формульних версій: порожня чернетка <c>Formulas</c>, клон
/// формульної версії, публікація формульної версії й імпорт пакета відповідають <c>422</c> з
/// <c>err.ECR-CALC-0422.formulaEngineRetired</c>; порожня чернетка без рушія стає скриптом.
/// </summary>
/// <remarks>
/// Мутації: прибрати будь-яку з чотирьох перевірок <c>FormulaEngineAuthoring.Allows</c> в обробниках — відповідна
/// перевірка червоніє (201/200 або інша відмова замість <c>formulaEngineRetired</c>); прибрати реєстрацію політики в
/// <c>Program.cs</c> — обробники отримують <c>null</c> (дозволено), і червоніють усі чотири.
/// Контроль: той самий запит із увімкненим ключем (хост тестів за замовчуванням) формульну чернетку заводить.
/// </remarks>
[Collection("SqlServer")]
public sealed class FormulaEngineAuthoringApiTests(SqlServerFixture sql)
{
    private const string Retired = "err.ECR-CALC-0422.formulaEngineRetired";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нова_формульна_чернетка_відхиляється_а_без_рушія_стає_скриптом()
    {
        var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var off = Disabled(app);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, off, "Calculation.View", "Calculation.EditFormula");

        var formulas = await client.PostAsJsonAsync(
            Versions(stand.MethodologyId),
            new { versionNumber = "F1", copyFromVersionId = (int?)null, level = "Configuration", engine = "Formulas" });
        await AssertRetiredAsync(formulas);

        var implicitEngine = await client.PostAsJsonAsync(
            Versions(stand.MethodologyId),
            new { versionNumber = "S1", copyFromVersionId = (int?)null, level = "Configuration" });
        var body = await implicitEngine.Content.ReadAsStringAsync();
        Assert.True(implicitEngine.IsSuccessStatusCode, $"{implicitEngine.StatusCode}: {body}");
        using var created = JsonDocument.Parse(body);
        Assert.Equal("Script", created.RootElement.GetProperty("engine").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_формульної_версії_відхиляється()
    {
        var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var off = Disabled(app);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, off, "Calculation.View", "Calculation.EditFormula");

        var clone = await client.PostAsJsonAsync(
            Versions(stand.MethodologyId),
            new { versionNumber = "C1", copyFromVersionId = stand.FormulasDraftId, level = "Configuration", engine = "Script" });
        await AssertRetiredAsync(clone);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_формульної_версії_відхиляється()
    {
        var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var off = Disabled(app);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, off, "Calculation.View", "Calculation.Publish");

        var publish = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.FormulasDraftId}/publish", UriKind.Relative),
            new { changeReason = "P-1124-1", effectiveFrom = "2024-01-01" });
        await AssertRetiredAsync(publish);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Імпорт_пакета_формул_відхиляється_навіть_сухим_прогоном()
    {
        using var app = new EcrApiFactory(sql);
        using var off = Disabled(app);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, off, "Calculation.View", "Calculation.EditFormula", "Calculation.EditConstant");

        var package = new { format = "ecr-methodology-package", version = 1, library = "Common", methodologies = Array.Empty<object>(), blockers = (string[]?)null };
        foreach (var dryRun in new[] { "true", "false" })
        {
            var import = await client.PostAsJsonAsync(
                new Uri($"/api/v1/methodologies/import?dryRun={dryRun}", UriKind.Relative), package);
            await AssertRetiredAsync(import);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Контроль_з_увімкненим_ключем_формульна_чернетка_заводиться()
    {
        var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Calculation.View", "Calculation.EditFormula");

        var formulas = await client.PostAsJsonAsync(
            Versions(stand.MethodologyId),
            new { versionNumber = "F1", copyFromVersionId = (int?)null, level = "Configuration", engine = "Formulas" });
        var body = await formulas.Content.ReadAsStringAsync();
        Assert.True(formulas.IsSuccessStatusCode, $"{formulas.StatusCode}: {body}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Продуктова_конфігурація_не_вмикає_формульний_авторинг()
    {
        // ⛔ Кожен appsettings*.json застосунку: увімкнений ключ у продуктовому файлі повернув би старий шлях мовчки.
        var api = Path.Combine(SolutionRoot(), "src", "Ecr.Api");
        var files = Directory.GetFiles(api, "appsettings*.json");
        Assert.NotEmpty(files);

        var declared = false;
        foreach (var file in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            if (json.RootElement.TryGetProperty("Calculations", out var calc)
                && calc.TryGetProperty("FormulaEngineAuthoring", out var authoring)
                && authoring.TryGetProperty("Enabled", out var enabled))
            {
                Assert.False(enabled.GetBoolean(), $"{Path.GetFileName(file)} вмикає {FormulaEngineAuthoring.EnabledKey}");
                declared |= Path.GetFileName(file) == "appsettings.json";
            }
        }

        Assert.True(declared, $"appsettings.json має явно оголошувати {FormulaEngineAuthoring.EnabledKey} = false");
        Assert.False(FormulaEngineAuthoring.From(null).Enabled);
        Assert.False(FormulaEngineAuthoring.From("garbage").Enabled);
    }

    private static WebApplicationFactory<Program> Disabled(EcrApiFactory app)
        => app.WithWebHostBuilder(b => b.UseSetting(FormulaEngineAuthoring.EnabledKey, "false"));

    private static Uri Versions(int methodologyId) => new($"/api/v1/methodologies/{methodologyId}/versions", UriKind.Relative);

    private static async Task AssertRetiredAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("ECR-CALC-0422", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(Retired, problem.RootElement.GetProperty("messageKey").GetString());
    }

    private async Task<Stand> ArrangeAsync()
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var methodology = new Methodology(
            EcrCode.Create($"FEA{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Formula authoring retired" }));
        var draft = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1,
            new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        methodology.AddVersion(draft);
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        Assert.Equal(MethodologyEngine.Formulas, draft.Engine);

        return new Stand(methodology.Id, draft.Id);
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Ecr.sln не знайдено.");
    }

    private sealed record Stand(int MethodologyId, int FormulasDraftId);
}
