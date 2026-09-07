using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// У дереві немає незакритих маркерів злиття.
/// </summary>
/// <remarks>
/// ⛔ Сторож з'явився заднім числом: у <c>docs/build/decisions.md</c> на
/// <c>main</c> пролежали три маркери — <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; HEAD</c>,
/// <c>=======</c>, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; feature/mapping-preview-b5</c> — і знайшов їх
/// не сторож, а людина, яка через кілька годин прийшла дописати свій рядок.
///
/// ⚠ Причина, чому це нікого не спинило, важливіша за сам випадок.
/// <see cref="JournalIntegrityTests"/> стереже <c>questions.md</c> і зробив би
/// це негайно; <c>decisions.md</c> у нього не входить, і жоден інший сторож
/// текстових документів не читає. Тобто ціла родина файлів була поза наглядом
/// не за рішенням, а за випадковістю — сторожі писалися під конкретні знахідки.
///
/// ⛔ Маркер у коді видно компіляторові, у документі — нікому. Саме тому
/// перевірка йде по ВСІХ відстежуваних текстових файлах, а не по кількох
/// названих: перелік, який треба доповнювати руками, одного дня відстане.
/// </remarks>
public sealed class ConflictMarkerTests
{
    /// <summary>Розширення, які варто читати як текст.</summary>
    /// <remarks>
    /// ⚠ Двійкові файли пропускаються не для швидкості: у них байти
    /// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> трапляються законно, і сторож давав би хибні
    /// спрацювання доти, доки його не вимкнули б.
    /// </remarks>
    private static readonly string[] Text =
    [
        ".cs", ".md", ".ts", ".tsx", ".json", ".sql", ".ps1", ".yml", ".yaml",
        ".csproj", ".props", ".config", ".txt", ".tsv",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жоден_файл_не_містить_маркерів_злиття()
    {
        var root = SolutionRoot();
        var offenders = new List<string>();

        foreach (var file in Files(root))
        {
            var line = 0;

            foreach (var text in File.ReadLines(file))
            {
                line++;

                // ⚠ Саме ПОЧАТОК рядка і саме сім символів. `=======` як
                // розділювач у таблиці markdown трапляється всередині рядка, а
                // рядок із самих дорівнянь буває підкресленням заголовка —
                // тому середній маркер вимагає ще й порожнечі після себе.
                if (!text.StartsWith("<<<<<<< ", StringComparison.Ordinal)
                    && !text.StartsWith(">>>>>>> ", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{Path.GetRelativePath(root, file)}:{line}");
            }
        }

        // ⛔ Повідомлення несе ФАЙЛ і РЯДОК: «десь є маркер» на дереві з тисяч
        // файлів означає ще один пошук руками — а сторож існує рівно щоб його
        // не робити.
        Assert.Empty(offenders.Order(StringComparer.Ordinal));
    }

    private static IEnumerable<string> Files(string root)
        => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => Text.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains("node_modules", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     // ⚠ Робочі каталоги агентів — це окремі копії дерева з
                     // власними незлитими станами; читати їх означало б
                     // падати на чужій незавершеній роботі.
                     && !f.Contains($"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     // ⛔ Реальні дані замовника (438 МБ) у репозиторій не
                     // потрапляють і читати їх тут нема потреби.
                     && !f.Contains($"{Path.DirectorySeparatorChar}source{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
