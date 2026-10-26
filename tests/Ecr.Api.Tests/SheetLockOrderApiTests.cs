// tests/Ecr.Api.Tests/SheetLockOrderApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Q-17.3a: ДЕТЕРМІНОВАНИЙ доказ порядку замків періодів. Спостерігач обгортає <see cref="ISheetEditGate"/> і пише
/// послідовність (виняткове/спільне блокування, період) усередині одного запиту; тест вимагає, щоб періоди йшли
/// у СТРОГОМУ порядку за зростанням (одне й те саме блокування одного періоду — підряд), а набір був очікуваним.
/// </summary>
/// <remarks>
/// ⛔ Чому не лише стрес «3→5 і 5→3» (<c>MonthMirrorRowsApiTests</c>): спільні блокування один одного не блокують,
/// і стрес проходить також на зламаному порядку (перевірено мутацією). Дедлок вимагає виняткового учасника в
/// черзі, тож порядок доводиться самою послідовністю викликів, а не відсутністю дедлока.
/// ⚠ Книга (Excel-імпорт) тут не покрита: її порядок (<c>EnsureWorkbookSheetsEditableAsync</c>) тримається тим
/// самим правилом, але потребує файла книги; покривається окремим тестом імпорту (коміт 5).
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetLockOrderApiTests(SqlServerFixture sql)
{
    private const string Password = "Lock-Order-Q17!";
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";
    private const int Year = 2026;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private static readonly AsyncLocal<List<(char Kind, int Period)>?> Sink = new();

    private static readonly int[] AllInstances = [202601, 202603, 202605, 202608];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task PATCH_з_5_у_колонку_місяця_3_бере_замки_3_потім_5(bool flag)
    {
        var s = await ArrangeAsync(view: 5, instanceMonths: [1, 3]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var seen = await ObserveAsync(() => PatchAsync(client, s, ("M3", 3m))).ConfigureAwait(true);

        AssertAscending(seen);
        Assert.Equal([Year * 100 + 3, Year * 100 + 5], DistinctPeriods(seen));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task PATCH_з_3_у_колонку_місяця_5_бере_замки_3_потім_5(bool flag)
    {
        var s = await ArrangeAsync(view: 3, instanceMonths: [1, 5]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var seen = await ObserveAsync(() => PatchAsync(client, s, ("M5", 5m))).ConfigureAwait(true);

        AssertAscending(seen);
        Assert.Equal([Year * 100 + 3, Year * 100 + 5], DistinctPeriods(seen));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task PATCH_не_місячного_стовпця_з_5_бере_замки_року_за_зростанням_власний_5_серед_них()
    {
        var s = await ArrangeAsync(view: 5, instanceMonths: [1, 3]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var seen = await ObserveAsync(() => PatchAsync(client, s, ("PLAIN", 4m), ("M3", 3m)), () => app.ErrorsText)
            .ConfigureAwait(true);

        AssertAscending(seen);
        var periods = DistinctPeriods(seen);
        Assert.Contains(Year * 100 + 1, periods); // якір
        Assert.Contains(Year * 100 + 3, periods); // власник M3
        Assert.Contains(Year * 100 + 5, periods); // власний
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task CreateRow_і_DeleteRow_з_5_беруть_замки_всіх_екземплярів_року_за_зростанням_без_дублів()
    {
        var s = await ArrangeAsync(view: 5, instanceMonths: [1, 3, 8]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var created = await ObserveAsync(() => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/rows", UriKind.Relative),
            new { tableInstanceId = s.Doc.TableInstanceId, rowKey = "LOCKROW" })).ConfigureAwait(true);
        AssertAscending(created);
        Assert.True(DistinctPeriods(created).Intersect(AllInstances).Count() == 4);

        var deleted = await ObserveAsync(() => client.DeleteAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}/rows/LOCKROW", UriKind.Relative)))
            .ConfigureAwait(true);
        AssertAscending(deleted);
        Assert.True(DistinctPeriods(deleted).Intersect(AllInstances).Count() == 4);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    /// <summary>Періоди блокувань зростають; блокування одного періоду йдуть підряд (виняткове, потім спільне).</summary>
    private static void AssertAscending(List<(char Kind, int Period)> seen)
    {
        Assert.NotEmpty(seen);

        var collapsed = new List<int>();
        foreach (var (_, period) in seen)
        {
            if (collapsed.Count == 0 || collapsed[^1] != period)
            {
                collapsed.Add(period);
            }
        }

        Assert.True(
            collapsed.SequenceEqual(collapsed.Distinct().Order()),
            $"Порядок замків періодів не зростає або період береться двічі не підряд: {string.Join(" → ", seen.Select(x => $"{x.Kind}{x.Period}"))}");
    }

    private static List<int> DistinctPeriods(List<(char Kind, int Period)> seen) => [.. seen.Select(x => x.Period).Distinct().Order()];

    private static async Task<List<(char Kind, int Period)>> ObserveAsync(
        Func<Task<HttpResponseMessage>> action, Func<string>? serverErrors = null)
    {
        var sink = new List<(char, int)>();
        Sink.Value = sink;
        try
        {
            using var response = await action().ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}\n{body}\n{serverErrors?.Invoke()}");
        }
        finally
        {
            Sink.Value = null;
        }

        lock (sink)
        {
            return [.. sink];
        }
    }

    /// <summary>Обгортка шлюзу: пише (X/E, період) блокувань аркуша, поки в тесті стоїть <see cref="Sink"/>.</summary>
    private sealed class RecordingSheetGate(ISheetEditGate inner) : ISheetEditGate
    {
        public async Task<DocumentStatus> EnterEditAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        {
            Note('E', periodKey);
            return await inner.EnterEditAsync(documentId, sheetDefId, periodKey, ct).ConfigureAwait(false);
        }

        public async Task<DocumentStatus> EnterEditNoWaitAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        {
            Note('N', periodKey);
            return await inner.EnterEditNoWaitAsync(documentId, sheetDefId, periodKey, ct).ConfigureAwait(false);
        }

        public async Task EnterSubmitAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        {
            Note('X', periodKey);
            await inner.EnterSubmitAsync(documentId, sheetDefId, periodKey, ct).ConfigureAwait(false);
        }

        public Task<int?> EnterStructureAsync(long documentId, bool exclusive, CancellationToken ct)
            => inner.EnterStructureAsync(documentId, exclusive, ct);

        public Task EnterHeaderAsync(long documentId, bool exclusive, CancellationToken ct)
            => inner.EnterHeaderAsync(documentId, exclusive, ct);

        private static void Note(char kind, PeriodKey period)
        {
            if (Sink.Value is { } sink)
            {
                lock (sink)
                {
                    sink.Add((kind, period.Value));
                }
            }
        }
    }

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Doc, string UserName, string RowKey,
        Dictionary<string, string> ColumnCodes, int ViewPeriod);

    private async Task<(EcrApiFactory App, HttpClient Client)> BootAsync(Scenario s, bool flag)
    {
        Environment.SetEnvironmentVariable(FlagVariable, flag ? "true" : "false");
        try
        {
            var app = new EcrApiFactory(sql)
            {
                ExtraServices = services =>
                {
                    var original = services.Last(d => d.ServiceType == typeof(ISheetEditGate));
                    services.AddScoped<ISheetEditGate>(sp => new RecordingSheetGate(
                        original.ImplementationFactory is { } factory
                            ? (ISheetEditGate)factory(sp)
                            : (ISheetEditGate)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)));
                },
            };
            app.Server.PreserveExecutionContext = true;
            var client = app.CreateClient();
            var login = await client.PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName = s.UserName, password = Password }).ConfigureAwait(false);
            Assert.True(login.IsSuccessStatusCode, $"Вхід {s.UserName}: {login.StatusCode}: {app.ErrorsText}");

            return (app, client);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagVariable, null);
        }
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Scenario s, params (string Alias, decimal Value)[] cells)
    {
        using var read = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false);
        var text = await read.Content.ReadAsStringAsync().ConfigureAwait(false);
        var version = JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == s.RowKey)
            .GetProperty("rowVersion").GetString()!;

        return await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Doc.TableInstanceId,
                periodKey = s.ViewPeriod,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = s.RowKey,
                        baseVersion = version,
                        cells = cells.Select(c => new
                        {
                            columnCode = s.ColumnCodes[c.Alias],
                            value = (object)c.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        }).ToArray(),
                    },
                },
            }).ConfigureAwait(false);
    }

    private async Task<Scenario> ArrangeAsync(int view, IEnumerable<int> instanceMonths)
    {
        var viewPeriod = (Year * 100) + view;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            viewPeriod, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

        var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId)
            .ToDictionaryAsync(c => c.Id).ConfigureAwait(false);
        var codes = new Dictionary<string, string> { ["PLAIN"] = columns[doc.ColumnDefIds[1]].Code };
        for (var m = 1; m <= 12; m++)
        {
            codes[$"M{m}"] = columns[doc.MonthColumnDefIds[m - 1]].Code;
        }

        for (var m = 1; m <= 12; m++)
        {
            var key = (Year * 100) + m;
            var period = key == viewPeriod
                ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == viewPeriod).ConfigureAwait(false)
                : db.Periods.Add(new Period(
                    doc.ProjectId, new PeriodKey(key), (byte)m,
                    new DateOnly(Year, m, 1), new DateOnly(Year, m, DateTime.DaysInMonth(Year, m)))).Entity;
            period.AdvanceTo(PeriodState.Open, Now);
        }

        var userName = $"q17l_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"Q17L{Guid.NewGuid():N}"[..14]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Q-17 lock order" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Submit));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);

        foreach (var month in instanceMonths.Where(m => (Year * 100) + m != viewPeriod))
        {
            await builder.AddPeriodInstanceAsync(doc, new PeriodKey((Year * 100) + month)).ConfigureAwait(false);
        }

        return new Scenario(builder, doc, userName, rowKey, codes, viewPeriod);
    }
}
