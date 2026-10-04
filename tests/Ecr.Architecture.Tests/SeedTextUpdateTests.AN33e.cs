using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>Тексти повторного рев'ю an33d (<c>AN-33e</c>): політика адреси Sql-джерела.</summary>
public sealed partial class SeedTextUpdateTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void AN33e_Текст_забороненого_параметра_Sql_називає_чинні_правила()
    {
        // ⛔ Після abb69f6a Server Certificate дозволено лише повним локальним шляхом X:\…, а не «лише не з
        // мережевого ресурсу»; заборонено ще Authentication, крім SqlPassword, і Server SPN. Користувач із
        // `certs\c.cer` мусить побачити правильну причину.
        var catalog = Catalog();

        foreach (var lang in new[] { "en", "ru", "kz" })
        {
            Assert.True(
                catalog.TryGetValue(("err.ECR-REQ-0422.dataSourceEndpointSqlForbiddenOption", lang), out var value),
                $"dataSourceEndpointSqlForbiddenOption [{lang}] немає в каталозі.");
            Assert.Contains(@"X:\", value, StringComparison.Ordinal);
            Assert.Contains("SqlPassword", value, StringComparison.Ordinal);
            Assert.Contains("Server SPN", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void AN33e_kz_обрізані_події_видалення_пропущено_а_не_проведено()
    {
        // ⛔ «жою өткізілді» означає «видалення проведено» — протилежне до en/ru («removal … was skipped»,
        // «удаление … пропущено»). Адміністратор вирішив би, що зниклі події вже прибрано.
        var catalog = Catalog();

        Assert.True(catalog.TryGetValue(("coverageEvents.eventsTruncated", "kz"), out var value));
        Assert.DoesNotContain("жою өткізілді", value, StringComparison.Ordinal);
        Assert.Contains("жою өткізіп жіберілді", value, StringComparison.Ordinal);
    }
}
