// tests/Ecr.Api.Tests/SubmissionTargetQueryCountTests.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
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
/// D-6 (PERF): храповик звернень до БД на <c>GET …/submission-target</c> річного аркуша з правами по місяцях — кількість
/// НЕ залежить від кількості поданих/затверджених місяців (1 поданий + 1 затверджений і 6 + 6 — те саме число).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Рецензія D-6: права рахувались поштучно — <c>CanApproveOfAsync</c> на кожен поданий місяць (умови, крок, маршрут —
/// ≈ 4–5 запитів), грант подання й повернення так само; 12 поданих місяців давали ~60–75 запитів. Тепер умови всіх
/// місяців — двома запитами, маршрут — раз на версію шаблону, режим погодження — одним запитом.
/// </para>
/// <para>
/// ⚠ Лічильник і мітка заміру — як у <see cref="PatchCellsQueryCountTests"/>: <see cref="SqlClientCommandCounter"/>, лише
/// команди асинхронного потоку самого запиту; теплий стан (кеш штампа й ревізії — 300 с).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmissionTargetQueryCountTests(SqlServerFixture sql)
{
    /// <summary>Стеля звернень на один теплий запит. ⛔ Лише знижується.</summary>
    /// <remarks>
    /// 17 — точка відліку (D-6, 21.11): штамп профілю, видимість документа й аркуша, календар подання (проєкт, періоди,
    /// стани, винятки документа), версія періоду, режим погодження (2), перший крок маршруту відкликання (проєкт, версія,
    /// маршрут), пакет прав (документ+проєкт, періоди з умовами, маршрут версії періоду), ворота джерела (EXISTS зв'язки).
    /// Поштучний шлях до пакета давав ~60–75 на 12 місяців.
    /// 19 — стеля для спільної тестової бази: коли в ній є хоч одна зв'язка HSE400 у стані <c>ApprovedOnly</c> (дані інших
    /// тестів), ворота джерела після EXISTS читають ще стани аркушів періодів і зв'язки споживача (+2; для цього документа
    /// зв'язок немає, далі нічого). На порожній базі — 17. Від кількості місяців не залежить у жодному з двох випадків.
    /// </remarks>
    private const int MaxCommands = 19;

    private const string Password = "Submission-Target-Count-D6!";

    private static readonly DateTime Now = new(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc);

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-559")]
    public async Task Кількість_звернень_не_залежить_від_кількості_місяців()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300, revisionWindow: TimeSpan.FromMinutes(5));
        app.Server.PreserveExecutionContext = true;
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var uri = new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/submission-target?sheetDefId={s.Doc.SheetDefId}&periodKey=202601",
            UriKind.Relative);

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

        async Task<(int Total, string Detail, string Body)> MeasureAsync()
        {
            // Розігрів того самого стану, далі замір.
            await GetAsync(client, uri, app).ConfigureAwait(true);
            counter.Tally.Reset();
            texts.Clear();
            var flag = new StrongBox<bool>(true);
            Measuring.Value = flag;
            string body;
            try
            {
                body = await GetAsync(client, uri, app).ConfigureAwait(true);
            }
            finally
            {
                flag.Value = false;
                Measuring.Value = null;
            }

            counter.AssertObserved();
            var seen = counter.Tally.Snapshot();
            return (seen.Total, Describe(seen, texts), body);
        }

        // Один поданий (січень) і один затверджений (лютий) місяць.
        await StatesAsync(s, submitted: [1], approved: [2]).ConfigureAwait(true);
        var few = await MeasureAsync().ConfigureAwait(true);
        Assert.Contains("\"canDecide\":true", few.Body, StringComparison.Ordinal);
        Assert.Contains("\"canReturn\":true", few.Body, StringComparison.Ordinal);

        // Шість поданих і шість затверджених.
        await StatesAsync(s, submitted: [1, 2, 3, 4, 5, 6], approved: [7, 8, 9, 10, 11, 12]).ConfigureAwait(true);
        var many = await MeasureAsync().ConfigureAwait(true);
        Assert.Contains("\"canRecall\":true", many.Body, StringComparison.Ordinal);

        Assert.True(
            many.Total == few.Total,
            $"Кількість звернень залежить від кількості місяців: {few.Total} (1+1) проти {many.Total} (6+6).\n"
            + $"1+1:\n{few.Detail}\n6+6:\n{many.Detail}");
        Assert.True(many.Total <= MaxCommands, $"{many.Total} звернень, стеля {MaxCommands}.\n{many.Detail}");
    }

    private sealed record Scenario(TestDocument Doc, string UserName, int UserId, int FirstStepId);

    private static async Task<string> GetAsync(HttpClient client, Uri uri, EcrApiFactory app)
    {
        using var response = await client.GetAsync(uri).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        return body;
    }

    /// <summary>Подані місяці — моє подання на першому кроці; затверджені — затверджені. Решта — чернетки.</summary>
    private async Task StatesAsync(Scenario s, int[] submitted, int[] approved)
    {
        await using var db = Context();
        await db.ApprovalStates.Where(a => a.DocumentId == s.Doc.DocumentId).ExecuteDeleteAsync().ConfigureAwait(false);
        foreach (var month in submitted.Concat(approved))
        {
            var state = new ApprovalState(s.Doc.DocumentId, s.Doc.SheetDefId, 202600 + month);
            state.Submit(s.UserId, Now, s.FirstStepId);
            if (approved.Contains(month))
            {
                state.Approve(s.UserId, Now);
            }

            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Документ року 2026: 12 відкритих місяців, таблиця <c>MonthsInColumns</c> з 12 колонками, маршрут «роль A → роль B»;
    /// користувач — ролі A і B, грант Approve і право <c>Document.Reopen</c>.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601).ConfigureAwait(false);

        await using var db = Context();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

        var jan = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == 202601).ConfigureAwait(false);
        jan.AdvanceTo(PeriodState.Open, Now);
        for (byte month = 2; month <= 12; month++)
        {
            var period = new Period(
                doc.ProjectId, new PeriodKey(202600 + month), month, new DateOnly(2026, month, 1),
                new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)));
            period.AdvanceTo(PeriodState.Open, Now);
            db.Periods.Add(period);
        }

        var table = new TableDef(
            doc.SheetDefId, EcrCode.Create($"T6Q{Guid.NewGuid():N}"[..12]), Name("Year table"), 2,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);

        var name = $"d6q_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var roleA = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("D-6 step A"));
        var roleB = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("D-6 step B"));
        db.Roles.AddRange(roleA, roleB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        for (byte month = 1; month <= 12; month++)
        {
            var column = new ColumnDef(table.Id, EcrCode.Create($"M{month:00}"), Name($"M{month}"), month, CellDataType.Decimal);
            column.SetMonth(true, month);
            db.ColumnDefs.Add(column);
        }

        db.RolePermissions.Add(new RolePermission(roleA.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(roleA.Id, Ecr.Application.Workflow.ReopenDocumentHandler.Permission));
        db.RoleAssignments.Add(new RoleAssignment(roleA.Id, user.Id, principalSid: null));
        db.ResourceGrants.Add(new ResourceGrant(roleA.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Approve));
        // Роль останнього кроку теж — повернення затвердженого вимагає саме її (P2-4).
        db.RoleAssignments.Add(new RoleAssignment(roleB.Id, user.Id, principalSid: null));

        var route = new ApprovalRoute(EcrCode.Create($"D6Q_{Guid.NewGuid():N}"[..20]), Name("D-6 route"), doc.ProjectId);
        route.AddStep(roleA.Id);
        route.AddStep(roleB.Id);
        db.ApprovalRoutes.Add(route);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(doc, name, user.Id, route.Steps[0].Id);
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string name)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private static string Describe(CommandTallySnapshot seen, IEnumerable<string> texts)
    {
        var text = new StringBuilder(seen.Format());
        text.AppendLine("Команди по черзі:");
        var i = 0;
        foreach (var command in texts)
        {
            var flat = string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            text.AppendLine(CultureInfo.InvariantCulture, $"  {++i,2}. {(flat.Length > 200 ? flat[..200] + "…" : flat)}");
        }

        return text.ToString();
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
