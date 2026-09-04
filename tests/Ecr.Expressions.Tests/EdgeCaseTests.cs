using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Двадцять чотири крайові випадки з <c>02c §8</c>. Кожен — окремий тест;
/// назва відповідає ідентифікатору випадку.
/// </summary>
public sealed class EdgeCaseTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E01_SUM_порожньої_множини_дорівнює_нулю() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E02_AVERAGE_порожньої_множини_дорівнює_null() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E03_null_помножений_на_нуль_дає_null() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E04_ділення_на_нуль_дає_помилку() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E05_ділення_на_null_дає_помилку() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E06_помилка_поширюється() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E07_IFERROR_перехоплює_помилку() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E08_IFERROR_не_перехоплює_null() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E09_конкатенація_з_null_дає_другий_операнд() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E10_порівняння_з_null_дає_null() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E11_рівність_null_дає_TRUE() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E12_округлення_двох_з_половиною_дає_три() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E13_округлення_мінус_двох_з_половиною_дає_мінус_три() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void E14_конверсія_різних_розмірностей_дає_помилку() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void E15_конверсія_градусів_у_Кельвіни_дає_273_15() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E16_крос_період_за_межу_проєкту_дає_null() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E17_цикл_відхиляє_публікацію() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E18_порівняння_числа_з_текстом_відхиляє_публікацію() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E19_зміна_Ordinal_не_змінює_результат_діапазону() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void E20_дублікат_RowKey_відхиляється()
    {
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("TBL"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);

        table.AddRow(new RowDef(table.Id, RowKey.Create("7001001"), 1, Name("Перший"), RowKind.Item));

        // ⚠ RowKey — це ІДЕНТИЧНІСТЬ рядка, на неї посилаються вирази і дані
        // всіх минулих періодів. Два рядки з одним ключем означають, що
        // посилання перестало бути однозначним, і жоден діапазон більше не
        // розкривається передбачувано.
        var error = Assert.Throws<DomainException>(() =>
            table.AddRow(new RowDef(table.Id, RowKey.Create("7001001"), 2, Name("Другий"), RowKind.Item)));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Single(table.Rows);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void E21_агрегація_різних_одиниць_без_CONVERT_відхиляє_публікацію() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E22_відсутня_комірка_бере_DefaultValue() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E23_явна_порожнеча_ігнорує_DefaultValue() => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E24_нуль_ціла_один_плюс_нуль_ціла_два_дорівнює_рівно_нуль_ціла_три()
        => Assert.Fail("not implemented");
}
