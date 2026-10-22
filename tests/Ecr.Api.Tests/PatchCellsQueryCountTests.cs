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
using Microsoft.Extensions.DependencyInjection;
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
/// <para>
/// ⛔ Те саме — вікно мемоїзації ревізії шаблону (<c>CacheLifetimes.Revision</c>,
/// 5 с, `RD-05`): 300 с замість продуктивних 5. Без цього храповик блимав
/// 22/23 — раунд, на який випадав сплив вікна за НАСТІННИМ годинником,
/// платив зайве <c>SELECT PresentationRevision FROM cfg.TemplateVersion</c>
/// у <c>MetadataCache.RevisionAsync</c>. Перечитування законне (так задумано),
/// але не має відношення до шляху запису, і хто за нього заплатить, вирішував
/// таймінг, а не код.
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
    /// контролер). 26 — п. 3 (стан рядків одним <c>GetRowsAsync</c>). 25 —
    /// п. 4 (нові версії з <c>OUTPUT inserted.RowVersion</c>, без читання після
    /// коміту). 22 — п. 5 (контекст доступу одним запитом замість чотирьох).
    /// 23 — ФВ-5.20a: один індексований пошук Reopen-стану аркуша для
    /// <c>IsLateEdit</c> (<c>D-70</c> б); свідомий +1, не дрейф.
    /// 24 — AN-36 / L6-02: спільний applock структури документа з читанням
    /// версії шаблону одним батчем (перенос версії не губить правку); свідомий
    /// +1, не дрейф.
    /// EA-B «передача місяця»: число НЕ змінилося — ознака «у документа є передачі»
    /// їде EXISTS у тому самому запиті умов доступу (`BuildContextAsync`), а читання
    /// передач і повторна перевірка під блокуванням аркуша відбуваються лише для
    /// документа, у якого передачі є. Документ без передач платить +0.
    /// </remarks>
    private const int MaxCommands = 24;

    private const string Password = "Api-Patch-Ratchet-2026!";

    private const int Rows = 10;

    private const int Columns = 10;

    /// <summary>Скільки теплих записів міряється.</summary>
    private const int MeasuredRounds = 3;

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WR-04")]
    public async Task PATCH_100_комірок_у_теплому_стані_не_перевищує_стелю_звернень()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300, revisionWindow: TimeSpan.FromMinutes(5));
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

        // ⛔ Теплі раунди мусять збігатися: розкид означає, що в шлях знову
        // протекла залежність від часу, і стеля знову ловила б таймінг.
        Assert.True(
            rounds.All(r => r.Total == worst.Total),
            $"Теплі заміри розходяться: {string.Join(", ", rounds.Select(r => r.Total))}.\n"
            + string.Join("\n", rounds.Select(r => r.Detail)));

        Assert.True(
            worst.Total <= MaxCommands,
            $"PATCH {Rows * Columns} комірок: {worst.Total} звернень до БД, стеля {MaxCommands}. "
            + $"Заміри: {string.Join(", ", rounds.Select(r => r.Total))}.\n{worst.Detail}");
    }

    /// <summary>
    /// PA2-S-02: гард «ключ рядка з прив'язки не править людина» (<c>EnsurePermitRowKeysUntouchedAsync</c>) не додає
    /// нічого до гарячого шляху Land: батч без ключів <c>P&lt;цифри&gt;</c> не питає прив'язок узагалі (0 звернень до
    /// <c>PermitSheetBinding</c>), батч із таким ключем платить рівно ОДИН запит (<c>AnyActiveRowsAsync</c>; прив'язок у
    /// таблиці немає, тож далі перелік колонок не читається). Той самий батч з однією коміркою, лише ключ рядка інший.
    /// </summary>
    /// <remarks>
    /// Мутаційний доказ (запущений): прибрати з гарда <c>permitRows.Count == 0 ||</c> (питає завжди) - червоніє про Land
    /// (прив'язки питає і батч без ключів <c>P&lt;цифри&gt;</c>).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WR-04")]
    public async Task PATCH_рядка_Land_не_питає_прив_язок_а_рядка_з_ключем_P_цифри_платить_один_запит()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            // Один рядок отримує ключ виду P<цифри> (як рядок, створений прив'язкою); решта - Land-ключі R<n>_<tag>.
            await db.TableRows.Where(r => r.Id == scenario.RowIds[0])
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RowKeyValue, "P1")).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300, revisionWindow: TimeSpan.FromMinutes(5));
        app.Server.PreserveExecutionContext = true;
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var sliceUri = new Uri($"/api/v1/documents/{scenario.DocumentId}/tables/{scenario.TableInstanceId}", UriKind.Relative);
        var patchUri = new Uri($"/api/v1/documents/{scenario.DocumentId}/cells", UriKind.Relative);
        var (columnCodes, versions) = await OpenSliceAsync(client, sliceUri, app).ConfigureAwait(true);
        var landKey = versions.Keys.Order(StringComparer.Ordinal).First(k => k != "P1");
        Assert.True(versions.ContainsKey("P1"), "Ключ P1 не потрапив у зріз.");

        // Розігрів (кеші метаданих, доступу, штампа): по одному запису кожного виду.
        versions[landKey] = await PatchRowAsync(client, patchUri, scenario, landKey, versions[landKey], columnCodes[0], "warm-l", app).ConfigureAwait(true);
        versions["P1"] = await PatchRowAsync(client, patchUri, scenario, "P1", versions["P1"], columnCodes[0], "warm-p", app).ConfigureAwait(true);

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

        async Task<(int Total, int Bindings)> MeasureAsync(string rowKey, string value)
        {
            counter.Tally.Reset();
            texts.Clear();
            var flag = new StrongBox<bool>(true);
            Measuring.Value = flag;
            try
            {
                versions[rowKey] = await PatchRowAsync(client, patchUri, scenario, rowKey, versions[rowKey], columnCodes[0], value, app).ConfigureAwait(true);
            }
            finally
            {
                flag.Value = false;
                Measuring.Value = null;
            }

            counter.AssertObserved();
            return (counter.Tally.Snapshot().Total, texts.Count(t => t.Contains("PermitSheetBinding", StringComparison.Ordinal)));
        }

        for (var round = 1; round <= MeasuredRounds; round++)
        {
            var land = await MeasureAsync(landKey, $"land-{round}").ConfigureAwait(true);
            var permit = await MeasureAsync("P1", $"permit-{round}").ConfigureAwait(true);

            Assert.True(land.Bindings == 0, $"Батч Land питає прив'язки ({land.Bindings}).");
            Assert.True(permit.Bindings == 1, $"Батч із ключем P1: звернень до прив'язок {permit.Bindings}, очікувалось 1.");
            Assert.True(
                permit.Total == land.Total + 1,
                $"Батч із ключем P1: {permit.Total} звернень, Land: {land.Total}; різниця має бути рівно 1.");
        }
    }

    /// <summary>Стеля звернень на теплий <c>PATCH</c> 10 Lookup-комірок Land (звичайна ціль, без групи).</summary>
    /// <remarks>
    /// Замір 22.10 (Permits PRMG, перевірки належності): ДО вбудування <c>LookupTargetMatcher</c> - 26, ПІСЛЯ - 26: збіг реєстру цілі й запису резолвер груп не кличе. Лише знижується.
    /// </remarks>
    private const int MaxLookupCommands = 26;

    /// <summary>
    /// PRMG: Land-колонка <c>Lookup</c> (ціль - звичайний довідник): теплий <c>PATCH</c> не платить нічого за групи.
    /// Резолвер підставлено з <c>ThrowOnUse</c>: будь-яке звернення (<c>Accepts</c>, <c>IsGroup</c>, оновлення знімка) - провал.
    /// Кількість запитів фіксується і не залежить від того, що група існує чи ні.
    /// </summary>
    /// <remarks>
    /// Мутаційний доказ (запущений): у <c>LookupTargetMatcher.EnsureFreshOnMismatchAsync</c> кликати <c>EnsureFreshAsync</c>
    /// безумовно - червоніє (<c>EnsureFreshCalls &gt; 0</c>); у <c>Belongs</c> прибрати швидкий шлях збігу - падає на <c>ThrowOnUse</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WR-04")]
    public Task PATCH_Lookup_комірок_Land_не_кличе_резолвер_груп_і_тримає_стелю_звернень() => LookupScenarioAsync(asGroup: false);

    /// <summary>
    /// PRMG: колонка <c>Lookup</c> з ціллю-ГРУПОЮ (прихований реєстр без записів, записи - реєстру-члена): та сама стеля, що в Land
    /// (<see cref="MaxLookupCommands"/>) - ПЕРЕВІРКА НЕ ДОДАЄ запитів: членство читається зі знімка в памʼяті, а 10 комірок - це 10
    /// звернень до <c>Accepts</c> (синхронних), без SQL. Єдиний допустимий +запит у проді - оновлення знімка
    /// (<c>EnsureFreshAsync</c>: один агрегатний запит ревізії не частіше за раз на 5 с на вузол, не на PATCH); у тесті резолвер -
    /// фейк, тож цей запит тут не міряється (його частота - у <c>PermitGroupResolverTests</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WR-04")]
    public Task PATCH_Lookup_комірок_цілі_група_приймає_члена_і_не_перевищує_стелю_Land() => LookupScenarioAsync(asGroup: true);

    private async Task LookupScenarioAsync(bool asGroup)
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);
        var entryIds = new List<long>();
        var groups = new FakePermitGroupResolver { ThrowOnUse = !asGroup };
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var registry = new Ecr.Domain.Entities.Configuration.RegistryDef(
                EcrCode.Create($"LANDQ_{Guid.NewGuid():N}"[..20]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Land query count" }),
                isTemporal: false);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync().ConfigureAwait(true);

            for (var i = 1; i <= 2; i++)
            {
                var entry = new Ecr.Domain.Entities.Dictionaries.RegistryEntry(
                    registry.Id, EcrCode.Create($"E{i}"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = $"Entry {i}" }));
                db.RegistryEntries.Add(entry);
                await db.SaveChangesAsync().ConfigureAwait(true);
                entryIds.Add(entry.Id);
            }

            var target = registry.Id;
            if (asGroup)
            {
                // Ціль колонки - прихований реєстр групи БЕЗ записів; склад - реєстр з entryIds.
                var hidden = new Ecr.Domain.Entities.Configuration.RegistryDef(
                    EcrCode.Create($"PRMGQ_{Guid.NewGuid():N}"[..20]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Group query count" }),
                    isTemporal: false);
                db.RegistryDefs.Add(hidden);
                await db.SaveChangesAsync().ConfigureAwait(true);
                target = hidden.Id;
                groups.WithGroup(hidden.Id, [registry.Id]);
            }

            await db.ColumnDefs.Where(c => c.Id == scenario.ColumnDefIds[2])
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.DataType, CellDataType.Lookup)
                    .SetProperty(c => c.LookupRegistryDefId, target)).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300, revisionWindow: TimeSpan.FromMinutes(5))
        {
            ExtraServices = services => services.AddSingleton<Ecr.Application.Permits.Groups.ILookupTargetResolver>(groups),
        };
        app.Server.PreserveExecutionContext = true;
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var sliceUri = new Uri($"/api/v1/documents/{scenario.DocumentId}/tables/{scenario.TableInstanceId}", UriKind.Relative);
        var patchUri = new Uri($"/api/v1/documents/{scenario.DocumentId}/cells", UriKind.Relative);
        var (columnCodes, versions) = await OpenSliceAsync(client, sliceUri, app).ConfigureAwait(true);
        var lookupCode = columnCodes[2];

        async Task<Dictionary<string, string>> PatchLookupAsync(IReadOnlyDictionary<string, string> current, long entryId)
        {
            var response = await client.PatchAsJsonAsync(patchUri, new
            {
                tableInstanceId = scenario.TableInstanceId,
                periodKey = scenario.PeriodKey,
                origin = "UserEdit",
                rows = current.Keys.Order(StringComparer.Ordinal).Select(rowKey => new
                {
                    rowKey,
                    baseVersion = current[rowKey],
                    cells = new[] { new { columnCode = lookupCode, value = (object)entryId } },
                }).ToArray(),
            }).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"PATCH Lookup: {response.StatusCode}\n{body}\n{app.ErrorsText}");

            return JsonDocument.Parse(body).RootElement.GetProperty("rowVersions").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        }

        versions = await PatchLookupAsync(versions, entryIds[0]).ConfigureAwait(true);

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
                // Значення чергується між двома записами: кожен раунд ЗМІНЮЄ комірки (перевірка належності виконується).
                versions = await PatchLookupAsync(versions, entryIds[round % 2]).ConfigureAwait(true);
            }
            finally
            {
                flag.Value = false;
                Measuring.Value = null;
            }

            counter.AssertObserved();
            rounds.Add((counter.Tally.Snapshot().Total, Describe(counter.Tally.Snapshot(), texts)));
        }

        var worst = rounds.MaxBy(r => r.Total);
        Assert.True(
            rounds.All(r => r.Total == worst.Total),
            $"Теплі заміри розходяться: {string.Join(", ", rounds.Select(r => r.Total))}.\n{worst.Detail}");
        Assert.True(
            worst.Total <= MaxLookupCommands,
            $"PATCH Lookup ({Rows} комірок): {worst.Total} звернень, стеля {MaxLookupCommands}.\n{worst.Detail}");

        if (asGroup)
        {
            // Група справді питалась (інакше «мало запитів» доводило б, що перевірка не виконувалась): по одному Accepts на комірку.
            Assert.True(groups.Calls >= Rows * MeasuredRounds, $"Accepts викликано {groups.Calls} разів.");
            Assert.True(groups.EnsureFreshCalls >= 1);
            return;
        }

        // ⛔ Збіг реєстру цілі й запису: груп не питали взагалі.
        Assert.Equal(0, groups.Calls);
        Assert.Equal(0, groups.EnsureFreshCalls);
    }

    /// <summary>Один <c>PATCH</c> однієї комірки одного рядка; повертає нову версію рядка.</summary>
    private static async Task<string> PatchRowAsync(
        HttpClient client, Uri patchUri, Scenario scenario, string rowKey, string baseVersion, string columnCode, string value, EcrApiFactory app)
    {
        var response = await client.PatchAsJsonAsync(patchUri, new
        {
            tableInstanceId = scenario.TableInstanceId,
            periodKey = scenario.PeriodKey,
            origin = "UserEdit",
            rows = new[] { new { rowKey, baseVersion, cells = new[] { new { columnCode, value = (object)value } } } },
        }).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"PATCH {rowKey}: {response.StatusCode}\n{body}\n{app.ErrorsText}");

        return JsonDocument.Parse(body).RootElement.GetProperty("rowVersions").GetProperty(rowKey).GetString()!;
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

        return new Scenario(document.DocumentId, document.TableInstanceId, userName, periodKey, document.RowIds, document.ColumnDefIds);
    }

    private sealed record Scenario(
        long DocumentId, long TableInstanceId, string UserName, int PeriodKey, IReadOnlyList<long> RowIds, IReadOnlyList<int> ColumnDefIds);
}
