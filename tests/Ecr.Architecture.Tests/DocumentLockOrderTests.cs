// tests/Ecr.Architecture.Tests/DocumentLockOrderTests.cs
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сторож єдиного порядку блокувань «спершу стани аркушів, потім рядок
/// документа»: у файлі, що бере обидва, виклик
/// <c>.LockWorkflowFactsAsync(</c> стоїть раніше за <c>.FindForUpdateAsync(</c>.
/// </summary>
/// <remarks>
/// ⛔ Зворотний порядок — дедлок 1205 із поданням і видаленням документа
/// (<c>DocumentLockOrderDeadlockTests</c>, Infrastructure). Поведінковий тест
/// ловить його для двох нинішніх обробників; цей сторож — для наступного,
/// хто візьме обидва замки в новому файлі.
///
/// ⚠ Межі, названі прямо: порівнюється ПОРЯДОК ПЕРШИХ ВИКЛИКІВ у файлі (без
/// розбору методів) і лише ці два методи; рядки-коментарі (<c>//</c>, <c>///</c>)
/// відкидаються, щоб згадка в поясненні не зсувала позицію. Файл із кількома
/// методами, що беруть обидва замки в різному порядку, сторож не розрізнить.
/// </remarks>
public sealed class DocumentLockOrderTests
{
    private const string StatesLock = ".LockWorkflowFactsAsync(";
    private const string DocumentLock = ".FindForUpdateAsync(";

    [Fact]
    public void Хто_бере_стани_й_документ_бере_спершу_стани()
    {
        var src = Path.Combine(SolutionRoot(), "src");
        var both = new List<string>();
        var wrong = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var code = string.Join('\n', File.ReadAllLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            var states = code.IndexOf(StatesLock, StringComparison.Ordinal);
            var document = code.IndexOf(DocumentLock, StringComparison.Ordinal);
            if (states < 0 || document < 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(src, file);
            both.Add(relative);
            if (document < states)
            {
                wrong.Add(relative);
            }
        }

        // ⚠ Не порожньо: інакше перейменування методу мовчки зробило б сторож вічнозеленим.
        Assert.Contains(both, f => f.EndsWith("DocumentHeaderHandlers.cs", StringComparison.Ordinal));
        Assert.Contains(both, f => f.EndsWith("ChangeDocumentKeyHandler.cs", StringComparison.Ordinal));
        Assert.True(
            wrong.Count == 0,
            "Рядок документа блокується раніше за стани аркушів (дедлок із поданням): " + string.Join(", ", wrong));
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
