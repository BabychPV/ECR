// tests/Ecr.Api.Tests/CrossSheetFormulaRecalculationTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Крос-аркушна формула шаблону наскрізно: правка комірки на аркуші A перераховує формулу аркуша B, що читає
/// її як <c>[SheetA].[Table].[RowKey].[Column]</c> і як діапазон <c>SUM([SheetA].[Table].[R1:R3].[Column])</c>.
/// </summary>
/// <remarks>
/// Справжній хост, справжній SQL Server, справжня публікація (<c>POST …/publish</c> пише граф
/// <c>cfg.FormulaDependency</c>) і справжня черга: <c>PATCH …/cells</c> ставить coalesced
/// <c>FormulaRecalculationJob</c>, виконавець тестового хоста її виконує (повторюваних тригерів немає —
/// <c>RecurringTriggerVeto</c>), тест опитує зріз аркуша B до появи очікуваного числа. Формули пишуться
/// авторським шляхом (<c>PUT …/formulas/column/{id}</c>) — тобто і збереження, і публікація приймають посилання
/// на інший аркуш. Семантика <c>null</c> — за <c>05-engines.md</c>: агрегат поглинає стерту комірку, бінарна
/// операція над стертим посиланням дає <c>null</c>.
///
/// <c>FormulaDef.IsCrossSheet</c> не виставляється ніде (D-227) — відкладення крос-аркушного rollup робить сама
/// черга зі злиттям (CAL-01); цей тест доводить, що відкладене таки рахується, а не губиться.
///
/// Мутаційні докази (локально, не комітяться):
/// (а) перевірено: <c>RecalculationService.RunAsync</c> читає лише екземпляр правки
///     (<c>knownInstances ?? [instance]</c> замість <c>GetTableInstancesAsync</c>) — таблиця аркуша B не потрапляє
///     в прогін, червоніє перше ж очікування («очікувано OUT1=20, OUT2=60; є OUT1=null, OUT2=null»);
/// (б) <c>PatchCellsHandler.EnqueueRecalculationAsync</c> не ставить <c>IFormulaRecalculationJob</c> — значення
///     аркуша B не з'являються, червоне так само;
/// (в) <c>ReferenceResolver.FindTable</c> за порожніх кандидатів повертає власну таблицю формули замість
///     <c>expr.ref.unknownTable</c> — публікація проходить (204), червоніє негативний тест.
/// </remarks>
[Collection("SqlServer")]
public sealed class CrossSheetFormulaRecalculationTests(SqlServerFixture sql)
{
    private const string Password = "Cross-Sheet-Recalc-1!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly TimeSpan RecalcDeadline = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.9")]
    public async Task Правка_аркуша_A_перераховує_посилання_і_суму_діапазону_на_аркуші_B()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // Авторський шлях: обидві крос-аркушні формули зберігаються…
        var single = $"[{s.SheetA}].[{s.TableA}].[{s.RowKeys[0]}].[{s.InputColumn}] * 2";
        var range = $"SUM([{s.SheetA}].[{s.TableA}].[{s.RowKeys[0]}:{s.RowKeys[2]}].[{s.InputColumn}])";
        await ExpectAsync(app, HttpStatusCode.OK, SaveFormulaAsync(client, s, s.Out1ColumnId, single)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, SaveFormulaAsync(client, s, s.Out2ColumnId, range)).ConfigureAwait(true);

        // …і публікація їх приймає (204 — без жодної діагностики).
        await ExpectAsync(app, HttpStatusCode.NoContent, PublishAsync(client, s.VersionId)).ConfigureAwait(true);

        // 1. Перше введення: R1 = 10, R2 = 20, R3 = 30.
        await PatchAsync(app, client, s, (s.RowKeys[0], 10m), (s.RowKeys[1], 20m), (s.RowKeys[2], 30m))
            .ConfigureAwait(true);
        await ExpectSheetBAsync(app, client, s, out1: 20m, out2: 60m).ConfigureAwait(true);

        // 2. Повторна правка — значення оновлюються, а не лишаються першими.
        await PatchAsync(app, client, s, (s.RowKeys[0], 15m)).ConfigureAwait(true);
        await ExpectSheetBAsync(app, client, s, out1: 30m, out2: 65m).ConfigureAwait(true);

        // 3. Стерта комірка всередині діапазону: SUM її поглинає, одиночне посилання не зачеплене.
        await PatchAsync(app, client, s, (s.RowKeys[1], null)).ConfigureAwait(true);
        await ExpectSheetBAsync(app, client, s, out1: 30m, out2: 45m).ConfigureAwait(true);

        // 4. Стерта комірка одиночного посилання: `null * 2` — null (поширюється), SUM = лише R3.
        await PatchAsync(app, client, s, (s.RowKeys[0], null)).ConfigureAwait(true);
        await ExpectSheetBAsync(app, client, s, out1: null, out2: 30m).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.9")]
    public async Task Посилання_на_неіснуючий_аркуш_чи_таблицю_публікація_відхиляє_expr_ref_unknownTable()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // Збереження відмовляє тим самим ключем…
        var unknownSheet = $"[NOSHEET].[{s.TableA}].[{s.RowKeys[0]}].[{s.InputColumn}] * 2";
        var saved = await ExpectAsync(
                app, HttpStatusCode.UnprocessableEntity, SaveFormulaAsync(client, s, s.Out1ColumnId, unknownSheet))
            .ConfigureAwait(true);
        Assert.Equal("expr.ref.unknownTable", MessageKey(saved));

        // …а формула, що потрапила в структуру в обхід збереження (неіснуюча таблиця на справжньому аркуші),
        // зупиняє публікацію з тією самою причиною.
        await using (var db = s.Builder.CreateContext())
        {
            var formula = new FormulaDef(
                s.TableBId, FormulaScope.Column, $"SUM([{s.SheetA}].[NOTABLE].[R1:R3].[{s.InputColumn}])",
                ExpressionDialect.Template);
            formula.AssignColumn(s.Out2ColumnId);
            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var refused = await ExpectAsync(app, HttpStatusCode.UnprocessableEntity, PublishAsync(client, s.VersionId))
            .ConfigureAwait(true);
        Assert.Equal("expr.ref.unknownTable", MessageKey(refused));

        await using var check = s.Builder.CreateContext();
        Assert.Equal(
            TemplateVersionStatus.Draft,
            await check.TemplateVersions.Where(v => v.Id == s.VersionId).Select(v => v.Status).SingleAsync()
                .ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder,
        TestDocument Document,
        int PeriodKey,
        int VersionId,
        string SheetA,
        string TableA,
        string InputColumn,
        IReadOnlyList<string> RowKeys,
        int TableBId,
        long InstanceB,
        string RowB,
        int Out1ColumnId,
        string Out1Code,
        int Out2ColumnId,
        string Out2Code,
        string UserName);

    private static Task<HttpResponseMessage> SaveFormulaAsync(HttpClient client, Scenario s, int columnId, string expression)
        => client.PutAsJsonAsync(
            new Uri(
                $"/api/v1/template-versions/{s.VersionId}/tables/{s.TableBId}/formulas/column/{columnId}",
                UriKind.Relative),
            new { dialect = "Template", expression });

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, int versionId)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative),
            new { reason = "крос-аркушна формула" });

    /// <summary>PATCH значень вхідної колонки аркуша A; <c>null</c> — стерти комірку.</summary>
    private static async Task PatchAsync(
        EcrApiFactory app, HttpClient client, Scenario s, params (string RowKey, decimal? Value)[] edits)
    {
        var versions = await RowVersionsAsync(app, client, s).ConfigureAwait(false);

        var rows = edits.Select(e => new
        {
            rowKey = e.RowKey,
            baseVersion = versions[e.RowKey],
            cells = new object[]
            {
                new { columnCode = s.InputColumn, value = e.Value?.ToString(CultureInfo.InvariantCulture) },
            },
        }).ToArray();

        await ExpectAsync(
                app, HttpStatusCode.OK,
                client.PatchAsJsonAsync(
                    new Uri($"/api/v1/documents/{s.Document.DocumentId}/cells", UriKind.Relative),
                    new
                    {
                        tableInstanceId = s.Document.TableInstanceId,
                        periodKey = s.PeriodKey,
                        origin = "UserEdit",
                        rows,
                    }))
            .ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, string>> RowVersionsAsync(EcrApiFactory app, HttpClient client, Scenario s)
    {
        var body = await ExpectAsync(
                app, HttpStatusCode.OK,
                client.GetAsync(new Uri(
                    $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative)))
            .ConfigureAwait(false);

        return JsonDocument.Parse(body).RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("rowKey").GetString()!, r => r.GetProperty("rowVersion").GetString()!);
    }

    /// <summary>
    /// Опитує зріз аркуша B (той самий GET, що й сітка після перерахунку), доки обидві формули не покажуть
    /// очікуване; черга асинхронна щодо PATCH, тож чекання — до <see cref="RecalcDeadline"/>.
    /// </summary>
    private static async Task ExpectSheetBAsync(EcrApiFactory app, HttpClient client, Scenario s, decimal? out1, decimal? out2)
    {
        var deadline = DateTime.UtcNow + RecalcDeadline;
        var last = string.Empty;
        (decimal? Out1, decimal? Out2) seen = default;

        while (true)
        {
            last = await ExpectAsync(
                    app, HttpStatusCode.OK,
                    client.GetAsync(new Uri(
                        $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.InstanceB}", UriKind.Relative)))
                .ConfigureAwait(false);

            var row = JsonDocument.Parse(last).RootElement.GetProperty("rows").EnumerateArray()
                .Single(r => r.GetProperty("rowKey").GetString() == s.RowB);
            var cells = row.GetProperty("cells");
            seen = (Number(cells, s.Out1Code), Number(cells, s.Out2Code));

            if (seen == (out1, out2))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                break;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        Assert.Fail(
            $"Аркуш B: очікувано OUT1={Show(out1)}, OUT2={Show(out2)}; є OUT1={Show(seen.Out1)}, OUT2={Show(seen.Out2)}.\n"
            + $"{last}\n{app.ErrorsText}");
    }

    private static decimal? Number(JsonElement cells, string code)
    {
        if (!cells.TryGetProperty(code, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String => decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string Show(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string? MessageKey(string body)
        => JsonDocument.Parse(body).RootElement.TryGetProperty("messageKey", out var key) ? key.GetString() : body;

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == expected,
            $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");

        return body;
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>
    /// Аркуш A — таблиця будівника (Fixed, рядки R1..R3, друга колонка — числовий вхід); аркуш B — нова таблиця
    /// з двома колонками-формулами й одним рядком. Версія — ЧЕРНЕТКА: формули пише й публікує сам тест.
    /// Період — поточний і відкритий, проєкт активний (як у <c>ApproverEditHttpFlowTests</c>), користувач має
    /// <c>Write</c> на проєкт і права автора шаблону.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder
            .BuildAsync(periodKey, columnCount: 2, rowCount: 3, rowMode: TableRowMode.Fixed)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using var db = builder.CreateContext();

        var tableA = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == document.TableDefId).ConfigureAwait(false);
        var input = await db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == document.ColumnDefIds[1]).ConfigureAwait(false);
        var rowKeys = await db.RowDefs.AsNoTracking()
            .Where(r => r.TableDefId == document.TableDefId)
            .OrderBy(r => r.Ordinal)
            .Select(r => r.RowKeyValue)
            .ToListAsync()
            .ConfigureAwait(false);

        // Аркуш B.
        var sheetB = new SheetDef(document.TemplateVersionId, EcrCode.Create($"XB{tag}"), Name("Sheet B"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableB = new TableDef(
            sheetB.Id, EcrCode.Create($"TB{tag}"), Name("Table B"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var out1 = new ColumnDef(tableB.Id, EcrCode.Create("OUT1"), Name("OUT1"), 1, CellDataType.Formula);
        var out2 = new ColumnDef(tableB.Id, EcrCode.Create("OUT2"), Name("OUT2"), 2, CellDataType.Formula);
        db.ColumnDefs.AddRange(out1, out2);
        const string rowB = "B1";
        db.RowDefs.Add(new RowDef(tableB.Id, RowKey.Create(rowB), 1, Name(rowB), RowKind.Item));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceB = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var rowIdB = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var key = new PeriodKey(periodKey);
        db.TableInstances.Add(new TableInstance(key, instanceB, document.DocumentId, tableB.Id, now));
        db.TableRows.Add(new TableRow(key, rowIdB, instanceB, RowKey.Create(rowB), 1, now));

        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, sheetB.Id));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var userName = await NewUserAsync(db, document.ProjectId).ConfigureAwait(false);

        return new Scenario(
            builder, document, periodKey, document.TemplateVersionId,
            document.SheetCode, tableA.Code, input.Code, rowKeys,
            tableB.Id, instanceB, rowB, out1.Id, out1.Code, out2.Id, out2.Code, userName);
    }

    /// <summary>Користувач із власною роллю: <c>Write</c> на проєкт і права автора/публікатора шаблону.</summary>
    private static async Task<string> NewUserAsync(EcrDbContext db, int projectId)
    {
        var name = $"xs_{Guid.NewGuid():N}"[..20];

        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"R{Guid.NewGuid():N}"[..12].ToUpperInvariant()),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Cross-sheet" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in new[] { "Document.View", "Template.View", "Template.Edit", "Template.Publish" })
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
