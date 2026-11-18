// tests/Ecr.Api.Tests/FixedRowReferenceRecalculationTests.cs
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
/// B26 (P-0118-24, t1): міст аргументу <c>Volume</c> таблиці T7b_00 — колонка-формула, що читає ФІКСОВАНИЙ рядок
/// ТІЄЇ САМОЇ таблиці (<c>([Period].Sequence = 1 ? [T7b_00].[S3_9].[January] : …)</c>). Доводимо наскрізь, що граф
/// перерахунку (<c>cfg.FormulaDependency</c> з публікації, <c>FormulaRecalculationJob</c> після PATCH) підхоплює таке
/// посилання: правка комірки рядка R3 перераховує колонку-формулу в УСІХ рядках, включно з іншими.
/// </summary>
/// <remarks>
/// Справжній хост, SQL Server, авторське збереження формули (<c>PUT …/formulas/column/{id}</c>) і публікація — як у
/// <see cref="CrossSheetFormulaRecalculationTests"/>. Методологія читає колонку-міст після формул шаблону
/// (<c>RecalculationJob</c>: спершу формули, потім методології), тож перерахований міст і є вхід <c>@Volume</c>.
/// Мутація: формула на інший рядок (<c>R2</c>) — червоніє перше ж очікування.
/// </remarks>
[Collection("SqlServer")]
public sealed class FixedRowReferenceRecalculationTests(SqlServerFixture sql)
{
    private const string Password = "Fixed-Row-Ref-Recalc-1!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly TimeSpan RecalcDeadline = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B26")]
    public async Task Правка_фіксованого_рядка_перераховує_міст_у_всіх_рядках_таблиці()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // Та сама форма, що генерує gen_ecr_model.py для Volume: дужки (не селектор YearMonths) і посилання
        // [Таблиця].[Рядок].[Колонка] на фіксований рядок у кожній гілці.
        var source = $"[{s.Table}].[{s.RowKeys[2]}].[{s.InputColumn}]";
        var expression = $"([Period].Sequence = 1 ? {source} : {source})";
        await ExpectAsync(app, HttpStatusCode.OK, SaveFormulaAsync(client, s, expression)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, PublishAsync(client, s.VersionId)).ConfigureAwait(true);

        // 1. Вхід у рядки R1/R2 і фіксований R3 = 1000: міст = 1000 у кожному рядку (не власний вхід рядка).
        await PatchAsync(app, client, s, (s.RowKeys[0], 2.5m), (s.RowKeys[1], 7m), (s.RowKeys[2], 1000m)).ConfigureAwait(true);
        await ExpectBridgeAsync(app, client, s, 1000m).ConfigureAwait(true);

        // 2. Правка ЛИШЕ фіксованого рядка: перераховуються й рядки R1/R2.
        await PatchAsync(app, client, s, (s.RowKeys[2], 500m)).ConfigureAwait(true);
        await ExpectBridgeAsync(app, client, s, 500m).ConfigureAwait(true);

        // 3. Стерта комірка фіксованого рядка: міст порожній (аргументу Volume немає -> результату немає).
        await PatchAsync(app, client, s, (s.RowKeys[2], null)).ConfigureAwait(true);
        await ExpectBridgeAsync(app, client, s, null).ConfigureAwait(true);
    }

    private sealed record Scenario(
        TestDocument Document,
        int PeriodKey,
        int VersionId,
        string Table,
        int TableDefId,
        string InputColumn,
        IReadOnlyList<string> RowKeys,
        int BridgeColumnId,
        string BridgeCode,
        string UserName);

    private static Task<HttpResponseMessage> SaveFormulaAsync(HttpClient client, Scenario s, string expression)
        => client.PutAsJsonAsync(
            new Uri(
                $"/api/v1/template-versions/{s.VersionId}/tables/{s.TableDefId}/formulas/column/{s.BridgeColumnId}",
                UriKind.Relative),
            new { dialect = "Template", expression });

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, int versionId)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative),
            new { reason = "B26 fixed-row bridge" });

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

    /// <summary>Опитує зріз таблиці, доки міст у КОЖНОМУ рядку не дорівнює очікуваному (черга асинхронна).</summary>
    private static async Task ExpectBridgeAsync(EcrApiFactory app, HttpClient client, Scenario s, decimal? expected)
    {
        var deadline = DateTime.UtcNow + RecalcDeadline;
        var last = string.Empty;
        var seen = new List<decimal?>();

        while (true)
        {
            last = await ExpectAsync(
                    app, HttpStatusCode.OK,
                    client.GetAsync(new Uri(
                        $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative)))
                .ConfigureAwait(false);

            var rows = JsonDocument.Parse(last).RootElement.GetProperty("rows").EnumerateArray().ToList();
            seen = [.. s.RowKeys.Select(k => Number(rows.Single(r => r.GetProperty("rowKey").GetString() == k).GetProperty("cells"), s.BridgeCode))];

            if (seen.All(v => v == expected))
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
            $"Міст: очікувано {Show(expected)} у всіх рядках; є {string.Join(", ", seen.Select(Show))}.\n{last}\n{app.ErrorsText}");
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

    /// <summary>Таблиця будівника (Fixed, R1..R3, друга колонка — вхід) + колонка-формула BRIDGE у ній; версія — чернетка.</summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder
            .BuildAsync(periodKey, columnCount: 2, rowCount: 3, rowMode: TableRowMode.Fixed)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();

        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == document.TableDefId).ConfigureAwait(false);
        var input = await db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == document.ColumnDefIds[1]).ConfigureAwait(false);
        var rowKeys = await db.RowDefs.AsNoTracking()
            .Where(r => r.TableDefId == document.TableDefId)
            .OrderBy(r => r.Ordinal)
            .Select(r => r.RowKeyValue)
            .ToListAsync()
            .ConfigureAwait(false);

        var bridge = new ColumnDef(table.Id, EcrCode.Create("BRIDGE"), Name("Volume bridge"), 50, CellDataType.Formula);
        db.ColumnDefs.Add(bridge);

        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

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
            document, periodKey, document.TemplateVersionId, table.Code, table.Id, input.Code, rowKeys,
            bridge.Id, bridge.Code, userName);
    }

    private static async Task<string> NewUserAsync(EcrDbContext db, int projectId)
    {
        var name = $"fr_{Guid.NewGuid():N}"[..20];

        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"R{Guid.NewGuid():N}"[..12].ToUpperInvariant()),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Fixed-row bridge" }));
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
