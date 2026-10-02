// tests/Ecr.Api.Tests/UnitReferenceHandlerTests.cs
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
/// Неіснуюча одиниця колонки шаблону й поля довідника — <c>422</c> з ключем, а не
/// <c>500</c> на новому зовнішньому ключі (HSE301 U1, аудит C6 п.1).
/// </summary>
/// <remarks>
/// ⛔ Міграція <c>U1UnitForeignKeys</c> поставила <c>FK_ColumnDef_Unit</c> і
/// <c>FK_RegField_Unit</c>. Без перевірки в обробниках описка в номері одиниці тепер
/// доходила б до бази й падала на 547 — голий <c>500</c> «зверніться до
/// адміністратора». Тут — справжній конвеєр: маршрут, обробник, middleware, текст
/// із сіду, та сама база з ключами.
///
/// Мутації: прибрати <c>RequireKnownUnitAsync</c> з <c>SaveColumnDefHandler</c> чи
/// <c>RequireKnownUnitsAsync</c> з <c>SaveRegistryDefinitionHandler</c> — відповідний
/// випадок стає <c>500</c> (<c>FK_ColumnDef_Unit</c> / <c>FK_RegField_Unit</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class UnitReferenceHandlerTests(SqlServerFixture sql)
{
    /// <summary>Одиниці з таким ідентифікатором у довіднику немає.</summary>
    private const int MissingUnitId = 999_999;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-U1")]
    public async Task Неіснуюча_одиниця_колонки_шаблону_422_з_ключем_а_не_500()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Edit");
        var (versionId, tableId) = await SeedDraftTableAsync();

        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/columns/U1_LIMIT", UriKind.Relative),
            new
            {
                headerL10n = new Dictionary<string, string> { ["en"] = "Limit" },
                ordinal = (int?)null,
                dataType = "Decimal",
                isRequired = false, isReadOnly = false, isHidden = false,
                precision = (byte?)null, scale = (byte?)null,
                defaultValue = (string?)null, displayFormat = (string?)null, styleId = (int?)null,
                lookupRegistryDefId = (int?)null, lookupFilter = (string?)null,
                unitId = MissingUnitId,
            });

        var problem = await ProblemAsync(response, app);

        Assert.Equal("ECR-TMPL-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-TMPL-0422.unknownUnit", problem.GetProperty("messageKey").GetString());
        Assert.Equal(
            "Column \"U1_LIMIT\": unit 999999 does not exist in the unit catalog.",
            problem.GetProperty("detail").GetString());

        // Відмова ДО запису: колонки немає.
        await using var db = Context();
        Assert.False(await db.ColumnDefs.AnyAsync(c => c.TableDefId == tableId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-U1")]
    public async Task Неіснуюча_одиниця_поля_довідника_422_з_ключем_а_не_500()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditDefinition", "Registry.Publish");
        var (code, registryId, fieldId) = await SeedRegistryAsync();

        var response = await client.PutDefinitionAsync(
            new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative),
            new
            {
                fields = new object[]
                {
                    new
                    {
                        id = (int?)fieldId, code = "Key", nameL10n = new { values = new { en = "Key" } },
                        dataType = "String", ordinal = 1, isRequired = false, isKey = true,
                        lookupRegistryDefId = (int?)null, unitId = (int?)null,
                    },
                    new
                    {
                        id = (int?)null, code = "Mass", nameL10n = new { values = new { en = "Mass" } },
                        dataType = "Decimal", ordinal = 2, isRequired = false, isKey = false,
                        lookupRegistryDefId = (int?)null, unitId = (int?)MissingUnitId,
                    },
                },
                rules = Array.Empty<object>(),
                reason = "HSE301 U1",
            });

        var problem = await ProblemAsync(response, app);

        Assert.Equal("ECR-REG-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-0422.unknownUnit", problem.GetProperty("messageKey").GetString());
        Assert.Equal(
            "Field \"Mass\": unit 999999 does not exist in the unit catalog.",
            problem.GetProperty("detail").GetString());

        // Відмова ДО запису: нового поля немає.
        await using var db = Context();
        Assert.Equal(1, await db.RegistryFieldDefs.CountAsync(f => f.RegistryDefId == registryId));
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"{response.StatusCode}: {body} {app.ErrorsText}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Шаблон із версією-чернеткою, аркушем і порожньою таблицею.</summary>
    private async Task<(int VersionId, int TableId)> SeedDraftTableAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var now = new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);
        await using var db = Context();

        var template = new Template(EcrCode.Create($"TU1{tag}"), Text($"U1 {tag}"), createdByUserId: 1, now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"SH{tag}"), Text("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"TB{tag}"), Text("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        return (version.Id, table.Id);
    }

    /// <summary>Довідник з одним ключовим полем без одиниці.</summary>
    private async Task<(string Code, int RegistryId, int FieldId)> SeedRegistryAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = Context();

        var registry = new RegistryDef(EcrCode.Create($"RU1{tag}"), Text("U1 test"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var field = new RegistryFieldDef(registry.Id, EcrCode.Create("Key"), Text("Key"), CellDataType.String, 1);
        field.MarkKey(true);
        db.RegistryFieldDefs.Add(field);
        await db.SaveChangesAsync();

        return (registry.Code, registry.Id, field.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
