// tests/Ecr.Architecture.Tests/EndpointCoverageTests.cs
using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожен ендпоінт, оголошений у контракті, існує і не є заглушкою.
/// </summary>
/// <remarks>
/// ⚠ Тест з'явився після аудиту, який знайшов **17 із 20** ендпоінтів Етапу 1
/// у стані `NotImplementedException` — через рік після того, як етап
/// вважався завершеним. Причина проста: заглушки перевірялися тестами, а
/// ендпоінт без тестової заглушки не перевірявся нічим.
///
/// Таблиця `02-contracts.md` §9 і є специфікацією; цей тест звіряє з нею
/// реалізацію, а не навпаки.
/// </remarks>
public sealed partial class EndpointCoverageTests
{
    /// <summary>Етапи, ендпоінти яких ще не реалізовані.</summary>
    /// <remarks>
    /// Список має ЗМЕНШУВАТИСЯ. Етап, який уже зробили, але забули прибрати
    /// звідси, знову робить пропуск невидимим.
    /// </remarks>
    private static readonly int[] DeferredStages = [5];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_ендпоінт_контракту_реалізований_або_належить_майбутньому_етапу()
    {
        var declared = Declared();
        Assert.NotEmpty(declared);

        var implemented = Implemented();

        var missing = declared
            .Where(d => !DeferredStages.Contains(d.Stage))
            .Where(d => !implemented.TryGetValue((d.Method, d.Path), out var done) || !done)
            .Select(d => $"{d.Method} {d.Path} (Етап {d.Stage})")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жоден_контролер_не_оголошує_маршруту_поза_контрактом()
    {
        var declared = Declared().Select(d => (d.Method, d.Path)).ToHashSet();

        // ⚠ Зворотний бік тієї самої перевірки. Маршрут, якого немає в
        // контракті, ніхто не описав клієнту — і зміниться він без
        // попередження, бо жоден документ його не тримає.
        var extra = Implemented().Keys
            .Where(k => !declared.Contains(k))
            .Select(k => $"{k.Method} {k.Path}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(extra);
    }

    /// <summary>Рядок таблиці ендпоінтів контракту.</summary>
    private sealed record Endpoint(string Method, string Path, int Stage);

    [GeneratedRegex(@"^\|\s*`(GET|POST|PUT|PATCH|DELETE)`\s*\|\s*`([^`]+)`\s*\|[^|]*\|\s*(\d)\s*\|",
                    RegexOptions.Multiline)]
    private static partial Regex ContractRow { get; }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter { get; }

    private static List<Endpoint> Declared()
    {
        var path = Path.Combine(SolutionRoot(), "docs", "build", "02-contracts.md");
        return [.. ContractRow.Matches(File.ReadAllText(path))
            .Select(m => new Endpoint(
                m.Groups[1].Value,
                Normalize(m.Groups[2].Value.Split('?')[0].TrimEnd('/')),
                int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)))];
    }

    /// <summary>Маршрути контролерів: чи є дія і чи не заглушка вона.</summary>
    /// <remarks>
    /// ⚠ Читається ВИХІДНИЙ КОД, а не рефлексія. Рефлексія бачить, що метод
    /// існує, але не бачить, що його тіло — це
    /// <c>throw new NotImplementedException</c>. Саме така сліпота і дозволила
    /// сімнадцяти ендпоінтам Етапу 1 роками рахуватися готовими.
    /// </remarks>
    private static Dictionary<(string Method, string Path), bool> Implemented()
    {
        var result = new Dictionary<(string, string), bool>();
        var directory = Path.Combine(SolutionRoot(), "src", "Ecr.Api", "Controllers");

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var source = File.ReadAllText(file);
            var route = RouteAttributeRegex.Match(source);
            var baseRoute = route.Success ? route.Groups[1].Value : string.Empty;

            foreach (Match action in ActionRegex.Matches(source))
            {
                var suffix = action.Groups[2].Value;
                var full = "/" + string.Join('/', new[] { baseRoute, suffix }.Where(p => p.Length > 0));

                // Заглушка впізнається за тілом одразу після сигнатури: у
                // реалізованій дії там код, у нереалізованій — throw.
                var implemented = !action.Groups["body"].Value.Contains(
                    "NotImplementedException", StringComparison.Ordinal);

                result[(action.Groups[1].Value.ToUpperInvariant(), Normalize(full))] = implemented;
            }
        }

        return result;
    }

    [GeneratedRegex(@"\[Route\(""([^""]+)""\)\]")]
    private static partial Regex RouteAttributeRegex { get; }

    [GeneratedRegex(
        @"\[Http(Get|Post|Put|Patch|Delete)(?:\(""([^""]*)""\))?\]"
        + @".*?public\s+(?:async\s+)?(?:Task<[^(]*?>|Task|IActionResult)\s+\w+\s*\("
        + @"[^)]*\)(?=(?<body>.{0,400}))",
        RegexOptions.Singleline)]
    private static partial Regex ActionRegex { get; }

    /// <summary>Прибирає імена і обмеження параметрів: <c>{id:long}</c> → <c>{}</c>.</summary>
    private static string Normalize(string route) => RouteParameter.Replace(route, "{}");

    /// <summary>Корінь рішення — від каталогу збірки вгору до <c>Ecr.sln</c>.</summary>
    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
    }
}
