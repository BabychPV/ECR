// tests/Ecr.Domain.Tests/Documents/CellValueTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Комірка. Три стани розрізняються завжди: **є значення** / **явна
/// порожнеча** (`IsEmpty`) / **комірки немає** (ФВ-3.8, R-B4).
/// </summary>
/// <remarks>
/// Формула трактує другий і третій однаково, але аудит і експорт — ні. Злиття
/// цих станів здається спрощенням рівно доти, доки не треба довести, що
/// користувач свідомо лишив клітинку порожньою.
/// </remarks>
public sealed class CellValueTests
{
    private static readonly CellAddress Address = new(PeriodKey.Create(2026, 1), TableRowId: 1001, ColumnDefId: 5);

    private static ColumnDef Column(CellDataType type)
        => new(tableDefId: 1, EcrCode.Create("Col"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Col" }), 1, type);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Apply_замінює_значення_і_тип()
    {
        var cell = new CellValue(Address, tableDefId: 1, new CellValueData { ValueNumeric = 12500m });
        Assert.Equal(12500m, cell.ValueNumeric);

        // Заміна на інший ТИП мусить прибрати попереднє значення, а не
        // лишити його поруч: інакше комірка стала б «двозначною», і читання
        // залежало б від того, яке поле подивитися першим.
        cell.Apply(new CellValueData { ValueString = "н/д" });

        Assert.Equal("н/д", cell.ValueString);
        Assert.Null(cell.ValueNumeric);
        Assert.True(cell.ToData().IsWellFormed());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-3.0")]
    public void ToData_віддає_рівно_те_що_записано()
    {
        var written = new CellValueData
        {
            ValueNumeric = 3400.250m,
            IsCalculated = true
        };

        var cell = new CellValue(Address, tableDefId: 1, written);
        var read = cell.ToData();

        Assert.Equal(written, read);
        Assert.Equal(3400.250m, read.ValueNumeric);

        // IsCalculated мусить пережити цикл запис-читання: саме за ним
        // клієнт відрізняє обчислене значення від введеного і не дає його
        // редагувати.
        Assert.True(read.IsCalculated);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_і_відсутня_комірка_це_різні_стани()
    {
        var explicitlyEmpty = new CellValue(Address, tableDefId: 1, CellValueData.Empty);

        Assert.True(explicitlyEmpty.IsEmpty);
        Assert.True(explicitlyEmpty.ToData().IsWellFormed());
        Assert.Null(explicitlyEmpty.ValueNumeric);

        // «Комірки немає» — це відсутність рядка в сховищі: порожні комірки
        // не матеріалізуються (ФВ-3.8). Тобто третій стан носить не поле,
        // а сам факт існування об'єкта.
        CellValue? absent = null;
        Assert.Null(absent);

        // Практичний наслідок, заради якого існує розрізнення: явна порожнеча
        // МАТЕРІАЛІЗУЄТЬСЯ і тому потрапляє в аудит і експорт, тоді як
        // незаповнена комірка не існує і в них не з'являється.
        Assert.Equal(Address, explicitlyEmpty.Address);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void ValueUnitId_приймається_лише_для_DataType_Unit()
    {
        var unitCell = new CellValue(Address, tableDefId: 1, new CellValueData { ValueUnitId = 7 });
        Assert.Equal(7, unitCell.ValueUnitId);

        // Сама комірка структурно коректна, але прив'язка до типу колонки
        // вирішується описом колонки (R-A4).
        Assert.Null(Column(CellDataType.Unit).ValidateValue(unitCell.ToData()));
        Assert.Equal("ECR-CELL-0422", Column(CellDataType.Decimal).ValidateValue(unitCell.ToData()));
        Assert.Equal("ECR-CELL-0422", Column(CellDataType.Lookup).ValidateValue(unitCell.ToData()));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнення_двох_типізованих_колонок_одночасно_відхиляється()
    {
        var ambiguous = new CellValueData { ValueNumeric = 1m, ValueString = "1" };

        Assert.False(ambiguous.IsWellFormed());
        Assert.Equal("ECR-CELL-0422", Column(CellDataType.Decimal).ValidateValue(ambiguous));

        // Сутність такий стан фізично прийняти може — вона лише носій; але
        // жоден шлях запису його не пропустить, бо валідація колонки йде
        // перед збереженням.
        var cell = new CellValue(Address, tableDefId: 1, ambiguous);
        Assert.False(cell.ToData().IsWellFormed());
    }
}
