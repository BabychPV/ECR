using System.Data.Common;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Чиста частина <see cref="StaleMapQueryHintInterceptor"/>: коли підказка дописується.</summary>
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Static)]
    public void Нуль_вимикає_підказку(int maxDop)
        => Assert.Equal(Tagged, StaleMapQueryHintInterceptor.Apply(Tagged, maxDop));
}

public sealed partial class DocumentStaleResultsStoreTests
{
    /// <summary>Лише запоминає текст команд, що пішли в SQL (після перехоплювача підказки).</summary>
    private sealed class SqlTextRecorder : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (Texts)
            {
                Texts.Add(command.CommandText);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запити_карти_йдуть_із_MAXDOP_а_результат_той_самий_що_без_підказки()
    {
        var r = await SwitchRigAsync(6);
        using var _ = r.Cache;

        var recorder = new SqlTextRecorder();
        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString).AddInterceptors(new StaleMapQueryHintInterceptor(2), recorder);
        await using var hinted = new EcrDbContext(options.Options);
        var withHint = await new StaleResultsMapCache(hinted, null, null, null).GetAsync(r.Period, [null, Me, 62], CancellationToken.None);

        await using var plain = r.Builder.CreateContext();
        var without = await new StaleResultsMapCache(plain, null, null, null).GetAsync(r.Period, [null, Me, 62], CancellationToken.None);

        // Без кешу кожен ключ («*», автор) - окремий повний запит `StaleMap:full`; усі йдуть з підказкою.
        var stale = recorder.Texts.Where(t => t.Contains("-- StaleMap:", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, stale.Count(t => t.Contains(StaleResultsMapCache.FullTag, StringComparison.Ordinal)));
        Assert.All(stale, t => Assert.EndsWith("OPTION (MAXDOP 2)", t, StringComparison.Ordinal));

        Assert.Equal(without.Count, withHint.Count);
        for (var i = 0; i < without.Count; i++)
        {
            Assert.Equal(
                without[i]!.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)),
                withHint[i]!.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)));
        }
    }
}
