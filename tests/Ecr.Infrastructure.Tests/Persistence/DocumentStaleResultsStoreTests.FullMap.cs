using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Повна карта періоду (<c>StaleResultsMapCache</c>: <c>-- StaleMap:full</c>, <c>-- StaleMap:authors</c>) - форма періоду
/// <see cref="StaleResultsByIdsQuery.PeriodDocuments"/> (той самий текст, що картка): ті самі рядки, що плоска форма періоду, а вартість не росте
/// з історією результатів періоду.
/// </summary>
/// <remarks>
/// ⛔ Стенд Land 21.11 (Query Store): <c>StaleMap:full</c> 1,1 тис. виконань, у середньому 1,8 с, до 58 с; <c>StaleMap:authors</c>
/// до 60 с. Фактичний план плоскої форми (202601, теплий кеш): <c>x</c> - 1,27 млн рядків історії (78 тис. актуальних) двічі,
/// <c>used</c> - ще раз; 26 тис. логічних читань, 0,56-0,76 с, 2-2,7 с ЦП (нова форма - 2,4 тис. читань, 49 мс). На тестовій
/// базі вартість стереже <see cref="Повна_карта_не_читає_історію_результатів_періоду"/> (карта 254 рядки, плоска форма 72 750).
/// Мутаційні докази (локально, не комітити):
/// <list type="bullet">
///   <item>у <c>StaleResultsMapCache.LoadAsync</c> / <c>LoadAuthorsAsync</c> повернути <c>StaleResultsQuery.Documents(db, p,
///   byUserId)</c> / <c>AuthorDocuments(db, p)</c> - червоний
///   <see cref="Повна_карта_не_читає_історію_результатів_періоду"/> (карта 72 750 рядків при межі 650);</item>
///   <item>у <c>StaleResultsByIdsQuery.CurrentRuns</c> прибрати <c>r0.DocumentId IS NULL OR</c> (лише власні прогони) - червоні
///   <see cref="Повна_карта_дає_ті_самі_рядки_що_плоска_форма_періоду"/> і обидва тести форми для набору;</item>
///   <item>там само прибрати <c>AND cr.CalculationRunId = r0.Id</c> з <c>EXISTS</c> - червоний той самий тест рівності
///   (документ K: новіший власний прогін без його чисел).</item>
/// </list>
/// </remarks>
public sealed partial class DocumentStaleResultsStoreTests
{
    /// <summary>
    /// Рівність повної карти періоду (без кешу - <c>StaleMap:full</c> для кожного фільтра автора; з кешем - «*» і карти
    /// авторів <c>StaleMap:authors</c>) з плоскою формою <see cref="StaleResultsQuery"/> на сценаріях форми для набору
    /// (дві актуальні області, новіший НЕактуальний прогін, довідник після прогону, <c>InputsAsOfUtc</c> раніше за старт,
    /// правка до прогону, <c>Recalculation</c>, інший період, документ без чисел) плюс другий проєкт того самого періоду
    /// (лише проєктний прогін; документ лише з власним прогоном).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повна_карта_дає_ті_самі_рядки_що_плоска_форма_періоду()
    {
        // Окремий період: карта - весь період, а в 202601 пишуть паралельні тести інших класів.
        const int Period = 202607;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(periodKey: Period, ct: CancellationToken.None);
        var second = await builder.BuildAsync(periodKey: Period, ct: CancellationToken.None);
        var column = chain.ColumnDefIds[1];
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var v1 = await VersionAsync(db);   // читає R1 (змінено після прогонів)
        var v2 = await VersionAsync(db);   // читає R2 (змінено до старту, але після InputsAsOfUtc)
        var v4 = await VersionAsync(db);   // довідників не читає
        await RegistryAsync(db, $"FM1{tag}", Now.AddHours(1), v1);
        await RegistryAsync(db, $"FM2{tag}", Now.AddMinutes(-30), v2);

        var docs = new Dictionary<string, long>();
        foreach (var name in new[] { "A", "B", "C", "E", "F", "G", "G2", "H", "I", "K", "N" })
        {
            docs[name] = await AddDocumentAsync(db, chain, $"FM{tag}-{name}");
        }

        // Прогін усього проєкту (Now, актуальний) з числами для більшості документів.
        var project = await RunAsync(db, chain, v4, Now, []);
        foreach (var name in new[] { "A", "B", "C", "E", "F", "G", "H", "I", "K" })
        {
            await ResultsAsync(project.Id, v4, Period, docs[name], 1);
        }

        await ResultsAsync(project.Id, v1, Period, docs["G"], 3);

        // K: новіший ВЛАСНИЙ актуальний прогін без чисел документа - актуальний прогін K лишається проєктним (у ньому
        // є числа), тож правка між стартами застарює. Ловить EXISTS без прив'язки до прогону (будь-які числа періоду).
        _ = await RunAsync(db, chain, v4, Now.AddHours(1), [], runDocumentId: docs["K"]);

        // E: пізніший актуальний прогін лише документа - правка між стартами НЕ застарює.
        var eRun = await RunAsync(db, chain, v4, Now.AddHours(1), [], runDocumentId: docs["E"]);
        await ResultsAsync(eRun.Id, v4, Period, docs["E"], 2);

        // F: новіший НЕактуальний прогін з числами документа - береться актуальний (старіший).
        var pending = await PendingRunAsync(db, chain, v4, Now.AddHours(2), []);
        await ResultsAsync(pending.Id, v4, Period, docs["F"], 2);

        // G2: власний актуальний прогін, входи «на момент» раніше за старт; довідник R2 змінено між ними.
        var g2Run = await RunAsync(db, chain, v2, Now, [], runDocumentId: docs["G2"]);
        g2Run.LimitInputsAsOf(Now.AddHours(-1));
        await db.SaveChangesAsync(CancellationToken.None);
        await ResultsAsync(g2Run.Id, v2, Period, docs["G2"], 2);

        await EditAsync(docs["A"], Period, column, Now.AddHours(3), user: 72);
        await EditAsync(docs["A"], Period, column, Now.AddHours(1), user: 71);
        await EditAsync(docs["B"], Period, column, Now.AddHours(-1));
        await EditAsync(docs["C"], Period, column, Now.AddHours(1), origin: "Recalculation");
        await EditAsync(docs["E"], Period, column, Now.AddMinutes(30));
        await EditAsync(docs["F"], Period, column, Now.AddHours(1), user: 72);
        await EditAsync(docs["G"], Period, column, Now.AddHours(2));
        await EditAsync(docs["G2"], Period, column, Now.AddMinutes(-10));
        await EditAsync(docs["I"], Period + 1, column, Now.AddHours(1));
        await EditAsync(docs["N"], Period, column, Now.AddHours(1));
        await EditAsync(docs["K"], Period, column, Now.AddMinutes(30));

        // Другий проєкт: S - лише прогін усього проєкту (читає R1 - застарілий за довідником), T - лише власний прогін
        // (правка після нього), U - документ проєкту без чисел.
        var s = second.DocumentId;
        var t = await AddDocumentAsync(db, second, $"FM{tag}-T");
        var u = await AddDocumentAsync(db, second, $"FM{tag}-U");
        var secondProject = await RunAsync(db, second, v1, Now, []);
        await ResultsAsync(secondProject.Id, v1, Period, s, 2);
        var tRun = await RunAsync(db, second, v4, Now, [], runDocumentId: t);
        await ResultsAsync(tRun.Id, v4, Period, t, 2);
        await EditAsync(t, Period, second.ColumnDefIds[1], Now.AddHours(1), user: 71);
        await EditAsync(u, Period, second.ColumnDefIds[1], Now.AddHours(1), user: 71);

        int?[] users = [null, 71, 72, 1];
        var plain = await new StaleResultsMapCache(db, null, null, null).GetAsync(Period, users, CancellationToken.None);
        using var cache = NewCache();
        var cached = await new StaleResultsMapCache(db, cache, new TestTimeSource(Now), new StaleCountsEpoch())
            .GetAsync(Period, users, CancellationToken.None);

        for (var i = 0; i < users.Length; i++)
        {
            var flat = (await StaleResultsQuery.Documents(db, Period, users[i]).ToListAsync())
                .Select(r => (r.DocumentId, r.Since)).Order().ToList();
            Assert.Equal(flat, plain[i]!.Select(p => (p.Key, p.Value)).Order().ToList());
            Assert.Equal(flat, cached[i]!.Select(p => (p.Key, p.Value)).Order().ToList());
        }

        // Сценарій не порожній: очікувані вердикти, порахані в тесті.
        var expected = new Dictionary<long, DateTime>
        {
            [docs["A"]] = Now.AddHours(1),
            [docs["F"]] = Now.AddHours(1),
            [docs["G"]] = Now.AddHours(1),
            [docs["G2"]] = Now.AddMinutes(-30),
            [docs["K"]] = Now.AddMinutes(30),
            [s] = Now.AddHours(1),
            [t] = Now.AddHours(1),
        };
        foreach (var (id, since) in expected)
        {
            Assert.True(plain[0]!.TryGetValue(id, out var got), $"{id} не в карті");
            Assert.True(Math.Abs((got!.Value - since).TotalSeconds) < 1, $"{id}: {got:O} != {since:O}");
        }

        foreach (var id in new[] { docs["B"], docs["C"], docs["E"], docs["H"], docs["I"], docs["N"], u })
        {
            Assert.False(plain[0]!.ContainsKey(id), $"{id} не мав би бути в карті");
        }

        Assert.Equal(new[] { docs["A"], t }.Order(), plain[1]!.Keys.Where(expected.ContainsKey).Order());
        Assert.Equal(new[] { docs["A"], docs["F"] }.Order(), plain[2]!.Keys.Where(expected.ContainsKey).Order());
    }

