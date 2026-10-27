// tests/Ecr.Api.Tests/MonthScheduledPeriodRowsApiTests.cs
using System.Collections.Concurrent;
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
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Api.Tests;

/// <summary>
/// HU-16 (Q-17): екземпляр таблиці «місяці в колонках» у НЕ-якірному періоді (напр. <c>Scheduled</c> 202612) без
/// дзеркал рядків якоря (створений до прапора або до появи якоря) показує у перегляді ті самі рядки, що й
/// <c>Open</c>: рядки якоря — авторитетні за складом. Значення місячних колонок — за правилами власника (маска ролі
/// як в <c>Open</c>). Прапор <c>Collection:MonthColumnRouting</c> вимкнено — як і раніше (рядки не з'являються).
/// </summary>
[Collection("SqlServer")]
public sealed class MonthScheduledPeriodRowsApiTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const string Password = "Month-Scheduled-Rows-HU16!";
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";
    private const int Anchor = 202601;
    private const int Dec = 202612;
    private const int AnchorRowCount = 14;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Scheduled_період_без_дзеркал_показує_рядки_якоря_і_значення_власника()
    {
        var s = await ArrangeAsync(scope: null).ConfigureAwait(true);
        await PutAsync(s.Builder, s.Doc, Anchor, s.Doc.RowIds[0], s.ColumnIds["M1"], 5m).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var slice = await ReadSliceAsync(client, s, app).ConfigureAwait(true);

        Assert.Equal(AnchorRowCount, slice.GetProperty("rows").GetArrayLength());
        var first = slice.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("rowKey").GetString() == s.RowKey);
        Assert.True(first.GetProperty("cells").TryGetProperty(s.ColumnCodes["M1"], out var m1), "M1 — значення власника (якоря)");
        Assert.Equal("5", m1.ToString().TrimEnd('0').TrimEnd('.'));

        // Другий перегляд нічого не додає (ідемпотентність): склад рівно якір.
        var again = await ReadSliceAsync(client, s, app).ConfigureAwait(true);
        Assert.Equal(AnchorRowCount, again.GetProperty("rows").GetArrayLength());
        Assert.Equal(AnchorRowCount, await LiveRowCountAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Scheduled_період_обмежена_роль_бачить_рядки_але_M1_чужого_місяця_замасковано()
    {
        var s = await ArrangeAsync(scope: (Dec, Dec)).ConfigureAwait(true);
        await PutAsync(s.Builder, s.Doc, Anchor, s.Doc.RowIds[0], s.ColumnIds["M1"], 5m).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var slice = await ReadSliceAsync(client, s, app).ConfigureAwait(true);

        Assert.Equal(AnchorRowCount, slice.GetProperty("rows").GetArrayLength());
        var first = slice.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("rowKey").GetString() == s.RowKey);
        Assert.False(first.GetProperty("cells").TryGetProperty(s.ColumnCodes["M1"], out var _m1), "M1 поза областю ролі мусить бути замаскований");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Прапор_вимкнено_Scheduled_період_лишається_без_рядків_і_нічого_не_створюється()
    {
        var s = await ArrangeAsync(scope: null).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: false).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var slice = await ReadSliceAsync(client, s, app).ConfigureAwait(true);

        Assert.Equal(0, slice.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, await LiveRowCountAsync(s).ConfigureAwait(true));
    }

    // ───────────────────── інваріанти добору (огляд Аудита-2, P2-1..P2-3) ─────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Closed_період_не_отримує_рядків_при_перегляді()
    {
        var s = await ArrangeAsync(scope: null).ConfigureAwait(true);
        await using (var db = s.Builder.CreateContext())
        {
            await db.Periods.Where(p => p.ProjectId == s.Doc.ProjectId && p.PeriodKeyValue == Dec)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.State, PeriodState.Closed)).ConfigureAwait(true);
        }

        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var slice = await ReadSliceAsync(client, s, app).ConfigureAwait(true);

        Assert.Equal(0, slice.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, await LiveRowCountAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task М_яко_видалений_ключ_не_воскресає_і_не_дублюється()
    {
        var s = await ArrangeAsync(scope: null).ConfigureAwait(true);
        await using (var db = s.Builder.CreateContext())
        {
            var id = await new BulkCellLoader(sql.ConnectionString, 1000).ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(true);
            var dead = new TableRow(new PeriodKey(Dec), id, s.December.TableInstanceId, RowKey.Create(s.RowKey), 1, Now);
            dead.SoftDelete(Now);
            db.TableRows.Add(dead);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var slice = await ReadSliceAsync(client, s, app).ConfigureAwait(true);

        Assert.Equal(AnchorRowCount - 1, slice.GetProperty("rows").GetArrayLength());
        Assert.DoesNotContain(
            slice.GetProperty("rows").EnumerateArray(), r => r.GetProperty("rowKey").GetString() == s.RowKey);
        await using var check = s.Builder.CreateContext();
        Assert.Equal(1, await check.TableRows.AsNoTracking().CountAsync(
            r => r.PeriodKeyValue == Dec && r.TableInstanceId == s.December.TableInstanceId && r.RowKeyValue == s.RowKey).ConfigureAwait(true));
        Assert.Equal(AnchorRowCount - 1, await LiveRowCountAsync(s).ConfigureAwait(true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Подано_або_затверджено_аркуш_склад_рядків_не_змінюється(bool approved)
    {
        var s = await ArrangeAsync(scope: null).ConfigureAwait(true);
        await using (var db = s.Builder.CreateContext())
        {
            var userId = await db.Users.AsNoTracking().Where(u => u.UserName == s.UserName).Select(u => u.Id).SingleAsync().ConfigureAwait(true);
            var state = new Ecr.Domain.Entities.Workflow.ApprovalState(s.Doc.DocumentId, s.Doc.SheetDefId, Dec);
            state.Submit(userId, Now);
            if (approved)
            {
                state.Approve(userId, Now);
            }

            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var slice = await ReadSliceAsync(client, s, app).ConfigureAwait(true);

        Assert.Equal(0, slice.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, await LiveRowCountAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Гонитва_паралельних_GET_дає_рівно_склад_якоря_без_дублів_і_без_помилок()
    {
        var s = await ArrangeAsync(scope: null).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var uri = new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.December.TableInstanceId}?periodKey={Dec}", UriKind.Relative);
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.GetAsync(uri))).ConfigureAwait(true);
        foreach (var response in responses)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}\n{text}\n{app.ErrorsText}");
            response.Dispose();
        }

        Assert.Equal(AnchorRowCount, await LiveRowCountAsync(s).ConfigureAwait(true));
        await using var check = s.Builder.CreateContext();
        var keys = await check.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == Dec && r.TableInstanceId == s.December.TableInstanceId)
            .Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(true);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        var again = await ReadSliceAsync(client, s, app).ConfigureAwait(true);
        Assert.Equal(AnchorRowCount, again.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Конфлікт_4091_при_доборі_віддає_слайс_200_без_добору()
    {
        var s = await ArrangeAsync(scope: null).ConfigureAwait(true);
        Environment.SetEnvironmentVariable(FlagVariable, "true");
        EcrApiFactory factory;
        HttpClient client;
        IDisposable app;
        try
        {
            factory = new EcrApiFactory(sql);
            var derived = factory.WithWebHostBuilder(b => b.ConfigureTestServices(
                services => services.AddSingleton(new SheetEditGatePolicy(TimeSpan.FromMilliseconds(500)))));
            app = derived;
            client = derived.CreateClient();
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagVariable, null);
        }

        using var _f = factory;
        using var _a = app;
        using var _c = client;
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = s.UserName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {factory.ErrorsText}");

        // Хтось подає цей аркуш: виняткове блокування тримається, добір (спільне блокування аркуша) упирається у 4091.
        await using var holder = s.Builder.CreateContext();
        await using var tx = await holder.Database.BeginTransactionAsync().ConfigureAwait(true);
        await new SheetEditGate(holder)
            .EnterSubmitAsync(s.Doc.DocumentId, s.Doc.SheetDefId, new PeriodKey(Dec), CancellationToken.None)
            .ConfigureAwait(true);

        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.December.TableInstanceId}?periodKey={Dec}",
            UriKind.Relative)).ConfigureAwait(true);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        await tx.RollbackAsync().ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"очікували 200 без добору, отримали {(int)response.StatusCode}\n{text}\n{factory.ErrorsText}");
        Assert.Equal(0, JsonDocument.Parse(text).RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, await LiveRowCountAsync(s).ConfigureAwait(true));
    }

    // ───────────────────── вартість читання слайса (SQL-команд на один GET) ─────────────────────

    /// <summary>
    /// Прапор ON, таблиця «місяці в колонках»: теплий GET слайса в Scheduled (рядки вже добрані) коштує не більше,
    /// ніж у Open + 3. Матеріалізуючий (перший) GET дорожчий — його число друкується. Мутація швидкого виходу
    /// <c>MirrorAnchorRowsIntoInstanceAsync</c> (<c>TrueForAll</c>) піднімає теплий GET за межу.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task GET_слайса_Scheduled_теплий_коштує_не_більше_за_Open_плюс_3()
    {
        var s = await ArrangeAsync(scope: null, addOpenInstance: true).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true, measured: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;
        using var counter = NewCounter(out var texts);

        await GetCountedAsync(client, s, s.OpenInstance!.TableInstanceId, Nov, counter, texts).ConfigureAwait(true); // розігрів
        var open = await GetCountedAsync(client, s, s.OpenInstance.TableInstanceId, Nov, counter, texts).ConfigureAwait(true);
        var first = await GetCountedAsync(client, s, s.December.TableInstanceId, Dec, counter, texts).ConfigureAwait(true);
        var warm = await GetCountedAsync(client, s, s.December.TableInstanceId, Dec, counter, texts).ConfigureAwait(true);
        var warm2 = await GetCountedAsync(client, s, s.December.TableInstanceId, Dec, counter, texts).ConfigureAwait(true);

        output.WriteLine($"HU16-SQL ON/місячна: Open={open.Total} Scheduled-перший(матеріалізація)={first.Total} " +
                         $"Scheduled-теплий={warm.Total}/{warm2.Total}");
        output.WriteLine("Перший Scheduled:\n" + first.Format());
        output.WriteLine("Теплий Scheduled:\n" + warm.Format());

        Assert.True(first.Total > warm.Total, $"перший GET мусить матеріалізувати (більше команд, ніж теплий)\n{first.Format()}");
        Assert.Equal(AnchorRowCount, await LiveRowCountAsync(s).ConfigureAwait(true));
        Assert.True(warm.Total <= open.Total + 3, $"теплий Scheduled {warm.Total} > Open {open.Total} + 3\n{warm.Format()}");
        Assert.True(warm2.Total <= open.Total + 3, $"теплий Scheduled #2 {warm2.Total} > Open {open.Total} + 3\n{warm2.Format()}");

        // Храповик (відносна межа сама не ловить мутацію: Open платить за той самий швидкий вихід). Лише знижується.
        Assert.True(open.Total <= MaxOnWarmCommands, $"Open {open.Total} > {MaxOnWarmCommands}\n{open.Format()}");
        Assert.True(warm.Total <= MaxOnWarmCommands, $"теплий Scheduled {warm.Total} > {MaxOnWarmCommands}\n{warm.Format()}");
    }

    /// <summary>Стеля команд на теплий GET слайса при ON (виміряно 39; без швидкого виходу добору — 45).</summary>
    private const int MaxOnWarmCommands = 39;

    /// <summary>Немісячна (PerPeriodInstance) таблиця при ON: маршрутизація не торкається — Scheduled == Open.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task GET_слайса_немісячної_таблиці_при_ON_не_платить_за_дзеркала()
    {
        var s = await ArrangeAsync(scope: null, addOpenInstance: true, monthly: false).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true, measured: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;
        using var counter = NewCounter(out var texts);

        await GetCountedAsync(client, s, s.OpenInstance!.TableInstanceId, Nov, counter, texts).ConfigureAwait(true);
        var open = await GetCountedAsync(client, s, s.OpenInstance.TableInstanceId, Nov, counter, texts).ConfigureAwait(true);
        await GetCountedAsync(client, s, s.December.TableInstanceId, Dec, counter, texts).ConfigureAwait(true);
        var sched = await GetCountedAsync(client, s, s.December.TableInstanceId, Dec, counter, texts).ConfigureAwait(true);

        output.WriteLine($"HU16-SQL ON/немісячна: Open={open.Total} Scheduled={sched.Total}");
        Assert.True(sched.Total <= open.Total + 1, $"немісячна: Scheduled {sched.Total} vs Open {open.Total}\n{sched.Format()}");
    }

    /// <summary>
    /// Прапор OFF: число команд на GET слайса не змінилося відносно коду до HU-16 (<c>c1ea1cbf8</c>):
    /// Open == <see cref="OffOpenCommands"/>, Scheduled (без рядків) == <see cref="OffScheduledCommands"/>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task GET_слайса_при_OFF_має_те_саме_число_команд_що_до_HU16()
    {
        var s = await ArrangeAsync(scope: null, addOpenInstance: true).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: false, measured: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;
        using var counter = NewCounter(out var texts);

        await GetCountedAsync(client, s, s.OpenInstance!.TableInstanceId, Nov, counter, texts).ConfigureAwait(true);
        var open = await GetCountedAsync(client, s, s.OpenInstance.TableInstanceId, Nov, counter, texts).ConfigureAwait(true);
        await GetCountedAsync(client, s, s.December.TableInstanceId, Dec, counter, texts).ConfigureAwait(true);
        var sched = await GetCountedAsync(client, s, s.December.TableInstanceId, Dec, counter, texts).ConfigureAwait(true);

        output.WriteLine($"HU16-SQL OFF: Open={open.Total} Scheduled={sched.Total}");
        output.WriteLine(open.Format());
        output.WriteLine(sched.Format());
        Assert.Equal(OffOpenCommands, open.Total);
        Assert.Equal(OffScheduledCommands, sched.Total);
    }

    /// <summary>Число команд OFF, знято на c1ea1cbf8 (до HU-16) тим самим сценарієм.</summary>
    private const int OffOpenCommands = 32;
    private const int OffScheduledCommands = 35;
    private const int Nov = 202611;

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    private static SqlClientCommandCounter NewCounter(out ConcurrentQueue<string> texts)
    {
        var queue = new ConcurrentQueue<string>();
        texts = queue;
        return new SqlClientCommandCounter(new CommandTally(), command =>
        {
            if (Measuring.Value is not { Value: true })
            {
                return false;
            }

            queue.Enqueue(command.CommandText ?? string.Empty);
            return true;
        });
    }

    private static async Task<CommandTallySnapshot> GetCountedAsync(
        HttpClient client, Scenario s, long tableInstanceId, int period,
        SqlClientCommandCounter counter, ConcurrentQueue<string> texts)
    {
        counter.Tally.Reset();
        texts.Clear();
        var flag = new StrongBox<bool>(true);
        Measuring.Value = flag;
        try
        {
            using var response = await client.GetAsync(new Uri(
                $"/api/v1/documents/{s.Doc.DocumentId}/tables/{tableInstanceId}?periodKey={period}",
                UriKind.Relative)).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}\n{text}");
        }
        finally
        {
            flag.Value = false;
            Measuring.Value = null;
        }

        var seen = counter.Tally.Snapshot();
        counter.AssertObserved();
        Assert.True(seen["SELECT doc.TableRow"] >= 1, "лічильник не бачить читання рядків — фільтр/підписка зламані\n" + seen.Format());
        return seen;
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Doc, string UserName, string RowKey, TestPeriodInstance December,
        Dictionary<string, int> ColumnIds, Dictionary<string, string> ColumnCodes, TestPeriodInstance? OpenInstance = null);

    private async Task<(EcrApiFactory App, HttpClient Client)> BootAsync(Scenario s, bool flag, bool measured = false)
    {
        Environment.SetEnvironmentVariable(FlagVariable, flag ? "true" : "false");
        try
        {
            var app = measured
                ? new EcrApiFactory(sql, stampCacheSeconds: 300, revisionWindow: TimeSpan.FromMinutes(5))
                : new EcrApiFactory(sql);
            if (measured)
            {
                app.Server.PreserveExecutionContext = true;
            }

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

    private static async Task<JsonElement> ReadSliceAsync(HttpClient client, Scenario s, EcrApiFactory app)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.December.TableInstanceId}?periodKey={Dec}",
            UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}\n{text}\n{app.ErrorsText}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<int> LiveRowCountAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();
        return await db.TableRows.AsNoTracking()
            .CountAsync(r => r.PeriodKeyValue == Dec && r.TableInstanceId == s.December.TableInstanceId && !r.IsDeleted)
            .ConfigureAwait(false);
    }

    private static async Task PutAsync(
        TestDocumentBuilder builder, TestDocument doc, int period, long rowId, int columnId, decimal value)
    {
        await using var db = builder.CreateContext();
        db.CellValues.Add(new CellValue(
            new CellAddress(new PeriodKey(period), rowId, columnId), doc.TableDefId,
            new CellValueData { ValueNumeric = value }));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Документ-якір 202601 з <see cref="AnchorRowCount"/> рядками; календар 1..12 (1..11 Open, 12 Scheduled);
    /// екземпляр 202612 заведено БЕЗ рядків (як екземпляр, створений до прапора/якоря).
    /// </summary>
    private async Task<Scenario> ArrangeAsync((int From, int To)? scope, bool addOpenInstance = false, bool monthly = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = monthly
            ? await builder.BuildAsync(
                Anchor, columnCount: 2, rowCount: AnchorRowCount, rowMode: TableRowMode.Mixed,
                layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false)
            : await builder.BuildAsync(
                Anchor, columnCount: 2, rowCount: AnchorRowCount, rowMode: TableRowMode.Mixed,
                layoutKind: TableLayoutKind.PerPeriodInstance, monthColumns: 0).ConfigureAwait(false);

        await using (var db = builder.CreateContext())
        {
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            for (var m = 1; m <= 12; m++)
            {
                var key = (2026 * 100) + m;
                var period = key == Anchor
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Anchor).ConfigureAwait(false)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m)))).Entity;
                if (m < 12)
                {
                    period.AdvanceTo(PeriodState.Open, Now);
                }
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var december = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Dec)).ConfigureAwait(false);
        if (monthly)
        {
            await using var db = builder.CreateContext();
            await db.TableRows.Where(r => r.PeriodKeyValue == Dec && r.TableInstanceId == december.TableInstanceId)
                .ExecuteDeleteAsync().ConfigureAwait(false);
        }

        // Open-екземпляр (листопад) з повним набором рядків — база порівняння вартості.
        var openInstance = addOpenInstance
            ? await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Nov)).ConfigureAwait(false)
            : null;

        var codes = new Dictionary<string, string>();
        var ids = new Dictionary<string, int>();
        string rowKey;
        await using (var db = builder.CreateContext())
        {
            var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId)
                .ToDictionaryAsync(c => c.Id).ConfigureAwait(false);
            for (var m = 1; monthly && m <= 12; m++)
            {
                ids[$"M{m}"] = doc.MonthColumnDefIds[m - 1];
                codes[$"M{m}"] = columns[doc.MonthColumnDefIds[m - 1]].Code;
            }

            rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
                .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        }

        var userName = $"hu16_{Guid.NewGuid():N}"[..20];
        await using (var db = builder.CreateContext())
        {
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"HU16{Guid.NewGuid():N}"[..14]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "HU-16 scheduled rows" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            var assignment = new RoleAssignment(role.Id, user.Id, null);
            if (scope is { } range)
            {
                assignment.SetScope(RoleAssignmentScope.Create(
                    [doc.ProjectId], periodFrom: new PeriodKey(range.From), periodTo: new PeriodKey(range.To)));
            }

            db.RoleAssignments.Add(assignment);
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Submit));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new Scenario(builder, doc, userName, rowKey, december, ids, codes, openInstance);
    }
}
