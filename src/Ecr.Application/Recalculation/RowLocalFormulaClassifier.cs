using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Functions;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Чи є формула <b>рядково-локальною</b>: чи читає вона виключно колонки
/// ТОГО САМОГО рядка ТІЄЇ САМОЇ таблиці того самого періоду.
/// </summary>
/// <remarks>
/// ⛔ Директива №14 частина 3, <c>CAL-03</c>. Колонкова формула віддає ціль у
/// КОЖНОМУ рядку таблиці (<c>RecalculationService.Targets</c>), тож правка
/// однієї комірки в таблиці на 500 рядків давала 500 обчислень. Для формули
/// виду <c>C3 = C1 + C2</c> 499 із них — робота, результат якої заздалегідь
/// відомий: входи тих рядків ніхто не чіпав.
///
/// ⛔ <b>Класифікатор КОНСЕРВАТИВНИЙ: усе, чого він не розпізнав, —
/// нелокальне</b> (пряма вимога §6 директиви). Помилка в цей бік коштує
/// зайвих обчислень; помилка в інший — НЕПЕРЕРАХОВАНА комірка, тобто
/// неправдиве число у звітності, яке саме не виправиться.
///
/// Локальним визнається лише те, що перелічене явно:
/// <list type="bullet">
/// <item>літерал (<c>02b</c> §1, <c>literal</c>);</item>
/// <item>унарна, бінарна операція, тернарний оператор (<c>02b</c> §2) — якщо
/// локальні всі операнди;</item>
/// <item>виклик функції діалекту шаблонів <b>без</b> ознаки
/// <c>AcceptsRange</c> (<c>FunctionRegistry.cs:44-58</c>): <c>CONVERT</c>,
/// <c>ROUND</c>, <c>ABS</c>, <c>IF</c>, <c>IFERROR</c>. Агрегати
/// (<c>SUM</c>, <c>AVERAGE</c>, <c>MIN</c>, <c>MAX</c>, <c>COUNT</c>,
/// <c>PRODUCT</c>, <c>SUMIF</c>) і будь-яке невідоме ім'я — нелокальні;</item>
/// <item>посилання на комірку виду <c>[Col]</c>, тобто
/// <c>RowSelector.Current</c> без коду аркуша й таблиці та зі зсувом періоду
/// <c>0</c> (<c>02b</c> §3.1 рядок «колонка в тому самому рядку»).</item>
/// </list>
///
/// ⚠ Перевірка <c>SheetCode is null &amp;&amp; TableCode is null</c> формально
/// надлишкова — парсер створює <c>RowSelector.Current</c> рівно для
/// односегментного посилання, а коди аркуша й таблиці бере з третього й
/// четвертого сегментів (<c>Parser.cs:762-779</c>). Вона лишається саме тому,
/// що це припущення про ЧУЖИЙ файл: якщо парсер колись почне будувати
/// <c>Current</c> інакше, ціна помилки тут — мовчки неперерахована комірка.
///
/// ⚠ Усе інше — <c>SymbolReferenceNode</c> (<c>HDR.*</c>, <c>@Arg</c>,
/// <c>CST.*</c>, <c>!Formula</c>) і <c>PeriodPropertyNode</c>
/// (<c>[Period].Days</c>) — нелокальне. Календарний контекст і шапка
/// однакові для всіх рядків, тобто теоретично локальності не псують; але
/// «теоретично» тут не перевага: вузол, якого класифікатор не бачив, мусить
/// отримувати відповідь «ні».
/// </remarks>
public static class RowLocalFormulaClassifier
{
    /// <summary>Каталог функцій діалекту шаблонів — джерело ознаки «агрегат».</summary>
    private static readonly FunctionRegistry Functions = new();

    /// <summary>Чи можна рахувати цю формулу лише в брудних рядках.</summary>
    /// <param name="formula">Формула шаблону.</param>
    /// <param name="root">Корінь розібраного виразу; <c>null</c> — вираз не розібрався.</param>
    public static bool IsRowLocal(FormulaDef? formula, AstNode? root)
    {
        if (formula is null || root is null)
        {
            return false;
        }

        // ⚠ Діалект методологій сюди не доходить (у `doc.CellValue` пишуть лише
        // формули шаблону), але правило локальності сформульоване в термінах
        // граматики ШАБЛОНУ — і мовчки застосувати його до іншої мови означало б
        // спиратися на те, чого ніхто не перевіряв.
        return formula.Dialect == ExpressionDialect.Template && IsLocal(root);
    }

    private static bool IsLocal(AstNode node)
        => node switch
        {
            LiteralNode => true,
            UnaryNode unary => IsLocal(unary.Operand),
            BinaryNode binary => IsLocal(binary.Left) && IsLocal(binary.Right),
            ConditionalNode conditional =>
                IsLocal(conditional.Condition)
                && IsLocal(conditional.WhenTrue)
                && IsLocal(conditional.WhenFalse),
            FunctionNode function =>
                Functions.GetSignature(function.Name) is { AcceptsRange: false }
                && function.Arguments.All(IsLocal),
            CellReferenceNode reference => IsOwnRowCell(reference),
            _ => false,
        };

    /// <summary>Посилання <c>[Col]</c> — колонка того самого рядка тієї самої таблиці.</summary>
    private static bool IsOwnRowCell(CellReferenceNode reference)
        => reference.PeriodOffset == 0
           && reference.SheetCode is null
           && reference.TableCode is null
           && reference.Row is RowSelector.Current;
}
