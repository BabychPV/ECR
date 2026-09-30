using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// `V-11`: версію документа визначає ПРОЄКТ.
/// </summary>
/// <remarks>
/// ⛔ До фікса <c>POST /documents</c> брав версію з тіла запиту, а все читання
/// документа — з проєкту (<c>RowStore</c>, <c>p.TemplateVersionId</c>). Документ
/// 7 стенду (проєкт P99819007 + шаблон FTPL01) створився, але сторінка казала
/// «This document has no sheets», а зріз давав 404 «Таблиці 185 немає в
/// структурі версії». Діалог «New document» при цьому пропонував версії ВСІХ
/// шаблонів, зокрема архівного, і вимагав <c>Template.View</c> (`V-12`).
///
/// ⚠ Адміністратор цих сценаріїв НЕ має <c>Template.View</c> (лише
/// <c>Template.Edit</c>/<c>Publish</c>) — саме тому ним і перевіряється, що
/// складу документа право на перегляд шаблонів не потрібне.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentVersionScenarios(SqlServerFixture sql)
{
    private const string Prefix = "V11D";

    private static readonly string[] Permissions =
    [
        "Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish",
    ];

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Finding", "V-11")]
    public async Task Документ_на_версії_чужого_проєкту_відхиляється_зрозумілою_відмовою()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, Prefix, Permissions);

        var own = await DataEntryScenarios.ArrangeRealDocumentAsync(app, admin, Prefix);
        var foreign = await DataEntryScenarios.ArrangeRealDocumentAsync(app, own.Admin, Prefix);
        var client = foreign.Admin.Client;

        Assert.NotEqual(own.VersionId, foreign.VersionId);

        var create = await client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new { projectId = own.ProjectId, templateVersionId = foreign.VersionId, sheetDefIds = new[] { foreign.SheetDefId } });

        var body = await create.Content.ReadAsStringAsync();
        Assert.True(
            create.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"документ на чужій версії: {create.StatusCode}: {body}; {app.ErrorsText}");

        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("ECR-DOC-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-DOC-0422.versionNotProject", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Finding", "V-11")]
    public async Task Документ_без_версії_в_запиті_заводиться_на_версії_проєкту_і_відкривається()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, Prefix, Permissions);

        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(app, admin, Prefix);
        var client = doc.Admin.Client;

        var create = await client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new { projectId = doc.ProjectId, sheetDefIds = new[] { doc.SheetDefId } });

        Assert.True(
            create.StatusCode == HttpStatusCode.Created,
            $"документ без версії: {create.StatusCode}: {await create.Content.ReadAsStringAsync()}; {app.ErrorsText}");
        var documentId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documentId").GetInt64();

        // ⛔ І документ ВІДКРИВАЄТЬСЯ: таблиці є, зріз читається — а не «no sheets».
        var tables = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={doc.PeriodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);
        var instance = (await tables.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single();

        var slice = await client.GetAsync(new Uri(
            $"/api/v1/documents/{documentId}/tables/{instance.GetProperty("tableInstanceId").GetInt64()}",
            UriKind.Relative));
        Assert.True(slice.StatusCode == HttpStatusCode.OK, $"зріз: {slice.StatusCode}: {app.ErrorsText}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Finding", "V-11")]
    public async Task Склад_нового_документа_дає_версію_проєкту_без_права_Template_View()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, Prefix, Permissions);

        var own = await DataEntryScenarios.ArrangeRealDocumentAsync(app, admin, Prefix);
        var foreign = await DataEntryScenarios.ArrangeRealDocumentAsync(app, own.Admin, Prefix);
        var client = foreign.Admin.Client;

        // Передумова: права на перегляд шаблонів у цього користувача справді немає.
        var structure = await client.GetAsync(
            new Uri($"/api/v1/template-versions/{own.VersionId}/structure", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, structure.StatusCode);

        // ⚠ Ендпоінт — лінії V-12 (`GetDocumentTemplateHandler`); тут перевіряється
        // половина V-11: склад — ВЕРСІЇ ПРОЄКТУ, без аркушів іншого шаблону.
        var composition = await client.GetAsync(
            new Uri($"/api/v1/projects/{own.ProjectId}/document-template", UriKind.Relative));
        var body = await composition.Content.ReadAsStringAsync();
        Assert.True(composition.StatusCode == HttpStatusCode.OK, $"склад: {composition.StatusCode}: {body}; {app.ErrorsText}");

        var dto = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(own.VersionId, dto.GetProperty("templateVersionId").GetInt32());

        // ⚠ Лише аркуші версії ПРОЄКТУ — аркуша іншого шаблону тут немає.
        var sheets = dto.GetProperty("sheets").EnumerateArray().Select(s => s.GetProperty("id").GetInt32()).ToList();
        Assert.Equal([own.SheetDefId], sheets);
    }
}
