// tests/Ecr.Api.Tests/PatchCellsQueryCountTests.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>WR-04</c>: храповик звернень до БД на <c>PATCH …/cells</c> із 100
/// комірками наявних рядків у теплому стані.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Храповик — число, яке має право лише знижуватися. Опустили кількість
/// звернень — опускається і <see cref="MaxCommands"/> тим самим комітом.
/// </para>
/// <para>
/// ⚠ Лічильник — <see cref="SqlClientCommandCounter"/>, а не
/// <see cref="DbCommandCounter"/>: гарячий шлях запису
/// (<c>NormalizedCellStore</c>) іде сирими командами повз EF, і EF-перехоплювач
/// не бачив би рівно <c>ClaimRows</c>/<c>MERGE</c>/<c>Touch</c>.
/// </para>
/// <para>
/// ⚠ Обсяг лічильника — процес, тож зараховуються лише команди, виконані в
/// асинхронному потоці САМОГО запиту: <c>AsyncLocal</c>-мітка ставиться перед
/// <c>PATCH</c> і знімається після відповіді, а <c>TestServer</c> із
/// <c>PreserveExecutionContext</c> несе її в конвеєр застосунку. Перерахунок,
/// поставлений попереднім записом, і періодичні задачі в число не потрапляють —
/// інакше храповик плавав би від таймінгу фону.
/// </para>
/// <para>
/// ⚠ «Теплий стан»: перед заміром — один такий самий <c>PATCH</c> (кеш
/// метаданих, профіль доступу, кеш штампа сеансу прогріті). Кеш штампа —
/// 300 с замість продуктивних 5: інакше повільна машина між двома запитами
/// додала б одне читання штампа, і храповик червонів би не від коду.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchCellsQueryCountTests(SqlServerFixture sql)
{
    /// <summary>Стеля звернень до БД на один теплий <c>PATCH</c> 100 комірок.</summary>
    /// <remarks>
    /// ⛔ Лише знижується. Базова лінія <c>MS-01-BASELINE.md</c> §3.1 — 41 (знята
    /// до <c>WR-01/02/05/06</c>); ціль <c>WR-04</c> — ≤ 10.
    ///
    /// Історія: 29 — на <c>782b7add</c> (до WR-04), тобто точка відліку
    /// храповика. 28 — п. 1 (без другого «дотику» рядків після
    /// <c>ApplyAsync</c>). 27 — п. 2 (екземпляр таблиці розв'язує лише
    /// контролер).
    /// </remarks>
    private const int MaxCommands = 27;

    private const string Password = "Api-Patch-Ratchet-2026!";

    private const int Rows = 10;

    private const int Columns = 10;

    /// <summary>Скільки теплих записів міряється.</summary>
    private const int MeasuredRounds = 3;

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Almaty").ToTimeZoneInfo();

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WR-04")]
    public async Task PATCH_100_комірок_у_теплому_стані_не_перевищує_стелю_звернень()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300);
        app.Server.PreserveExecutionContext = true;
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var sliceUri = new Uri(
            $"/api/v1/documents/{scenario.DocumentId}/tables/{scenario.TableInstanceId}", UriKind.Relative);
        var patchUri = new Uri($"/api/v1/documents/{scenario.DocumentId}/cells", UriKind.Relative);

        var (columnCodes, versions) = await OpenSliceAsync(client, sliceUri, app).ConfigureAwait(true);
        Assert.Equal(Columns, columnCodes.Count);
        Assert.Equal(Rows, versions.Count);

        // ── Розігрів ─────────────────────────────────────────────────────
        versions = await PatchAsync(client, patchUri, scenario, columnCodes, versions, round: 0, app)
            .ConfigureAwait(true);

        // ── Замір ────────────────────────────────────────────────────────
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

        var rounds = new List<(int Total, string Detail)>();
        for (var round = 1; round <= MeasuredRounds; round++)
        {
            counter.Tally.Reset();
            texts.Clear();

            var flag = new StrongBox<bool>(true);
            Measuring.Value = flag;
            try
            {
                versions = await PatchAsync(client, patchUri, scenario, columnCodes, versions, round, app)
                    .ConfigureAwait(true);
            }
            finally
            {
                flag.Value = false;
                Measuring.Value = null;
            }

            var seen = counter.Tally.Snapshot();

            // ⛔ Підлога: лічильник мусить бачити САМЕ команди запису цього
            // запиту — інакше «звернень мало» означало б «фільтр усе відкинув».
            counter.AssertObserved();
            Assert.True(
                seen["MERGE doc.CellValue"] >= 1,
                $"Лічильник не бачить MERGE doc.CellValue — фільтр або підписка зламані.\n{Describe(seen, texts)}");
            Assert.True(seen.Total >= 1, Describe(seen, texts));

            rounds.Add((seen.Total, Describe(seen, texts)));
        }

        // ⚠ Суворо: береться НАЙБІЛЬШЕ з теплих замірів, а не найменше. Шлях
        // детермінований, і розкид між ними — сам по собі знахідка.
        var worst = rounds.MaxBy(r => r.Total);

        Assert.True(
            worst.Total <= MaxCommands,
            $"PATCH {Rows * Columns} комірок: {worst.Total} звернень до БД, стеля {MaxCommands}. "
            + $"Заміри: {string.Join(", ", rounds.Select(r => r.Total))}.\n{worst.Detail}");
    }

    /// <summary>Розклад звернень: категорії і тексти, скорочені до суті.</summary>
    private static string Describe(CommandTallySnapshot seen, IEnumerable<string> texts)
    {
        var text = new StringBuilder(seen.Format());
        text.AppendLine("Команди по черзі:");

        var i = 0;
        foreach (var command in texts)
        {
            var flat = string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            text.AppendLine(CultureInfo.InvariantCulture, $"  {++i,2}. {(flat.Length > 180 ? flat[..180] + "…" : flat)}");
        }

        return text.ToString();
    }

    /// <summary>Коди колонок і версії рядків — так, як їх бачить клієнт.</summary>
    private static async Task<(List<string> Columns, Dictionary<string, string> Versions)> OpenSliceAsync(
        HttpClient client, Uri sliceUri, EcrApiFactory app)
    {
        var opened = await client.GetAsync(sliceUri).ConfigureAwait(false);
        var body = await opened.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}\n{body}\n{app.ErrorsText}");

        var root = JsonDocument.Parse(body).RootElement;
        var columns = root.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ToList();
        var versions = root.GetProperty("rows").EnumerateArray()
            .ToDictionary(
                r => r.GetProperty("rowKey").GetString()!,
                r => r.GetProperty("rowVersion").GetString()!,
                StringComparer.Ordinal);

        return (columns, versions);
    }

    /// <summary>
    /// Один <c>PATCH</c> усіх 100 комірок; повертає нові версії рядків із
    /// відповіді.
    /// </summary>
    private static async Task<Dictionary<string, string>> PatchAsync(
        HttpClient client,
        Uri patchUri,
        Scenario scenario,
        IReadOnlyList<string> columnCodes,
        IReadOnlyDictionary<string, string> versions,
        int round,
        EcrApiFactory app)
    {
        var rows = versions.Keys.Order(StringComparer.Ordinal).Select((rowKey, r) => new
        {
            rowKey,
            baseVersion = versions[rowKey],
            cells = columnCodes.Select((code, c) => new
            {
                columnCode = code,
                value = c == 0
                    ? (object)$"r{round}-{r}"
                    : (object)(((round * 1000) + (r * 10) + c) / 100m).ToString(CultureInfo.InvariantCulture),
            }).ToArray(),
        }).ToArray();

        var response = await client.PatchAsJsonAsync(patchUri, new
        {
            tableInstanceId = scenario.TableInstanceId,
            periodKey = scenario.PeriodKey,
            origin = "UserEdit",
            rows,
        }).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"PATCH раунд {round}: {response.StatusCode}\n{body}\n{app.ErrorsText}");

        var root = JsonDocument.Parse(body).RootElement;

        // ⛔ Запис справді стався — інакше «мало звернень» доводило б лише, що
        // батч нічого не записав.
        Assert.Equal(Rows * Columns, root.GetProperty("appliedCells").GetInt32());

        var next = root.GetProperty("rowVersions").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

        // ⚠ Відповідь мусить нести НОВУ версію кожного записаного рядка: інакше
        // наступний раунд упреться в ECR-CELL-0409, і храповик мовчки міряв би
        // шлях відмови замість шляху запису.
        foreach (var (rowKey, old) in versions)
        {
            Assert.True(next.TryGetValue(rowKey, out var fresh), $"Відповідь без версії рядка {rowKey}.");
            Assert.NotEqual(old, fresh);
        }

        return next;
    }

    /// <summary>Клієнт із чинним сеансом уже заведеного локального користувача.</summary>
    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>
    /// Документ 10×10, власна роль із грантом <c>Write</c>, активний проєкт і
    /// відкритий поточний період — та сама підготовка, що в
    /// <c>CellWriteRoundTripTests</c>.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        // Поточний період: `PeriodStateJob` на старті закрив би минулий.
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder
            .BuildAsync(periodKey, columnCount: Columns, rowCount: Rows, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);

        var userName = $"ratchet_{Guid.NewGuid():N}"[..20];
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"RATCHET_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "WR-04 ratchet writer" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);

        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document.DocumentId, document.TableInstanceId, userName, periodKey);
    }

    private sealed record Scenario(long DocumentId, long TableInstanceId, string UserName, int PeriodKey);
}
