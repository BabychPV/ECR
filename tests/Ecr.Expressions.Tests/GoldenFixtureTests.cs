using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Обчислення формул фікстури і звірка з **очікуваними числами з
/// `water-demo.json`**.
/// </summary>
/// <remarks>
/// Очікування беруться з файлу, а не з коду тесту. Якщо код дає інше — правий
/// файл (`08-workflow.md` §4). Підганяти очікування під результат коду
/// заборонено: саме так «зелені» тести перестають щось означати.
/// </remarks>
public sealed class GoldenFixtureTests
{
    private static readonly FixtureWorkbook Workbook = FixtureWorkbook.Load();

    /// <summary>Місячні колонки фікстури.</summary>
    private static readonly string[] Months = ["Jan", "Feb", "Mar"];

    [Theory]
    [InlineData("7001001", "3650.750")]
    [InlineData("7001002", "1750.750")]   // Feb — комірки НЕМАЄ
    [InlineData("7001003", "4100.500")]   // Mar — ЯВНА порожнеча
    [InlineData("7001101", "1380.500")]
    [InlineData("7001102", "930.000")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F1_сума_місяців_у_рядку(string rowKey, string expected)
    {
        // Обидва «дірчасті» рядки дають ту саму відповідь: null в агрегаті
        // ПОГЛИНАЄТЬСЯ незалежно від того, комірки немає чи вона явно порожня.
        // Різниця між цими станами видна лише через DefaultValue.
        Assert.Equal(Decimal(expected), Number("Water_07", "Main", rowKey, "Total"));
        Assert.Equal(
            Decimal(Workbook.Expected("Water_07.Main.Total", rowKey).GetString()!),
            Number("Water_07", "Main", rowKey, "Total"));
    }

    [Theory]
    [InlineData("Jan", "4750.625")]
    [InlineData("Feb", "4020.500")]
    [InlineData("Mar", "3041.375")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F2_баланс_підсумовує_два_діапазони(string column, string expected)
    {
        Assert.Equal(Decimal(expected), Number("Water_07", "Main", "7009000", column));
        Assert.Equal(
            Decimal(Workbook.Expected("Water_07.Main.7009000", column).GetString()!),
            Number("Water_07", "Main", "7009000", column));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F3_крос_аркушний_rollup_поверхневих_вод()
    {
        // Формула читає ЧУЖИЙ аркуш: [Water_07].[Main]… з аркуша Water_070.
        foreach (var month in new[] { "Jan", "Feb", "Mar" })
        {
            var expected = Decimal(Workbook.Expected("Water_070.Rollup", "C001", month).GetString()!);
            Assert.Equal(expected, Number("Water_070", "Rollup", "C001", month));
        }

        Assert.Equal(4000.500m, Number("Water_070", "Rollup", "C001", "Jan"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рядок_C009_збігається_з_рядком_7009000_бо_це_два_шляхи_до_того_самого_числа()
    {
        // 7009000 = SUM(діапазон1) + SUM(діапазон2) на своєму аркуші.
        // C009    = C001 + C002, де кожен — окремий крос-аркушний rollup.
        // Різні шляхи, одне число: розбіжність тут означала б, що rollup і
        // баланс рахують не те саме, і два звіти показували б різні підсумки.
        foreach (var month in new[] { "Jan", "Feb", "Mar" })
        {
            Assert.Equal(
                Number("Water_07", "Main", "7009000", month),
                Number("Water_070", "Rollup", "C009", month));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void F6_конверсія_кубометрів_у_тонни_через_щільність()
    {
        // (4750.625 + 4020.500 + 3041.375) m3 = 11812.500 m3
        //   × 1000 kg_per_m3        → 11812500.000 kg
        //   CONVERT(kg → t): / 1000 → 11812.500 t
        var months = Months.Sum(m => Number("Water_07", "Main", "7009000", m));
        Assert.Equal(11812.500m, months);

        // ⛔ Вираз F6 із фікстури не розбирається ЖОДНИМ діалектом, і це не
        // випадковість, а невирішена половина Q-066:
        //   • `Template` не має `CST.` — шаблон, який знає про методології,
        //     перестає бути переносним;
        //   • `Methodology` не має `[Jan]` — методологія не читає комірки
        //     документа безпосередньо.
        // Формула лежить у таблиці шаблону, а написана мовою, якої немає.
        // Записано як P-07; тут ця обставина ЗАФІКСОВАНА тестом, а не
        // прихована пропуском.
        const string fixtureExpression =
            "CONVERT(([Jan] + [Feb] + [Mar]) * CST.CST_WATER_DENSITY, 'kg', 't')";

        Assert.False(Expr.Parse(fixtureExpression, ExpressionDialect.Template).IsSuccess);
        Assert.False(Expr.Parse(fixtureExpression, ExpressionDialect.Methodology).IsSuccess);
        Assert.Contains(Workbook.SkippedFormulas, s => s.StartsWith("F6:", StringComparison.Ordinal));

        // Число при цьому обчислюване, і саме воно предмет тесту. Щільність —
        // звичайне значення документа: рекомендація Q-066 саме така, бо
        // коефіцієнт залежить від речовини й умов і мусить бути видимим
        // у даних, а не захованим у «конверсії м³ → т» (ФВ-16.5).
        var context = Context("Water_07", "Main", "7009000");
        context.SetCell("Water_07", "Main", "7009000", "Density", ExpressionValue.Number(1000m));

        var value = Expr.Eval(
            "CONVERT(([Jan] + [Feb] + [Mar]) * [Density], 'kg', 't')",
            context,
            ExpressionDialect.Template);

        Assert.Equal(
            Decimal(Workbook.Expected("Water_070.Rollup", "C009", "TotalTons").GetString()!),
            value.AsNumber());
        Assert.Equal(11812.500m, value.AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void F7_одиниця_на_рядок_приводить_тонни_і_кілограми_до_спільної_одиниці()
    {
        // Колонка `AmountUnit` має dataType = Unit: одиниця лежить у КОЖНІЙ
        // комірці окремо (ФВ-16.8, R-A4, D-87).
        var expected = Workbook.Expected("Waste_08.Items.AmountKg");

        foreach (var row in expected.EnumerateObject())
        {
            var amount = Workbook.Cell("Waste_08", "Items", row.Name, "Amount");
            var unit = Workbook.Cell("Waste_08", "Items", row.Name, "AmountUnit");

            var converted = Workbook.Context.Convert(amount, (string)unit.Value!, "kg");

            Assert.Equal(Decimal(row.Value.GetString()!), converted.AsNumber());
        }

        // 12.500 t і 3400.000 kg — те саме число після приведення, різні до
        // нього. ⛔ Без CONVERT SUM склав би 12.5 із 3400 і дав 3412.5:
        // правдоподібне число, менше за правильне у 4.6 раза.
        Assert.Equal(
            12500.000m,
            Workbook.Context.Convert(ExpressionValue.Number(12.500m), "t", "kg").AsNumber());
        Assert.Equal(
            3400.000m,
            Workbook.Context.Convert(ExpressionValue.Number(3400.000m), "kg", "kg").AsNumber());
    }

    /// <summary>Контекст фікстури, спрямований на конкретний рядок.</summary>
    private static TestEvaluationContext Context(string sheet, string table, string row)
    {
        var context = Workbook.Context;
        context.CurrentSheet = sheet;
        context.CurrentTable = table;
        context.CurrentRow = row;
        return context;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F9_крос_період_у_першому_періоді_дає_null_а_не_помилку()
    {
        // 202601 — перший період проєкту. Січень не має попереднього місяця,
        // і це нормальна ситуація, а не збій.
        var value = Expr.Eval(
            "[Period:-1].[Main].[7001001].[Total]", Workbook.Context);

        Assert.True(value.IsNull);
        Assert.False(value.IsError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void F10_предикатний_діапазон_підсумовує_лише_рядки_за_умовою()
    {
        // ⚠ AmountKg береться з розділу expected фікстури, а не рахується:
        // формула F7 вживає CONVERT, якого в діалекті шаблонів немає (Q-066).
        // Предмет цього тесту — предикат, а не конверсія.
        var context = Workbook.Context;
        foreach (var row in Workbook.Expected("Waste_08.Items.AmountKg").EnumerateObject())
        {
            context.SetCell("Waste_08", "Items", row.Name, "AmountKg",
                ExpressionValue.Number(Decimal(row.Value.GetString()!)));
        }

        context.CurrentSheet = "Waste_08";
        context.CurrentTable = "Items";

        var sum = Expr.Number(
            "SUM([Items].[WHERE [WasteType] = 'W-01'].[AmountKg])", context);

        Assert.Equal(Decimal(Workbook.Expected("Waste_08.PredicateSum_W01").GetString()!), sum);
        Assert.Equal(15900.000m, sum);
    }

    private static decimal Number(string sheet, string table, string row, string column)
        => Workbook.Cell(sheet, table, row, column).AsNumber()
           ?? throw new InvalidOperationException(
               $"{sheet}.{table}.{row}.{column} не має числового значення: " +
               $"{Workbook.Cell(sheet, table, row, column).Type}");

    private static decimal Decimal(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
