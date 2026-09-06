using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Зведення журналів має збігатися з тілами записів (<c>H-12</c>).
/// </summary>
/// <remarks>
/// ⛔ Третій випадок того самого класу за один пакет, і тому він закривається
/// машиною, а не обіцянкою:
///
/// <list type="number">
/// <item><description>каталог помилок розійшовся у три боки — контракт 48,
/// код 44, кидається 55 (<c>H-5</c>);</description></item>
/// <item><description>зведення <c>questions.md</c> позначало п'ять записів
/// не тим статусом, що їхні тіла, і не мало <c>Q-063</c>
/// взагалі;</description></item>
/// <item><description><c>problems.md</c> називав три різні числа кодів у
/// трьох місцях.</description></item>
/// </list>
///
/// ⚠ Причина щоразу одна: **зведену таблицю правлять окремо від запису**, і
/// вона розходиться мовчки. Ліки теж одні — зведення має обчислюватися, а
/// доки воно ведеться руками, за ним має стежити сторож.
/// </remarks>
public sealed partial class JournalIntegrityTests
{
    /// <summary>Заголовок запису: <c>### Q-042 · CONFLICT · …</c>.</summary>
    [GeneratedRegex(@"^### (Q-\d{3})\b", RegexOptions.Multiline)]
    private static partial Regex EntryHeading();

    /// <summary>Рядок статусу в тілі запису.</summary>
    [GeneratedRegex(@"^\*\*Статус:\*\*\s*(OPEN|RESOLVED)\b", RegexOptions.Multiline)]
    private static partial Regex EntryStatus();

    /// <summary>Рядок зведеної таблиці.</summary>
    [GeneratedRegex(@"^\|\s*\**\s*(Q-\d{3})\s*\**\s*\|(.*)\|\s*$", RegexOptions.Multiline)]
    private static partial Regex SummaryRow();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Статус_у_зведенні_збігається_зі_статусом_у_тілі_запису()
    {
        var text = Journal();
        var summary = SummaryStatuses(text);
        var bodies = BodyStatuses(text);

        // ⛔ Порівнюються лише ті записи, що є в обох місцях: відсутність у
        // таблиці ловить окремий сторож нижче, і зливати дві причини в одне
        // падіння означало б показувати другу замість першої.
        var drifted = bodies
            .Where(pair => summary.TryGetValue(pair.Key, out var declared)
                           && declared != pair.Value)
            .Select(pair => $"{pair.Key}: тіло каже {pair.Value}, зведення — {summary[pair.Key]}")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(drifted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожен_запис_журналу_є_у_зведенні()
    {
        // ⚠ Запис, якого немає в таблиці, гірший за запис із неправильним
        // статусом: перший читач не побачить узагалі. Саме так `Q-063` —
        // єдине відкрите питання журналу — не потрапляв у жоден перелік.
        var text = Journal();
        var summary = SummaryStatuses(text);

        var missing = BodyStatuses(text).Keys
            .Where(id => !summary.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void У_зведенні_немає_записів_яких_не_існує()
    {
        // ⚠ Зворотний напрям, і він не менш важливий: рядок про запис, якого
        // немає, обіцяє читачеві історію, за якою нікуди піти.
        var text = Journal();
        var bodies = BodyStatuses(text);

        var phantom = SummaryStatuses(text).Keys
            .Where(id => !bodies.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(phantom);
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

    /// <summary>Текст журналу питань.</summary>
    private static string Journal()
        => File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "build", "questions.md"));

    /// <summary>Статуси, оголошені у зведеній таблиці.</summary>
    private static Dictionary<string, string> SummaryStatuses(string text)
    {
        var start = text.IndexOf("## Зведення", StringComparison.Ordinal);
        var end = text.IndexOf("## Записи", StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "У журналі немає розділів «Зведення» і «Записи».");

        var table = text[start..end];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match row in SummaryRow().Matches(table))
        {
            // ⚠ Статус — в ОСТАННІЙ колонці, і читається він як слово, а не як
            // підрядок: «RESOLVED · рішення людини» і «OPEN — потрібне
            // підтвердження» мають дати те саме, що голі `RESOLVED` і `OPEN`.
            var cells = row.Groups[2].Value.Split('|');
            var status = cells.Length > 0 ? cells[^1] : string.Empty;

            result[row.Groups[1].Value] =
                status.Contains("OPEN", StringComparison.Ordinal) ? "OPEN" : "RESOLVED";
        }

        return result;
    }

    /// <summary>Статуси, оголошені в тілах записів.</summary>
    private static Dictionary<string, string> BodyStatuses(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var headings = EntryHeading().Matches(text);

        for (var i = 0; i < headings.Count; i++)
        {
            var from = headings[i].Index;
            var to = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;

            // ⛔ Береться ОСТАННІЙ статус запису, а не перший: `Q-042` свого
            // часу ніс два рядки — `RESOLVED`, а нижче `OPEN`, — і саме
            // останній був чинним. Перший дав би протилежну відповідь.
            var statuses = EntryStatus().Matches(text[from..to]);
            if (statuses.Count > 0)
            {
                result[headings[i].Groups[1].Value] = statuses[^1].Groups[1].Value;
            }
        }

        return result;
    }

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
