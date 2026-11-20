// tests/Ecr.Api.Tests/DependencyGraphEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Calculations.SourceBindings;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B179 L4/L5: <c>GET /api/v1/dependency-graph</c> і <c>PUT /api/v1/document-source-bindings/{id}/flags</c> — диференційна
/// пара Allow/Deny за правом <c>Calculation.ManageSourceBinding</c> на справжньому шляху HTTP, форма графа (вузли, ребра
/// з доказами, зв'язка, рівні), аудит і ефект перемикання бітів.
/// </summary>
/// <remarks>
/// Мутації (локально): прибрати <c>PermissionCheck.RequireAsync</c> у <c>GetDependencyGraphHandler</c> /
/// <c>SwitchDocumentSourceFlagsHandler</c> - Deny-тести червоні; не писати подію аудиту у <c>SwitchDocumentSourceFlagsHandler</c> -
/// тест аудиту червоний; не викликати <c>SetFlags</c> - тест перемикання червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class DependencyGraphEndpointTests(SqlServerFixture sql)
{
    private static readonly Uri Graph = new("/api/v1/dependency-graph", UriKind.Relative);
    private static readonly DateTime Now = new(2099, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private sealed record World(int BindingId, string BindingCode, int SourceTemplateId, int ConsumerTemplateId, string RegistryCode, int RegistryId);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task З_правом_граф_віддає_вузли_ребра_з_доказами_зв_язку_і_рівні()
    {
        var w = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, DocumentSourceBindings.Permission);

        var response = await admin.GetAsync(Graph);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        var root = JsonDocument.Parse(body).RootElement;

        Assert.False(string.IsNullOrEmpty(root.GetProperty("revision").GetString()));
        Assert.Contains(
            root.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("kind").GetString() == "registry" && n.GetProperty("code").GetString() == w.RegistryCode);

        var fills = Assert.Single(
            root.GetProperty("edges").EnumerateArray(),
            e => e.GetProperty("kind").GetString() == "fills" && e.GetProperty("toId").GetInt32() == w.RegistryId);
        Assert.Equal("template", fills.GetProperty("fromKind").GetString());
        Assert.Equal(w.SourceTemplateId, fills.GetProperty("fromId").GetInt32());
        var evidence = Assert.Single(fills.GetProperty("evidence").EnumerateArray());
        Assert.Equal("sourceBinding", evidence.GetProperty("source").GetString());
        Assert.Equal(w.BindingId, evidence.GetProperty("id").GetInt32());

        var read = Assert.Single(
            root.GetProperty("edges").EnumerateArray(),
            e => e.GetProperty("kind").GetString() == "templateFormula" && e.GetProperty("fromId").GetInt32() == w.RegistryId);
        Assert.Equal(w.ConsumerTemplateId, read.GetProperty("toId").GetInt32());
        Assert.True(read.GetProperty("evidenceTotal").GetInt32() >= 1);

        var link = Assert.Single(
            root.GetProperty("templateDependencies").EnumerateArray(),
            d => d.GetProperty("bindingId").GetInt32() == w.BindingId);
        Assert.Equal(w.SourceTemplateId, link.GetProperty("sourceTemplateId").GetInt32());
        Assert.Equal(w.ConsumerTemplateId, link.GetProperty("consumerTemplateId").GetInt32());
        Assert.True(link.GetProperty("viaTemplateFormula").GetBoolean());

        int LevelOf(int templateId) => root.GetProperty("levels").EnumerateArray()
            .Single(l => l.GetProperty("templateIds").EnumerateArray().Any(t => t.GetInt32() == templateId))
            .GetProperty("level").GetInt32();
        Assert.True(LevelOf(w.SourceTemplateId) < LevelOf(w.ConsumerTemplateId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Без_права_граф_і_перемикання_бітів_403_з_кодом_права_і_біти_не_змінюються()
    {
        var w = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var viewer = await SystemHealthControllerTests.SignedInAsync(sql, app, "System.ViewHealth", "Calculation.View");

        var graph = await viewer.GetAsync(Graph);
        Assert.Equal(HttpStatusCode.Forbidden, graph.StatusCode);
        var body = await graph.Content.ReadAsStringAsync();
        Assert.Contains(DocumentSourceBindings.Permission, body, StringComparison.Ordinal);
        Assert.DoesNotContain(w.RegistryCode, body, StringComparison.Ordinal);

        var put = await viewer.PutAsJsonAsync(FlagsUri(w.BindingId), new { readFromDocument = false, recalculateDependents = false });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Contains(DocumentSourceBindings.Permission, await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal((true, true), await FlagsAsync(w.BindingId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Перемикання_бітів_міняє_їх_пише_подію_аудиту_і_повтор_нічого_не_робить()
    {
        var w = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, DocumentSourceBindings.Permission);

        var first = await admin.PutAsJsonAsync(FlagsUri(w.BindingId), new { recalculateDependents = false });
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"{first.StatusCode}: {firstBody}");
        var result = JsonDocument.Parse(firstBody).RootElement;
        Assert.True(result.GetProperty("changed").GetBoolean());
        Assert.False(result.GetProperty("binding").GetProperty("recalculateDependents").GetBoolean());
        Assert.True(result.GetProperty("binding").GetProperty("readFromDocument").GetBoolean());
        Assert.Equal((true, false), await FlagsAsync(w.BindingId));

        var events = await AuditAsync(w.BindingCode);
        var single = Assert.Single(events);
        Assert.Contains("\"kind\":\"flags\"", single, StringComparison.Ordinal);
        Assert.Contains("\"oldRecalculateDependents\":true", single, StringComparison.Ordinal);
        Assert.Contains("\"newRecalculateDependents\":false", single, StringComparison.Ordinal);

        // Повтор: нічого не змінилося — і другої події немає.
        var again = await admin.PutAsJsonAsync(FlagsUri(w.BindingId), new { recalculateDependents = false });
        Assert.False(JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("changed").GetBoolean());
        Assert.Single(await AuditAsync(w.BindingCode));

        // Другий біт — незалежно від першого.
        var second = await admin.PutAsJsonAsync(FlagsUri(w.BindingId), new { readFromDocument = false });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((false, false), await FlagsAsync(w.BindingId));
        Assert.Equal(2, (await AuditAsync(w.BindingCode)).Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Перемикання_бітів_без_жодного_біта_422_а_невідома_зв_язка_404()
    {
        var w = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, DocumentSourceBindings.Permission);

        var empty = await admin.PutAsJsonAsync(FlagsUri(w.BindingId), new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        var problem = JsonDocument.Parse(await empty.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("err.ECR-REQ-0422.sourceFlagsInvalid", problem.GetProperty("messageKey").GetString());
        Assert.Equal((true, true), await FlagsAsync(w.BindingId));

        var missing = await admin.PutAsJsonAsync(FlagsUri(int.MaxValue), new { readFromDocument = false });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static Uri FlagsUri(int id) => new($"/api/v1/document-source-bindings/{id}/flags", UriKind.Relative);

    private async Task<(bool Read, bool Recalculate)> FlagsAsync(int id)
    {
        await using var db = sql.CreateContext();
        var b = await db.DocumentSourceBindings.AsNoTracking().SingleAsync(x => x.Id == id);
        return (b.ReadFromDocument, b.RecalculateDependents);
    }

    private async Task<List<string>> AuditAsync(string bindingCode)
    {
        await using var db = sql.CreateContext();
        var pattern = "%\"code\":\"" + bindingCode + "\"%";
        return await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentSourceBindingChanged' AND DetailsJson LIKE {pattern}")
            .ToListAsync();
    }

    /// <summary>Шаблон-джерело наповнює довідник; шаблон-споживач (опублікована версія) читає його формулою.</summary>
    private async Task<World> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync();

        await using var db = sql.CreateContext();
        var registryCode = $"B179G{tag}";
        var registry = new RegistryDef(EcrCode.Create(registryCode), Name(registryCode), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        db.RegistryFieldDefs.Add(new RegistryFieldDef(registry.Id, EcrCode.Create("VALUE"), Name("VALUE"), CellDataType.Decimal, 1));
        var binding = new DocumentSourceBinding($"B179G_{tag}", source.TemplateId, registry.Id);
        db.DocumentSourceBindings.Add(binding);

        var template = new Template(EcrCode.Create($"B179C{tag}"), Name("consumer"), 1, Now);
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
        version.Publish(1, Now);
        await db.SaveChangesAsync();

        return new World(binding.Id, binding.Code, source.TemplateId, template.Id, registryCode, registry.Id);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
