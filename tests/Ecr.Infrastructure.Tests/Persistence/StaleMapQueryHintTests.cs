// tests/Ecr.Infrastructure.Tests/Persistence/StaleMapQueryHintTests.cs
using System.Data.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Чиста частина <see cref="StaleMapQueryHintInterceptor"/>: коли й яка підказка дописується.</summary>
/// <remarks>
/// ⛔ ДОКАЗ (локально): у <c>EffectiveMaxDop</c> повернути <c>configured</c> замість <c>Math.Min(configured, cap)</c> —
/// червоні <c>Межа_сервера_нижча_за_налаштовану_перемагає</c> і <c>MAXDOP_бази_перекриває_серверний</c>.
/// </remarks>
public sealed class StaleMapQueryHintInterceptorTests
{
    private const string Tagged = "-- StaleMap:full\n\nSELECT TOP(@p0) [e].[DocumentId] FROM (SELECT 1 AS DocumentId) AS [e] ORDER BY [e].[DocumentId]";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Static)]
    public void Тегований_запит_отримує_OPTION_MAXDOP_в_кінці()
    {
        var text = StaleMapQueryHintInterceptor.Apply(Tagged, 2);

        Assert.StartsWith(Tagged, text, StringComparison.Ordinal);
        Assert.EndsWith("\nOPTION (MAXDOP 2)", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Static)]
    public void Крапка_з_комою_в_кінці_не_ламає_підказку()
        => Assert.EndsWith("\nOPTION (MAXDOP 1)", StaleMapQueryHintInterceptor.Apply(Tagged + ";\n", 1), StringComparison.Ordinal);

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("-- інший тег\nSELECT 1")]
    [InlineData("-- StaleMap:full\nSELECT 1 OPTION (RECOMPILE)")]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Static)]
    public void Чужі_запити_й_запити_з_власним_OPTION_не_чіпаються(string text)
        => Assert.Equal(text, StaleMapQueryHintInterceptor.Apply(text, 2));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Static)]
    public void Без_чинної_межі_підказки_немає()
    {
        Assert.Equal(Tagged, StaleMapQueryHintInterceptor.Apply(Tagged, null));
        Assert.Equal(Tagged, StaleMapQueryHintInterceptor.Apply(Tagged, 0));
    }

    /// <summary>
    /// Чинна межа = min(налаштоване, MAXDOP бази якщо &gt; 0, інакше серверний; 0 — без межі). Вимкнено (≤ 0) чи межа
    /// сервера невідома — підказки немає: не знаючи межі, паралелізм не піднімаємо.
    /// </summary>
    [Theory]
    [InlineData(4, 6, 0, 4)] // сервер дозволяє більше — діє налаштоване
    [InlineData(4, 0, 0, 4)] // сервер «усі процесори» — діє налаштоване
    [InlineData(4, 2, 0, 2)] // сервер нижче — діє серверний (не піднімаємо)
    [InlineData(4, 1, 0, 1)] // MAXDOP 1 на сервері — голе OPTION (MAXDOP 4) підняло б паралелізм
    [InlineData(4, 8, 3, 3)] // MAXDOP бази перекриває серверний
    [InlineData(2, 1, 6, 2)] // MAXDOP бази 6 перекриває серверний 1 — діє налаштоване 2
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Static)]
    public void Межа_сервера_нижча_за_налаштовану_перемагає(int configured, int server, int database, int expected)
        => Assert.Equal(expected, StaleMapQueryHintInterceptor.EffectiveMaxDop(configured, new ServerDop(server, database)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Static)]
    public void MAXDOP_бази_перекриває_серверний()
        => Assert.Equal(1, StaleMapQueryHintInterceptor.EffectiveMaxDop(4, new ServerDop(Server: 0, Database: 1)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Static)]
    public void Нуль_вимикає_підказку_а_невідома_межа_сервера_теж(int configured)
    {
        Assert.Null(StaleMapQueryHintInterceptor.EffectiveMaxDop(configured, new ServerDop(6, 0)));
        Assert.Null(StaleMapQueryHintInterceptor.EffectiveMaxDop(4, server: null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Static)]
    public void Типова_межа_чотири()
        => Assert.Equal(4, StaleMapQueryHintInterceptor.DefaultMaxDop);
}

public sealed partial class DocumentStaleResultsStoreTests
{
    /// <summary>Лише запам'ятовує текст команд, що пішли в SQL (після перехоплювача підказки).</summary>
    private sealed class SqlTextRecorder : DbCommandInterceptor
    {
        private readonly List<string> texts = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (texts)
            {
                texts.Add(command.CommandText);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public List<string> Tagged(string tag)
        {
            lock (texts)
            {
                return [.. texts.Where(t => t.Contains("-- " + tag, StringComparison.Ordinal))];
            }
        }
    }

