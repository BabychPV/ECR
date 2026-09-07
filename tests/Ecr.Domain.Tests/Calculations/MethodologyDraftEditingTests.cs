// tests/Ecr.Domain.Tests/Calculations/MethodologyDraftEditingTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Редагування вмісту версії методології (`ФВ-9.15`) тримає **домен**.
/// </summary>
/// <remarks>
/// ⛔ Правило одне: чернетку правлять, опубліковану — ні (ФВ-13.2). Ціна
/// помилки максимальна і не має симптому — правка опублікованої версії тихо
/// змінює числа у формах, які вже подали регуляторові, і жоден diff публікації
/// цього не покаже, бо публікації не буде.
///
/// ⚠ Тому перевіряється саме сутність, а не обробник. Обробників, які правлять
/// версію, буде кілька (формули, константи, правила), і правило, розкидане по
/// них, забудеться на другому.
/// </remarks>
public sealed class MethodologyDraftEditingTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int TonneUnit = 3;

    private static readonly DateTime Now = new(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 1, 1);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.2")]
    public void Опублікована_версія_не_приймає_ні_правки_ні_складу_формул()
    {
        var version = Draft();
        var formula = version.AddFormula(Code("gsec"), "@Flow * CST.k1", FormulaResultType.Number, TonneUnit);

        version.Publish(Reviewer, "Уточнено коефіцієнт", From, testsPassed: true, Now);

        // ⛔ Три двері в одну кімнату, і замкнені мають бути всі три. Правка
        // виразу змінює число; додана формула додає вихід, якого не було в
        // опублікованому складі; видалена лишає рядок `calc.CalculationResult`
        // без формули, якою його порахували (ФВ-9.13).
        var edited = Assert.Throws<DomainException>(
            () => version.EditFormula(formula, "@Flow * 2", FormulaResultType.Number, TonneUnit));
        Assert.Equal("ECR-CALC-0409", edited.ErrorCode);

        var added = Assert.Throws<DomainException>(
            () => version.AddFormula(Code("extra"), "1", FormulaResultType.Number, null));
        Assert.Equal("ECR-CALC-0409", added.ErrorCode);

        var removed = Assert.Throws<DomainException>(() => version.RemoveFormula(formula));
        Assert.Equal("ECR-CALC-0409", removed.ErrorCode);

        // Вираз лишився тим, яким його опублікували.
        Assert.Equal("@Flow * CST.k1", formula.Expression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.2")]
    public void Виведена_з_обігу_версія_теж_незмінна()
    {
        var version = Draft();
        var formula = version.AddFormula(Code("gsec"), "@Flow", FormulaResultType.Number, TonneUnit);

        version.Publish(Reviewer, "Причина", From, testsPassed: true, Now);
        version.Deprecate();

        // ⚠ Не «тим більше». Виведена з обігу версія лишається ЧИННОЮ для
        // періодів, які вона рахувала (`Methodology.VersionOn` бере і
        // `Deprecated`), тож її числа так само треба вміти відтворити.
        var error = Assert.Throws<DomainException>(
            () => version.EditFormula(formula, "@Flow * 2", FormulaResultType.Number, TonneUnit));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Чернетка_правиться_і_вираз_справді_змінюється()
    {
        var version = Draft();
        var formula = version.AddFormula(Code("gsec"), "@Flow", FormulaResultType.Number, TonneUnit);

        version.EditFormula(formula, "@Flow * CST.k1", FormulaResultType.Number, TonneUnit);

        Assert.Equal("@Flow * CST.k1", formula.Expression);
        Assert.Equal(TonneUnit, formula.OutputUnitId);

        // Видалення чернеткою дозволене — саме воно і є «прибрати формулу».
        version.RemoveFormula(formula);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Формула_чужої_версії_не_правиться_через_цю()
    {
        var version = Draft();
        var foreign = new MethodologyFormula(9_999, Code("gsec"), "@Flow");

        // ⛔ Без цієї перевірки обробник, який прочитав чернетку і формулу
        // ОКРЕМИМИ запитами, правив би формулу опублікованої версії, тримаючи
        // в руках чернетку: перевірка стану дивилася б не на ту версію.
        var error = Assert.Throws<DomainException>(
            () => version.EditFormula(foreign, "@Flow * 2", FormulaResultType.Number, null));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal("@Flow", foreign.Expression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Порожній_вираз_відхиляється_а_не_зберігається()
    {
        var version = Draft();
        var formula = version.AddFormula(Code("gsec"), "@Flow", FormulaResultType.Number, TonneUnit);

        // ⛔ Формула без виразу не зникає з розрахунку — вона лишається
        // оголошеним виходом і дає нуль, який нічим не відрізняється від
        // порахованого.
        foreach (var empty in new[] { string.Empty, "   " })
        {
            var error = Assert.Throws<DomainException>(
                () => version.EditFormula(formula, empty, FormulaResultType.Number, TonneUnit));

            Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        }

        Assert.Equal("@Flow", formula.Expression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-16.6")]
    public void Числову_формулу_з_одиницею_можна_перевести_в_текстову()
    {
        var version = Draft();
        var formula = version.AddFormula(Code("verdict"), "@Flow", FormulaResultType.Number, TonneUnit);

        // ⛔ Порядок кроків усередині домену несучий: `SetResultType` відхиляє
        // текст на формулі з одиницею, а `SetOutputUnit` — одиницю на тексті.
        // Доки одиницю не було чим зняти, перевести формулу в текст було
        // неможливо взагалі — і не через заборону, а через порядок викликів.
        version.EditFormula(formula, "'Сверхнорматив'", FormulaResultType.Text, null);

        Assert.Equal(FormulaResultType.Text, formula.ResultType);
        Assert.Null(formula.OutputUnitId);

        // Одиниця на текстовому результаті лишається помилкою.
        var error = Assert.Throws<DomainException>(
            () => version.EditFormula(formula, "'Сверхнорматив'", FormulaResultType.Text, TonneUnit));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Клон_переносить_режими_і_лишається_чернеткою()
    {
        var source = Draft();
        source.SetModes(NumericMode.Strict, CalendarMode.Fixed365, TraceLevel.Full);
        source.Publish(Reviewer, "Причина", From, testsPassed: true, Now);

        var clone = source.CloneAsDraft("2.0", Author, Now);

        // ⛔ Конструктор ставить `Legacy` і `Actual` — правильно для НОВОЇ
        // методології і руйнівно для клону: версія, зроблена «щоб виправити
        // одну формулу», рахувала б іншою арифметикою і на іншій тривалості
        // періоду (ФВ-9.9, ФВ-16.11). Ані diff публікації, ані golden set не
        // назвали б причини — вони показують результат, а не режим.
        Assert.Equal(NumericMode.Strict, clone.NumericMode);
        Assert.Equal(CalendarMode.Fixed365, clone.CalendarMode);
        Assert.Equal(TraceLevel.Full, clone.TraceLevel);
        Assert.Equal(source.Level, clone.Level);

        // ⚠ Клон — саме чернетка: без вікна дії, без публікатора, з автором,
        // який не зможе його опублікувати (D-40).
        Assert.Equal(TemplateVersionStatus.Draft, clone.Status);
        Assert.Null(clone.EffectiveFrom);
        Assert.Null(clone.PublishedAt);
        Assert.Equal(Author, clone.CreatedByUserId);
        Assert.Equal(source.MethodologyId, clone.MethodologyId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.4")]
    public void Вимкнене_правило_можна_лишити_вимкненим()
    {
        var rule = new MethodologyRule(1, Code("offshore"), "{}", 10);
        Assert.True(rule.IsActive);

        // ⛔ Конструктор вмикає правило, і клон версії без цього методу вмикав
        // би вимкнене — тобто тихо змінював би те, ЩО ВЗАГАЛІ рахується, у
        // версії, зробленій «щоб нічого не змінювати».
        rule.SetActive(false);

        Assert.False(rule.IsActive);
    }

    private static MethodologyVersion Draft()
        => new(1, "1.0", CalculationLevel.Configuration, Author, Now);

    private static EcrCode Code(string value) => EcrCode.Create(value);
}
