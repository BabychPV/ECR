using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Матриця трасування: кожна вимога має тест (<c>D-135</c>).
/// </summary>
/// <remarks>
/// ⛔ Причина — <c>ФВ-9.16c</c>. Вимога існувала в ТЗ, тому всі вважали, що
/// вона існує в коді: вставка з Excel відхиляла зайві знаки цілим батчем,
/// тобто головний шлях введення даних не працював, і 758 тестів цього не
/// бачили. **«Є в ТЗ» гарантує не більше, ніж «закрито» в журналі.**
///
/// ⚠ Рахуються ЛИШЕ листові ідентифікатори — ті, що містять крапку. Групові
/// (<c>ФВ-7</c>) — це заголовки розділів, вони тесту не потребують. Без цього
/// правила знаменник у різних людей був би різний, і «нуль непокритих»
/// означало б різні речі.
///
/// ⚠ Джерело істини — <c>docs/tz/02-requirements.md</c>, і тільки він. Згадки
/// в <c>build/</c> і <c>reference/</c> — цитати, не оголошення.
/// </remarks>
public sealed partial class RequirementTraceTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_вимога_має_тест_або_явне_звільнення()
    {
        var declared = Declared();
        var covered = Covered();
        var exempt = Exempt();

        var orphaned = covered.Keys.Except(declared, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var uncovered = declared
            .Except(covered.Keys, StringComparer.Ordinal)
            .Except(exempt.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Write(declared, covered, exempt, uncovered, orphaned);

        // ⛔ Осиротілі — окрема і не менш важлива множина: тест, який заявляє
        // покриття НЕІСНУЮЧОЇ вимоги, дає хибне відчуття, що щось перевірено.
        // Найчастіше це друкарська помилка в ідентифікаторі.
        Assert.Empty(orphaned);

        // ⚠ Звільнення, що посилається на неіснуючу вимогу, — теж брехня:
        // хтось вважає її закритою, а її немає.
        Assert.Empty(exempt.Keys.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal));

        Assert.Empty(uncovered);
    }

    /// <summary>Листові вимоги з ТЗ.</summary>
    private static HashSet<string> Declared()
    {
        var text = File.ReadAllText(
            Path.Combine(SolutionRoot(), "docs", "tz", "02-requirements.md"));

        return RequirementRegex.Matches(text)
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Вимога → тести, що її заявляють.</summary>
    /// <remarks>
    /// ⛔ Обидва вирази прив'язані до ПОЗИЦІЇ — атрибут і початок заголовка.
    /// Згадка <c>ФВ</c> у коментарі покриттям не рахується, інакше покриття
    /// можна «досягти» коментарем.
    /// </remarks>
    private static Dictionary<string, List<string>> Covered()
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(string requirement, string where)
        {
            if (!found.TryGetValue(requirement, out var list))
            {
                list = [];
                found[requirement] = list;
            }

            list.Add(where);
        }

        foreach (var file in Sources(Path.Combine(SolutionRoot(), "tests"), ".cs"))
        {
            foreach (Match match in NetTraitRegex.Matches(File.ReadAllText(file)))
            {
                Add(match.Groups[1].Value, Path.GetFileName(file));
            }
        }

        var web = Path.Combine(SolutionRoot(), "src", "Ecr.Web");
        foreach (var file in Sources(web, ".ts").Concat(Sources(web, ".tsx")))
        {
            if (file.Contains("node_modules", StringComparison.Ordinal))
            {
                continue;
            }


            foreach (Match match in WebTitleRegex.Matches(File.ReadAllText(file)))
            {
                Add(match.Groups[1].Value, Path.GetFileName(file));
            }
        }

        return found;
    }

    /// <summary>Звільнені вимоги: ідентифікатор → причина.</summary>
    private static Dictionary<string, string> Exempt()
    {
        var path = Path.Combine(SolutionRoot(), "contracts", "trace-exempt.md");
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return ExemptRegex.Matches(File.ReadAllText(path))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim(), StringComparer.Ordinal);
    }

    /// <summary>Пише артефакт; він поза `docs/` — там `CHECKSUMS.txt`.</summary>
    private static void Write(
        HashSet<string> declared,
        Dictionary<string, List<string>> covered,
        Dictionary<string, string> exempt,
        List<string> uncovered,
        List<string> orphaned)
    {
        var report = new StringBuilder();
        report.Append("# Матриця трасування: вимоги\n\n");
        report.Append("> Згенеровано `RequirementTraceTests`. Руками не правити.\n\n");
        report.Append(CultureInfo.InvariantCulture, $"| Усього листових вимог | {declared.Count} |\n");
        report.Append("|---|---|\n");
        report.Append(CultureInfo.InvariantCulture, $"| Покрито тестами | {declared.Intersect(covered.Keys, StringComparer.Ordinal).Count()} |\n");
        report.Append(CultureInfo.InvariantCulture, $"| Звільнено | {exempt.Count} |\n");
        report.Append(CultureInfo.InvariantCulture, $"| **Непокрито** | **{uncovered.Count}** |\n");
        report.Append(CultureInfo.InvariantCulture, $"| Осиротілих посилань | {orphaned.Count} |\n\n");

        report.Append("## Непокриті\n\n");
        report.Append(uncovered.Count == 0
            ? "Немає.\n\n"
            : string.Join("\n", uncovered.Select(r => $"- `{r}`")) + "\n\n");

        if (orphaned.Count > 0)
        {
            report.Append("## Осиротілі посилання\n\n");
            report.Append(string.Join("\n", orphaned.Select(r => $"- `{r}`")) + "\n\n");
        }

        report.Append("## Покриті\n\n| Вимога | Тести |\n|---|---|\n");
        foreach (var pair in covered.OrderBy(p => Order(p.Key)))
        {
            report.Append(CultureInfo.InvariantCulture, $"| `{pair.Key}` | {string.Join(", ", pair.Value.Distinct().Order(StringComparer.Ordinal))} |\n");
        }

        if (exempt.Count > 0)
        {
            report.Append("\n## Звільнені\n\n| Вимога | Причина |\n|---|---|\n");
            foreach (var pair in exempt.OrderBy(p => Order(p.Key)))
            {
                report.Append(CultureInfo.InvariantCulture, $"| `{pair.Key}` | {pair.Value} |\n");
            }
        }

        var target = Path.Combine(SolutionRoot(), "artifacts", "trace", "requirements.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, report.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Порядок як у ТЗ: група, номер, літера.</summary>
    private static (int, int, string) Order(string requirement)
    {
        var match = OrderRegex.Match(requirement);

        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), match.Groups[3].Value);
    }

    private static IEnumerable<string> Sources(string root, string extension)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, $"*{extension}", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains("node_modules", StringComparison.Ordinal))
            : [];

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }

    /// <summary>Листовий ідентифікатор вимоги — обов'язково з крапкою.</summary>
    [GeneratedRegex(@"ФВ-\d+\.\d+[a-z]?")]
    private static partial Regex RequirementRegex { get; }

    /// <summary>Трейт .NET; ключ ASCII — кирилиця в `--filter` поводиться непередбачувано.</summary>
    [GeneratedRegex(@"\[Trait\(\s*""Requirement""\s*,\s*""(ФВ-\d+\.\d+[a-z]?)""\s*\)\]")]
    private static partial Regex NetTraitRegex { get; }

    /// <summary>Заголовок тесту клієнта: ідентифікатор до двокрапки.</summary>
    [GeneratedRegex(@"(?m)^\s*(?:it|test)(?:\.each\([^)]*\))?\(\s*['""`](ФВ-\d+\.\d+[a-z]?):")]
    private static partial Regex WebTitleRegex { get; }

    /// <summary>Рядок звільнення: `| ФВ-x.y | причина |`.</summary>
    [GeneratedRegex(@"(?m)^\|\s*`?(ФВ-\d+\.\d+[a-z]?)`?\s*\|([^|]+)\|")]
    private static partial Regex ExemptRegex { get; }

    [GeneratedRegex(@"ФВ-(\d+)\.(\d+)([a-z]?)")]
    private static partial Regex OrderRegex { get; }
}
