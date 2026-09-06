using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Клієнт не вигадує кодів помилок (<c>ФВ-14.9a</c>, <c>P-25</c> рядок 6).
/// </summary>
/// <remarks>
/// ⛔ Сторож існує тому, що <c>ContractIntegrityTests</c> дивиться лише в
/// <c>src/**.cs</c>. Код у <c>.tsx</c> для нього не існує — і саме там прожив
/// <c>ECR-PER-0409</c>: родини <c>PER</c> в каталозі немає, код вигадали для
/// показу компонента <c>ErrorAlert</c> на демо-сторінці. Клієнт демонстрував
/// помилку, якої сервер не віддасть ніколи.
///
/// ⚠ Шкода не в самій демо-сторінці. Формат <c>ECR-&lt;ДОМЕН&gt;-&lt;HTTP&gt;</c>
/// — це **маршрут**: за родиною клієнт вирішує, у який обробник віддати
/// відмову. Вигаданий код у прикладі стає зразком, який копіюють, і копія
/// потрапляє вже в робочий шлях.
///
/// ⚠ Плейсхолдер у демонстрації потрібен, і він дозволений — але **не з
/// каталогу і не схожий на нього**: <c>HTTP-500</c>, тобто рівно та форма, яку
/// <c>client.ts</c> породжує сам у <c>problemOf</c>, коли відповідь не є
/// <c>problem+json</c>. Вона очевидно не з каталогу, і показувати їй є що.
/// </remarks>
public sealed partial class ClientErrorCodeTests
{
    /// <summary>Код помилки в тексті клієнта.</summary>
    /// <remarks>
    /// ⚠ Той самий вираз, що й у <c>ContractIntegrityTests</c>: два різні
    /// поняття «коду» дали б два різні переліки, і розбіжність між ними ніхто
    /// б не побачив.
    /// </remarks>
    [GeneratedRegex(@"ECR-[A-Z]{3,4}-\d{4}")]
    private static partial Regex ErrorCode();

    /// <summary>Оголошення коду в каталозі домену.</summary>
    [GeneratedRegex(@"""(ECR-[A-Z]{3,4}-\d{4})""")]
    private static partial Regex CatalogEntry();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Клієнт_не_згадує_кодів_яких_немає_в_каталозі()
    {
        var root = SolutionRoot();

        var catalog = CatalogEntry()
            .Matches(File.ReadAllText(Path.Combine(
                root, "src", "Ecr.Domain", "Errors", "ErrorCodes.cs")))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(catalog);

        var unknown = new List<string>();
        var web = Path.Combine(root, "src", "Ecr.Web", "src");

        foreach (var file in Sources(web))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in ErrorCode().Matches(text))
            {
                if (!catalog.Contains(match.Value))
                {
                    unknown.Add($"{Path.GetFileName(file)}: {match.Value}");
                }
            }
        }

        // ⛔ Повідомлення несе ФАЙЛ і КОД: «клієнт згадує невідомий код» не
        // сказало б, чи це друкарська помилка в робочому шляху, чи вигаданий
        // приклад у демонстрації, — а це різні речі й лікуються по-різному.
        Assert.Empty(unknown.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>Файли клієнта, які варто читати.</summary>
    private static IEnumerable<string> Sources(string root)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".ts", StringComparison.Ordinal)
                         || f.EndsWith(".tsx", StringComparison.Ordinal))
                .Where(f => !f.Contains("node_modules", StringComparison.Ordinal))
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
}
