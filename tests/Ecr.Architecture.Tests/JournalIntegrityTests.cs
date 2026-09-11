using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожен запис журналу (`docs/build/questions/Q-N.md`) несе id/статус/доказ,
/// а генерований індекс (`docs/build/questions.md`) відповідає папці (`H-12`).
/// </summary>
/// <remarks>
/// ⛔ До 2026-09-11 журнал був ОДНИМ файлом: рядок зведення й тіло запису
/// правились окремо, і вже тричі за один пакет розходилися мовчки (каталог
/// помилок, статуси п'яти записів, три числа кодів у трьох місцях). Ліки
/// тоді — сторож, що звіряє зведення з тілами в ТОМУ САМОМУ файлі.
///
/// ⛔ Директива паралельного аудиту (2026-09-11) додала ДРУГУ причину
/// розходження: спільний файл — це спільна точка конфлікту git-мержу для
/// БУДЬ-ЯКИХ двох паралельних задач, незалежно від того, наскільки акуратно
/// кожна з них веде свій запис. Рішення — один запис, один файл
/// (`docs/build/questions/Q-N.md`), а `questions.md` стає ГЕНЕРОВАНИМ
/// індексом (`tools/journal/regen-index.ps1`). Сторож тепер звіряє те саме
/// узгодження, але між частинами ОДНОГО файлу запису (frontmatter проти
/// останнього `**Статус:**` у тілі) і між папкою та індексом (а не між
/// рядком і тілом того самого файлу, як було).
/// </remarks>
public sealed partial class JournalIntegrityTests
{
    /// <summary>Ідентифікатор запису: <c>Q-042</c>, <c>Q-1234</c> — три й більше цифр.</summary>
    /// <remarks>
    /// ⚠ `\d{3,}`, а не рівно `\d{3}`: стара межа мовчки зламалася б на
    /// `Q-1000`. Наскрізна нумерація (`Правило 3`) не обіцяє спинитися на
    /// трьох цифрах.
    /// </remarks>
    [GeneratedRegex(@"^Q-\d{3,}$")]
    private static partial Regex QuestionId();

