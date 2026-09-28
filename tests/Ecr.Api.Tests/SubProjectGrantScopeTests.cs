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
///
/// ⚠ Контрольний тест (грант на B є) — щоб фікс не «закрив» законний доступ.
/// </remarks>
[Collection("SqlServer")]
public sealed class SubProjectGrantScopeTests(SqlServerFixture sql)
{
    private const string Password = "Sub-Project-Grant-2026!";

    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>DenyIfInvisible</c> у
    /// <c>AccessDecisionService.CanApproveAsync</c> І передумову проєкту в
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

    /// <summary>Невидимий документ не розповідає про свій стан (B-08).</summary>
    /// <param name="action">Дія робочого процесу.</param>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ для перевірки ЧИТАННЯ окремо від передумови в
    /// <c>EditRules.Effective</c>, по одному на кожен із трьох методів. Стан
    /// аркуша підібрано так, щоб без <c>DenyIfInvisible</c> відмова все одно
    /// була, але з причиною про СТАН: approve і reopen на <c>Draft</c> →
    /// <c>BusinessRule</c> «аркуш у стані Draft», submit на <c>Submitted</c> →
    /// <c>DocumentSubmitted</c>. Тобто стан чужого документа видно. З
    /// перевіркою — <c>NoGrant</c>, як на будь-який невидимий.
    /// </remarks>
    [Theory]
    [InlineData("approve")]
    [InlineData("submit")]
    [InlineData("reopen")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S2")]
    public async Task Дія_над_невидимим_документом_відмовляє_NoGrant_а_не_станом_аркуша(string action)
    {
        var scenario = await ArrangeAsync(action == "submit" ? DocumentStatus.Submitted : null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, [(ResourceKind.Project, scenario.ProjectA, GrantLevel.Read),
                      (ResourceKind.Sheet, scenario.B.SheetDefId, GrantLevel.Approve)],
                "Document.Reopen")
            .ConfigureAwait(true);

        object body = action switch
        {
            "approve" => new { sheetDefId = scenario.B.SheetDefId, periodKey = scenario.B.PeriodKey.Value, approved = true, reason = (string?)null },
            "reopen" => new { sheetDefId = scenario.B.SheetDefId, periodKey = scenario.B.PeriodKey.Value, reason = "S2 oracle" },
            _ => new { sheetDefId = scenario.B.SheetDefId, periodKey = scenario.B.PeriodKey.Value },
        };

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{scenario.B.DocumentId}/{action}", UriKind.Relative), body)
            .ConfigureAwait(true);

        var problem = await AssertDeniedAsync(response, app).ConfigureAwait(true);
        Assert.True(
            Reason(problem) == nameof(EditDenyReason.NoGrant),
            $"{action}: очікували причину NoGrant\n{problem}");
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

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"PATCH у B: {(int)response.StatusCode}\n{await response.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

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

    private static async Task<string> AssertDeniedAsync(HttpResponseMessage response, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"очікували відмову, отримали {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        return body;
    }

    /// <summary>Причина відмови з тіла проблеми; <c>null</c> — поля немає.</summary>
    private static string? Reason(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        foreach (var name in new[] { "reason", "params" })
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("reason", out var inner))
            {
                return inner.GetString();
            }
        }

        return null;
    }

    /// <summary>Два проєкти одного шаблону; документ — у B.</summary>
    /// <param name="state">Стан аркуша в B; <c>null</c> — рядка стану немає (Draft).</param>
    private async Task<(TestDocument B, int ProjectA)> ArrangeAsync(DocumentStatus? state)
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
            b.TemplateVersionId, PeriodKind.Monthly, policyId, "Asia/Almaty");
        db.Projects.Add(projectA);

        // Аркуш у складі документа B: без цього подання відмовляє раніше за
        // права (`HasSheetAsync`, 404), і тест не доводив би нічого про S2.
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
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
