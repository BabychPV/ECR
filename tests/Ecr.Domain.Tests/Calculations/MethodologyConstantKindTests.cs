// tests/Ecr.Domain.Tests/Calculations/MethodologyConstantKindTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Константа методології не завжди число (директива ПК-1 №05, поправка 2-біс).
/// </summary>
/// <remarks>
/// ⛔ Регресія тут не видна ніде, крім чисел: правило «нечислове значення —
/// помилка публікації», записане в пакеті, було <b>перевернуте</b>. Воно
/// відхилило б 108 законних констант корпусу і зламало б механізм категорій
/// цілком, бо саме нечислові константи стоять операндом порівняння в
/// <c>if(@Land_Category = CST.k1_CategorySelection_, …)</c>.
/// </remarks>
public sealed class MethodologyConstantKindTests
{
    private const int VersionId = 51;
    private const int TonneUnit = 23;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.5")]
    public void Текстова_константа_зберігається_а_не_відхиляється()
    {
        // ⛔ Упаде, щойно нечислове значення знову стане помилкою: у корпусі
        // таких 108, і ~90 із них ужиті у виразах. «Summer» — не число і не
        // помилка, а значення, з яким порівнюють категорію ділянки.
        var summer = MethodologyConstant.OfText(
            VersionId, EcrCode.Create("k1_CategorySelection_"), "Summer", ConstantKind.Text);

        Assert.Equal(ConstantKind.Text, summer.Kind);
        Assert.Equal("Summer", summer.TextValue);
        Assert.True(summer.IsResolved);
        Assert.True(summer.IsAllowedInExpression);

        // ⚠ Одиниці немає і бути не може: вимір — властивість числа.
        Assert.Null(summer.UnitId);
        Assert.Null(summer.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.5")]
    public void Мітка_категорії_у_вираз_не_допускається()
    {
        // ⛔ Упаде, якщо мітку зрівняти з текстом. Мітка бере участь ЛИШЕ в
        // резолвінгу категорії; підставлена у вираз, вона дає правдоподібне
        // порівняння з ключем звуження замість значення.
        var label = MethodologyConstant.OfText(
            VersionId, EcrCode.Create("k1_Season_"), "<1500", ConstantKind.CategoryLabel);

        Assert.Equal(ConstantKind.CategoryLabel, label.Kind);
        Assert.False(label.IsAllowedInExpression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.5")]
    public void Прочерк_не_стає_нулем_а_лишається_нерозібраним()
    {
        // ⛔ Найдорожчий випадок. `n_ECW_C11_13_ = '-'` ужита в 16 формулах.
        // Тихий нуль дав би 16 правдоподібних і неправильних чисел; виняток
        // імпорту обірвав би перенесення решти 6504 констант. Тому — рядок
        // без числа, який доїжджає до публікації.
        var dash = MethodologyConstant.FromImport(
            VersionId, EcrCode.Create("n_ECW_C11_13_"), "-", ConstantKind.Numeric, TonneUnit);

        Assert.Equal(ConstantKind.Numeric, dash.Kind);
        Assert.Null(dash.Value);
        Assert.Equal("-", dash.TextValue);
        Assert.False(dash.IsResolved);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.5")]
    public void Порожній_текст_константи_відхиляється()
    {
        // ⛔ `k22_HSE30X_Int_FG_ = ''` ужита в 5 формулах. Порожній рядок — не
        // мітка і не значення; прийняти його означало б, що п'ять формул
        // рахують із порожнечею і ніхто про це не дізнається.
        var error = Assert.Throws<DomainException>(() => MethodologyConstant.OfText(
            VersionId, EcrCode.Create("k22_HSE30X_Int_FG_"), "  ", ConstantKind.Text));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.5")]
    public void Вид_текстової_константи_визначається_вживанням()
    {
        // ⚠ Іншої ознаки в джерелі не існує: у `CInfo` текст і мітка лежать в
        // одній колонці. Правило оголошене в домені, щоб імпортер не завів
        // другого, розбіжного з цим.
        Assert.Equal(ConstantKind.Text, MethodologyConstant.ClassifyText(true));
        Assert.Equal(ConstantKind.CategoryLabel, MethodologyConstant.ClassifyText(false));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Число_розбирається_інваріантно_а_не_за_локаллю()
    {
        // ⛔ Упаде на машині з українською локаллю, якщо розбір стане
        // культурозалежним: `0.85` там розібралося б як 85, і коефіцієнт емісії
        // виріс би стократно без жодної помилки.
        Assert.True(MethodologyConstant.TryParseNumeric("0.85", out var dot));
        Assert.Equal(0.85m, dot);

        // Кома — не роздільник дробової частини в джерелі (02b §5), і мовчазне
        // її прийняття означало б, що `0,85` інколи 0.85, а інколи 85.
        Assert.False(MethodologyConstant.TryParseNumeric("0,85", out _));
        Assert.False(MethodologyConstant.TryParseNumeric("-", out _));
        Assert.False(MethodologyConstant.TryParseNumeric(string.Empty, out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.6")]
    public void Текстова_формула_не_має_одиниці_результату()
    {
        // ⛔ Одиниця на текстовому результаті не має симптому: перевірка
        // розмірностей при публікації порівнює одиниці, а не значення, і
        // зрівняла б `'Сверхнорматив'` з тоннами без жодного зауваження.
        var formula = new MethodologyFormula(
            VersionId, EcrCode.Create("Verdict"), "if(!Total > 1, 'Сверхнорматив', 'В норме')");

        formula.SetResultType(FormulaResultType.Text);

        var error = Assert.Throws<DomainException>(() => formula.SetOutputUnit(TonneUnit));
        Assert.Equal("ECR-CALC-0422", error.ErrorCode);

        // ⚠ Друга половина: порядок викликів не має вирішувати, спрацює
        // перевірка чи ні.
        var numeric = new MethodologyFormula(VersionId, EcrCode.Create("Total"), "1 + 2");
        numeric.SetOutputUnit(TonneUnit);

        Assert.Equal("ECR-CALC-0422",
            Assert.Throws<DomainException>(() => numeric.SetResultType(FormulaResultType.Text)).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Методологія_не_імпортує_саму_себе()
    {
        // ⛔ Самоімпорт нічого не додає, зате дає ребро-петлю в
        // `calc.MethodologyDependency` — і топологічне сортування називає його
        // циклом, зупиняючи перерахунок УСІХ методологій, а не однієї.
        var error = Assert.Throws<DomainException>(
            () => new MethodologyImport(VersionId, importedMethodologyId: 4, ownerMethodologyId: 4));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);

        var edge = Assert.Throws<DomainException>(() => new MethodologyDependency(4, 4));
        Assert.Equal("ECR-CALC-0422", edge.ErrorCode);
    }
}
