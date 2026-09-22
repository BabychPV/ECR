using System.Text.RegularExpressions;
using Ecr.Application.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожне поле порівняння версій методології (<see cref="MethodologyDiffFields"/>)
/// має рядок каталогу <c>methodologyDiffField.&lt;поле&gt;</c> у сіді І гілку з
/// цим ключем у клієнтському <c>DiffFieldLabel.tsx</c>.
/// </summary>
/// <remarks>
/// ⛔ Напрям «сервер → клієнт», як у <see cref="UsageKindCatalogTests"/>:
/// <c>EndpointCoverageTests</c> вимагає рядок сіду лише для ключів, яких клієнт
/// ПРОСИТЬ. Поле, якого клієнт не знає, мовчки падає в <c>default</c> і
/// показується сирим значенням — саме так усі 13 полів жили в
/// <c>VersionDiffModal</c> до цього сторожа.
///
/// ⚠ Клієнтський файл читається без коментарів: ключ, названий у коментарі, не
/// є гілкою <c>switch</c>. Перевіряється і <c>case '&lt;поле&gt;'</c>, і
/// літерал <c>t('methodologyDiffField.&lt;поле&gt;')</c> — саме літерал, щоб
/// його бачив і сторож каталогу.
/// </remarks>
public sealed partial class MethodologyDiffFieldCatalogTests
{
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    private const string ClientFile = "src/Ecr.Web/src/features/methodologies/DiffFieldLabel.tsx";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_поле_порівняння_методології_має_ключ_каталогу_в_сіді_і_в_клієнті()
    {
        // ⛔ Порожній перелік (рефлексія перестала бачити поля) дав би зелене.
        Assert.NotEmpty(MethodologyDiffFields.All);

        var seeded = SeedKeys();
        Assert.NotEmpty(seeded);

        var clientPath = Path.Combine(SourceTree.Root, ClientFile.Replace('/', Path.DirectorySeparatorChar));
        var clientExists = File.Exists(clientPath);
        var client = clientExists ? StripComments(File.ReadAllText(clientPath)) : string.Empty;

        var problems = new List<string>();
        foreach (var field in MethodologyDiffFields.All.Order(StringComparer.Ordinal))
        {
            var key = $"methodologyDiffField.{field}";
            if (!seeded.Contains(key))
            {
                problems.Add($"  {field}: у {SeedFile} немає рядка {key}");
            }

            if (!client.Contains($"case '{field}'", StringComparison.Ordinal))
            {
                problems.Add($"  {field}: у {ClientFile} немає гілки case '{field}'");
            }

            if (!client.Contains($"t('{key}')", StringComparison.Ordinal))
            {
                problems.Add($"  {field}: у {ClientFile} немає літерала t('{key}')");
            }
        }

        Assert.True(
            problems.Count == 0,
            (clientExists ? string.Empty : $"Файлу {ClientFile} немає.{Environment.NewLine}")
            + "Поле порівняння версій (MethodologyDiffFields) без назви — клієнт показав би його сирим значенням. "
            + "Заведи рядок каталогу англійською в 09-seed.sql і гілку в DiffFieldLabel.tsx:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, problems));
    }

    /// <summary>Ключі блоку <c>MERGE sys_ecr.UiString AS t</c>.</summary>
    private static HashSet<string> SeedKeys()
    {
        var text = File.ReadAllText(
            Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar)));

        var start = text.IndexOf("MERGE sys_ecr.UiString AS t", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У {SeedFile} немає блоку MERGE sys_ecr.UiString AS t.");

        var end = text.IndexOf("WHEN NOT MATCHED", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Блок MERGE sys_ecr.UiString AS t у {SeedFile} не закінчується.");

        return SeedRow().Matches(text[start..end])
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Прибирає <c>//</c>- і <c>/* */</c>-коментарі (рядків із ними у файлі немає).</summary>
    private static string StripComments(string source) =>
        LineComment().Replace(BlockComment().Replace(source, string.Empty), string.Empty);

    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'[a-z]{2}'\s*,\s*N'(?:[^']|'')*'\s*,\s*[01]\s*\)")]
    private static partial Regex SeedRow();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineComment();
}
