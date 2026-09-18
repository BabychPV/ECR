using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Парольна механіка чинного рішення в продукт НЕ переноситься
/// (<c>ФВ-2.19</c>).
/// </summary>
/// <remarks>
/// ⛔ Ця вимога до 2026-09-18 стояла в <c>contracts/trace-exempt.md</c> із
/// причиною «відсутність механізму перевіряється відсутністю коду, а не
/// тестом». Це неправда рівно в цьому репозиторії: тут уже є сторожі, які
/// стверджують саме про ТЕКСТ джерел
/// (<see cref="ErrorTitleCatalogTests"/>, <see cref="ForbiddenApiTests"/>),
/// і негативне твердження «цього в коді немає» пишеться ними дешево. А
/// «перевіряється відсутністю коду» означає «перевіряється тим, що ніхто
/// не подивився»: саме так у чинному рішенні й з'явився пароль у відкритому
/// тексті — його теж ніхто не додавав навмисно.
///
/// ⚠ Голка розібрана на дві частини (<see cref="LegacySecret"/>), щоб сам
/// сторож не був єдиним місцем у дереві, де цей пароль записаний цілим
/// рядком: інакше перший же пошук секретів по репозиторію знаходив би його
/// тут і вважав знахідкою.
/// </remarks>
public sealed class LegacyPasswordTests
{
    /// <summary>Пароль аркушів чинного рішення — зібраний, не записаний.</summary>
    private static string LegacySecret => "1qaz" + "xcde3";

    /// <summary>Форма VBA, що його питала.</summary>
    private const string LegacyForm = "frmPassword";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-2.19")]
    public void Пароль_чинного_рішення_не_потрапив_у_жоден_файл_продукту()
    {
        var offenders = ShippedFiles()
            .Where(file => file.Text.Contains(LegacySecret, StringComparison.OrdinalIgnoreCase)
                        || file.Text.Contains(LegacyForm, StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-2.19")]
    public void Голка_сторожа_справді_зустрічається_в_описі_чинного_рішення()
    {
        // ⛔ Без цієї перевірки сторож вище лишався б зеленим і тоді, коли
        // шукає рядок, якого не існує ніде, — одрук у голці перетворив би
        // його на перевірку порожнечі. Опис чинного рішення
        // (`docs/reference/as-is/`) цитує і пароль, і форму навмисно: це
        // документ про те, ЩО МИ ЗАМІНЮЄМО, і саме там голка мусить
        // знаходитися.
        var asIs = Directory
            .EnumerateFiles(
                Path.Combine(RepositoryRoot(), "docs", "reference", "as-is"),
                "*.md",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        Assert.Contains(asIs, text => text.Contains(LegacySecret, StringComparison.Ordinal));
        Assert.Contains(asIs, text => text.Contains(LegacyForm, StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-2.19")]
    public void Захист_структури_не_має_власного_пароля()
    {
        // ⚠ Друга половина `ФВ-2.19`: «захист структури забезпечується
        // правами, а не спільним паролем у коді». Пароль користувача
        // (`Entities/Security/User.PasswordHash`) — інша річ і лишається;
        // заборонено саме поле пароля на СТРУКТУРІ — шаблоні, аркуші,
        // таблиці, періоді. Воно й було б прямим перенесенням
        // `ToggleProtection` із чинного рішення.
        var configuration = Path.Combine(
            RepositoryRoot(), "src", "Ecr.Domain", "Entities", "Configuration");

        var offenders = Directory
            .EnumerateFiles(configuration, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => new SourceFile(
                    Path.GetRelativePath(RepositoryRoot(), path).Replace('\\', '/'),
                    File.ReadAllText(path))
                .CodeLines()
                .Where(line => line.Text.Contains("Password", StringComparison.OrdinalIgnoreCase))
                .Select(line => $"{Path.GetFileName(path)}:{line.Line} {line.Text.Trim()}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>Усе, що постачається користувачеві: код, розмітка, схема, сід.</summary>
    /// <remarks>
    /// ⚠ Саме <c>src/</c>, а не весь репозиторій: <c>docs/reference/as-is/</c>
    /// описує чинне рішення і зобов'язаний називати і пароль, і форму —
    /// заборонити їх там означало б заборонити документувати те, від чого ми
    /// йдемо.
    /// </remarks>
    private static IEnumerable<SourceFile> ShippedFiles()
    {
        foreach (var path in SourceTree.Walk(Path.Combine(RepositoryRoot(), "src")))
        {
            if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new SourceFile(
                Path.GetRelativePath(RepositoryRoot(), path).Replace('\\', '/'),
                File.ReadAllText(path));
        }
    }

    /// <summary>Розширення, у яких пароль міг би опинитися осмислено.</summary>
    private static readonly string[] Extensions =
        [".cs", ".ts", ".tsx", ".js", ".jsx", ".sql", ".json", ".html", ".css", ".ps1", ".razor", ".config"];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
