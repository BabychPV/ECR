// tests/Ecr.Application.Tests/Localization/UiStringResolverFormatTests.cs
using Ecr.Application.Localization;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>
/// Підстановка <c>{name}</c> у шаблон каталогу (`Q-304`) — той самий синтаксис,
/// що й клієнтський <c>t()</c> (`shared/i18n/index.ts`), тепер і на сервері:
/// health-перевірки (`/admin/health`) резолвлять текст каталогом напряму,
/// без клієнтської локалізації (на відміну від `Q-303`, де це робив клієнт,
/// бо `Ecr.Expressions` не має DI).
/// </summary>
public sealed class UiStringResolverFormatTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Підставляє_одне_ім_я()
    {
        var result = UiStringResolver.Format(
            "Database is unavailable: {reason}.", new Dictionary<string, string> { ["reason"] = "timeout" });

        Assert.Equal("Database is unavailable: timeout.", result);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Підставляє_кілька_імен_незалежно_від_порядку_в_шаблоні()
    {
        var result = UiStringResolver.Format(
            "Partitions ahead: {count} — below the minimum of {minimum}.",
            new Dictionary<string, string> { ["minimum"] = "2", ["count"] = "0" });

        Assert.Equal("Partitions ahead: 0 — below the minimum of 2.", result);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Без_підстановок_шаблон_повертається_як_є()
    {
        Assert.Equal("Database is available.", UiStringResolver.Format("Database is available.", null));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Невідоме_ім_я_у_фігурних_дужках_лишається_непідставленим()
    {
        // ⚠ Той самий інваріант, що й клієнтський t(): застарілий шаблон
        // каталогу (перейменували параметр — забули оновити один із двох)
        // не має кидати виняток і не має мовчки зникати — краще видимий
        // `{unknown}`, ніж порожнє місце чи 500.
        var result = UiStringResolver.Format(
            "Sources with a failed last run: {count}.", new Dictionary<string, string> { ["total"] = "3" });

        Assert.Equal("Sources with a failed last run: {count}.", result);
    }
}
