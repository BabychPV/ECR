using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Один SPA обслуговує всі п'ять областей; розділення — маршрутами і правами
/// (<c>ФВ-14.1</c>).
/// </summary>
/// <remarks>
/// ⛔ Вимога стояла в <c>contracts/trace-exempt.md</c> як «архітектурний факт,
/// видимий зі структури репозиторію». Видимий — так; перевірений — ні. Другий
/// застосунок не з'являється рішенням, яке хтось оголосить: він з'являється
/// однією теці з власним <c>index.html</c> «щоб адміністрування не заважало»,
/// і після цього спільними лишаються тільки слова. Структуру репозиторію
/// читає той самий тест, що й решту архітектурних правил.
///
/// ⚠ Перевіряються ДВІ сторони твердження, бо поодинці кожна обходиться.
/// «Один застосунок» без другої сторони задовольнив би порожній SPA з одним
/// маршрутом; «усі області» без першої — п'ять окремих застосунків, у
/// кожного свій реєстр.
/// </remarks>
public sealed partial class SinglePageApplicationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.1")]
    public void Точка_входу_клієнта_в_репозиторії_рівно_одна()
    {
        // Три незалежні ознаки застосунку: сторінка-оболонка, модуль входу і
        // монтування React. Другий SPA додає всі три; підробити цей тест
        // можна лише не додавши жодної — тобто не створивши другий застосунок.
        Assert.Equal(
            ["src/Ecr.Web/index.html"],
            ClientFiles("index.html"));

        Assert.Equal(
            ["src/Ecr.Web/src/main.tsx"],
            ClientFiles("main.tsx"));

        var mounts = ClientSources()
            .Where(file => MountRegex().IsMatch(file.Text))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["src/Ecr.Web/src/main.tsx"], mounts);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.1")]
    public void Той_самий_реєстр_маршрутів_покриває_всі_пʼять_областей()
    {
        // ⛔ Області названі самою вимогою: робота з даними, конфігурування,
        // звіти, адміністрування, експлуатація. Кожна представлена маршрутом,
        // який зникне з ЦЬОГО реєстру першим, якщо область винесуть в окремий
        // застосунок, — і тест впаде саме на тій області, яку винесли.
        var paths = DeclaredRoutePaths();

        var areas = new (string Area, string Path)[]
        {
            ("робота з даними", "/documents/:id"),
            ("конфігурування", "/admin/templates"),
            ("звіти", "/admin/snapshots"),
            ("адміністрування", "/admin/security"),
            ("експлуатація", "/admin/health"),
        };

        var missing = areas
            .Where(area => !paths.Contains(area.Path))
            .Select(area => $"{area.Area}: немає маршруту {area.Path}")
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.1")]
    public void Адміністративні_пункти_розділені_правом_а_не_застосунком()
    {
        // ⚠ Друге речення вимоги: «Розділення — маршрутами і правами, не
        // окремими застосунками». Пункт `/admin/*`, показаний у навбарі БЕЗ
        // права, — це саме той випадок, заради якого хтось наступного разу
        // запропонує «винести адміністрування окремо, щоб його не бачили».
        var registry = File.ReadAllText(RoutesPath());

        var offenders = EntryRegex()
            .Matches(registry)
            .Where(entry => entry.Groups["path"].Value.StartsWith("/admin/", StringComparison.Ordinal))
            .Where(entry => entry.Value.Contains("showInNav: true", StringComparison.Ordinal))
            .Where(entry => !entry.Value.Contains("permission:", StringComparison.Ordinal))
            .Select(entry => entry.Groups["path"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);

        // Порожній реєстр пройшов би цикл вище — і означав би застосунок без
        // адміністрування взагалі.
        Assert.NotEmpty(EntryRegex().Matches(registry));
    }

    /// <summary>Шляхи, оголошені реєстром маршрутів клієнта.</summary>
    private static HashSet<string> DeclaredRoutePaths()
        => EntryRegex()
            .Matches(File.ReadAllText(RoutesPath()))
            .Select(m => m.Groups["path"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string RoutesPath()
        => Path.Combine(RepositoryRoot(), "src", "Ecr.Web", "src", "app", "routes.ts");

    /// <summary>Файли клієнта з такою назвою — шляхами відносно кореня.</summary>
    private static List<string> ClientFiles(string name)
        => SourceTree.Walk(Path.Combine(RepositoryRoot(), "src"))
            .Where(path => string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Модулі клієнта.</summary>
    private static IEnumerable<SourceFile> ClientSources()
        => SourceTree.Walk(Path.Combine(RepositoryRoot(), "src"))
            .Where(path => Path.GetExtension(path) is ".ts" or ".tsx")
            .Select(path => new SourceFile(Relative(path), File.ReadAllText(path)));

    private static string Relative(string path)
        => Path.GetRelativePath(RepositoryRoot(), path).Replace('\\', '/');

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }

    /// <summary>Монтування React у DOM.</summary>
    [GeneratedRegex(@"createRoot\s*\(\s*\w")]
    private static partial Regex MountRegex();

    /// <summary>
    /// Один запис реєстру: від <c>path:</c> до рядка, що закриває САМ запис.
    /// </summary>
    /// <remarks>
    /// ⚠ Кінець — рівно <c>"  },"</c> з двома пробілами: усі вкладені об'єкти
    /// (<c>handle</c>, <c>crumb</c>) закриваються глибше, і «перший-ліпший
    /// <c>},</c>» обрізав би запис на середині — саме там, де стоїть
    /// <c>showInNav</c>, який цей тест і читає.
    /// </remarks>
    [GeneratedRegex(
        @"path:\s*'(?<path>[^']+)',\r?\n(?<tail>(?:.*\r?\n)*?  \},\r?\n)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex EntryRegex();
}
