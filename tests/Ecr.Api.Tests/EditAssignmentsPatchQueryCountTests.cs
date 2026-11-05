// tests/Ecr.Api.Tests/EditAssignmentsPatchQueryCountTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
/// D-454 (PM-EDITASSIGN-P3, п. d): теплий <c>PATCH …/cells</c> ВИКОНАВЦЯ з Write-передачею аркуша не платить за кількість
/// колонок документа - число звернень до БД однакове для 2 і 30 колонок, з колонковим грантом делегатора (повний перебір
/// колонок у <c>OwnLevelOver</c>) і без нього; і лише на сталу малу різницю більше за той самий запис власника з Write на
/// проєкт (без передачі).
/// </summary>
/// <remarks>
/// <para>
/// Лічильник - <see cref="SqlClientCommandCounter"/> з <c>AsyncLocal</c>-міткою (як <c>PatchCellsQueryCountTests</c>); кеш
/// штампа й вікно ревізії шаблону розширено до 300 с / 5 хв, щоб таймінг не додавав читань.
/// </para>
/// <para>
/// Мутаційний доказ (запущений): у <c>AccessDecisionService.EditAssignmentsAsync</c>/<c>ParticipationGrantsAsync</c> додати
/// безглуздий запит на кожну колонку (напр. <c>await db.ColumnDefs.AnyAsync(…)</c> у циклі) - червоніє рівність 2 ≡ 30.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentsPatchQueryCountTests(SqlServerFixture sql)
{
    private const string Password = "Api-Edit-QC-2026!";

    /// <summary>Стеля надбавки виконавця з передачею над записом власника з Write на проєкт (звернень).</summary>
    /// <remarks>
    /// ⛔ Храповик: лише знижується. Замір 05.11 (теплий PATCH 2 рядків): власник 24, виконавець з передачею 35 - надбавка 11
    /// (профіль делегатора, проєкт, читання передач, повторна перевірка під блокуванням; без мемоізації між викликами
    /// <c>ParticipationGrantsAsync</c> у межах запиту). Від числа колонок надбавка НЕ залежить (2 ≡ 30).
    /// </remarks>
    private const int MaxExecutorSurcharge = 11;

    private const int MeasuredRounds = 3;

    private const int Rows = 2;

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P3-QUERYCOUNT-EXECUTOR-PATCH")]
    public async Task PATCH_виконавця_з_передачею_не_росте_запитами_з_кількістю_колонок(bool delegatorHasColumnGrant)
    {
        var narrow = await ArrangeAsync(columns: 2, delegatorHasColumnGrant, executorGetsAssignment: true).ConfigureAwait(true);
        var wide = await ArrangeAsync(columns: 30, delegatorHasColumnGrant, executorGetsAssignment: true).ConfigureAwait(true);
        var owner = await ArrangeAsync(columns: 2, delegatorHasColumnGrant: false, executorGetsAssignment: false).ConfigureAwait(true);

        var narrowCount = await MeasureAsync(narrow).ConfigureAwait(true);
        var wideCount = await MeasureAsync(wide).ConfigureAwait(true);
        var ownerCount = await MeasureAsync(owner).ConfigureAwait(true);

        Assert.True(
            wideCount == narrowCount,
            $"PATCH виконавця з передачею: 2 колонки - {narrowCount} звернень, 30 колонок - {wideCount} (мають збігатися). "
            + $"Колонковий грант делегатора: {delegatorHasColumnGrant}.");
        Assert.True(
            narrowCount - ownerCount <= MaxExecutorSurcharge,
            $"Виконавець з передачею платить {narrowCount} звернень проти {ownerCount} у власника з Write (надбавка > {MaxExecutorSurcharge}).");
        Assert.True(narrowCount >= ownerCount, $"Виконавець {narrowCount} < власник {ownerCount}: замір підозрілий.");
    }

    /// <summary>Найгірше з теплих замірів звернень до БД на один PATCH усіх комірок двох рядків.</summary>
    private async Task<int> MeasureAsync(Scenario s)
    {
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300, revisionWindow: TimeSpan.FromMinutes(5));
        app.Server.PreserveExecutionContext = true;
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var sliceUri = new Uri($"/api/v1/documents/{Num(s.Document.DocumentId)}/tables/{Num(s.Document.TableInstanceId)}", UriKind.Relative);
        var (columns, versions) = await OpenSliceAsync(client, sliceUri, app).ConfigureAwait(true);
        Assert.Equal(s.Columns, columns.Count);

        // Розігрів: кеші метаданих, доступу, штампа.
        versions = await PatchAsync(client, s, columns, versions, round: 0, app).ConfigureAwait(true);

        var worst = 0;
        using var counter = new SqlClientCommandCounter(new CommandTally(), _ => Measuring.Value is { Value: true });
        var rounds = new List<int>();
        for (var round = 1; round <= MeasuredRounds; round++)
        {
            counter.Tally.Reset();
            var flag = new StrongBox<bool>(true);
            Measuring.Value = flag;
            try
            {
                versions = await PatchAsync(client, s, columns, versions, round, app).ConfigureAwait(true);
            }
            finally
            {
                flag.Value = false;
                Measuring.Value = null;
            }

            counter.AssertObserved();
            var total = counter.Tally.Snapshot().Total;
            rounds.Add(total);
            worst = Math.Max(worst, total);
        }

        Assert.True(rounds.All(r => r == worst), $"Теплі заміри розходяться: {string.Join(", ", rounds)}.");
        return worst;
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<(List<string> Columns, Dictionary<string, string> Versions)> OpenSliceAsync(
        HttpClient client, Uri sliceUri, EcrApiFactory app)
    {
        var opened = await client.GetAsync(sliceUri).ConfigureAwait(false);
        var body = await opened.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}\n{body}\n{app.ErrorsText}");

        var root = JsonDocument.Parse(body).RootElement;
        var columns = root.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("code").GetString()!).ToList();
        var versions = root.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("rowKey").GetString()!, r => r.GetProperty("rowVersion").GetString()!, StringComparer.Ordinal);
        return (columns, versions);
    }

    private static async Task<Dictionary<string, string>> PatchAsync(
        HttpClient client, Scenario s, List<string> columns, IReadOnlyDictionary<string, string> versions, int round, EcrApiFactory app)
    {
        var rows = versions.Keys.Order(StringComparer.Ordinal).Select((rowKey, r) => new
        {
            rowKey,
            baseVersion = versions[rowKey],
            cells = columns.Select((code, c) => new
            {
                columnCode = code,
                value = c == 0
                    ? (object)$"r{round}-{r}"
                    : (object)(((round * 1000) + (r * 10) + c) / 100m).ToString(CultureInfo.InvariantCulture),
            }).ToArray(),
        }).ToArray();

        var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.Document.DocumentId)}/cells", UriKind.Relative),
            new { tableInstanceId = s.Document.TableInstanceId, periodKey = s.PeriodKey, origin = "UserEdit", rows }).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"PATCH раунд {round}: {response.StatusCode}\n{body}\n{app.ErrorsText}");

        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(Rows * columns.Count, root.GetProperty("appliedCells").GetInt32());
        return root.GetProperty("rowVersions").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private sealed record Scenario(TestDocument Document, int PeriodKey, string UserName, int Columns);

    /// <summary>
    /// Документ у відкритому поточному періоді; делегатор з Manage (опційно ще й колонковий грант Manage, щоб у профілі були
    /// правила колонок і <c>OwnLevelOver</c> ходив по всіх колонках); виконавець лише з Read і Write-передачею аркуша - або
    /// (контроль) власник із Write на проєкт без передач.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(int columns, bool delegatorHasColumnGrant, bool executorGetsAssignment)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder.BuildAsync(periodKey, columnCount: columns, rowCount: Rows, rowMode: TableRowMode.Mixed).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods.FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        async Task<(User User, Role Role)> NewUserAsync(string prefix, GrantLevel level)
        {
            var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"EQ_{Guid.NewGuid():N}"[..20]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Edit assignment query count" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, level));
            await db.SaveChangesAsync().ConfigureAwait(false);
            return (user, role);
        }

        if (!executorGetsAssignment)
        {
            var (writer, _) = await NewUserAsync("eqw", GrantLevel.Write).ConfigureAwait(false);
            return new Scenario(document, periodKey, writer.UserName, columns);
        }

        var (delegator, delegatorRole) = await NewUserAsync("eqd", GrantLevel.Manage).ConfigureAwait(false);
        var (executor, _) = await NewUserAsync("eqe", GrantLevel.Read).ConfigureAwait(false);

        if (delegatorHasColumnGrant)
        {
            // Колонковий грант (не заборона) - вмикає повний перебір колонок, не міняючи рівнів.
            db.ResourceGrants.Add(new ResourceGrant(delegatorRole.Id, ResourceKind.Column, document.ColumnDefIds[1], GrantLevel.Manage));
        }

        var assignment = new EditAssignment(
            document.DocumentId,
            EditAssignmentSlice.Create(document.SheetCode, tableCode: null, monthNumber: null, periodKey: null),
            isExclusive: false,
            delegatedByUserId: delegator.Id,
            handoffId: Guid.NewGuid(),
            createdByUserId: delegator.Id,
            utcNow: now,
            level: GrantLevel.Write);
        assignment.AddRecipient(executor.Id, delegator.Id, now);
        new EditAssignmentStore(db).Add(assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document, periodKey, executor.UserName, columns);
    }
}
