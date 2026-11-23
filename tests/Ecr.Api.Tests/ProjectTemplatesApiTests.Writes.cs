// tests/Ecr.Api.Tests/ProjectTemplatesApiTests.Writes.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>B74 P3 (D-576): шляхи запису — шаблони проєкту, проєкт з кількома шаблонами, документ за шаблоном.</summary>
public sealed partial class ProjectTemplatesApiTests
{    /// <summary>
    /// Приєднання → перелік → документ ЗА шаблоном B (версія пари, ключ з кодом шаблону) → відмови від'єднання.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутації: (1) <c>CreateDocumentHandler</c> бере версію проєкту замість версії пари — червоне (аркуш B «не у
    /// версії», 422 unknownSheets); (2) ключ без коду (завжди <c>NextBusinessKeyAsync</c>) — червоне на префіксі;
    /// (3) <c>DetachProjectTemplateHandler</c> без перевірки документів — червоне (204 замість 409); (4) картка документа
    /// з версією проєкту (<c>DocumentStore.FindAsync</c> до B74 P3) — червоне на <c>templateVersion</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Decision", "D-576")]
    public async Task Шаблон_приєднується_документ_заводиться_за_ним_з_кодом_у_ключі_а_відєднання_з_документами_відмовляє()
    {
        var s = await ArrangeAsync(ManagerPermissions, GrantLevel.Manage).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // Основний шаблон — один рядок.
        var before = await TemplatesAsync(app, client, s.A.ProjectId).ConfigureAwait(true);
        var primary = Assert.Single(before);
        Assert.Equal(s.A.TemplateId, primary.GetProperty("templateId").GetInt32());
        Assert.True(primary.GetProperty("isPrimary").GetBoolean());
        Assert.Equal(1, primary.GetProperty("documentCount").GetInt32());

        // Приєднання B; повтор — 409 templateAlreadyAttached.
        var attach = await AttachAsync(client, s.A.ProjectId, s.B.TemplateVersionId).ConfigureAwait(true);
        Assert.True(attach.StatusCode == HttpStatusCode.Created, $"{attach.StatusCode}: {await attach.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        var again = await AttachAsync(client, s.A.ProjectId, s.B.TemplateVersionId).ConfigureAwait(true);
        await AssertErrorAsync(again, HttpStatusCode.Conflict, "ECR-PRD-0409", "err.ECR-PRD-0409.templateAlreadyAttached").ConfigureAwait(true);

        var after = await TemplatesAsync(app, client, s.A.ProjectId).ConfigureAwait(true);
        Assert.Equal(2, after.Count);
        var b = Assert.Single(after, t => t.GetProperty("templateId").GetInt32() == s.B.TemplateId);
        Assert.False(b.GetProperty("isPrimary").GetBoolean());
        Assert.Equal(s.B.TemplateVersionId, b.GetProperty("currentTemplateVersionId").GetInt32());
        Assert.Equal(0, b.GetProperty("documentCount").GetInt32());

        // Склад нового документа — за шаблоном B (без параметра — основний, як доти).
        var composition = await GetJsonAsync(
            app, client, $"/api/v1/projects/{s.A.ProjectId}/document-template?templateId={s.B.TemplateId}").ConfigureAwait(true);
        Assert.Equal(s.B.TemplateVersionId, composition.GetProperty("templateVersionId").GetInt32());
        Assert.Contains(s.B.SheetDefId, composition.GetProperty("sheets").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        var legacy = await GetJsonAsync(app, client, $"/api/v1/projects/{s.A.ProjectId}/document-template").ConfigureAwait(true);
        Assert.Equal(s.A.TemplateVersionId, legacy.GetProperty("templateVersionId").GetInt32());

        // Документ за шаблоном B: версія пари, ключ з кодом шаблону, нумерація на пару.
        var first = await CreateDocumentAsync(app, client, s.A.ProjectId, s.B.TemplateId, s.B.SheetDefId).ConfigureAwait(true);
        var second = await CreateDocumentAsync(app, client, s.A.ProjectId, s.B.TemplateId, s.B.SheetDefId).ConfigureAwait(true);
        var ofA = await CreateDocumentAsync(app, client, s.A.ProjectId, s.A.TemplateId, s.A.SheetDefId).ConfigureAwait(true);

        var card = await GetJsonAsync(app, client, $"/api/v1/documents/{first}?periodKey={s.A.PeriodKey.Value}").ConfigureAwait(true);
        Assert.Equal($"{s.BCode}-P{s.A.ProjectId}-0001", card.GetProperty("businessKey").GetString());
        Assert.Equal(s.BCode, card.GetProperty("templateCode").GetString());
        Assert.Equal(s.BVersion, card.GetProperty("templateVersion").GetString());
        // ⛔ S17/доступ: грант на ПРОЄКТ відкриває документ другого шаблону; знімок читання — версії пари (аркуш B видно).
        Assert.Contains(s.B.SheetCode, card.GetProperty("sheets").EnumerateArray().Select(x => x.GetProperty("code").GetString()));

        var secondCard = await GetJsonAsync(app, client, $"/api/v1/documents/{second}").ConfigureAwait(true);
        Assert.Equal($"{s.BCode}-P{s.A.ProjectId}-0002", secondCard.GetProperty("businessKey").GetString());
        var aCard = await GetJsonAsync(app, client, $"/api/v1/documents/{ofA}").ConfigureAwait(true);
        Assert.Equal($"{s.ACode}-P{s.A.ProjectId}-0001", aCard.GetProperty("businessKey").GetString());
        Assert.Equal(s.AVersion, aCard.GetProperty("templateVersion").GetString());

        // Перелік несе шаблон кожного документа.
        var list = await GetJsonAsync(app, client, $"/api/v1/documents?projectId={s.A.ProjectId}&limit=50").ConfigureAwait(true);
        var codes = list.GetProperty("items").EnumerateArray()
            .ToDictionary(x => x.GetProperty("id").GetInt64(), x => x.GetProperty("templateCode").GetString());
        Assert.Equal(s.BCode, codes[first]);
        Assert.Equal(s.ACode, codes[ofA]);

        // Структурну правку версії B тримає факт документів НА НІЙ (а не на версії проєкту).
        using (var scope = app.Services.CreateScope())
        {
            var versions = scope.ServiceProvider.GetRequiredService<Ecr.Application.Ports.ITemplateVersionStore>();
            Assert.True(await versions.HasDocumentsAsync(s.B.TemplateVersionId, CancellationToken.None).ConfigureAwait(true));
            // 2 документи шаблону B у проєкті A + документ власного проєкту шаблону B (фікстура).
            Assert.Equal(3, await versions.CountDocumentsAsync(s.B.TemplateVersionId, CancellationToken.None).ConfigureAwait(true));
        }

        // Від'єднання: документи шаблону — 409 з кількістю; основний — 409.
        var detachB = await client.DeleteAsync(new Uri($"/api/v1/projects/{s.A.ProjectId}/templates/{s.B.TemplateId}", UriKind.Relative)).ConfigureAwait(true);
        var detachBody = await AssertErrorAsync(detachB, HttpStatusCode.Conflict, "ECR-PRD-0409", "err.ECR-PRD-0409.templateHasDocuments").ConfigureAwait(true);
        Assert.Equal("2", detachBody.GetProperty("documentCount").GetString());
        var detachA = await client.DeleteAsync(new Uri($"/api/v1/projects/{s.A.ProjectId}/templates/{s.A.TemplateId}", UriKind.Relative)).ConfigureAwait(true);
        await AssertErrorAsync(detachA, HttpStatusCode.Conflict, "ECR-PRD-0409", "err.ECR-PRD-0409.primaryTemplate").ConfigureAwait(true);
    }

    /// <summary>
    /// Шаблон поза проєктом: склад і створення — 422 <c>templateNotInProject</c>; від'єднання без документів — 204, далі
    /// створення за ним — 422. Маршрут пари (B155) блокує від'єднання.
    /// </summary>
    /// <remarks>⛔ Мутація: прибрати перевірку <c>HasOwnRouteAsync</c> — червоне (204 замість 409 templateHasRoute).</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Decision", "D-576")]
    public async Task Шаблон_поза_проєктом_422_відєднання_без_документів_204_а_з_маршрутом_пари_409()
    {
        var s = await ArrangeAsync(ManagerPermissions, GrantLevel.Manage).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var outside = await client.GetAsync(new Uri(
            $"/api/v1/projects/{s.A.ProjectId}/document-template?templateId={s.B.TemplateId}", UriKind.Relative)).ConfigureAwait(true);
        await AssertErrorAsync(outside, HttpStatusCode.UnprocessableEntity, "ECR-DOC-0422", "err.ECR-DOC-0422.templateNotInProject").ConfigureAwait(true);

        var create = await PostDocumentAsync(client, s.A.ProjectId, s.B.TemplateId, s.B.SheetDefId).ConfigureAwait(true);
        await AssertErrorAsync(create, HttpStatusCode.UnprocessableEntity, "ECR-DOC-0422", "err.ECR-DOC-0422.templateNotInProject").ConfigureAwait(true);

        (await AttachAsync(client, s.A.ProjectId, s.B.TemplateVersionId).ConfigureAwait(true)).EnsureSuccessStatusCode();

        // Власний маршрут пари проєкт × B — від'єднання відмовляє (конфігурація не губиться мовчки).
        int routeId;
        await using (var db = Context())
        {
            var route = new ApprovalRoute(
                EcrCode.Create($"B74R_{Guid.NewGuid():N}"[..20]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "B74 pair route" }),
                s.A.ProjectId);
            db.ApprovalRoutes.Add(route);
            await db.SaveChangesAsync().ConfigureAwait(true);
            routeId = route.Id;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE wf.ApprovalRoute SET TemplateId = {s.B.TemplateId} WHERE Id = {routeId}").ConfigureAwait(true);
        }

        var blocked = await client.DeleteAsync(new Uri($"/api/v1/projects/{s.A.ProjectId}/templates/{s.B.TemplateId}", UriKind.Relative)).ConfigureAwait(true);
        await AssertErrorAsync(blocked, HttpStatusCode.Conflict, "ECR-PRD-0409", "err.ECR-PRD-0409.templateHasRoute").ConfigureAwait(true);

        await using (var db = Context())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE wf.ApprovalRoute SET IsActive = 0 WHERE Id = {routeId}").ConfigureAwait(true);
        }

        var detach = await client.DeleteAsync(new Uri($"/api/v1/projects/{s.A.ProjectId}/templates/{s.B.TemplateId}", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(detach.StatusCode == HttpStatusCode.NoContent, $"{detach.StatusCode}: {await detach.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        Assert.Single(await TemplatesAsync(app, client, s.A.ProjectId).ConfigureAwait(true));

        var afterDetach = await PostDocumentAsync(client, s.A.ProjectId, s.B.TemplateId, s.B.SheetDefId).ConfigureAwait(true);
        await AssertErrorAsync(afterDetach, HttpStatusCode.UnprocessableEntity, "ECR-DOC-0422", "err.ECR-DOC-0422.templateNotInProject").ConfigureAwait(true);

        var missing = await client.DeleteAsync(new Uri($"/api/v1/projects/{s.A.ProjectId}/templates/{s.B.TemplateId}", UriKind.Relative)).ConfigureAwait(true);
        await AssertErrorAsync(missing, HttpStatusCode.NotFound, "ECR-TMPL-0404", "err.ECR-TMPL-0404.templateNotInProject").ConfigureAwait(true);
    }

    /// <summary>
    /// ⛔ S17: невидимий проєкт (заборона на проєкт) — 404 на перелік, приєднання й від'єднання, як неіснуючий. Без гранта
    /// <c>Manage</c> приєднання — 403, перелік (Read) — 200.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Finding", "S17")]
    public async Task Невидимий_проєкт_404_а_без_гранта_Manage_приєднання_403()
    {
        var denied = await ArrangeAsync(ManagerPermissions, GrantLevel.Read, deny: true).ConfigureAwait(true);
        var reader = await ArrangeAsync(ManagerPermissions, GrantLevel.Write).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        var deniedClient = await SignInAsync(app, denied.UserName).ConfigureAwait(true);
        foreach (var response in new[]
                 {
                     await deniedClient.GetAsync(new Uri($"/api/v1/projects/{denied.A.ProjectId}/templates", UriKind.Relative)).ConfigureAwait(true),
                     await AttachAsync(deniedClient, denied.A.ProjectId, denied.B.TemplateVersionId).ConfigureAwait(true),
                     await deniedClient.DeleteAsync(new Uri($"/api/v1/projects/{denied.A.ProjectId}/templates/{denied.A.TemplateId}", UriKind.Relative)).ConfigureAwait(true),
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var readerClient = await SignInAsync(app, reader.UserName).ConfigureAwait(true);
        Assert.Single(await TemplatesAsync(app, readerClient, reader.A.ProjectId).ConfigureAwait(true));
        var forbidden = await AttachAsync(readerClient, reader.A.ProjectId, reader.B.TemplateVersionId).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    /// <summary>
    /// B151/B165 у спільному проєкті: «один документ на проєкт» рахується НА ШАБЛОН — документ шаблону A не заважає
    /// першому документу B; другий документ B — <c>409 ECR-DOC-4092</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Finding", "B165")]
    public async Task Один_документ_на_шаблон_у_спільному_проєкті()
    {
        var s = await ArrangeAsync(ManagerPermissions, GrantLevel.Manage).ConfigureAwait(true);
        await using (var db = Context())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.Template SET SingleDocumentPerProject = 1 WHERE Id = {s.B.TemplateId}").ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);
        (await AttachAsync(client, s.A.ProjectId, s.B.TemplateVersionId).ConfigureAwait(true)).EnsureSuccessStatusCode();

        _ = await CreateDocumentAsync(app, client, s.A.ProjectId, s.B.TemplateId, s.B.SheetDefId).ConfigureAwait(true);
        var second = await PostDocumentAsync(client, s.A.ProjectId, s.B.TemplateId, s.B.SheetDefId).ConfigureAwait(true);
        await AssertErrorAsync(second, HttpStatusCode.Conflict, "ECR-DOC-4092", "err.ECR-DOC-4092.documentExistsForTemplate").ConfigureAwait(true);

        // Шаблон A (без прапорця) — далі кілька документів.
        _ = await CreateDocumentAsync(app, client, s.A.ProjectId, s.A.TemplateId, s.A.SheetDefId).ConfigureAwait(true);
    }

    /// <summary>
    /// Створення проєкту «рік · об'єкт» одразу з кількома шаблонами: перший — основний, решта приєднані; той самий шаблон
    /// двічі — 422 <c>duplicateTemplate</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Decision", "D-576")]
    public async Task Проєкт_створюється_з_кількома_шаблонами()
    {
        var s = await ArrangeAsync(ManagerPermissions, GrantLevel.Manage).ConfigureAwait(true);
        int policyId;
        await using (var db = Context())
        {
            policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var code = $"B74Y{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        var created = await client.PostAsJsonAsync(new Uri("/api/v1/projects", UriKind.Relative), new
        {
            code,
            nameL10n = new Dictionary<string, string> { ["en"] = "2026 · site" },
            timeZoneId = "Asia/Atyrau",
            periodKind = "Monthly",
            year = 2026,
            periodPolicyId = policyId,
            templateVersionIds = new[] { s.A.TemplateVersionId, s.B.TemplateVersionId },
        }).ConfigureAwait(true);
        var text = await created.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {text}\n{app.ErrorsText}");
        var projectId = JsonDocument.Parse(text).RootElement.GetProperty("projectId").GetInt32();

        var templates = await TemplatesAsync(app, client, projectId).ConfigureAwait(true);
        Assert.Equal(2, templates.Count);
        Assert.True(templates.Single(t => t.GetProperty("templateId").GetInt32() == s.A.TemplateId).GetProperty("isPrimary").GetBoolean());
        Assert.False(templates.Single(t => t.GetProperty("templateId").GetInt32() == s.B.TemplateId).GetProperty("isPrimary").GetBoolean());

        var duplicate = await client.PostAsJsonAsync(new Uri("/api/v1/projects", UriKind.Relative), new
        {
            code = code[..^1] + "Z",
            nameL10n = new Dictionary<string, string> { ["en"] = "dup" },
            timeZoneId = "Asia/Atyrau",
            periodKind = "Monthly",
            year = 2026,
            periodPolicyId = policyId,
            templateVersionIds = new[] { s.A.TemplateVersionId, s.A2VersionId },
        }).ConfigureAwait(true);
        await AssertErrorAsync(duplicate, HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422", "err.ECR-TMPL-0422.duplicateTemplate").ConfigureAwait(true);
    }
}
