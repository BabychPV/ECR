// tests/Ecr.Domain.Tests/Documents/TableRowTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Рядок таблиці. Головне тут — <c>ModifiedAt</c>: без його підняття
/// <c>RowVersion</c> не змінюється, і оптимістичне блокування **тихо не
/// працює** (B04 §2.4).
/// </summary>
public sealed class TableRowTests
{
    private static readonly PeriodKey Period = PeriodKey.Create(2026, 1);
    private static readonly DateTime Created = new(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc);

    private static TableRow Row(string key = "7001001")
        => new(Period, id: 1001, tableInstanceId: 50, RowKey.Create(key), ordinal: 1, utcNow: Created);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Touch_піднімає_ModifiedAt()
    {
        var row = Row();
        Assert.Equal(Created, row.ModifiedAt);

        var later = Created.AddMinutes(5);
        row.Touch(later);

        Assert.Equal(later, row.ModifiedAt);
        Assert.NotEqual(Created, row.ModifiedAt);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_комірки_рядка_піднімає_ModifiedAt_рядка()
    {
        var row = Row();
        var cell = new CellValue(
            new CellAddress(Period, row.Id, ColumnDefId: 5), tableDefId: 1,
            new CellValueData { ValueNumeric = 100m });

        var editedAt = Created.AddHours(2);

        // Це і є контракт запису: змінив комірку — «доторкнувся» до рядка.
        // Без Touch RowVersion у БД лишиться колишнім, і наступний PATCH із
        // застарілою baseVersion пройде як коректний. Помилка не проявиться
        // ніде, крім втрачених чужих правок.
        cell.Apply(new CellValueData { ValueNumeric = 200m });
        row.Touch(editedAt);

        Assert.Equal(200m, cell.ValueNumeric);
        Assert.Equal(editedAt, row.ModifiedAt);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Soft_delete_лишає_рядок_у_сховищі()
    {
        var row = Row();
        var deletedAt = Created.AddDays(1);

        row.SoftDelete(deletedAt);

        Assert.True(row.IsDeleted);

        // Ідентичність зберігається: на рядок посилаються комірки, аудит і
        // формули. Фізичне видалення зробило б історію нечитабельною (ФВ-7.6).
        Assert.Equal(1001, row.Id);
        Assert.Equal("7001001", row.RowKeyValue);
        Assert.Equal(Period, row.PeriodKey);

        // Видалення — теж зміна, тому ModifiedAt піднімається: інакше клієнт
        // зі старою версією не побачив би конфлікту.
        Assert.Equal(deletedAt, row.ModifiedAt);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void RowKey_динамічного_рядка_є_GUID_у_форматі_N()
    {
        var dynamicKey = RowKey.NewDynamic();
        var row = Row(dynamicKey.Value);

        Assert.Equal(dynamicKey, row.RowKey);
        Assert.Equal(32, row.RowKeyValue.Length);
        Assert.DoesNotContain('-', row.RowKeyValue);

        // Ключ фіксованого рядка приходить із cfg.RowDef і виглядає інакше —
        // обидві форми мусять лишатися валідними RowKey.
        Assert.Equal("7001001", Row().RowKeyValue);
    }
}
