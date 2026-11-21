using System.Data.Common;
using System.Globalization;
using System.Xml.Linq;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Застарілість для набору документів (<see cref="StaleResultsByIdsQuery"/>): картка, сторінка при переповненні карти,
/// лікування карти. Ті самі рядки, що плоска форма періоду, звужена по id, а вартість не росте з історією результатів
/// документа й з результатами інших документів періоду.
/// </summary>
/// <remarks>
/// ⛔ Стенд Land 21.11: картка великого документа 6-49 с і 503 (таймаут у <c>-- StaleMap:direct</c>): звужена плоска форма
/// читала всю історію прогонів документа й довідникову частину ВСЬОГО періоду (52,8 тис. читань, 4,2 с ЦП на теплому кеші).
/// Мутаційні докази (локально, не комітити):
/// <list type="bullet">
///   <item>у <c>DocumentStore.StaleResultsDirectAsync</c> повернути <c>StaleResultsQuery.Documents(db, p).Where(r =&gt;
///   ids.Contains(r.DocumentId))</c> - червоний <see cref="Картка_не_читає_історію_результатів_документа_і_довідники_всього_періоду"/>;</item>
///   <item>у <c>StaleResultsByIdsQuery</c> прибрати <c>AND cr.CalculationRunId = r0.Id</c> з <c>EXISTS</c> - червоний
///   <see cref="Форма_для_набору_дає_ті_самі_рядки_що_плоска_форма_періоду_звужена_по_id"/>.</item>
/// </list>
/// </remarks>
public sealed partial class DocumentStaleResultsStoreTests
{
    /// <summary>
    /// Рівність рядків (документ, «відколи») і карт авторів з плоскою формою періоду на сценаріях, що розрізняють
    /// предикат: дві актуальні області, новіший НЕактуальний прогін, довідник після прогону (читає / не читає),
    /// <c>InputsAsOfUtc</c> раніше за старт, правка до прогону, <c>Recalculation</c>, інший період, документ без чисел.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Форма_для_набору_дає_ті_самі_рядки_що_плоска_форма_періоду_звужена_по_id()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        var column = chain.ColumnDefIds[1];
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var v1 = await VersionAsync(db);   // читає R1 (змінено після прогонів)
        var v2 = await VersionAsync(db);   // читає R2 (змінено до старту, але після InputsAsOfUtc)
        var v4 = await VersionAsync(db);   // довідників не читає
        await RegistryAsync(db, $"BI1{tag}", Now.AddHours(1), v1);
        await RegistryAsync(db, $"BI2{tag}", Now.AddMinutes(-30), v2);

        var docs = new Dictionary<string, long>();
        foreach (var name in new[] { "A", "B", "C", "E", "F", "G", "G2", "H", "I", "N" })
        {
            docs[name] = await AddDocumentAsync(db, chain, $"BI{tag}-{name}");
        }

        // Прогін усього проєкту (Now, актуальний) з числами для більшості документів.
        var project = await RunAsync(db, chain, v4, Now, []);
        foreach (var name in new[] { "A", "B", "C", "E", "F", "G", "H", "I" })
        {
            await ResultsAsync(project.Id, v4, period, docs[name], 1);
        }

        await ResultsAsync(project.Id, v1, period, docs["G"], 3);

        // E: пізніший актуальний прогін лише документа - правка між стартами НЕ застарює.
        var eRun = await RunAsync(db, chain, v4, Now.AddHours(1), [], runDocumentId: docs["E"]);
        await ResultsAsync(eRun.Id, v4, period, docs["E"], 2);

        // F: новіший НЕактуальний прогін з числами документа - береться актуальний (старіший).
        var pending = await PendingRunAsync(db, chain, v4, Now.AddHours(2), []);
        await ResultsAsync(pending.Id, v4, period, docs["F"], 2);

