using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// Три різні стани комірки, які **не можна зводити один до одного** (R-B4):
/// «не заповнювали», «заповнили порожнім», «є значення».
/// </summary>
public sealed class CellValueDataTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнене_рівно_одним_значенням_вважається_коректним()
    {
        Assert.True(new CellValueData { ValueNumeric = 12500.000m }.IsWellFormed());
        Assert.True(new CellValueData { ValueString = "Свердловина 7" }.IsWellFormed());
        Assert.True(new CellValueData { ValueBool = false }.IsWellFormed());
        Assert.True(new CellValueData { ValueDate = new DateTime(2026, 1, 31) }.IsWellFormed());
        Assert.True(new CellValueData { ValueRegistryEntryId = 42 }.IsWellFormed());
        Assert.True(new CellValueData { ValueUnitId = 7 }.IsWellFormed());

        // ValueBool = false — це ЗНАЧЕННЯ, а не відсутність значення.
        // Якби перевірка дивилася на «істинність», а не на «наявність»,
        // будь-яке false і будь-який нуль вважалися б незаповненими.
        Assert.True(new CellValueData { ValueNumeric = 0m }.IsWellFormed());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнене_двома_значеннями_одночасно_вважається_некоректним()
    {
        Assert.False(new CellValueData { ValueNumeric = 1m, ValueString = "1" }.IsWellFormed());
        Assert.False(new CellValueData { ValueNumeric = 1m, ValueUnitId = 7 }.IsWellFormed());
        Assert.False(new CellValueData { ValueRegistryEntryId = 42, ValueString = "Свердловина 7" }.IsWellFormed());

        // Порожня комірка без жодного значення теж некоректна, якщо не позначена
        // явно: «нічого не заповнено і не сказано, що порожньо» — це не стан,
        // а незбережений рядок.
        Assert.False(new CellValueData().IsWellFormed());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_не_має_жодного_значення()
    {
        var empty = CellValueData.Empty;

        Assert.True(empty.IsEmpty);
        Assert.True(empty.IsWellFormed());
        Assert.Null(empty.ValueString);
        Assert.Null(empty.ValueNumeric);
        Assert.Null(empty.ValueDate);
        Assert.Null(empty.ValueBool);
        Assert.Null(empty.ValueRegistryEntryId);
        Assert.Null(empty.ValueUnitId);

        // «Порожньо» і «нуль» — різні наміри: IsEmpty із заповненим значенням
        // некоректне, інакше нуль перетворився б на порожнечу при збереженні.
        Assert.False((empty with { ValueNumeric = 0m }).IsWellFormed());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_і_відсутність_комірки_це_різні_стани()
    {
        // «Заповнили порожнім» — комірка існує і матеріалізована.
        CellValueData explicitlyEmpty = CellValueData.Empty;

        // «Не заповнювали» — комірки немає взагалі; порожні комірки не
        // матеріалізуються (ФВ-3.8), тому цей стан представлений відсутністю
        // об'єкта, а не якимось його значенням.
        CellValueData? notFilled = null;

        Assert.NotNull(explicitlyEmpty);
        Assert.Null(notFilled);

        // Головне, заради чого існує розрізнення: клієнт бере DefaultValue
        // колонки лише для «не заповнювали». Для «заповнили порожнім» дефолт
        // підставляти не можна — користувач свідомо сказав «тут нічого немає».
        Assert.True(explicitlyEmpty.IsEmpty);
    }
}
