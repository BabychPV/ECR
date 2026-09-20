// tests/Ecr.Application.Tests/Reporting/ReportColumnNamesTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Reporting;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Reporting;

/// <summary>
/// R9: підписи колонок зрізу мовами каталогу — ланцюг вибору мови й відмова на
/// зламаній назві.
/// </summary>
public sealed class ReportColumnNamesTests
{
    private static readonly Dictionary<string, string> ThreeLanguages = new()
    {
        ["en"] = "Amount", ["ru"] = "Объём", ["kz"] = "Мөлшері",
    };

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Назва_береться_мовою_запиту_без_урахування_регістру()
    {
        Assert.Equal("Объём", ReportColumnNames.Of("Value", ThreeLanguages, "ru"));
        Assert.Equal("Мөлшері", ReportColumnNames.Of("Value", ThreeLanguages, "kz"));

        // Ключі порівнюються так само, як у решти `LocalizedText`: без регістру.
        Assert.Equal("Amount", ReportColumnNames.Of("Value", ThreeLanguages, "EN"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Фолбек_іде_мова_запиту_потім_en_потім_код_колонки()
    {
        // ⛔ Мутація, якою перевірено цей тест: в `ReportColumnNames.Of` прибрати
        // ланку `Named(names, Fallback)` (лишити `Named(names, language) ?? code`).
        // Падає рівно другий рядок нижче — і жоден інший тест репозиторію.
        Assert.Equal("Объём", ReportColumnNames.Of("Value", ThreeLanguages, "ru"));
        Assert.Equal("Amount", ReportColumnNames.Of("Value", ThreeLanguages, "uk"));
        Assert.Equal("Value", ReportColumnNames.Of("Value", null, "uk"));

        // ⚠ Третя ланка — саме КОД, а не «перша наявна мова»: порядок у
        // словнику не визначений, і заголовок держформи інакше плавав би.
        Assert.Equal(
            "Value",
            ReportColumnNames.Of("Value", new Dictionary<string, string> { ["kz"] = "Мөлшері" }, "uk"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Порожня_назва_відмовляє_422_із_ключем_каталогу()
    {
        // ⛔ Порожня назва — не «назви немає»: вона доїхала б до заголовка книги
        // порожньою коміркою, і колонка лишилася б без підпису взагалі.
        var error = Assert.Throws<BusinessRuleException>(
            () => ReportDefinitionSpec.ColumnsJson(
                [new("Value", "number", new Dictionary<string, string> { ["en"] = "  " })]));

        Assert.Equal(ErrorCodes.ReportInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0422.columnName", error.Details!["messageKey"]);
        Assert.Equal("Value", error.Details["columnCode"]);
        Assert.Equal("en", error.Details["language"]);
        Assert.Equal("text", error.Details["part"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Ключ_що_не_є_кодом_мови_відмовляє_бо_вибраний_не_буде_ніколи()
    {
        // ⚠ Мова запиту зводиться до первинного субтега (`LanguageCodes.FromTag`),
        // тож `en-GB` не збігся б ніколи — це назва, якої ніхто не побачить.
        var error = Assert.Throws<BusinessRuleException>(
            () => ReportDefinitionSpec.ColumnsJson(
                [new("Value", "number", new Dictionary<string, string> { ["en-GB"] = "Amount" })]));

        Assert.Equal("language", error.Details!["part"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Опис_без_назв_серіалізується_побайтно_як_до_R9()
    {
        // ⛔ Це і є умова «зміна адитивна» (`D-53`): опис, у якому назв немає,
        // мусить дати ТОЙ САМИЙ `ColumnsJson`, інакше кожна наявна версія звіту
        // мовчки змінила б вигляд при першому ж перезаписі.
        Assert.Equal(
            """[{"code":"Value","kind":"number"}]""",
            ReportDefinitionSpec.ColumnsJson([new("Value", "number")]));

        Assert.Equal(
            """[{"code":"Value","kind":"number","nameL10n":{"en":"Amount"}}]""",
            ReportDefinitionSpec.ColumnsJson(
                [new("Value", "number", new Dictionary<string, string> { ["en"] = "Amount" })]));
    }
}