        // G2: власний актуальний прогін, входи «на момент» раніше за старт; довідник R2 змінено між ними.
        var g2Run = await RunAsync(db, chain, v2, Now, [], runDocumentId: docs["G2"]);
        g2Run.LimitInputsAsOf(Now.AddHours(-1));
        await db.SaveChangesAsync(CancellationToken.None);
        await ResultsAsync(g2Run.Id, v2, period, docs["G2"], 2);

        await EditAsync(docs["A"], period, column, Now.AddHours(3), user: 72);
        await EditAsync(docs["A"], period, column, Now.AddHours(1), user: 71);
        await EditAsync(docs["B"], period, column, Now.AddHours(-1));
        await EditAsync(docs["C"], period, column, Now.AddHours(1), origin: "Recalculation");
        await EditAsync(docs["E"], period, column, Now.AddMinutes(30));
        await EditAsync(docs["F"], period, column, Now.AddHours(1), user: 72);
        await EditAsync(docs["G"], period, column, Now.AddHours(2));
        await EditAsync(docs["G2"], period, column, Now.AddMinutes(-10));
        await EditAsync(docs["I"], period + 1, column, Now.AddHours(1));
        await EditAsync(docs["N"], period, column, Now.AddHours(1));

        var all = docs.Values.Order().ToArray();

        async Task<List<(long, DateTime?)>> FlatAsync(long[] ids)
            => [.. (await StaleResultsQuery.Documents(db, period).Where(r => ids.Contains(r.DocumentId)).ToListAsync())
                .Select(r => (r.DocumentId, r.Since)).Order()];

        async Task<List<(long, DateTime?)>> ByIdsAsync(long[] ids)
            => [.. (await StaleResultsByIdsQuery.Documents(db, period, ids).ToListAsync())
                .Select(r => (r.DocumentId, r.Since)).Order()];

        // Сценарій не порожній: очікувані вердикти, порахані в тесті.
        var expected = new Dictionary<long, DateTime>
        {
            [docs["A"]] = Now.AddHours(1),
            [docs["F"]] = Now.AddHours(1),
            [docs["G"]] = Now.AddHours(1),
            [docs["G2"]] = Now.AddMinutes(-30),
        };
        var byIds = await ByIdsAsync(all);
        Assert.Equal(expected.Keys.Order(), byIds.Select(r => r.Item1));
        Assert.All(byIds, r => Assert.True(
            Math.Abs((r.Item2!.Value - expected[r.Item1]).TotalSeconds) < 1, $"{r.Item1}: {r.Item2:O} != {expected[r.Item1]:O}"));

        Assert.Equal(await FlatAsync(all), byIds);
        foreach (var id in all)
        {
            Assert.Equal(await FlatAsync([id]), await ByIdsAsync([id]));
        }

        // Карти авторів (лікування накладки): ті самі пари (автор, документ, відколи).
        var authorsFlat = (await StaleResultsQuery.AuthorDocuments(db, period).Where(r => all.Contains(r.DocumentId)).ToListAsync())
            .Select(r => (r.UserId, r.DocumentId, r.Since)).Order().ToList();
        var authorsByIds = (await StaleResultsByIdsQuery.AuthorDocuments(db, period, all).ToListAsync())
            .Select(r => (r.UserId, r.DocumentId, r.Since)).Order().ToList();
        Assert.Contains(authorsByIds, r => r.UserId == 71 && r.DocumentId == docs["A"]);
        Assert.Contains(authorsByIds, r => r.UserId == 72 && r.DocumentId == docs["F"]);
        Assert.Equal(authorsFlat, authorsByIds);

