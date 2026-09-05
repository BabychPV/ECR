using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Параметри рядка запиту клієнта існують на сервері.
/// </summary>
/// <remarks>
/// ⛔ Сторож «кожна адреса клієнта існує на сервері» рядок запиту
/// <b>відрізає</b> — інакше кожна адреса з параметром вважалася б чужою. Через
/// це половина контракту не перевірялася нічим: шлях правильний, а ім'я
/// параметра — ні, і сервер мовчки бере значення за замовчуванням.
///
/// ⚠ Саме так виглядав `A7-28`: клієнт надсилав період, сервер читав його з
/// іншого місця, і валідація йшла по періоду <c>0</c>, відповідаючи «помилок
/// немає». Відмови не було — була неправдива відповідь.
/// </remarks>
public sealed partial class QueryParameterTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_параметр_запиту_клієнта_існує_на_сервері()
    {
        var server = ServerParameters();
        Assert.NotEmpty(server);

        var offenders = new List<string>();

        foreach (var (file, url) in ClientUrls())
        {
            var path = Normalize(url.Split('?')[0]);
            var names = QueryNameRegex.Matches(url).Select(m => m.Groups[1].Value).ToList();

            if (names.Count == 0)
            {
                continue;
            }

            // Шлях, якого сервер не знає, — не наша турбота: його ловить
            // сторож адрес, і дублювати ту саму скаргу тут означало б
            // отримати дві на один дефект.
            if (!server.TryGetValue(path, out var accepted))
            {
                continue;
            }

            foreach (var name in names.Where(n => !accepted.Contains(n)))
            {
                offenders.Add($"{path}?{name}= ({file}) — сервер приймає: "
                              + (accepted.Count == 0 ? "нічого" : string.Join(", ", accepted.Order(StringComparer.Ordinal))));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Параметри запиту, яких сервер не читає — він мовчки візьме значення "
            + "за замовчуванням:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Order(StringComparer.Ordinal)));
    }

    /// <summary>Адреси з рядком запиту в коді клієнта.</summary>
    /// <remarks>
    /// ⚠ Ловляться і склеєні адреси: перелік документів будує рядок запиту
    /// конкатенацією, і сам шаблон параметра там окремим літералом.
    /// </remarks>
    private static IEnumerable<(string File, string Url)> ClientUrls()
    {
        var web = Path.Combine(SolutionRoot(), "src", "Ecr.Web", "src");

        var files = Directory
            .EnumerateFiles(web, "*.ts", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(web, "*.tsx", SearchOption.AllDirectories))
            .Where(f => !f.Contains("schema.d.ts", StringComparison.Ordinal)
                        && !f.Contains("__tests__", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);

            foreach (Match match in ClientUrlRegex.Matches(text))
            {
                var url = match.Groups[1].Value;

                // Продовження склеєної адреси: `+ '&periodKey=' + …`. Шлях
                // береться з попереднього літерала того самого виразу.
                yield return (Path.GetFileName(file), url);
            }
        }
    }

    /// <summary>
    /// Імена <c>[FromQuery]</c> кожної дії контролера.
    /// </summary>
    /// <remarks>
    /// ⚠ Береться ІМ'Я параметра, а не його тип. Вираз, який хапає тип,
    /// зеленітиме на будь-якому імені — і сторож перевірятиме те, що ніколи
    /// не розходиться.
    /// </remarks>
    private static Dictionary<string, HashSet<string>> ServerParameters()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var directory = Path.Combine(SolutionRoot(), "src", "Ecr.Api", "Controllers");

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var source = File.ReadAllText(file);
            var route = RouteRegex.Match(source);
            var baseRoute = route.Success ? route.Groups[1].Value : string.Empty;

            foreach (Match action in ActionRegex.Matches(source))
            {
                var suffix = action.Groups[2].Value;
                var full = Normalize("/" + string.Join('/', new[] { baseRoute, suffix }.Where(p => p.Length > 0)));

                var names = FromQueryRegex
                    .Matches(action.Groups["args"].Value)
                    .Select(m => m.Groups[1].Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // ⛔ ОБ'ЄДНАННЯ, а не присвоєння. Один шлях має кілька
                // методів: `GET /templates` приймає `limit` і `cursor`,
                // `POST /templates` — нічого. Присвоєння лишало б у
                // переліку той метод, що трапився останнім, і сторож
                // доповідав би про `limit`, якого «сервер не приймає».
                //
                // ⚠ Це та сама вада, через яку тринадцятий сторож не бачив
                // семи дій створення (`A7-42`): шлях без методу. Тут вона
                // дешевша — сторож клієнтського боку методу не знає, і
                // об'єднання лише послаблює перевірку, а не спотворює її.
                if (result.TryGetValue(full, out var existing))
                {
                    existing.UnionWith(names);
                }
                else
                {
                    // Дія без параметрів запиту теж має бути в переліку:
                    // інакше «сервер не приймає нічого» не відрізнити від
                    // «шляху немає».
                    result[full] = names;
                }
            }
        }

        return result;
    }

    /// <summary>Зводить параметри шляху до однакового заповнювача.</summary>
    private static string Normalize(string path)
    {
        var withoutInterpolation = InterpolationRegex.Replace(path, "{p}");

        return BraceRegex.Replace(withoutInterpolation.TrimEnd('/'), "{p}");
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }

    /// <summary>Адреса API з рядком запиту.</summary>
    [GeneratedRegex(@"['""`](/api/v1/(?:\$\{[^}]*\}|[^'""`\s])*\?[^'""`]*)['""`]")]
    private static partial Regex ClientUrlRegex { get; }

    /// <summary>Ім'я параметра в рядку запиту.</summary>
    [GeneratedRegex(@"[?&](\w+)=")]
    private static partial Regex QueryNameRegex { get; }

    /// <summary>Базовий маршрут контролера.</summary>
    [GeneratedRegex(@"\[Route\(""([^""]+)""\)\]")]
    private static partial Regex RouteRegex { get; }

    /// <summary>Дія контролера разом зі списком аргументів.</summary>
    [GeneratedRegex(
        @"\[Http(Get|Post|Put|Patch|Delete)(?:\(""([^""]*)""\))?\][^;{]*?public\s+[^(]*\((?<args>[^)]*)\)",
        RegexOptions.Singleline)]
    private static partial Regex ActionRegex { get; }

    /// <summary>Ім'я параметра, позначеного <c>[FromQuery]</c>.</summary>
    [GeneratedRegex(@"\[FromQuery\]\s*[\w?<>\[\],\s\.]*?(\w+)\s*(?:,|$|\))")]
    private static partial Regex FromQueryRegex { get; }

    [GeneratedRegex(@"\$\{(?:[^{}]|\{[^{}]*\})*\}")]
    private static partial Regex InterpolationRegex { get; }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex BraceRegex { get; }
}
