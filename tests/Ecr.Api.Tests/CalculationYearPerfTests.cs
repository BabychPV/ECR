using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents.CalculationYear;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Api.Tests;

/// <summary>
/// «Calculation results» за рік на великому синтетичному документі (20 таблиць × 1 000 динамічних рядків × 12 місяців:
/// вихід методології на кожен рядок і місяць, формула шаблону в половині таблиць, підпис рядка з колонки назви) — заміри
/// по фазах (читач, будівник, HTTP, середній час кожного виду SQL-запиту) і розмір відповіді.
/// </summary>
/// <remarks>
/// ⚠ Храповик — лише детерміноване: розмір зведення ≤ 500 КБ і сторінки ≤ 200 КБ, лічильники виходів і «проблем». Час —
/// у вивід тесту, без порогів: на спільній машині він плаває в рази (поряд ідуть інші збірки), і поріг перетворив би тест
/// на випадковий. Мутація (локально): повернути одну відповідь з усіма числами замість зведення — 15.8 МБ, червоніє
/// «зведення > 500 КБ».
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationYearPerfTests(ITestOutputHelper output)
{
    private const string Password = "Calc-Year-Perf-R!42";
    private const int Tables = 20, RowsPerTable = 1000;
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Permissions = ["Document.View", "Calculation.View"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Великий_документ_заміри_по_фазах()
    {
        // ⚠ Власна база: ~250 МБ заливки перетнули б стелю файлів спільної бази колекції (Z8-03).
        var database = SqlServerFixture.WithOwnDatabase("_cyperf");
        await database.InitializeAsync().ConfigureAwait(true);
        try
        {
            await MeasureAsync(database).ConfigureAwait(true);
        }
        finally
        {
            await database.DisposeAsync().ConfigureAwait(true);
        }
    }

    private async Task MeasureAsync(SqlServerFixture database)
    {
        var s = await ArrangeAsync(database).ConfigureAwait(true);
        var reader = new TimedReader();
        using var app = new EcrApiFactory(database)
        {
            ExtraServices = services => services.AddScoped<ICalculationYearReader>(sp =>
                reader.Wrap((CalculationResultStore)sp.GetRequiredService<ICalculationResultStore>())),
        };
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var root = $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results/year";

        // Зведення: 20 таблиць, 30 000 виходів (20 000 методології + 10 000 формул), 1 000 «проблем» (червневий пропуск).
        var summary = await MeasureGetAsync(app, client, reader, $"{root}/summary?year=2026", "summary").ConfigureAwait(true);
        var view = JsonDocument.Parse(summary.Body).RootElement;
        var tables = view.GetProperty("tables").EnumerateArray().ToList();
        Assert.Equal(Tables, tables.Count);
        Assert.Equal(Tables * RowsPerTable * 3 / 2, view.GetProperty("totalRows").GetInt32());
        Assert.Equal(Tables * RowsPerTable / 20, view.GetProperty("problemRows").GetInt32());

        // ⛔ Храповик розміру (рішення координатора 11.11): перший запит екрана ≤ 500 КБ на такому документі.
        Assert.True(summary.Bytes <= 500 * 1024, $"зведення {summary.Bytes} Б > 500 КБ");

        var problems = await MeasureGetAsync(app, client, reader, $"{root}/summary?year=2026&problems=true", "summary problems").ConfigureAwait(true);
        Assert.Equal(Tables * RowsPerTable / 20, JsonDocument.Parse(problems.Body).RootElement.GetProperty("matchedRows").GetInt32());

        // Сторінка таблиці (200 рядків) і остання сторінка таблиці з формулами.
        var key = tables[0].GetProperty("key").GetString();
        var page = await MeasureGetAsync(app, client, reader, $"{root}/rows?year=2026&table={key}&limit=200", "rows page 1").ConfigureAwait(true);
        var rows = JsonDocument.Parse(page.Body).RootElement;
        Assert.Equal(200, rows.GetProperty("rows").GetArrayLength());
        Assert.Equal(tables[0].GetProperty("rows").GetInt32(), rows.GetProperty("total").GetInt32());
        Assert.True(page.Bytes <= 200 * 1024, $"сторінка {page.Bytes} Б > 200 КБ");
        var last = tables[^1].GetProperty("key").GetString();
        var seed = await MeasureGetAsync(app, client, reader, $"{root}/rows?year=2026&table={last}&limit=800", "rows 800").ConfigureAwait(true);
        var after = JsonDocument.Parse(seed.Body).RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(after);
        await MeasureGetAsync(app, client, reader, $"{root}/rows?year=2026&table={last}&cursor={Uri.EscapeDataString(after)}&limit=200", "rows page 5 (cursor)").ConfigureAwait(true);
        await MeasureGetAsync(app, client, reader, $"{root}/rows?year=2026&table={key}&q=_77&limit=200", "rows search").ConfigureAwait(true);

        // Для порівняння: огляд усього документа зі значеннями (те, що будує задача експорту).
        var full = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            using var scope = app.Services.CreateScope();
            var access = scope.ServiceProvider.GetRequiredService<IAccessDecisionService>();
            var builder = scope.ServiceProvider.GetRequiredService<CalculationYearViewBuilder>();
            var profile = await access.BuildProfileAsync(s.UserId, CancellationToken.None).ConfigureAwait(true);
            var clock = Stopwatch.StartNew();
            var built = await builder.BuildAsync(profile, s.DocumentId, 2026, CancellationToken.None).ConfigureAwait(true);
            full.Add(clock.Elapsed.TotalMilliseconds);
            Assert.Equal(Tables * RowsPerTable * 3 / 2, built.Rows.Count);
        }

        output.WriteLine($"full build (export) ms {string.Join(", ", full.Select(Ms))}");
    }

    /// <summary>Шість викликів: перший холодний, медіана 2–6 — теплий час; час читача фактів — окремо.</summary>
    private async Task<(byte[] Body, long Bytes)> MeasureGetAsync(
        EcrApiFactory app, HttpClient client, TimedReader reader, string url, string name)
    {
        var http = new List<double>();
        var readerTimes = new List<double>();
        byte[] body = [];
        var logFrom = app.ServerLog.Count;
        for (var i = 0; i < 6; i++)
        {
            var clock = Stopwatch.StartNew();
            using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
            body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clock.Stop();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{name}: {response.StatusCode}\n{app.ErrorsText}");
            http.Add(clock.Elapsed.TotalMilliseconds);
            readerTimes.Add(reader.LastMs);
        }

        output.WriteLine(
            $"{name}: {body.Length / 1024.0:0.0} KB; cold {Ms(http[0])} ms; warm median {Ms(Median(http.Skip(1)))} ms "
            + $"(reader {Ms(Median(readerTimes.Skip(1)))} ms); all {string.Join(", ", http.Select(Ms))}");

        // Фази SQL: середній час кожного виду запиту за шість викликів (≥ 5 мс) — куди йде час читача.
        foreach (var group in Commands(app.ServerLog.Skip(logFrom)).GroupBy(c => c.Sql).Select(g => (g.Key, Avg: g.Average(c => c.Ms), N: g.Count()))
                     .Where(g => g.Avg >= 5).OrderByDescending(g => g.Avg))
        {
            output.WriteLine($"    sql avg {group.Avg:0} ms ×{group.N}: {group.Key}");
        }

        return (body, body.Length);
    }

    /// <summary>Виконані команди EF з журналу сервера: (тривалість, перші 90 символів SQL).</summary>
    private static IEnumerable<(int Ms, string Sql)> Commands(IEnumerable<string> log)
    {
        foreach (var line in log.Where(l => l.Contains("Executed DbCommand (", StringComparison.Ordinal)))
        {
            var start = line.IndexOf("Executed DbCommand (", StringComparison.Ordinal) + 20;
            var ms = int.Parse(line[start..line.IndexOf("ms", start, StringComparison.Ordinal)], NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            var sql = line.IndexOf("CommandTimeout=", StringComparison.Ordinal) is var t and >= 0 ? line[(line.IndexOf(']', t) + 1)..].Trim() : line;
            var compact = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            yield return (ms, compact[..Math.Min(90, compact.Length)]);
        }
    }

    private static string Ms(double v) => v.ToString("0", CultureInfo.InvariantCulture);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    /// <summary>Обгортка читача фактів: час останнього виклику.</summary>
    private sealed class TimedReader
    {
        public double LastMs { get; private set; }

        public ICalculationYearReader Wrap(ICalculationYearReader inner) => new Impl(this, inner);

        private sealed class Impl(TimedReader owner, ICalculationYearReader inner) : ICalculationYearReader
        {
            public async Task<CalculationYearFacts> ReadYearAsync(CalculationYearReadRequest request, CancellationToken ct)
            {
                var clock = Stopwatch.StartNew();
                var facts = await inner.ReadYearAsync(request, ct).ConfigureAwait(false);
                owner.LastMs = clock.Elapsed.TotalMilliseconds;
                return facts;
            }

            public Task<IReadOnlyList<YearRow>> ReadRowsAsync(
                IReadOnlyList<int> periodKeys, IReadOnlyCollection<long> tableInstanceIds, CancellationToken ct)
                => inner.ReadRowsAsync(periodKeys, tableInstanceIds, ct);

            public Task<IReadOnlyList<TableInstanceRef>> ReadTableInstancesAsync(
                long documentId, IReadOnlyList<int> periodKeys, CancellationToken ct)
                => inner.ReadTableInstancesAsync(documentId, periodKeys, ct);
        }
    }

    private sealed record Scenario(long DocumentId, int UserId, string UserName);

    /// <summary>
    /// Документ на 2026 рік: 2 аркуші × 10 таблиць (динамічні рядки; колонки «назва», «вхід», «формула»), 12 відкритих
    /// місяців, актуальний проєктний прогін кожного місяця. На кожен рядок і місяць — число методології (тонни) і
    /// вхідна комірка; у парних таблицях — ще й розрахована комірка формули. Кожен 20-й рядок без числа в червні
    /// (пропуск усередині року — «проблема»). Набірними INSERT, а не EF: через EF це хвилини.
    /// </summary>
    private static async Task<Scenario> ArrangeAsync(SqlServerFixture sql)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var b = await builder.BuildAsync(rowCount: 1).ConfigureAwait(false);
        await builder.OpenPeriodAsync(b).ConfigureAwait(false);
        for (var m = 2; m <= 12; m++)
        {
            await builder.AddPeriodAsync(b, PeriodKey.Create(2026, m)).ConfigureAwait(false);
        }

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var userName = $"cyp{Guid.NewGuid():N}"[..20];
        await using var db = Context(sql);
        foreach (var period in await db.Periods.Where(p => p.ProjectId == b.ProjectId && p.PeriodKeyValue > 202601).ToListAsync().ConfigureAwait(false))
        {
            period.AdvanceTo(PeriodState.Open, Now);
        }

        var tableIds = new List<int>();
        for (var sh = 0; sh < 2; sh++)
        {
            var sheet = new SheetDef(b.TemplateVersionId, EcrCode.Create($"CYP{sh}{tag}"), Name($"Perf sheet {sh}"), 10 + sh);
            db.SheetDefs.Add(sheet);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.DocumentSheets.Add(new Ecr.Domain.Entities.Documents.DocumentSheet(b.DocumentId, sheet.Id));
            for (var t = 0; t < Tables / 2; t++)
            {
                var table = new TableDef(
                    sheet.Id, EcrCode.Create($"CYPT{sh}{t}{tag}"), Name($"Perf table {sh}.{t}"), t + 1,
                    TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
                db.TableDefs.Add(table);
                await db.SaveChangesAsync().ConfigureAwait(false);
                db.ColumnDefs.AddRange(
                    new ColumnDef(table.Id, EcrCode.Create($"CYPN{sh}{t}{tag}"), Name("Name"), 1, CellDataType.String),
                    new ColumnDef(table.Id, EcrCode.Create($"CYPI{sh}{t}{tag}"), Name("Fuel"), 2, CellDataType.Decimal),
                    new ColumnDef(table.Id, EcrCode.Create($"CYPF{sh}{t}{tag}"), Name("Formula"), 3, CellDataType.Decimal));
                tableIds.Add(table.Id);
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        var methodology = new Methodology(EcrCode.Create($"CYP{tag}"), Name("Combustion"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        var inputs = await db.ColumnDefs.Where(c => tableIds.Contains(c.TableDefId) && c.Ordinal == 2).ToListAsync().ConfigureAwait(false);
        foreach (var input in inputs)
        {
            db.CalculationBindings.Add(new CalculationBinding(input.TableDefId, input.Id, methodology.Id, "E_CO2", "{}"));
        }

        var runs = new List<CalculationRun>();
        for (var m = 1; m <= 12; m++)
        {
            var run = new CalculationRun(b.ProjectId, 202600 + m, triggeredByUserId: null, Now);
            run.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
            run.MakeCurrent();
            runs.Add(run);
        }

        db.CalculationRuns.AddRange(runs);

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Calc year perf"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        foreach (var permission in Permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Write));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tonne = await (from u in db.Units.AsNoTracking()
                           join d in db.Dimensions.AsNoTracking() on u.DimensionId equals d.Id
                           where d.Code == "Mass"
                           orderby u.Id
                           select u.Id).FirstAsync().ConfigureAwait(false);

        var tableList = string.Join(',', tableIds);
        var runList = string.Join(',', runs.Select(r => $"({r.PeriodKey!.Value.ToString(CultureInfo.InvariantCulture)},{r.Id.ToString(CultureInfo.InvariantCulture)})"));
        db.Database.SetCommandTimeout(600);
        // Лише числа й ідентифікатори, створені тут же (не ввід ззовні).
        var seed = $$"""
            SET NOCOUNT ON;
            DECLARE @doc bigint = {{b.DocumentId}}, @now datetime2(3) = '2026-01-15T10:00:00';
            CREATE TABLE #m (pk int PRIMARY KEY);
            INSERT #m VALUES (202601),(202602),(202603),(202604),(202605),(202606),(202607),(202608),(202609),(202610),(202611),(202612);
            CREATE TABLE #n (i int PRIMARY KEY);
            INSERT #n SELECT TOP ({{RowsPerTable}}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            CREATE TABLE #t (id int PRIMARY KEY);
            INSERT #t SELECT CAST(value AS int) FROM STRING_SPLIT('{{tableList}}', ',');
            CREATE TABLE #r (pk int PRIMARY KEY, runId bigint);
            INSERT #r VALUES {{runList}};

            INSERT doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, SheetCopyId, CreatedAt, ModifiedAt)
            SELECT m.pk, NEXT VALUE FOR doc.TableInstanceSeq, @doc, t.id, NULL, @now, @now FROM #t t CROSS JOIN #m m;

            INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, ModifiedAt)
            SELECT ti.PeriodKey, NEXT VALUE FOR doc.TableRowSeq, ti.Id, CONCAT('EQ', ti.TableDefId, '_', n.i), NULL, n.i, @now
            FROM doc.TableInstance ti JOIN #m m ON m.pk = ti.PeriodKey JOIN #t t ON t.id = ti.TableDefId CROSS JOIN #n n
            WHERE ti.DocumentId = @doc;

            INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric, IsCalculated, IsEmpty)
            SELECT r.PeriodKey, r.Id, c.Id, c.TableDefId,
                   CASE WHEN c.Ordinal = 1 THEN CONCAT(N'Heater ', r.RowKey) END,
                   CASE WHEN c.Ordinal = 1 THEN NULL ELSE CAST(r.Ordinal AS decimal(34,16)) / 8 END,
                   CASE WHEN c.Ordinal = 3 THEN 1 ELSE 0 END, 0
            FROM doc.TableInstance ti JOIN #m m ON m.pk = ti.PeriodKey JOIN #t t ON t.id = ti.TableDefId
            JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id
            JOIN cfg.ColumnDef c ON c.TableDefId = ti.TableDefId AND (c.Ordinal < 3 OR ti.TableDefId % 2 = 0)
            WHERE ti.DocumentId = @doc;

            INSERT calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId, TableInstanceId)
            SELECT NEXT VALUE FOR calc.CalculationResultSeq, run.runId, {{version.Id}}, r.PeriodKey, @doc, r.RowKey, N'E_CO2',
                   CAST(r.Ordinal AS decimal(34,16)) / 4 + r.PeriodKey % 100, {{tonne}}, r.TableInstanceId
            FROM doc.TableInstance ti JOIN #m m ON m.pk = ti.PeriodKey JOIN #t t ON t.id = ti.TableDefId
            JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id
            JOIN #r run ON run.pk = r.PeriodKey
            WHERE ti.DocumentId = @doc AND NOT (r.Ordinal % 20 = 0 AND r.PeriodKey = 202606);
            """;
        await db.Database.ExecuteSqlRawAsync(seed).ConfigureAwait(false);

        return new Scenario(b.DocumentId, user.Id, userName);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static EcrDbContext Context(SqlServerFixture sql)
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
