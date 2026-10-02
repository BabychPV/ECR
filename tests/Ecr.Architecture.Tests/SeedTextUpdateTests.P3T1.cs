using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>Тексти пакета P3 тестувального проходу №1 (<c>COLL:p3-t1</c> і суміжні правки каталогу).</summary>
public sealed partial class SeedTextUpdateTests
{
    private static Dictionary<(string, string), string> Catalog()
        => CatalogRows(File.ReadAllText(
            Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar))));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void T1_06_Тексти_429_не_згадують_HTTP_заголовок_Retry_After()
    {
        var catalog = Catalog();
        var rate = catalog.Where(row => row.Key.Item1.Contains("-0429.", StringComparison.Ordinal)).ToList();

        // ⛔ Порожній відбір дав би зелене, нічого не перевіривши.
        Assert.True(rate.Count >= 12, $"Очікувалось 4 ключі × 3 мови, знайдено {rate.Count}.");
        Assert.Empty(rate
            .Where(row => row.Value.Contains("Retry-After", StringComparison.OrdinalIgnoreCase))
            .Select(row => $"{row.Key.Item1} [{row.Key.Item2}]"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void T1_09_Текст_самозвязку_не_називає_обмеження_бази()
    {
        var catalog = Catalog();

        foreach (var lang in new[] { "en", "ru", "kz" })
        {
            Assert.True(
                catalog.TryGetValue(("err.ECR-TMPL-0422.relationSelfLink", lang), out var value),
                $"relationSelfLink [{lang}] немає в каталозі.");
            Assert.DoesNotContain("CK_", value, StringComparison.Ordinal);
        }
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("err.ECR-REQ-0422.notificationChannelRecipientInvalid", "{address}")]
    [InlineData("err.ECR-USR-0422.emailInvalid", "{email}")]
    public void T1_Тексти_пакета_P3_мають_плейсхолдер_у_всіх_трьох_мовах(string key, string placeholder)
    {
        var catalog = Catalog();

        foreach (var lang in new[] { "en", "ru", "kz" })
        {
            Assert.True(catalog.TryGetValue((key, lang), out var value), $"{key} [{lang}] немає в каталозі.");
            Assert.Contains(placeholder, value, StringComparison.Ordinal);
        }
    }
}
