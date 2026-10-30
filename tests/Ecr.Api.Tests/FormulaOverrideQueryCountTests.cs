// tests/Ecr.Api.Tests/FormulaOverrideQueryCountTests.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ D-461 (вимога людини 30.10 «продуктивно й кібербезпечно»): число звернень до БД на теплий <c>PATCH …/cells</c> і
/// <c>GET</c> зрізу НЕ росте з кількістю рядків/ручних значень — позначки читаються й пишуться НАБОРОМ (одна команда на
/// батч), а право — з профілю (без звернення).
/// </summary>
/// <remarks>
/// Той самий лічильник і та сама методика, що в <c>PatchCellsQueryCountTests</c> (<c>SqlClientCommandCounter</c>, мітка
/// <c>AsyncLocal</c> лише на потоці запиту; теплий стан). Числа пишуться в вивід тесту (звіт D-461).
/// Мутація (локально): <c>FormulaOverrideStore.ApplyAsync</c> — команда на кожну позначку — червоніє «10 ручних = 1 ручне».
/// </remarks>
[Collection("SqlServer")]
public sealed class FormulaOverrideQueryCountTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const string Password = "Override-Ratchet-D461!";
    private const int Rows = 10;

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();
    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-461")]
    public async Task Звернення_PATCH_і_зрізу_не_ростуть_з_кількістю_ручних_значень()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300, revisionWindow: TimeSpan.FromMinutes(5));
        app.Server.PreserveExecutionContext = true;
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var texts = new ConcurrentQueue<string>();
        using var counter = new SqlClientCommandCounter(new CommandTally(), command =>
        {
            if (Measuring.Value is not { Value: true })
            {
                return false;
            }

            texts.Enqueue(command.CommandText ?? string.Empty);
            return true;
        });

        async Task<int> MeasureAsync(Func<Task> call)
        {
            counter.Tally.Reset();
            texts.Clear();
            var flag = new StrongBox<bool>(true);
            Measuring.Value = flag;
            try
            {
                await call().ConfigureAwait(false);
            }
            finally
            {
                flag.Value = false;
                Measuring.Value = null;
            }

            counter.AssertObserved();
            return counter.Tally.Snapshot().Total;
        }

        // Розігрів: зріз, вхід, одне ручне значення й повернення.
        await PatchAsync(app, client, s, s.InputCode, [0], v => v, null).ConfigureAwait(true);
        await PatchAsync(app, client, s, s.FCode, [0], v => v * 3, "warm").ConfigureAwait(true);
        await PatchAsync(app, client, s, s.FCode, [0], _ => null, null).ConfigureAwait(true);

        var input = await MeasureAsync(() => PatchAsync(app, client, s, s.InputCode, [.. Enumerable.Range(0, Rows)], v => v + 1, null))
            .ConfigureAwait(true);
        var one = await MeasureAsync(() => PatchAsync(app, client, s, s.FCode, [1], v => v * 7, "one"))
            .ConfigureAwait(true);
        var ten = await MeasureAsync(() => PatchAsync(app, client, s, s.FCode, [.. Enumerable.Range(0, Rows)], v => v * 11, "ten"))
            .ConfigureAwait(true);
        var sliceTen = await MeasureAsync(() => SliceAsync(app, client, s)).ConfigureAwait(true);
        var restoreOne = await MeasureAsync(() => PatchAsync(app, client, s, s.FCode, [1], _ => null, null))
            .ConfigureAwait(true);
        var restoreNine = await MeasureAsync(() => PatchAsync(app, client, s, s.FCode, [0, 2, 3, 4, 5, 6, 7, 8, 9], _ => null, null))
            .ConfigureAwait(true);
        var sliceNone = await MeasureAsync(() => SliceAsync(app, client, s)).ConfigureAwait(true);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"D-461 звернення: PATCH входу {Rows} комірок = {input}; ручне 1 = {one}; ручні {Rows} = {ten}; "
            + $"повернення 1 = {restoreOne}; повернення 9 = {restoreNine}; зріз з {Rows} ручними = {sliceTen}; зріз без ручних = {sliceNone}"));

        Assert.True(ten == one, $"PATCH: 10 ручних = {ten} звернень, 1 ручне = {one}.\n{string.Join("\n", texts)}");
        Assert.True(restoreNine == restoreOne, $"Повернення: 9 = {restoreNine}, 1 = {restoreOne}.");
        Assert.True(sliceTen == sliceNone, $"Зріз: з ручними {sliceTen}, без {sliceNone}.");

        // Ручне значення платить сталу добавку до звичайного запису (позначки одним MERGE), не більше трьох команд.
        Assert.True(one - input is >= 0 and <= 3, $"Ручне {one} проти входу {input}.");
    }

    private sealed record Scenario(TestDocumentBuilder Builder, TestDocument Document, int PeriodKey, string InputCode, string FCode, string UserName);

    private static async Task PatchAsync(
        EcrApiFactory app, HttpClient client, Scenario s, string column, int[] rows, Func<int, int?> value, string? reason)
    {
        var versions = JsonDocument.Parse(await SliceAsync(app, client, s, quiet: true).ConfigureAwait(false)).RootElement
            .GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("rowKey").GetString()!, r => r.GetProperty("rowVersion").GetString()!);
        var keys = versions.Keys.Order(StringComparer.Ordinal).ToList();

        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = s.PeriodKey,
                origin = "UserEdit",
                rows = rows.Select(i => new
                {
                    rowKey = keys[i],
                    baseVersion = versions[keys[i]],
                    cells = new object[]
                    {
                        new
                        {
                            columnCode = column,
                            value = value(i + 1)?.ToString(CultureInfo.InvariantCulture),
                            overrideReason = reason,
                        },
                    },
                }).ToArray(),
            }).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}\n{body}\n{app.ErrorsText}");
    }

    private static async Task<string> SliceAsync(EcrApiFactory app, HttpClient client, Scenario s, bool quiet = false)
    {
        // ⚠ Допоміжне читання версій перед PATCH не входить у замір PATCH.
        var flag = Measuring.Value;
        if (quiet && flag is not null)
        {
            flag.Value = false;
        }

        try
        {
            using var response = await client.GetAsync(new Uri(
                $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}\n{body}\n{app.ErrorsText}");
            return body;
        }
        finally
        {
            if (quiet && flag is not null)
            {
                flag.Value = true;
            }
        }
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>
    /// Таблиця Mixed 10 рядків: C1 текст, C2 вхід, FX — колонкова формула <c>[C2] * 2</c> з «Дозволити ручне значення»
    /// (структура пишеться напряму: перерахунок тут не міряється). Користувач — Write + <c>Document.OverrideFormula</c>.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;
        var document = await builder.BuildAsync(periodKey, columnCount: 2, rowCount: Rows, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        var input = await db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == document.ColumnDefIds[1]).ConfigureAwait(false);
        var f = new ColumnDef(document.TableDefId, EcrCode.Create("FX"), Name("F"), 3, CellDataType.Formula);
        db.ColumnDefs.Add(f);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var formula = new FormulaDef(document.TableDefId, FormulaScope.Column, $"[{input.Code}] * 2", ExpressionDialect.Template);
        formula.AssignColumn(f.Id);
        formula.SetAllowManualOverride(true);
        db.FormulaDefs.Add(formula);

        var name = $"d461q_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(
            EcrCode.Create($"R{Guid.NewGuid():N}"[..12].ToUpperInvariant()),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-461 ratchet" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, EditRules.OverrideFormulaPermission));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods.FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(builder, document, periodKey, input.Code, f.Code, name);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
