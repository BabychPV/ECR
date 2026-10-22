using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-45: предикат «результати застаріли» (<c>StaleResultsQuery</c>) лишається ПЛОСКИМ (JOIN + GROUP BY), без
/// корельованого <c>APPLY</c> на документ.
/// </summary>
/// <remarks>
/// ⛔ Корельований <c>CROSS APPLY … UNION ALL</c> оптимізатор виконував вкладеними циклами зі спулом (≈15 млн
/// логічних читань на 91 документ; 17–18 с на 192 тис. результатів і 4,5 млн правок; перелік документів давав
/// 503 на 60-й секунді). Плоска форма — 0,6–1,7 с на тих самих даних. Сторож по ТЕКСТУ SQL, бо план залежить від
/// обсягу, а тестова база мала. Мутація: повернути <c>CROSS APPLY</c> у SQL — тест червоний.
/// </remarks>
public sealed class StaleResultsQueryShapeTests
{
    private const string Path = "src/Ecr.Infrastructure/Persistence/StaleResultsQuery.cs";

    [Fact]
    public void Запит_застарілості_не_використовує_корельований_APPLY()
    {
        var file = SourceTree.Production("Ecr.Infrastructure").Single(f => f.Path == Path);

        var sql = file.Text
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(l => l.ToUpperInvariant())
            .ToArray();

        Assert.DoesNotContain(sql, l => l.Contains("CROSS APPLY", StringComparison.Ordinal)
                                        || l.Contains("OUTER APPLY", StringComparison.Ordinal));
        Assert.Contains(sql, l => l.Contains("JOIN AUD.CELLCHANGE", StringComparison.Ordinal));
    }

    /// <summary>
    /// rc30 (2026-10-22): довідникова гілка - множинний підзапит <c>used</c> + <c>INNER HASH JOIN</c>, а НЕ корельований
    /// <c>EXISTS</c> на пару «документ × довідник». Занижена оцінка агрегату документів (2-3 рядки проти сотень) робила
    /// <c>EXISTS</c> внутрішньою стороною вкладених циклів із <c>Table Spool</c> (7,7 млн рядків; 30 с таймаути на CI).
    /// Мутація: повернути <c>AND EXISTS (SELECT 1 …)</c> або прибрати <c>INNER HASH JOIN</c> - тест червоний.
    /// </summary>
    [Fact]
    public void Довідникова_гілка_через_хеш_з_єднання_а_не_корельований_EXISTS()
    {
        var file = SourceTree.Production("Ecr.Infrastructure").Single(f => f.Path == Path);

        var sql = file.Text
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(l => l.ToUpperInvariant())
            .ToArray();

        Assert.DoesNotContain(sql, l => l.Contains("EXISTS", StringComparison.Ordinal));
        Assert.Contains(sql, l => l.Contains("INNER HASH JOIN", StringComparison.Ordinal));
        Assert.Contains(sql, l => l.Contains("AS USED", StringComparison.Ordinal));
        Assert.Contains(sql, l => l.Contains("RD.ID = USED.REGISTRYDEFID", StringComparison.Ordinal));
    }
}
