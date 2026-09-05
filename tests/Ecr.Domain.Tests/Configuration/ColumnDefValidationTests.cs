using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>Структурна валідація значення проти опису колонки.</summary>
public sealed class ColumnDefValidationTests
{
    private static ColumnDef Column(CellDataType type, string code = "Volume")
        => new(tableDefId: 1, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), ordinal: 1, type);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Текст_у_числовій_колонці_відхиляється_з_ECR_CELL_0422()
    {
        var column = Column(CellDataType.Decimal);

        Assert.Equal("ECR-CELL-0422", column.ValidateValue(new CellValueData { ValueString = "12500" }));

        // Те саме число, але типізовано — приймається. Тобто відхиляє саме
        // невідповідність типу, а не «схоже на число».
        Assert.Null(column.ValidateValue(new CellValueData { ValueNumeric = 12500m }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Порожнє_значення_в_обовязковій_колонці_відхиляється()
    {
        var optional = Column(CellDataType.Decimal);
        var required = Column(CellDataType.Decimal);
        required.SetRequired(true);

        // «Заповнили порожнім» у необов'язковій колонці — легітимний стан.
        Assert.Null(optional.ValidateValue(CellValueData.Empty));

        // В обов'язковій — ні: свідомий намір лишити порожнім не скасовує вимоги.
        Assert.Equal("ECR-CELL-0422", required.ValidateValue(CellValueData.Empty));

        // Нуль обов'язковість задовольняє — це значення, а не порожнеча.
        Assert.Null(required.ValidateValue(new CellValueData { ValueNumeric = 0m }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-8.8")]
    [Trait("Requirement", "ФВ-8.3")]
    public void Lookup_колонка_вимагає_посилання_на_запис_реєстру_а_не_текст()
    {
        var column = Column(CellDataType.Lookup, "Substance");
        column.SetLookup(registryDefId: 3);

        Assert.Null(column.ValidateValue(new CellValueData { ValueRegistryEntryId = 77 }));

        // Підпис замість ідентифікатора — найпоширеніша помилка клієнта:
        // у комірці зберігається Id, а не Display (ФВ-8.8). Прийняти текст
        // означало б зафіксувати назву, яка завтра зміниться в довіднику.
        Assert.Equal("ECR-CELL-0422", column.ValidateValue(new CellValueData { ValueString = "Нафтопродукти" }));
        Assert.Equal("ECR-CELL-0422", column.ValidateValue(new CellValueData { ValueNumeric = 77m }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-2.4")]
    public void Колонка_типу_Unit_зберігає_посилання_на_одиницю()
    {
        var column = Column(CellDataType.Unit, "AmountUnit");

        // У комірці лежить ValueUnitId → uom.Unit(Id), а не текст «kg»
        // (R-A4, ФВ-16.8). Символ одиниці локалізований і змінюваний;
        // збережений рядок перетворив би історію на набір підписів, які
        // залежать від мови інтерфейсу того, хто заповнював.
        Assert.Null(column.ValidateValue(new CellValueData { ValueUnitId = 8 }));

        Assert.Equal("ECR-CELL-0422", column.ValidateValue(new CellValueData { ValueString = "kg" }));
        Assert.Equal("ECR-CELL-0422", column.ValidateValue(new CellValueData { ValueNumeric = 8m }));

        // ⛔ Одиниця КОЛОНКИ такій колонці не задається: у неї одиниця на
        // рядок, і колонкова означала б, що та сама комірка має дві одиниці
        // одночасно — а котра з них правильна, з'ясувалося б на звірці.
        var error = Assert.Throws<DomainException>(() => column.SetUnit(8));
        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Null(column.UnitId);

        // Звичайній числовій колонці — задається, і це норма (ФВ-16.1).
        var numeric = Column(CellDataType.Decimal);
        numeric.SetUnit(8);
        Assert.Equal(8, numeric.UnitId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-16.1")]
    public void Запис_в_обчислену_колонку_відхиляється_з_ECR_CELL_4221()
    {
        foreach (var type in new[] { CellDataType.Formula, CellDataType.Calculated })
        {
            var column = Column(type, "Total");
            Assert.True(column.IsComputed);

            // Відхиляється будь-який запис, навіть коректний за типом:
            // джерелом значення є рушій, а не користувач.
            Assert.Equal("ECR-CELL-4221", column.ValidateValue(new CellValueData { ValueNumeric = 1m }));
            Assert.Equal("ECR-CELL-4221", column.ValidateValue(CellValueData.Empty));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-16.1")]
    public void Число_з_більшою_кількістю_знаків_ніж_Scale_відхиляється()
    {
        var column = Column(CellDataType.Decimal);
        column.SetNumericFormat(precision: 18, scale: 3);

        Assert.Null(column.ValidateValue(new CellValueData { ValueNumeric = 12500.123m }));

        // Четвертий знак не округлюється, а відхиляється: мовчазне округлення
        // змінило б число у звіті, і виявилося б це лише на звірці з еталоном.
        Assert.Equal("ECR-CELL-0422", column.ValidateValue(new CellValueData { ValueNumeric = 12500.1234m }));

        // Менша кількість знаків — не порушення.
        Assert.Null(column.ValidateValue(new CellValueData { ValueNumeric = 12500.1m }));

        // Precision рахує ВСІ значущі цифри, не лише дробові.
        var narrow = Column(CellDataType.Decimal, "Narrow");
        narrow.SetNumericFormat(precision: 5, scale: 2);
        Assert.Null(narrow.ValidateValue(new CellValueData { ValueNumeric = 123.45m }));
        Assert.Equal("ECR-CELL-0422", narrow.ValidateValue(new CellValueData { ValueNumeric = 1234.56m }));
    }
}
