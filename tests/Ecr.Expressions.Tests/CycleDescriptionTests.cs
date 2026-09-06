using Ecr.Expressions.Graph;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Опис знайденого циклу формул (<c>ФВ-9.4</c>, <c>ECR-TMPL-4221</c>).
/// </summary>
/// <remarks>
/// ⛔ Клас з'явився, щоб зняти суперечність, яка жила в коді:
/// <c>BuildEvaluationOrder</c> документував, що формула, залежна від себе, —
/// це цикл і публікація має його побачити, а **обидва** викликачі відсіювали
/// самопосилання перед побудовою графа. Обіцянка рушія була недосяжна.
///
/// ⚠ Кожна сторона мала половину рації, і рішення враховує обидві:
/// самопосилання **є** циклом (рушій правий по суті), але описується
/// **окремими словами** (викликачі праві щодо форми — «цикл: X → X» нічого не
/// пояснює).
/// </remarks>
public sealed class CycleDescriptionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Самопосилання_описується_окремо_а_не_як_цикл_із_себе_в_себе()
    {
        // ⛔ Головне твердження. «Формули утворюють цикл: Total → Total» читач
        // сприймає як збій сортувальника і йде шукати проблему не туди —
        // а проблема в його власній формулі, і вона має бути названа.
        var text = CycleDescription.Describe([1], _ => "Total");

        Assert.Contains("Total", text, StringComparison.Ordinal);
        Assert.Contains("власний результат", text, StringComparison.Ordinal);
        Assert.DoesNotContain("→", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Самопосилання_згадує_що_чинна_система_мовчала()
    {
        // ⚠ Це не риторика, а вказівка для звірки: у чинній системі така
        // формула не рахувалася зовсім — ітерація до нерухомої точки виходила
        // після ста проходів без результату і без запису. Наша явна відмова —
        // ПОКРАЩЕННЯ, і в звіті золотої звірки вона має піти окремою
        // категорією, а не як наш дефект (`H-24d-2`).
        var text = CycleDescription.Describe([7], _ => "ECW_EC_tons_184");

        Assert.Contains("чинній системі", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Справжній_цикл_показує_шлях()
    {
        var names = new Dictionary<int, string> { [1] = "A", [2] = "B", [3] = "C" };
        var text = CycleDescription.Describe([1, 2, 3], id => names[id]);

        Assert.Contains("A → B → C", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_без_відомого_шляху_не_вигадує_його()
    {
        // ⚠ Порожній шлях означає «цикл є, але сортувальник не назвав який».
        // Дописати сюди щось правдоподібне означало б показати користувачеві
        // формули, яких у циклі немає.
        Assert.Equal("Формули утворюють цикл.", CycleDescription.Describe(null));
        Assert.Equal("Формули утворюють цикл.", CycleDescription.Describe([]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Без_імен_показуються_ідентифікатори()
    {
        // Резервний шлях: краще числа, ніж порожнеча.
        Assert.Contains("12 → 13", CycleDescription.Describe([12, 13]), StringComparison.Ordinal);
    }
}