    /// <summary>
    /// Сторож вартості повної карти (ratchet за історією): скільки рядків <c>calc.CalculationResult</c> ЧИТАЮТЬ бойові запити
    /// <c>-- StaleMap:full</c> і <c>-- StaleMap:authors</c> (фактичний план, <c>ActualRowsRead</c>), коли в періоді є історія
    /// прогонів двох документів (24 тис. рядків). Межа - рядки АКТУАЛЬНИХ прогонів періоду (довідникова гілка читає їх, щоб
    /// знайти версії методологій) і по рядку на пошук. Контроль середовища: плоска форма на тих самих даних читає історію.
    /// </summary>
    /// <remarks>
    /// ⚠ Межа для карти авторів тут - стеля, а не мутаційний доказ: плоска форма авторів на малій тестовій базі обирає план
    /// через <c>IX_CalculationResult_Run</c> (500 рядків), а не історію (на Land - історію, до 60 с). Форму авторів стереже
    /// <c>StaleResultsByIdsQueryShapeTests.Повна_карта_періоду_йде_формою_для_набору_над_документами_періоду</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повна_карта_не_читає_історію_результатів_періоду()
    {
        const int Period = 202609;
        const int CurrentRows = 200;
        const int HistoryRuns = 5;
        const int HistoryRowsPerRun = 4_000;

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(periodKey: Period, ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var version = await VersionAsync(db);
        await RegistryAsync(db, $"FC{tag}", Now.AddHours(-5), version);   // ребро є: довідникова гілка рахується
        var run = await RunAsync(db, chain, version, Now, [chain.DocumentId]);
        await ResultsAsync(run.Id, version, Period, chain.DocumentId, CurrentRows - 1);
        await EditAsync(chain.DocumentId, Period, chain.ColumnDefIds[1], Now.AddHours(1), user: 71);

        for (var i = 0; i < HistoryRuns; i++)
        {
            var old = await PendingRunAsync(db, chain, version, Now.AddHours(-10 + i), []);
            await ResultsAsync(old.Id, version, Period, chain.DocumentId, HistoryRowsPerRun);
            old.Supersede();
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // Другий документ: власний актуальний прогін і власна історія.
        var other = await AddDocumentAsync(db, chain, $"FC{tag}-OTHER");
        var otherRun = await RunAsync(db, chain, version, Now, [], runDocumentId: other);
        await ResultsAsync(otherRun.Id, version, Period, other, 50);
        var otherOld = await PendingRunAsync(db, chain, version, Now.AddHours(-3), []);
        await ResultsAsync(otherOld.Id, version, Period, other, HistoryRowsPerRun);
        otherOld.Supersede();
        await db.SaveChangesAsync(CancellationToken.None);

        var full = new CommandCapture(StaleResultsMapCache.FullTag);
        var authors = new CommandCapture(StaleResultsMapCache.AuthorsTag);
        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).AddInterceptors(full, authors);
        await using var captured = new EcrDbContext(options.Options);

        var map = (await new StaleResultsMapCache(captured, null, null, null).GetAsync(Period, [null], CancellationToken.None))[0];
        Assert.True(map!.ContainsKey(chain.DocumentId), "Документ із правкою після прогону мав би бути застарілим");
        var fullCommands = full.Commands.ToList();
        Assert.NotEmpty(fullCommands);

        using var cache = NewCache();
        var byAuthor = (await new StaleResultsMapCache(captured, cache, new TestTimeSource(Now), new StaleCountsEpoch())
            .GetAsync(Period, [71], CancellationToken.None))[0];
        Assert.True(byAuthor!.ContainsKey(chain.DocumentId));
        Assert.NotEmpty(authors.Commands);

        _ = await StaleResultsQuery.Documents(captured, Period)
            .TagWith(StaleResultsMapCache.FullTag)
            .OrderBy(r => r.DocumentId)
            .Take(StaleResultsMapCache.DefaultMaxDocuments + 1)
            .ToListAsync();
        var flatCommand = Assert.Single(full.Commands.Except(fullCommands));

        long mapRows = 0;
        foreach (var command in fullCommands)
        {
            mapRows += await ResultRowsReadAsync(command);
        }

        long authorRows = 0;
        foreach (var command in authors.Commands)
        {
            authorRows += await ResultRowsReadAsync(command);
        }

        var flat = await ResultRowsReadAsync(flatCommand);
        var current = await CurrentResultRowsAsync(Period);
        var report = $"рядків CalculationResult прочитано: карта {mapRows}, автори {authorRows}, плоска форма {flat}; "
                     + $"актуальних рядків періоду {current}";

        // Контроль середовища: плоска форма читає історію періоду - інакше замір ні про що.
        Assert.True(flat >= (HistoryRuns + 1) * HistoryRowsPerRun, "Плоска форма не читала історію - замір ні про що: " + report);

        // ⛔ ГОЛОВНЕ: рядки актуальних прогонів (довідникова гілка) і по рядку на пошук `EXISTS`, але не історія.
        Assert.True(mapRows <= current + 400, "Повна карта читає історію результатів періоду: " + report);
        Assert.True(authorRows <= current + 400, "Карти авторів читають історію результатів періоду: " + report);
    }

    /// <summary>Рядки результатів актуальних прогонів періоду (усіх документів, зокрема з попередніх запусків тесту).</summary>
    private async Task<long> CurrentResultRowsAsync(int periodKey)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
              FROM calc.CalculationResult AS cr
              JOIN calc.CalculationRun AS r ON r.Id = cr.CalculationRunId AND r.Status = N'Current' AND r.PeriodKey = @period
             WHERE cr.PeriodKey = @period
            """;
        command.Parameters.AddWithValue("@period", periodKey);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
