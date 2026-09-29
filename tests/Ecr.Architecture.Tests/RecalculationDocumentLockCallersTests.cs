// tests/Ecr.Architecture.Tests/RecalculationDocumentLockCallersTests.cs
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сторож: лок перерахунку документа (<c>ecr:recalc:doc:{id}</c>) беруть лише
/// задачі перерахунку, і лише через <c>RecalculationDocumentLock</c>.
/// </summary>
/// <remarks>
/// ⛔ Чому це небезпечно розширювати. Лок — сесійний <c>sp_getapplock</c> на
/// ОКРЕМОМУ з'єднанні, а робота задачі йде на іншому. Взаємне очікування між ним
/// і рядковими блокуваннями SQL Server дедлоком не бачить — висіло б до
/// таймауту (15 хв). Безпечно, доки: (1) лок чекають лише задачі
/// <c>RecalculationJob</c> і <c>FormulaRecalculationJob</c>, (2) ПОЗА транзакцією
/// (перевіряє сам <c>RecalculationDocumentLock.AcquireAsync</c>), (3) транзакції
/// запису (PATCH, імпорт, подання, публікація версії шаблону) його не беруть
/// зовсім. Новий викликач — спершу розбір порядку блокувань, потім рядок у
/// <see cref="AllowedCallers"/>.
/// <para>
/// ⚠ Межі, названі прямо: текстовий пошук по <c>src/**/*.cs</c> без рядків-коментарів.
/// Ресурс, зібраний з частин рядка, сторож не побачить; зате виклик
/// <c>SqlDistributedLock.AcquireAsync</c> (очікувальний) поза дозволеним файлом — побачить.
/// </para>
/// </remarks>
public sealed class RecalculationDocumentLockCallersTests
{
    private const string Resource = "ecr:recalc:doc";
    private const string Helper = "RecalculationDocumentLock.AcquireAsync(";
    private const string WaitingAcquire = "SqlDistributedLock.AcquireAsync(";

    private static readonly string[] AllowedCallers = ["RecalculationJob.cs", "FormulaRecalculationJob.cs"];

    private const string HelperFile = "RecalculationDocumentLock.cs";

    [Fact]
    public void Лок_документа_беруть_лише_задачі_перерахунку()
    {
        var src = Path.Combine(SolutionRoot(), "src");
        var callers = new List<string>();
        var wrong = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var code = string.Join('\n', File.ReadAllLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            if (code.Contains(Helper, StringComparison.Ordinal))
            {
                callers.Add(name);
                if (!AllowedCallers.Contains(name, StringComparer.Ordinal))
                {
                    wrong.Add($"{name}: {Helper}");
                }
            }

            if (name != HelperFile && code.Contains(Resource, StringComparison.Ordinal))
            {
                wrong.Add($"{name}: ресурс «{Resource}» поза {HelperFile}");
            }

            if (name != HelperFile && code.Contains(WaitingAcquire, StringComparison.Ordinal))
            {
                wrong.Add($"{name}: очікувальний {WaitingAcquire} поза {HelperFile}");
            }
        }

        // ⚠ Не порожньо: інакше перейменування зробило б сторож вічнозеленим.
        Assert.Equal(AllowedCallers.Order(StringComparer.Ordinal), callers.Order(StringComparer.Ordinal));
        Assert.True(wrong.Count == 0, "Лок перерахунку документа поза дозволеними задачами: " + string.Join("; ", wrong));
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
}
