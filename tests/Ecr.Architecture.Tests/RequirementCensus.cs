using System.Text.RegularExpressions;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Перепис вимог: скільки оголошено, покрито і звільнено.
/// </summary>
/// <remarks>
/// ⛔ Клас винесений заради ОДНОГО місця, де ці числа рахуються. Доти їх було
/// два: <see cref="RequirementTraceTests"/> обчислював їх у прогоні, а
/// <c>docs/build/roadmap.md</c> і <c>docs/build/pk1-handover.md</c> тримали
/// переписані руками — і вже розійшлися: у плані стояло
/// <b>252 · 221 · 27 · 4</b>, тоді як замір давав <b>253 · 224 · 27 · 3</b>.
///
/// ⚠ Розбіжність була не косметична. Серед «покритих» опинилася
/// <c>ФВ-9.15</c> — «адміністратор редагує методології <b>у вебі</b>», — бо
/// тести перевірок публікації взяли її трейт. Екрана не існує (це крок
/// <c>III.1</c>), і непокритих мовчки стало на одну менше. Саме проти таких
/// «покриттів» написаний <see cref="RequirementTraceTests"/>, і саме він їх
/// не бачить: він питає, чи є трейт, а не чи правда те, що трейт каже.
/// </remarks>
internal static class RequirementCensus
{
    /// <summary>Листовий ідентифікатор вимоги — обов'язково з крапкою.</summary>
    /// <remarks>
    /// ⚠ Групові (<c>ФВ-7</c>) — заголовки розділів, тесту не потребують.
    /// Без цього правила знаменник у різних людей був би різний.
    /// </remarks>
    private static readonly Regex Requirement = new(@"ФВ-\d+\.\d+[a-z]?", RegexOptions.Compiled);

    /// <summary>Трейт .NET, що заявляє покриття.</summary>
    private static readonly Regex NetTrait = new(
        @"\[Trait\(\s*""Requirement""\s*,\s*""(ФВ-\d+\.\d+[a-z]?)""\s*\)\]", RegexOptions.Compiled);

    /// <summary>Заголовок клієнтського тесту: ідентифікатор до двокрапки.</summary>
    private static readonly Regex WebTitle = new(
        @"(?m)^\s*(?:it|test)(?:\.each\([^)]*\))?\(\s*['""`](ФВ-\d+\.\d+[a-z]?):", RegexOptions.Compiled);

    /// <summary>Рядок звільнення: <c>| ФВ-x.y | причина |</c>.</summary>
    private static readonly Regex Exempt = new(
        @"(?m)^\|\s*`?(ФВ-\d+\.\d+[a-z]?)`?\s*\|", RegexOptions.Compiled);

    /// <summary>Рахує вимоги від кореня рішення.</summary>
    /// <param name="root">Корінь репозиторію.</param>
    /// <returns>Оголошені, покриті, звільнені й непокриті.</returns>
    public static Census Take(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var declared = Requirement
            .Matches(File.ReadAllText(Path.Combine(root, "docs", "tz", "02-requirements.md")))
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

        var covered = Covered(root);
        var exempt = ExemptSet(root);

        var uncovered = declared
            .Except(covered, StringComparer.Ordinal)
            .Except(exempt, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new Census(
            declared.Count,
            declared.Intersect(covered, StringComparer.Ordinal).Count(),
            exempt.Count,
            uncovered);
    }

    /// <summary>Вимоги, покриття яких заявлено трейтом або заголовком тесту.</summary>
    private static HashSet<string> Covered(string root)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Sources(Path.Combine(root, "tests"), ".cs"))
        {
            foreach (Match match in NetTrait.Matches(File.ReadAllText(file)))
            {
                found.Add(match.Groups[1].Value);
            }
        }

        var web = Path.Combine(root, "src", "Ecr.Web");
        foreach (var file in Sources(web, ".ts").Concat(Sources(web, ".tsx")))
        {
            foreach (Match match in WebTitle.Matches(File.ReadAllText(file)))
            {
                found.Add(match.Groups[1].Value);
            }
        }

        return found;
    }

    /// <summary>Звільнені вимоги.</summary>
    private static HashSet<string> ExemptSet(string root)
    {
        var path = Path.Combine(root, "contracts", "trace-exempt.md");

        return File.Exists(path)
            ? Exempt.Matches(File.ReadAllText(path))
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private static IEnumerable<string> Sources(string root, string extension)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, $"*{extension}", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains("node_modules", StringComparison.Ordinal))
            : [];
}

/// <summary>Результат перепису.</summary>
/// <param name="Declared">Листових вимог у ТЗ.</param>
/// <param name="Covered">Із них заявлено покритими.</param>
/// <param name="Exempt">Звільнено з поясненням.</param>
/// <param name="Uncovered">Непокриті — поіменно, а не числом.</param>
internal sealed record Census(
    int Declared, int Covered, int Exempt, IReadOnlyList<string> Uncovered);
