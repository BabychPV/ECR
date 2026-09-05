using Ecr.Adapters.Excel;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests;

/// <summary>
/// Трансляція виразів у синтаксис Excel і назад (ТЗ §13.5 п.6).
/// </summary>
/// <remarks>
/// ⚠ Найпідступніша частина експорту: наша мова адресує рядки за
/// <c>RowKey</c>, Excel — за координатами. Помилка тут дає книгу, у якій
/// формула порахує щось своє й не поскаржиться.
/// </remarks>
public sealed class FormulaTranslatorTests
{
    private static Dictionary<(string, string, string), string> Coordinates() => new()
    {
        [("T1", "R10", "C1")] = "'Аркуш'!B3",
        [("T1", "R11", "C1")] = "'Аркуш'!B4",
        [("T1", "R12", "C1")] = "'Аркуш'!B5",
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Посилання_на_комірку_стає_координатою_книги()
    {
        var excel = new FormulaTranslator().ToExcel("[T1].[R10].[C1] + 1", Coordinates());

        Assert.Contains("'Аркуш'!B3", excel, StringComparison.Ordinal);
        Assert.StartsWith("=", excel, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Діапазон_рядків_стає_діапазоном_комірок()
    {
        var excel = new FormulaTranslator().ToExcel("SUM([T1].[R10:R12].[C1])", Coordinates());

        Assert.Contains("'Аркуш'!B3:'Аркуш'!B5", excel, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Невідоме_посилання_дає_REF_а_не_порожнє_місце()
    {
        // ⚠ #REF! видно людині як помилку. Порожнє місце або нуль виглядали б
        // як значення — і формула мовчки рахувала б менше, ніж має.
        var excel = new FormulaTranslator().ToExcel("[T1].[R99].[C1] + 1", Coordinates());

        Assert.Contains(FormulaTranslator.MissingReference, excel, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Нерозібраний_вираз_у_книгу_НЕ_потрапляє()
    {
        // Текст нашої граматики в комірці Excel — це або #NAME?, або, гірше,
        // випадково валідна формула, яка порахує щось своє.
        Assert.Equal(string.Empty, new FormulaTranslator().ToExcel("[T1].[R10", Coordinates()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Порожній_вираз_дає_порожній_результат()
        => Assert.Equal(string.Empty, new FormulaTranslator().ToExcel("   ", Coordinates()));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Зворотна_трансляція_повертає_наше_посилання()
    {
        var reverse = new Dictionary<string, (string, string, string)>
        {
            ["'АРКУШ'!B3"] = ("T1", "R10", "C1"),
        };

        var expression = new FormulaTranslator().FromExcel("='Аркуш'!B3+1", reverse);

        Assert.Equal("[T1].[R10].[C1]+1", expression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Нерозпізнана_формула_Excel_НЕ_вгадується()
    {
        // ⛔ Мовчазна здогадка створила б неправильне правило обчислення, і
        // помилку знайшли б у числах, а не в конфігурації.
        Assert.Null(new FormulaTranslator().FromExcel(
                "=VLOOKUP(A1,B:C,2,0)", new Dictionary<string, (string, string, string)>()));
    }
}
