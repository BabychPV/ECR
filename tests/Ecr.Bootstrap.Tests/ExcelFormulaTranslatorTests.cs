using Ecr.Bootstrap.Excel;
using Xunit;

namespace Ecr.Bootstrap.Tests;

/// <summary>
/// Переклад формул Excel у діалект шаблонів — лише однозначний випадок.
/// </summary>
/// <remarks>
/// ⛔ Мутаційна точка: прибери в <c>TranslateReference</c> перевірку
/// «той самий рядок» — і <see cref="Посилання_на_інший_рядок_відмова"/>
/// червоніє: <c>C6+D5</c> тихо став би <c>[A]+[B]</c>, тобто правдоподібною,
/// але іншою формулою.
/// </remarks>
public sealed class ExcelFormulaTranslatorTests
{
    private static readonly Dictionary<int, string> Columns = new() { [3] = "A", [4] = "B", [5] = "C" };

    private readonly ExcelFormulaTranslator _translator = new();

    [Theory]
    [InlineData("C5+D5*2", "[A]+[B]*2")]
    [InlineData("=$C5-D$5", "[A]-[B]")]
    [InlineData("SUM(C5:E5)", "SUM([A], [B], [C])")]
    [InlineData("ROUND(C5/1000;2)", "ROUND([A]/1000,2)")]
    [InlineData("IF(C5>0,D5/C5,0)", "IF([A]>0,[B]/[A],0)")]
    [InlineData("_xlfn.IFERROR(D5/C5,0)", "IFERROR([B]/[A],0)")]
    public void Формула_того_самого_рядка_перекладається(string excel, string expected)
    {
        var result = _translator.Translate(excel, 5, Columns);

        Assert.True(result.IsSuccess, result.Problem);
        Assert.Equal(expected, result.Expression);
    }

    [Fact]
    public void Посилання_на_інший_рядок_відмова()
    {
        var result = _translator.Translate("C6+D5", 5, Columns);

        Assert.False(result.IsSuccess);
        Assert.Contains("інший рядок", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Вертикальний_підсумок_відмова()
        => Assert.False(_translator.Translate("SUM(C2:C4)", 5, Columns).IsSuccess);

    [Theory]
    [InlineData("Лист2!C5+1", "інший аркуш")]
    [InlineData("'Аркуш 2'!C5", "інший аркуш")]
    [InlineData("VLOOKUP(C5,A1:B9,2,0)", "VLOOKUP")]
    [InlineData("C5*Коефіцієнт", "іменований діапазон")]
    [InlineData("B5+C5", "за колонки даних")]
    [InlineData("SUM({1,2})", "масив")]
    public void Нерозпізнане_не_вгадується(string excel, string reason)
    {
        var result = _translator.Translate(excel, 5, Columns);

        Assert.False(result.IsSuccess);
        Assert.Contains(reason, result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Вираз_якого_не_приймає_парсер_діалекту_відмова()
    {
        var result = _translator.Translate("C5+", 5, Columns);

        Assert.False(result.IsSuccess);
        Assert.Contains("не розбирається", result.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A", 1)]
    [InlineData("Z", 26)]
    [InlineData("AA", 27)]
    [InlineData("$AB", 28)]
    public void Номер_колонки_за_літерами(string letters, int number)
        => Assert.Equal(number, ExcelFormulaTranslator.ColumnNumber(letters));
}
