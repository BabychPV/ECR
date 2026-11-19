// tests/Ecr.Api.Tests/DependencyCyclePublishTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B179 L3 (дизайн §4): публікація версії шаблону, що замкнула б цикл залежностей документів, відхиляється
/// <c>ECR-TMPL-0422</c> / <c>err.ECR-TMPL-0422.dependencyCycle</c> із шляхом кодами; версія без циклу проходить, і чужий
/// наявний цикл її не блокує.
/// </summary>
/// <remarks>
/// <para>
/// Справжній хост, SQL Server, публікація через <c>POST …/publish</c> (граф із DI — <c>IDependencyGraphProvider</c>).
/// Шаблон наповнює довідник зв'язкою <c>cfg.DocumentSourceBinding</c>, формула версії читає довідник <c>REGCOUNT</c>.
/// </para>
/// <para>
/// Мутації (локально): прибрати виклик перевірки в обробнику → <see cref="Версія_що_читає_довідник_який_наповнює_сам_шаблон_відхиляється_зі_шляхом"/>
/// червоний; не додавати ребра нової версії — те саме; будь-який наявний цикл графа блокує →
/// <see cref="Чужий_наявний_цикл_не_блокує_публікацію_іншого_шаблону"/> червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class DependencyCyclePublishTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Версія_що_читає_довідник_який_наповнює_сам_шаблон_відхиляється_зі_шляхом()
    {
        var tag = Tag();
        var registry = await RegistryAsync($"B179R{tag}");
        var draft = await DraftAsync($"B179T{tag}", registry.Code);
        await BindAsync(tag, draft.TemplateId, registry.Id);

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Publish");
        using var response = await PublishAsync(client, draft.VersionId);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        var path = $"{draft.TemplateCode} → {registry.Code} → {draft.TemplateCode}";
        Assert.Equal("ECR-TMPL-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal(DependencyCyclePublishCheck.MessageKey, problem.GetProperty("messageKey").GetString());
        Assert.Equal(path, problem.GetProperty("cycle").GetString());
        Assert.Contains(path, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Транзакція відкочена: версія лишилась чернеткою, ребер залежностей немає.
        await using var db = Context();
        Assert.Equal(TemplateVersionStatus.Draft, await db.TemplateVersions.Where(v => v.Id == draft.VersionId).Select(v => v.Status).SingleAsync());
        Assert.False(await db.FormulaDependencies.AnyAsync(d => d.FormulaDefId == draft.FormulaId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Версія_без_циклу_публікується()
    {
        var tag = Tag();
        var filled = await RegistryAsync($"B179F{tag}");
        var read = await RegistryAsync($"B179R{tag}");
        var draft = await DraftAsync($"B179T{tag}", read.Code);
        await BindAsync(tag, draft.TemplateId, filled.Id);

        await PublishOkAsync(draft);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Чужий_наявний_цикл_не_блокує_публікацію_іншого_шаблону()
    {
        // Чужий цикл X → XR → X: версія X опублікована прямо в базі (ребра — тим самим видобуванням, що й публікація).
        var tag = Tag();
        var foreignRegistry = await RegistryAsync($"B179XR{tag}");
        var foreign = await DraftAsync($"B179X{tag}", foreignRegistry.Code);
        await BindAsync(tag + "X", foreign.TemplateId, foreignRegistry.Id);
        await using (var db = Context())
        {
            var store = new TemplateVersionStore(db);
            var version = await store.GetWithStructureAsync(foreign.VersionId, CancellationToken.None);
            await store.ReplaceFormulaDependenciesAsync(
                foreign.VersionId, PublishChecks.Dependencies(version, new RealFormulaEngine()), CancellationToken.None);
            version.Publish(1, Now);
            await db.SaveChangesAsync();
        }

        var filled = await RegistryAsync($"B179F{tag}");
        var draft = await DraftAsync($"B179T{tag}", foreignRegistry.Code);
        await BindAsync(tag, draft.TemplateId, filled.Id);

        await PublishOkAsync(draft);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private async Task PublishOkAsync(Draft draft)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View", "Template.Publish");
        using var response = await PublishAsync(client, draft.VersionId);
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
    }

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, int versionId)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative), new { reason = "B179 L3" });

    private static string Tag() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<(int Id, string Code)> RegistryAsync(string code)
    {
        await using var db = Context();
        var registry = new RegistryDef(EcrCode.Create(code), Name(code), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        db.RegistryFieldDefs.Add(new RegistryFieldDef(registry.Id, EcrCode.Create("VALUE"), Name("VALUE"), CellDataType.Decimal, 1));
        await db.SaveChangesAsync();
        return (registry.Id, code);
    }

    /// <summary>Чернетка з однією формулою колонки, що читає довідник <paramref name="registryCode"/>.</summary>
    private async Task<Draft> DraftAsync(string templateCode, string registryCode)
    {
        await using var db = Context();
        var template = new Template(EcrCode.Create(templateCode), Name(templateCode), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create("S1"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(sheet.Id, EcrCode.Create("T1"), Name("T1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var column = new ColumnDef(table.Id, EcrCode.Create("CNT"), Name("CNT"), 1, CellDataType.Formula);
        db.ColumnDefs.Add(column);
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item));
        await db.SaveChangesAsync();

        var formula = new FormulaDef(table.Id, FormulaScope.Column, $"REGCOUNT('{registryCode}', ROW.VALUE > 0)", ExpressionDialect.Template);
        formula.AssignColumn(column.Id);
        db.FormulaDefs.Add(formula);
        await db.SaveChangesAsync();

        return new Draft(template.Id, templateCode, version.Id, formula.Id);
    }

    private async Task BindAsync(string tag, int templateId, int registryId)
    {
        await using var db = Context();
        db.DocumentSourceBindings.Add(new DocumentSourceBinding($"B179L3_{tag}", templateId, registryId));
        await db.SaveChangesAsync();
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Draft(int TemplateId, string TemplateCode, int VersionId, int FormulaId);
}