    /// <summary>YAML-подібний frontmatter на початку файлу запису.</summary>
    [GeneratedRegex(@"\A---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    /// <summary>Один рядок frontmatter: <c>ключ: значення</c>.</summary>
    [GeneratedRegex(@"^(\w+):\s*(.*)$", RegexOptions.Multiline)]
    private static partial Regex FrontmatterLine();

    /// <summary>Рядок статусу в тілі запису (`## Деталі` і глибше).</summary>
    [GeneratedRegex(@"^\*\*Статус:\*\*\s*(OPEN|RESOLVED)\b", RegexOptions.Multiline)]
    private static partial Regex BodyStatus();

    /// <summary>Розділ другого рівня: <c>## Назва</c> до наступного <c>## </c> або кінця файлу.</summary>
    [GeneratedRegex(@"^## (.+?)\s*\n(.*?)(?=\n## |\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Section();

    /// <summary>Рядок зведеної таблиці в індексі: <c>| Q-NNN | ... |</c>.</summary>
    [GeneratedRegex(@"^\|\s*\**\s*(Q-\d{3,})\s*\**\s*\|", RegexOptions.Multiline)]
    private static partial Regex IndexRow();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожен_запис_має_коректний_frontmatter_і_доказ()
    {
        var failures = new List<string>();

        foreach (var file in QuestionFiles())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var text = File.ReadAllText(file);

            var frontmatterMatch = Frontmatter().Match(text);
            if (!frontmatterMatch.Success)
            {
                failures.Add($"{name}: немає frontmatter (--- ... --- на початку файлу).");
                continue;
            }

            var fields = FrontmatterLine().Matches(frontmatterMatch.Groups[1].Value)
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim(), StringComparer.Ordinal);

            if (!fields.TryGetValue("id", out var id) || !QuestionId().IsMatch(id))
            {
                failures.Add($"{name}: frontmatter 'id' відсутній або не має форми Q-NNN.");
            }
            else if (!string.Equals(id, name, StringComparison.Ordinal))
            {
                failures.Add($"{name}: ім'я файлу не збігається з frontmatter id ('{id}').");
            }

            if (!fields.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type))
            {
                failures.Add($"{name}: frontmatter 'type' відсутній.");
            }

            if (!fields.TryGetValue("status", out var frontStatus)
                || frontStatus is not ("OPEN" or "RESOLVED"))
            {
                failures.Add($"{name}: frontmatter 'status' відсутній або не OPEN/RESOLVED.");
                continue;
            }

            // ⛔ "Доказ" — це не порожня секція "Деталі" (сама лише назва
            // проблеми без пояснення, як її закрито, нікому не допоможе), і
            // останній рядок `**Статус:**` у тілі, що узгоджений із
            // frontmatter — так само, як раніше звірялися зведення й тіло в
            // одному файлі.
            var detailsMatch = Section().Matches(text)
                .FirstOrDefault(m => string.Equals(m.Groups[1].Value.Trim(), "Деталі", StringComparison.Ordinal));

            if (detailsMatch is null || string.IsNullOrWhiteSpace(detailsMatch.Groups[2].Value))
            {
                failures.Add($"{name}: секція '## Деталі' відсутня або порожня — немає доказу.");
                continue;
            }

            var bodyStatusMatches = BodyStatus().Matches(detailsMatch.Groups[2].Value);
            if (bodyStatusMatches.Count == 0)
            {
                failures.Add($"{name}: у '## Деталі' немає рядка '**Статус:**'.");
                continue;
            }

            // Береться ОСТАННІЙ — той самий інваріант, що й у старому
            // сторожі: запис міг спершу нести OPEN, а RESOLVED дописали
            // нижче після вирішення.
            var lastBodyStatus = bodyStatusMatches[^1].Groups[1].Value;
            if (lastBodyStatus != frontStatus)
            {
                failures.Add(
                    $"{name}: frontmatter status='{frontStatus}', а останній '**Статус:**' у тілі — '{lastBodyStatus}'.");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожен_файл_запису_є_в_індексі()
    {
        var folderIds = QuestionFiles().Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.Ordinal);
        var indexIds = IndexIds();

        var missing = folderIds.Except(indexIds).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"Файл(и) є в docs/build/questions/, але немає в індексі (запусти tools/journal/regen-index.ps1): "
            + string.Join(", ", missing));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void В_індексі_немає_записів_яких_нема_у_папці()
    {
        var folderIds = QuestionFiles().Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.Ordinal);
        var indexIds = IndexIds();

        var phantom = indexIds.Except(folderIds).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.True(
            phantom.Count == 0,
            $"Індекс містить запис(и), яких немає в docs/build/questions/ (індекс застарів): "
            + string.Join(", ", phantom));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Числа_вимог_у_плані_збігаються_із_заміром()
    {
        // ⛔ План сам вимагає від себе саме цього: «Це зведення **обчислюється**
        // з директив, а не ведеться паралельно їм». Рядок «Стан на дату» цього
        // правила не дотримувався — числа переписували руками, і вони вже
        // розійшлися: 252 · 221 · 27 · 4 у плані проти 253 · 224 · 27 · 3
        // у замірі.
        //
        // ⚠ Розбіжність ховала більше, ніж арифметику. Непокритих стало менше
        // не тому, що вимогу закрили, а тому, що `ФВ-9.15` («адміністратор
        // редагує методології У ВЕБІ») отримала трейт від тестів перевірок
        // публікації — екрана не існує, а матриця показувала покриття.
        var census = RequirementCensus.Take(RepositoryRoot());
        var stated = PlanCensus();

        Assert.Equal(
            $"{census.Declared} · {census.Covered} · {census.Exempt} · {census.Uncovered.Count}",
            stated);
    }

    /// <summary>Усі файли записів журналу.</summary>
    private static IEnumerable<string> QuestionFiles()
        => Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot(), "docs", "build", "questions"), "Q-*.md");

    /// <summary>Ідентифікатори, оголошені в генерованому індексі.</summary>
    private static HashSet<string> IndexIds()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "build", "questions.md"));
        return IndexRow().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Числа з рядка «Стан на дату» плану.</summary>
    /// <remarks>
    /// ⚠ Рядок читається цілком і зводиться до чотирьох чисел, а не
    /// порівнюється дослівно: слова навколо них — це виклад, і правити його
    /// має бути можна без червоного тесту.
    /// </remarks>
    private static string PlanCensus()
    {
        var text = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "docs", "build", "roadmap.md"));

        var row = PlanCensusRow().Match(text);
        Assert.True(row.Success, "У плані немає рядка «Вимог ТЗ (листових)».");

        return string.Join(" · ", row.Groups.Cast<Group>().Skip(1).Select(g => g.Value));
    }

    /// <summary>Рядок плану: <c>| Вимог ТЗ (листових) | 253 · покрито 224 · … |</c>.</summary>
    [GeneratedRegex(
        @"\|\s*Вимог ТЗ \(листових\)\s*\|\s*(\d+)\s*·\s*покрито\s*(\d+)\s*·\s*звільнено\s*(\d+)\s*·\s*\**непокрито\s*(\d+)\**\s*\|")]
    private static partial Regex PlanCensusRow();

    /// <summary>Корінь репозиторію — від каталогу збірки вгору до `docs`.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
