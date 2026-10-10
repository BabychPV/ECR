using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожен ключ відмови, яку адаптер збору кидає при читанні, має гілку в
/// клієнтському <c>adapterRefusal</c> (<c>features/integration/collectionRunMessage.ts</c>).
/// </summary>
/// <remarks>
/// ⛔ Ключ без гілки не падає: <c>adapterRefusal</c> повертає <c>null</c>,
/// <c>resolve</c> гасить увесь конверт, і на екрані прогону та журналу покриття
/// лишається сирий JSON. Так сталося з <c>elementNameAmbiguous</c> (AN-81, Y4-02):
/// ключ завели в адаптер і в сід, а в клієнтський перелік — ні.
///
/// ⚠ Ключі з інших шляхів (події джерела, перегляд каталогу) до прогону збору
/// не доходять — вони в <see cref="NotFromReadAsync"/> з причиною.
/// </remarks>
public sealed partial class AdapterRefusalClientKeysTests
{
    private const string ClientFile = "src/Ecr.Web/src/features/integration/collectionRunMessage.ts";

    private static readonly string[] AdapterFiles =
    [
        "src/Ecr.Adapters.PiAf/PiSqlClientDataSource.cs",
        "src/Ecr.Adapters.PiAf/PiWebApiDataSource.cs",
        "src/Ecr.Adapters.PiAf/SourceUnitConverter.cs",
        "src/Ecr.Adapters.Sql/SqlDataSource.cs",
        "src/Ecr.Application/Integration/SourceRowOrder.cs",
    ];

    /// <summary>Ключі, яких <c>ReadAsync</c> не кидає, — і чому.</summary>
    private static readonly Dictionary<string, string> NotFromReadAsync = new(StringComparer.Ordinal)
    {
        ["err.ECR-INT-0422.eventIdMissing"] = "ReadEventsAsync — синк подій, не прогін збору",
        ["err.ECR-INT-0422.eventQueryNotConfigured"] = "ReadEventsAsync — синк подій, не прогін збору",
        ["err.ECR-INT-0422.eventTimestampUnreadable"] = "ReadEventsAsync — синк подій, не прогін збору",
        ["err.ECR-INT-0503.catalogUnavailable"] = "перегляд каталогу PI Web API, не читання значень",
    };

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_відмова_адаптера_при_читанні_має_гілку_в_adapterRefusal()
    {
        var adapterKeys = AdapterFiles
            .SelectMany(f => AdapterKey().Matches(File.ReadAllText(Path.Combine(SourceTree.Root, f))))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // ⚠ Без ключів тест був би зеленим ні про що (перейменували файл чи формат).
        Assert.Contains("err.ECR-INT-0422.elementNameAmbiguous", adapterKeys);

        var clientCases = ClientCase()
            .Matches(File.ReadAllText(Path.Combine(SourceTree.Root, ClientFile)))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = adapterKeys
            .Where(k => !clientCases.Contains(k) && !NotFromReadAsync.ContainsKey(k))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Відмова адаптера без гілки в adapterRefusal ({ClientFile}) — на екрані прогону "
            + "буде сирий JSON. Допиши case або внеси в NotFromReadAsync з причиною: "
            + string.Join(", ", missing));
    }

    [GeneratedRegex(@"""(err\.ECR-[A-Z]+-\d{4}\.[A-Za-z]+)""")]
    private static partial Regex AdapterKey();

    [GeneratedRegex(@"case\s+'(err\.ECR-[A-Z]+-\d{4}\.[A-Za-z]+)'")]
    private static partial Regex ClientCase();
}
