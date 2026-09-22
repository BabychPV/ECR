using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожен вид залежного об'єкта (<see cref="UsageKinds"/>) має рядок каталогу
/// <c>usageKind.&lt;вид&gt;</c> у сіді І гілку з цим ключем у клієнтському
/// <c>UsageKindLabel.tsx</c>.
/// </summary>
/// <remarks>
/// ⛔ Напрям «сервер → клієнт». <c>EndpointCoverageTests</c> уже вимагає, щоб
/// кожен ключ, якого просить клієнт, був у сіді, — але про вид, якого клієнт
/// НЕ знає, він мовчить: такий вид просто падає в гілку <c>default</c> і
/// показується сирим значенням. Саме так сім із дванадцяти видів жили без
/// назви: екран одиниць показував <c>kind</c> як є, а вкладка довідника знала
/// лише п'ять.
///
/// ⚠ Клієнтський файл читається без коментарів: ключ, названий у коментарі,
/// не є гілкою <c>switch</c>. Перевіряється і <c>case '&lt;вид&gt;'</c>, і
/// літерал <c>t('usageKind.&lt;вид&gt;')</c> — саме літерал, щоб його бачив і
/// сторож каталогу.
/// </remarks>
public sealed partial class UsageKindCatalogTests
{
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    private const string ClientFile = "src/Ecr.Web/src/features/usage/UsageKindLabel.tsx";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_вид_використання_має_ключ_каталогу_в_сіді_і_в_клієнті()
    {
        // ⛔ Порожній перелік (рефлексія перестала бачити поля) дав би зелене.
        Assert.NotEmpty(UsageKinds.All);

        var seeded = SeedKeys();
        Assert.NotEmpty(seeded);

        var clientPath = Path.Combine(SourceTree.Root, ClientFile.Replace('/', Path.DirectorySeparatorChar));
        var clientExists = File.Exists(clientPath);
        var client = clientExists ? StripComments(File.ReadAllText(clientPath)) : string.Empty;

        var problems = new List<string>();
        foreach (var kind in UsageKinds.All.Order(StringComparer.Ordinal))
        {
            var key = $"usageKind.{kind}";
            if (!seeded.Contains(key))
            {
                problems.Add($"  {kind}: у {SeedFile} немає рядка {key}");
            }

            if (!client.Contains($"case '{kind}'", StringComparison.Ordinal))
            {
                problems.Add($"  {kind}: у {ClientFile} немає гілки case '{kind}'");
            }

            if (!client.Contains($"t('{key}')", StringComparison.Ordinal))
            {
                problems.Add($"  {kind}: у {ClientFile} немає літерала t('{key}')");
            }
        }

        Assert.True(
            problems.Count == 0,
            (clientExists ? string.Empty : $"Файлу {ClientFile} немає.{Environment.NewLine}")
            + "Вид використання (UsageKinds) без назви — клієнт показав би його сирим значенням. "
            + "Заведи рядок каталогу англійською в 09-seed.sql і гілку в UsageKindLabel.tsx:"
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