        // Картка (продуктивний шлях) - той самий вердикт.
        var card = await new DocumentStore(db).FindAsync(docs["G2"], new PeriodKeyFilter(period), CancellationToken.None);
        Assert.True(card!.ResultsStale);
    }

    /// <summary>
    /// Сторож вартості (ratchet за історією): скільки рядків <c>calc.CalculationResult</c> ЧИТАЄ запит картки
    /// (<c>-- StaleMap:direct</c>, БОЙОВИЙ текст, перехоплений з <c>FindAsync</c>; фактичний план, <c>ActualRowsRead</c>), коли
    /// в документа є історія прогонів (20 тис. рядків), а в періоді - інший документ з 20 тис. рядків тієї самої версії
    /// методології. Межа - рядки АКТУАЛЬНОГО прогону документа (200) і по рядку на пошук; історія й чужі документи - ні.
    /// Контроль середовища: звужена плоска форма на тих самих даних читає історію - інакше замір ні про що.
    /// </summary>
    /// <remarks>
    /// ⚠ Рядки, а не логічні читання: читання на маленькій тестовій базі ростуть і від глибини B-дерева (перша редакція
    /// цього тесту: 6 → 33 сторінки при незмінній формі), рядки фактичного плану - лише від того, ЩО читається.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Картка_не_читає_історію_результатів_документа_і_довідники_всього_періоду()
    {
        const int CurrentRows = 200;
        const int HistoryRuns = 5;
        const int HistoryRowsPerRun = 4_000;
        const int OtherDocumentRows = 20_000;

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var version = await VersionAsync(db);
        await RegistryAsync(db, $"BC{tag}", Now.AddHours(-5), version);   // ребро є: довідникова гілка рахується
        var run = await RunAsync(db, chain, version, Now, [chain.DocumentId]);
        await ResultsAsync(run.Id, version, period, chain.DocumentId, CurrentRows - 1);
        await EditAsync(chain.DocumentId, period, chain.ColumnDefIds[1], Now.AddHours(1));

        for (var i = 0; i < HistoryRuns; i++)
        {
            var old = await PendingRunAsync(db, chain, version, Now.AddHours(-10 + i), []);
            await ResultsAsync(old.Id, version, period, chain.DocumentId, HistoryRowsPerRun);
            old.Supersede();
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var other = await AddDocumentAsync(db, chain, $"BC{tag}-OTHER");
        var otherRun = await RunAsync(db, chain, version, Now, [], runDocumentId: other);
        await ResultsAsync(otherRun.Id, version, period, other, OtherDocumentRows);

        var (card, flat, stale) = await CardCostAsync(chain, period);
        var report = $"рядків CalculationResult прочитано: картка {card}, плоска форма {flat}";

        Assert.True(stale, report);

        // Контроль середовища: плоска форма, звужена по id, читає історію документа (і довідникову частину періоду).
        Assert.True(flat >= HistoryRuns * HistoryRowsPerRun, "Плоска форма не читала історію - замір ні про що: " + report);

        // ⛔ ГОЛОВНЕ: картка читає рядки актуального прогону документа (довідникова гілка) і по рядку на пошук `EXISTS`
        // (актуальні прогони періоду × 2 гілки; спільна тестова база - запас 200), але не історію й не чужий документ.
        Assert.True(card <= CurrentRows + 200, "Картка читає історію результатів чи чужі документи періоду: " + report);
    }

    /// <summary>Рядки <c>calc.CalculationResult</c>, прочитані карткою (бойовий текст) і звуженою плоскою формою; вердикт картки.</summary>
    private async Task<(long Card, long Flat, bool Stale)> CardCostAsync(TestDocument chain, int period)
    {
        var capture = new CommandCapture(StaleResultsMapCache.DirectTag);
        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).AddInterceptors(capture);
        await using var db = new EcrDbContext(options.Options);

        var card = await new DocumentStore(db).FindAsync(chain.DocumentId, new PeriodKeyFilter(period), CancellationToken.None);
        var cardCommand = Assert.Single(capture.Commands);

        long[] ids = [chain.DocumentId];
        _ = await StaleResultsQuery.Documents(db, period)
            .Where(r => ids.Contains(r.DocumentId))
            .TagWith(StaleResultsMapCache.DirectTag)
            .OrderBy(r => r.DocumentId)
            .Take(1)
            .ToListAsync();
        var flatCommand = capture.Commands[^1];
        Assert.NotSame(cardCommand, flatCommand);

        return (await ResultRowsReadAsync(cardCommand), await ResultRowsReadAsync(flatCommand), card!.ResultsStale == true);
    }

    /// <summary>
    /// Виконує захоплену команду з <c>SET STATISTICS XML ON</c> і сумує <c>ActualRowsRead</c> (за відсутності -
    /// <c>ActualRows</c>) операторів доступу до <c>CalculationResult</c> у фактичному плані. План без жодного такого
    /// оператора - падіння з текстом, а не нуль (нуль пройшов би будь-яку межу).
    /// </summary>
    private async Task<long> ResultRowsReadAsync(CapturedCommand captured)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "SET STATISTICS XML ON;";
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = captured.Text;
        foreach (var parameter in captured.Parameters)
        {
            command.Parameters.Add((SqlParameter)((ICloneable)parameter).Clone());
        }

        string? plan = null;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase))
                    {
                        plan = reader.GetString(0);
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        Assert.NotNull(plan);
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var operators = XDocument.Parse(plan).Descendants(ns + "RelOp")
            .Where(op => op.Elements().Any(e => e.Elements(ns + "Object")
                .Any(o => (string?)o.Attribute("Table") == "[CalculationResult]")))
            .ToList();
        if (operators.Count == 0)
        {
            throw new InvalidOperationException("У фактичному плані немає доступу до CalculationResult:" + Environment.NewLine + plan);
        }

        return operators
            .SelectMany(op => op.Elements(ns + "RunTimeInformation").Elements(ns + "RunTimeCountersPerThread"))
            .Sum(t => long.Parse(
                (string?)t.Attribute("ActualRowsRead") ?? (string?)t.Attribute("ActualRows") ?? "0", CultureInfo.InvariantCulture));
    }

    /// <summary>Захоплена команда: текст і копії параметрів.</summary>
    private sealed record CapturedCommand(string Text, IReadOnlyList<SqlParameter> Parameters);

    /// <summary>Запам'ятовує команди з тегом (текст і копії параметрів), що пішли в SQL.</summary>
    private sealed class CommandCapture(string tag) : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("-- " + tag, StringComparison.Ordinal))
            {
                Commands.Add(new CapturedCommand(
                    command.CommandText,
                    [.. command.Parameters.Cast<SqlParameter>().Select(p => (SqlParameter)((ICloneable)p).Clone())]));
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Довідник зі зміною даних о <paramref name="changedAt"/>, який читає версія методології.</summary>
    private static async Task<int> RegistryAsync(EcrDbContext db, string code, DateTime changedAt, int versionId)
    {
        var registry = new RegistryDef(EcrCode.Create(code), Name("Registry"), isTemporal: false);
        registry.MarkDataChanged(changedAt);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync(CancellationToken.None);
        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(versionId, "F1", registry.Id, "X"));
        await db.SaveChangesAsync(CancellationToken.None);
        return registry.Id;
    }

    /// <summary><paramref name="count"/> результатів прогону для документа одним INSERT … SELECT.</summary>
    private async Task ResultsAsync(long runId, int versionId, int periodKey, long documentId, int count)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = """
            DECLARE @unit int = (SELECT MIN(Id) FROM uom.Unit);
            WITH d AS (SELECT v FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS x (v)),
                 n AS (SELECT a.v + 10 * b.v + 100 * c.v + 1000 * e.v + 10000 * f.v + 1 AS n
                         FROM d AS a CROSS JOIN d AS b CROSS JOIN d AS c CROSS JOIN d AS e CROSS JOIN d AS f)
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            SELECT NEXT VALUE FOR calc.CalculationResultSeq, @run, @version, @period, @document,
                   CONCAT(N'row-', n.n), N'E_CO2', CAST(1 AS decimal(34,16)), @unit
              FROM n
             WHERE n.n <= @count;
            """;
        command.Parameters.AddWithValue("@run", runId);
        command.Parameters.AddWithValue("@version", versionId);
        command.Parameters.AddWithValue("@period", periodKey);
        command.Parameters.AddWithValue("@document", documentId);
        command.Parameters.AddWithValue("@count", count);
        await command.ExecuteNonQueryAsync();
    }
}
