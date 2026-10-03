using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>
/// T2-09 (б): локалізовані повідомлення містили СИРІ назви станів — ru «…состояние периода — Grace.»,
/// kz «кезең күйі — Grace», «находится в состоянии Published». Назва стану підміняється підписом каталогу
/// <c>status.&lt;родина&gt;.&lt;стан&gt;</c> мовою читача.
/// </summary>
public sealed class StateParameterLocalizationTests
{
    private static UiStringCatalog Catalog(params (string Key, string Value)[] rows)
        => new("ru", 1, rows.ToDictionary(r => r.Key, r => r.Value));

    private static Dictionary<string, string> Params(string name, string value)
        => new Dictionary<string, string> { [name] = value };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Стан_періоду_підміняється_підписом_каталогу()
    {
        var catalog = Catalog(
            ("status.period.Grace", "Льготный период"),
            ("err.ECR-PRD-0409.reopenOnlyClosed", "Переоткрыть можно только закрытый период; состояние периода — {state}."));

        var text = UiStringResolver.Format(
            UiStringResolver.Resolve(catalog, "err.ECR-PRD-0409.reopenOnlyClosed"),
            UiStringResolver.WithLocalizedStates(catalog, "err.ECR-PRD-0409.reopenOnlyClosed", Params("state", "Grace")));

        Assert.Equal("Переоткрыть можно только закрытый период; состояние периода — Льготный период.", text);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData("err.ECR-TMPL-0409.structurallyFrozen", "status", "Published", "status.version.Published", "Опубликована")]
    [InlineData("err.ECR-DOC-0409.submitWrongState", "status", "Approved", "status.sheet.Approved", "Утверждён")]
    [InlineData("err.ECR-JOB-0409.notActive", "state", "Succeeded", "status.job.Succeeded", "Выполнена")]
    [InlineData("err.ECR-PRJ-0422.notDraft", "status", "Archived", "status.project.Archived", "В архиве")]
    public void Кожна_родина_станів_береться_зі_свого_словника(
        string messageKey, string parameter, string raw, string labelKey, string label)
    {
        var catalog = Catalog((labelKey, label));

        var result = UiStringResolver.WithLocalizedStates(catalog, messageKey, Params(parameter, raw));

        Assert.Equal(label, result[parameter]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Стану_немає_в_каталозі_або_повідомлення_не_про_стан_підстановки_без_змін()
    {
        var catalog = Catalog(("status.period.Grace", "Льготный период"));

        // Повідомлення про стан, але підпису такого стану немає — лишається сира назва, а не порожнеча.
        Assert.Equal(
            "Weird",
            UiStringResolver.WithLocalizedStates(catalog, "err.ECR-PRD-0409.reopenOnlyClosed", Params("state", "Weird"))["state"]);

        // Інше повідомлення з параметром `state` не чіпається (його значення може бути не станом).
        Assert.Equal(
            "Grace",
            UiStringResolver.WithLocalizedStates(catalog, "err.ECR-REQ-0422.someOther", Params("state", "Grace"))["state"]);
    }
}
