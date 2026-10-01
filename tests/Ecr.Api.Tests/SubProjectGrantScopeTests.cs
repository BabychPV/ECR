// tests/Ecr.Api.Tests/SubProjectGrantScopeTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// S2 (enterprise-аудит безпеки, 2026-09-28): грант на аркуш, таблицю чи
/// колонку не дає дій у проєкті, якого користувач не бачить.
/// </summary>
/// <remarks>
/// ⛔ Сценарій експлойту. Два проєкти ОДНОГО шаблону (A і B) ділять версію
/// шаблону, а отже й <c>SheetDefId</c>/<c>ColumnDefId</c>. Роль має
/// <c>Project:A = Read</c> і <c>Sheet:S = Approve</c> (чи <c>Column:C =
/// Write</c>) — тобто «аркуш S у проєкті A». Гранта на проєкт B немає зовсім.
/// До фіксу <c>POST /documents/{docB}/approve|submit|reopen</c> давав
/// <c>204</c> і змінював стан документа, якого ця людина навіть не бачить.
/// Тепер — <c>404 ECR-DOC-0404</c>, як на будь-якому маршруті невидимого
/// документа (B-08). Причину відмови самої служби (<c>NoGrant</c>, а не стан
/// аркуша) тримає <c>Ecr.Infrastructure.Tests…WorkflowInvisibleDocumentDecisionTests</c>:
/// тут її перекриває обробник.
///
/// ⚠ Контрольний тест (грант на B є) — щоб фікс не «закрив» законний доступ.
/// </remarks>
[Collection("SqlServer")]
public sealed class SubProjectGrantScopeTests(SqlServerFixture sql)
{
    private const string Password = "Sub-Project-Grant-2026!";

    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати ВСІ три шари — видимість в
    /// <c>ApproveSheetHandler</c>, <c>DenyIfInvisible</c> у
    /// <c>AccessDecisionService.CanApproveAsync</c> і передумову проєкту в
    /// <c>EditRules.Effective</c> → <c>204</c>, аркуш у B затверджено.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S2")]
    public async Task Грант_на_аркуш_із_проєкту_A_не_затверджує_аркуш_у_проєкті_B()
    {
        var scenario = await ArrangeAsync(DocumentStatus.Submitted).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, [(ResourceKind.Project, scenario.ProjectA, GrantLevel.Read),
                      (ResourceKind.Sheet, scenario.B.SheetDefId, GrantLevel.Approve)])
            .ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{scenario.B.DocumentId}/approve", UriKind.Relative),
            new { sheetDefId = scenario.B.SheetDefId, periodKey = scenario.B.PeriodKey.Value, approved = true, reason = (string?)null })
            .ConfigureAwait(true);

        await AssertDeniedAsync(response, app).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(scenario.B).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S2")]
    public async Task Грант_на_аркуш_із_проєкту_A_не_подає_аркуш_у_проєкті_B()
    {
        var scenario = await ArrangeAsync(state: null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, [(ResourceKind.Project, scenario.ProjectA, GrantLevel.Read),
                      (ResourceKind.Sheet, scenario.B.SheetDefId, GrantLevel.Approve)])
            .ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{scenario.B.DocumentId}/submit", UriKind.Relative),
            new { sheetDefId = scenario.B.SheetDefId, periodKey = scenario.B.PeriodKey.Value })
            .ConfigureAwait(true);

        await AssertDeniedAsync(response, app).ConfigureAwait(true);
        Assert.NotEqual(DocumentStatus.Submitted, await StatusAsync(scenario.B).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S2")]
    public async Task Грант_на_аркуш_із_проєкту_A_не_повертає_в_роботу_аркуш_у_проєкті_B()
    {
        var scenario = await ArrangeAsync(DocumentStatus.Submitted).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, [(ResourceKind.Project, scenario.ProjectA, GrantLevel.Read),
                      (ResourceKind.Sheet, scenario.B.SheetDefId, GrantLevel.Approve)],
                "Document.Reopen")
            .ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{scenario.B.DocumentId}/reopen", UriKind.Relative),
            new { sheetDefId = scenario.B.SheetDefId, periodKey = scenario.B.PeriodKey.Value, reason = "S2 exploit" })
            .ConfigureAwait(true);

        await AssertDeniedAsync(response, app).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(scenario.B).ConfigureAwait(true));
    }

    /// <summary>
    /// Невидимий документ на маршрутах робочого процесу виглядає як
    /// неіснуючий — <c>404 ECR-DOC-0404</c> (B-08).
    /// </summary>
    /// <param name="action">Дія робочого процесу.</param>
    /// <param name="noSheetInDocument">
    /// Аркуша немає в складі документа — перевірка складу стоїть у submit і
    /// recall ДО прав і давала б <c>404 sheetNotInDocument</c>, тобто інше тіло.
    /// </param>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ, по одному на кожен обробник: прибрати
    /// <c>DocumentVisibility.RequireVisibleAsync</c> — approve/reopen/recall
    /// дають <c>403 ECR-ACCS-0403</c>, submit — <c>403</c> (або <c>404</c> з
    /// іншим <c>messageKey</c>, коли аркуша немає в складі). Стан аркуша
    /// підібрано так, щоб без видимості відмова все одно була: тест ловить
    /// саме ВИД відмови, а не її наявність.
    /// </remarks>
    [Theory]
    [InlineData("approve", false)]
    [InlineData("submit", false)]
    [InlineData("submit", true)]
    [InlineData("reopen", false)]
    [InlineData("recall", false)]
    [InlineData("recall", true)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S2")]
    [Trait("Requirement", "B-08")]
    public async Task Дія_над_невидимим_документом_дає_404_як_неіснуючий(string action, bool noSheetInDocument)
    {
        var scenario = await ArrangeAsync(
                action is "submit" or "recall" ? DocumentStatus.Submitted : null,
                includeSheet: !noSheetInDocument)
            .ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, [(ResourceKind.Project, scenario.ProjectA, GrantLevel.Read),
                      (ResourceKind.Sheet, scenario.B.SheetDefId, GrantLevel.Approve)],
                "Document.Reopen")
            .ConfigureAwait(true);

        object body = action switch
        {
            "approve" => new { sheetDefId = scenario.B.SheetDefId, periodKey = scenario.B.PeriodKey.Value, approved = true, reason = (string?)null },
            "reopen" or "recall" => new { sheetDefId = scenario.B.SheetDefId, periodKey = scenario.B.PeriodKey.Value, reason = "S2 oracle" },
            _ => new { sheetDefId = scenario.B.SheetDefId, periodKey = scenario.B.PeriodKey.Value },
        };

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{scenario.B.DocumentId}/{action}", UriKind.Relative), body)
            .ConfigureAwait(true);

        await AssertDeniedAsync(response, app).ConfigureAwait(true);
    }

    /// <summary>Запис у колонку документа B за грантом «колонка C у проєкті A».</summary>
    /// <remarks>
    /// ⚠ Цей шлях закритий і ДО фіксу — <c>PatchCellsHandler</c> питає
    /// <c>CanReadDocumentAsync</c> першим (V-02) і відповідає <c>404</c>. Тест
    /// тримає це як регресію на рівні HTTP; сама передумова в
    /// <c>EditRules.Effective</c> доведена чистими тестами
    /// (<c>AccessDecisionTests</c>), бо тут її перекриває обробник.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S2")]
    public async Task Грант_на_колонку_із_проєкту_A_не_пише_в_документ_проєкту_B()
    {
        var scenario = await ArrangeAsync(state: null).ConfigureAwait(true);
        var column = scenario.B.ColumnDefIds[1];
        var (rowKey, columnCode) = await RowAndColumnAsync(scenario.B, column).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, [(ResourceKind.Project, scenario.ProjectA, GrantLevel.Read),
                      (ResourceKind.Column, column, GrantLevel.Write)])
            .ConfigureAwait(true);

        var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{scenario.B.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = scenario.B.TableInstanceId,
                periodKey = scenario.B.PeriodKey.Value,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion = (string?)null,
                        cells = new object[] { new { columnCode, value = (object)42m } },
                    },
                },
            }).ConfigureAwait(true);

        await AssertDeniedAsync(response, app).ConfigureAwait(true);

        await using var db = Context();
        Assert.False(await db.CellValues.AnyAsync(
            c => c.PeriodKeyValue == scenario.B.PeriodKey.Value && c.ColumnDefId == column).ConfigureAwait(true));
    }

    /// <summary>Контроль: грант на аркуш ПІД видимим проєктом діє, як і раніше.</summary>
    /// <remarks>
    /// ⛔ Без нього «фікс» <c>return GrantLevel.None</c> в <c>Effective</c>
    /// пройшов би всі тести вище.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S2")]
    public async Task Грант_на_аркуш_під_видимим_проєктом_затверджує()
    {
        var scenario = await ArrangeAsync(DocumentStatus.Submitted).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, [(ResourceKind.Project, scenario.B.ProjectId, GrantLevel.Read),
                      (ResourceKind.Sheet, scenario.B.SheetDefId, GrantLevel.Approve)])
            .ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{scenario.B.DocumentId}/approve", UriKind.Relative),
            new { sheetDefId = scenario.B.SheetDefId, periodKey = scenario.B.PeriodKey.Value, approved = true, reason = (string?)null })
            .ConfigureAwait(true);

        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            $"approve у видимому B: {(int)response.StatusCode}\n{await response.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        Assert.Equal(DocumentStatus.Approved, await StatusAsync(scenario.B).ConfigureAwait(true));
    }

    /// <summary>
    /// Невидимий документ: <c>404 ECR-DOC-0404</c> з тим самим
    /// <c>messageKey</c>, що й на відсутній документ (B-08).
    /// </summary>
    private static async Task AssertDeniedAsync(HttpResponseMessage response, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"очікували 404, отримали {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-DOC-0404", root.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-DOC-0404.document", root.GetProperty("messageKey").GetString());
    }

    /// <summary>Два проєкти одного шаблону; документ — у B.</summary>
    /// <param name="state">Стан аркуша в B; <c>null</c> — рядка стану немає (Draft).</param>
    /// <param name="includeSheet">Чи внести аркуш у склад документа B.</param>
    private async Task<(TestDocument B, int ProjectA)> ArrangeAsync(DocumentStatus? state, bool includeSheet = true)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);

        await using var db = Context();
        var tag = $"{Guid.NewGuid():N}"[..10];

        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync().ConfigureAwait(false);

        // ⚠ Та сама версія шаблону, що в B, — рівно так, як клон проєкту
        // копіює `TemplateVersionId`.
        var projectA = new Project(
            EcrCode.Create($"PA{tag}"), Name("S2 project A"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            b.TemplateVersionId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(projectA);

        // Аркуш у складі документа B: без цього подання відмовляє раніше за
        // права (`HasSheetAsync`, 404), і тест не доводив би нічого про S2.
        if (includeSheet)
        {
            db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        if (state is { } target)
        {
            // Подав bootstrap (Id 1), не тестовий користувач: інакше
            // затвердження зупинило б правило чотирьох очей, а не S2.
            var approval = new ApprovalState(b.DocumentId, b.SheetDefId, b.PeriodKey.Value);
            approval.Submit(1, Now);
            if (target == DocumentStatus.Approved)
            {
                approval.Approve(1, Now);
            }

            db.ApprovalStates.Add(approval);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return (b, projectA.Id);
    }

    private async Task<DocumentStatus?> StatusAsync(TestDocument document)
    {
        await using var db = Context();

        return await db.ApprovalStates
            .AsNoTracking()
            .Where(a => a.DocumentId == document.DocumentId
                        && a.SheetDefId == document.SheetDefId
                        && a.PeriodKey == document.PeriodKey.Value)
            .Select(a => (DocumentStatus?)a.Status)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    private async Task<(string RowKey, string ColumnCode)> RowAndColumnAsync(TestDocument document, int columnDefId)
    {
        await using var db = Context();

        var rowKey = await db.TableRows
            .AsNoTracking()
            .Where(r => r.PeriodKeyValue == document.PeriodKey.Value && r.Id == document.RowIds[0])
            .Select(r => r.RowKeyValue)
            .SingleAsync()
            .ConfigureAwait(false);

        var code = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => c.Id == columnDefId)
            .Select(c => c.Code)
            .SingleAsync()
            .ConfigureAwait(false);

        return (rowKey, code);
    }

    /// <summary>Клієнт із сеансом і роллю з названими грантами.</summary>
    private async Task<HttpClient> SignedInAsync(
        EcrApiFactory app, (ResourceKind Kind, int Id, GrantLevel Level)[] grants, params string[] permissions)
    {
        var name = $"s2_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("S2 sub-project grant"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            foreach (var (kind, id, level) in grants)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, kind, id, level));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
