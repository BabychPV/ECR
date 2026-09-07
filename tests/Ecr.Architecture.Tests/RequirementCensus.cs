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
    /// <summary>Будь-яка ЗГАДКА листового ідентифікатора вимоги.</summary>
    /// <remarks>
    /// ⚠ Групові (<c>ФВ-7</c>) — заголовки розділів, тесту не потребують.
    /// Без цього правила знаменник у різних людей був би різний.
    /// </remarks>
    private static readonly Regex Mentioned = new(@"ФВ-\d+\.\d+[a-z]?", RegexOptions.Compiled);

    /// <summary>
    /// Рядок, яким вимога <b>оголошується</b>: <c>**ФВ-9.4** Порядок формул…</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Знаменник рахується з ВИЗНАЧЕНЬ, а не зі згадок, і це не педантизм.
    /// Зі згадок виходило <b>254</b>, і зайвим був <c>ФВ-9.11a</c> — не
    /// вимога, а старий ідентифікатор <c>B18</c> із СЕРЕДНЬОЇ колонки таблиці
    /// походження (<c>02-requirements.md:1311</c>), тобто те, на що цю вимогу
    /// колись замінили. Знаменник, у якому є неіснуюча вимога, робить
    /// відсоток покриття неправдивим у кожному звіті, де він з'являється.
    ///
    /// ⚠ Викинути всю таблицю походження не можна: у тій самій колонці стоять
    /// і чинні ідентифікатори. Розрізняє їх лише наявність власного
    /// визначення.
    /// </remarks>
    private static readonly Regex Declaration = new(
        @"(?m)^\*\*(ФВ-\d+\.\d+[a-z]?)[ *]", RegexOptions.Compiled);

    /// <summary>
    /// Ідентифікатори, які згадані в ТЗ навмисно і вимогами <b>не є</b>.
    /// </summary>
    /// <remarks>
    /// ⛔ Перелік ОГОЛОШЕНИЙ, а не виведений з правила. `ФВ-9.11a` — старий
    /// ідентифікатор `B18` §14.3 із середньої колонки таблиці походження
    /// (<c>02-requirements.md:1311</c>): вимогу «числова сумісність» замінили
    /// на <c>ФВ-9.9</c> плюс <c>ФВ-9.17</c>, і рядок таблиці про це й
    /// розповідає. У ТЗ він потрібен; у знаменнику покриття — ні.
    ///
    /// ⚠ Виняток саме поіменний, а не «пропускати всю таблицю походження»: у
    /// тій самій колонці стоять і чинні ідентифікатори (<c>ФВ-6.8</c>,
    /// <c>ФВ-2.10</c>, <c>ФВ-15.4</c>), і правило «пропускати таблицю»
    /// сховало б і їх.
    ///
    /// ⛔ Сам файл ТЗ не правиться: він накритий <c>docs/CHECKSUMS.txt</c>, і
    /// це документ замовника. Дефект був у ЛІЧИЛЬНИКУ, а не в документі.
    /// </remarks>
    private static readonly HashSet<string> Historical =
        new(StringComparer.Ordinal) { "ФВ-9.11a" };

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

        var text = File.ReadAllText(Path.Combine(root, "docs", "tz", "02-requirements.md"));

        var declared = Declaration.Matches(text)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // ⚠ Згадана, але ніде не оголошена — це або одрук, або ідентифікатор
        // із чужої нумерації. Перелік іде назовні, а не гасне тут: саме він
        // знайшов `ФВ-9.11a` у знаменнику.
        var phantom = Mentioned.Matches(text)
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal)
            .Except(declared, StringComparer.Ordinal)
            .Except(Historical, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var covered = Covered(root).Intersect(declared, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var exempt = ExemptSet(root).Intersect(declared, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        // ⛔ Кошики НЕПЕРЕСІЧНІ за побудовою, і перетин виноситься назовні, а
        // не вирішується мовчки. Доти `Exempt` рахувався сирим переліком, і
        // `ФВ-6.15a` із `ФВ-9.3` потрапляли одночасно в «покрито» і
        // «звільнено»: `229 + 27 = 256` при 254 оголошених. Арифметика, яка не
        // сходиться, — єдине, що про це говорило, і жоден сторож її не питав.
        //
        // ⚠ Обрати за людину, що з двох станів правильний, тут неможливо:
        // «звільнено» означає «кодом не перевіряється», «покрито» — протилежне.
        // Тому перетин повертається переліком, а сторож на ньому падає.
        var contested = covered.Intersect(exempt, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var uncovered = declared
            .Except(covered, StringComparer.Ordinal)
            .Except(exempt, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new Census(
            declared.Count,
            covered.Except(exempt, StringComparer.Ordinal).Count(),
            exempt.Count,
            uncovered,
            phantom,
            contested);
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
/// <param name="Declared">Листових вимог, ОГОЛОШЕНИХ у ТЗ власним визначенням.</param>
/// <param name="Covered">Заявлено покритими і не звільнених.</param>
/// <param name="Exempt">Звільнено з поясненням.</param>
/// <param name="Uncovered">Непокриті — поіменно, а не числом.</param>
/// <param name="Phantom">Згадані в ТЗ, але ніде не оголошені.</param>
/// <param name="Contested">Одночасно покриті і звільнені — суперечність.</param>
/// <remarks>
/// ⛔ <c>Declared == Covered + Exempt + Uncovered</c> — тотожність, а не
/// побажання: три кошики непересічні за побудовою. Доти, доки вона не
/// трималася, зведення можна було читати як завгодно.
/// </remarks>
internal sealed record Census(
    int Declared,
    int Covered,
    int Exempt,
    IReadOnlyList<string> Uncovered,
    IReadOnlyList<string> Phantom,
    IReadOnlyList<string> Contested);