    /// <summary>Підказка, якої чекати на цьому сервері: min(налаштоване, межа сервера), як у перехоплювачі.</summary>
    private async Task<string> ExpectedHintAsync(int configured)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        var server = await StaleMapQueryHintInterceptor.ReadServerDopAsync(connection, null, CancellationToken.None);
        var effective = StaleMapQueryHintInterceptor.EffectiveMaxDop(configured, server);
        Assert.NotNull(effective);
        return $"OPTION (MAXDOP {effective})";
    }

    private EcrDbContext HintedContext(SqlTextRecorder recorder, int maxDop, StaleCountsEpoch? epoch = null)
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString).AddInterceptors(new StaleMapQueryHintInterceptor(maxDop), recorder);
        if (epoch is not null)
        {
            options.AddInterceptors(epoch);
        }

        return new EcrDbContext(options.Options);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запити_карти_йдуть_із_MAXDOP_а_результат_той_самий_що_без_підказки()
    {
        var r = await SwitchRigAsync(6);
        using var _ = r.Cache;
        var hint = await ExpectedHintAsync(2);

        var recorder = new SqlTextRecorder();
        await using var hinted = HintedContext(recorder, 2);
        var withHint = await new StaleResultsMapCache(hinted, null, null, null).GetAsync(r.Period, [null, Me, 62], CancellationToken.None);

        await using var plain = r.Builder.CreateContext();
        var without = await new StaleResultsMapCache(plain, null, null, null).GetAsync(r.Period, [null, Me, 62], CancellationToken.None);

        // Без кешу кожен ключ («*», автор) - окремий повний запит `StaleMap:full`; усі йдуть з підказкою.
        Assert.Equal(3, recorder.Tagged(StaleResultsMapCache.FullTag).Count);
        Assert.All(recorder.Tagged("StaleMap:"), t => Assert.EndsWith(hint, t, StringComparison.Ordinal));

        Assert.Equal(without.Count, withHint.Count);
        for (var i = 0; i < without.Count; i++)
        {
            Assert.Equal(
                without[i]!.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)),
                withHint[i]!.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)));
        }
    }

    /// <summary>
    /// Кешована карта: «автори» одним згрупованим запитом (<c>StaleMap:authors</c>), а після перемикання прогону документа —
    /// точкове лікування (<c>StaleMap:delta</c>). Обидва — з підказкою, і карта дорівнює свіжій без підказки.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запити_авторів_і_лікування_карти_теж_із_MAXDOP_і_тим_самим_результатом()
    {
        var r = await SwitchRigAsync(6);
        using var _ = r.Cache;
        var hint = await ExpectedHintAsync(4);
        var recorder = new SqlTextRecorder();

        async Task AssertHintedEqualsFreshAsync()
        {
            await using var hinted = HintedContext(recorder, 4, r.Epoch);
            var cached = await CachedMap(r, hinted).GetAsync(r.Period, MapUsers, CancellationToken.None);

            await using var freshDb = r.Builder.CreateContext();
            var fresh = await new StaleResultsMapCache(freshDb, null, null, null).GetAsync(r.Period, MapUsers, CancellationToken.None);

            Assert.Equal(fresh.Count, cached.Count);
            for (var i = 0; i < fresh.Count; i++)
            {
                Assert.Equal(
                    fresh[i]!.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)),
                    cached[i]!.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)));
            }
        }

        await AssertHintedEqualsFreshAsync();
        Assert.NotEmpty(recorder.Tagged(StaleResultsMapCache.AuthorsTag));

        // Прогін документа 0 після правки — карта лікується точково (delta), а не повним завантаженням.
        await SwitchDocumentRunAsync(r, r.Docs[0], Now.AddHours(2));
        await AssertHintedEqualsFreshAsync();
        Assert.NotEmpty(recorder.Tagged(StaleResultsMapCache.DeltaTag));

        Assert.All(recorder.Tagged("StaleMap:"), t => Assert.EndsWith(hint, t, StringComparison.Ordinal));
    }

    /// <summary>Картка документа — вузький прямий запит по id (<c>StaleMap:direct</c>): з підказкою і тим самим вердиктом.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прямий_запит_картки_теж_із_MAXDOP_і_тим_самим_результатом()
    {
        var r = await RigAsync();
        using var _ = r.Cache;
        var hint = await ExpectedHintAsync(4);
        var recorder = new SqlTextRecorder();

        await using var hinted = HintedContext(recorder, 4, r.Epoch);
        await using var plain = r.Builder.CreateContext();
        foreach (var id in new[] { r.A, r.B, r.Fresh })
        {
            var withHint = await CachedStore(r, hinted).FindAsync(id, new PeriodKeyFilter(r.Period), CancellationToken.None);
            var without = await new DocumentStore(plain).FindAsync(id, new PeriodKeyFilter(r.Period), CancellationToken.None);

            Assert.Equal(without!.ResultsStale, withHint!.ResultsStale);
        }

        var direct = recorder.Tagged(StaleResultsMapCache.DirectTag);
        Assert.Equal(3, direct.Count);
        Assert.All(direct, t => Assert.EndsWith(hint, t, StringComparison.Ordinal));
    }
}
