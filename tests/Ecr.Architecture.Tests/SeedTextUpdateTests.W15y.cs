using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// W15y-T1: текст <c>document.calcIssues.titleBlocked</c> змінено (066c6ec95: «подання заблоковано» → «у поданні може бути
/// відмовлено») без рядка «Змінені тексти наявних ключів» — живий стенд лишився зі старим текстом. Рядок оновлення є
/// в кожній мові, зі СТАРИМ значенням до 066c6ec95 і новим, що збігається з каталогом.
/// </summary>
public sealed partial class SeedTextUpdateTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("en", "Calculation for {period} is not finished · submit is blocked")]
    [InlineData("ru", "Расчёт за {period} не завершён · подача заблокирована")]
    [InlineData("kz", "{period} есебі аяқталмады · тапсыру бұғатталған")]
    public void W15y_T1_Заголовок_панелі_проблем_оновлюється_на_розгорнутих_базах(string lang, string oldValue)
    {
        var text = SeedSql.Text;
        var catalog = CatalogRows(text);
        Assert.True(
            catalog.TryGetValue(("document.calcIssues.titleBlocked", lang), out var current),
            $"document.calcIssues.titleBlocked ({lang}) немає в каталозі.");

        Assert.Contains(
            UpdateRows(text),
            u => u.Key == "document.calcIssues.titleBlocked" && u.Lang == lang
                 && string.Equals(u.Old, oldValue, StringComparison.Ordinal)
                 && string.Equals(u.New, current, StringComparison.Ordinal));
    }
}
