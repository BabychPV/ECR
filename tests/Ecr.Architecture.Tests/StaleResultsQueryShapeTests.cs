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
}
