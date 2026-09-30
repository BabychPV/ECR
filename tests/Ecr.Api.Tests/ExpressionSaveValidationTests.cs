// tests/Ecr.Api.Tests/ExpressionSaveValidationTests.cs
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
/// Хибна формула й хибне правило валідації не зберігаються, а публікація
/// називає конкретну причину з ключем (V-19, UX-прохід 2026-09-24).
/// </summary>
/// <remarks>
/// ⛔ До виправлення <c>[CDEC] * * 2</c>, <c>[NOPE] + 1</c> і правило
/// <c>[CDEC] &gt;= </c> повертали <c>200</c> і лягали в структуру; публікація
/// потім відмовляла загальним «does not pass validation · ECR-TMPL-0422», а
/// справжня причина («Колонки 'NOPE' немає в таблиці …») лежала лише в
/// <c>diagnostics</c> українським реченням без ключа. Прогін — крізь справжній
/// хост, справжній конвеєр помилок і справжню базу: локалізація причини
/// відбувається саме в <c>ExceptionHandlingMiddleware</c> за каталогом із
/// <c>09-seed.sql</c>, і обробниковий тест її не побачив би.
/// </remarks>
[Collection("SqlServer")]
public sealed class ExpressionSaveValidationTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Хибна_формула_і_хибне_правило_відхиляються_на_збереженні_з_ключем_причини()
    {
        var draft = await ArrangeAsync();

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish");

        var formulaUrl = $"/api/v1/template-versions/{draft.VersionId}/tables/{draft.TableId}/formulas/column/{draft.FormulaColumnId}";

        // Синтаксис: подвійний оператор.
        var syntax = await RejectedAsync(await client.PutAsJsonAsync(
            new Uri(formulaUrl, UriKind.Relative), new { dialect = "Template", expression = "[CDEC] * * 2" }));
        Assert.StartsWith("expr.", syntax.GetProperty("messageKey").GetString(), StringComparison.Ordinal);

        // Посилання: колонки немає.
        var reference = await RejectedAsync(await client.PutAsJsonAsync(
            new Uri(formulaUrl, UriKind.Relative), new { dialect = "Template", expression = "[NOPE] + 1" }));
        Assert.Equal("expr.ref.unknownColumn", reference.GetProperty("messageKey").GetString());
        Assert.Equal(
            $"Column \"NOPE\" does not exist in table \"{draft.TableCode}\".",
            reference.GetProperty("detail").GetString());

        // Правило валідації: вираз обірваний.
        var rule = await RejectedAsync(await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/tables/{draft.TableId}/validation-rules/RULE1", UriKind.Relative),
            new
            {
                severity = "Error",
                scope = 0,
                expression = "[CDEC] >= ",
                messageL10n = new Dictionary<string, string> { ["en"] = "Non-negative" },
                columnDefId = (int?)null,
                isActive = true,
            }));
        Assert.StartsWith("expr.", rule.GetProperty("messageKey").GetString(), StringComparison.Ordinal);

        // ⛔ Нічого з цього не лягло в структуру.
        await using var db = Context();
        Assert.False(await db.FormulaDefs.AnyAsync(f => f.TableDefId == draft.TableId));
        Assert.False(await db.ValidationRules.AnyAsync(r => r.TableDefId == draft.TableId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_з_хибною_формулою_називає_конкретну_причину_англійською_з_ключем()
    {
        var draft = await ArrangeAsync();

        // Формула, збережена ДО того, як збереження почало відмовляти, — прямо в базу.
        await using (var db = Context())
        {
            var formula = new FormulaDef(draft.TableId, FormulaScope.Column, "[NOPE] + 1", ExpressionDialect.Template);
            formula.AssignColumn(draft.FormulaColumnId);
            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync();
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish");

        var problem = await RejectedAsync(await client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/publish", UriKind.Relative),
            new { reason = "V-19" }));

        // ⛔ Не «The template does not pass validation», а сама причина.
        Assert.Equal("expr.ref.unknownColumn", problem.GetProperty("messageKey").GetString());
        Assert.Equal(
            $"Column \"NOPE\" does not exist in table \"{draft.TableCode}\".",
            problem.GetProperty("detail").GetString());
    }

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

    private async Task<Draft> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"V19{tag}"), Name("V-19"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"FT{tag}"), Name("FT2"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var dec = new ColumnDef(table.Id, EcrCode.Create("CDEC"), Name("CDEC"), 1, CellDataType.Decimal);
        var frm = new ColumnDef(table.Id, EcrCode.Create("CFRM"), Name("CFRM"), 2, CellDataType.Formula);
        db.ColumnDefs.AddRange(dec, frm);
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item));
        await db.SaveChangesAsync();

        return new Draft(version.Id, table.Id, table.Code, frm.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Draft(int VersionId, int TableId, string TableCode, int FormulaColumnId);
}
