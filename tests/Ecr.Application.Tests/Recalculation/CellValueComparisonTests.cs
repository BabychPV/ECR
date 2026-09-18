// tests/Ecr.Application.Tests/Recalculation/CellValueComparisonTests.cs
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// «Те саме чи інше» — єдине рішення, на якому тримається <c>DAT-02</c> п. 1.
/// </summary>
/// <remarks>
/// ⛔ Директива №14 частина 3. Перерахунок більше не пише незміненого, і ціна
/// помилки в цьому порівнянні — асиметрична: зайве «рівні» ТИХО не запише
/// нового числа (той самий клас мовчазної відмови, що й `A7-63`), а зайве
/// «різні» поверне рівно ту ваду, яку правка й прибирає. Тому кожне поле
/// значення перевіряється окремо, а не одним <c>record</c>-порівнянням: у
/// <see cref="CellValueData"/> їх вісім, і мовчазна втрата одного з них не
/// видна ніяк.
/// </remarks>
public sealed class CellValueComparisonTests
{
    private static CellValueData Calculated(decimal value)
        => new() { ValueNumeric = value, IsCalculated = true };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "DAT-02")]
    public void Комірки_не_було_це_не_рівність()
    {
        // ⛔ `null` = «комірки в базі НЕМАЄ» (R-B4). Вважати її рівною
        // порахованому означало б, що перший прогін на новому рядку не запише
        // нічого, і число з'явиться лише після другої правки входу.
        Assert.False(CellValueComparison.AreEqual(null, Calculated(0m)));
        Assert.False(CellValueComparison.AreEqual(null, Calculated(15m)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "R-B4")]
    public void Явна_порожнеча_не_дорівнює_нулю()
    {
        // ⛔ `IsEmpty` — це «користувач свідомо лишив комірку порожньою», а не
        // нуль. Склеїти їх тут означало б втратити різницю назавжди: у звіт
        // порожнеча й нуль ідуть по-різному.
        var empty = CellValueData.Empty;

        Assert.False(CellValueComparison.AreEqual(empty, Calculated(0m)));
        Assert.True(CellValueComparison.AreEqual(empty, CellValueData.Empty));
    }

    [Theory]
    [InlineData("1.0", "1.00")]
    [InlineData("15", "15.000")]
    [InlineData("0", "0.0")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "DAT-02")]
    public void Масштаб_decimal_не_є_зміною(string stored, string computed)
    {
        // ⚠ `1.0m` і `1.00m` — те саме число з різним масштабом. `decimal.==`
        // дає істину, `ToString()` — різні рядки. Порівнювати поданням означало
        // б переписувати всю таблицю щоразу, коли формула поверне той самий
        // результат з іншим масштабом, — а саме масштаб тут і не стабільний:
        // його задає арифметика виразу, не колонка.
        var a = decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture);
        var b = decimal.Parse(computed, System.Globalization.CultureInfo.InvariantCulture);

        Assert.NotEqual(
            a.ToString(System.Globalization.CultureInfo.InvariantCulture),
            b.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(CellValueComparison.AreEqual(Calculated(a), Calculated(b)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Різні_числа_це_зміна()
        => Assert.False(CellValueComparison.AreEqual(Calculated(15m), Calculated(15.5m)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рядки_порівнюються_Ordinal()
    {
        var stored = new CellValueData { ValueString = "га", IsCalculated = true };

        Assert.True(CellValueComparison.AreEqual(
            stored, new CellValueData { ValueString = "га", IsCalculated = true }));

        // ⚠ Регістр — ЗМІНА. Значення комірки не має культури, і «рівність без
        // урахування регістру» перетворила б правку `га → ГА` на «нічого не
        // сталося».
        Assert.False(CellValueComparison.AreEqual(
            stored, new CellValueData { ValueString = "ГА", IsCalculated = true }));

        // Порожній рядок і відсутність рядка — різні стани.
        Assert.False(CellValueComparison.AreEqual(
            stored, new CellValueData { ValueString = string.Empty, IsCalculated = true }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Дата_порівнюється_за_моментом()
    {
        var stored = new CellValueData
        {
            ValueDate = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            IsCalculated = true,
        };

        Assert.True(CellValueComparison.AreEqual(stored, new CellValueData
        {
            ValueDate = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            IsCalculated = true,
        }));

        // ⚠ Доба різниці — зміна; без цього рядка тест проходив би й на
        // порівнянні самих лише дат без часу.
        Assert.False(CellValueComparison.AreEqual(stored, new CellValueData
        {
            ValueDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            IsCalculated = true,
        }));

        // Секунда різниці — теж зміна: колонка `datetime2`, не `date`.
        Assert.False(CellValueComparison.AreEqual(stored, new CellValueData
        {
            ValueDate = new DateTime(2026, 1, 31, 0, 0, 1, DateTimeKind.Utc),
            IsCalculated = true,
        }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Прапорець_порівнюється_як_значення_а_не_як_наявність()
    {
        var yes = new CellValueData { ValueBool = true, IsCalculated = true };

        Assert.True(CellValueComparison.AreEqual(yes, new CellValueData { ValueBool = true, IsCalculated = true }));

        // ⛔ `false` і `null` — різні стани: друге означає «комірка не логічна».
        // Порівняння через `GetValueOrDefault()` зробило б їх однаковими.
        Assert.False(CellValueComparison.AreEqual(yes, new CellValueData { ValueBool = false, IsCalculated = true }));
        Assert.False(CellValueComparison.AreEqual(
            new CellValueData { ValueBool = false, IsCalculated = true },
            new CellValueData { IsCalculated = true }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "R-A2")]
    public void Введене_людиною_число_не_дорівнює_тому_самому_обчисленому()
    {
        // ⛔ 15 від оператора і 15 від формули — РІЗНІ стани: зріз позначає
        // друге як недоступне для правки (`CalculatedCell`). Пропустити перехід
        // означало б лишити комірку назавжди «людською» з першої ж збіжності
        // чисел — і оператор правив би те, що наступний прогін однаково
        // затре.
        var manual = new CellValueData { ValueNumeric = 15m };

        Assert.False(CellValueComparison.AreEqual(manual, Calculated(15m)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Посилання_на_довідник_і_одиницю_входять_у_порівняння()
    {
        var entry = new CellValueData { ValueRegistryEntryId = 42, IsCalculated = true };
        Assert.True(CellValueComparison.AreEqual(
            entry, new CellValueData { ValueRegistryEntryId = 42, IsCalculated = true }));
        Assert.False(CellValueComparison.AreEqual(
            entry, new CellValueData { ValueRegistryEntryId = 43, IsCalculated = true }));

        var unit = new CellValueData { ValueUnitId = 7, IsCalculated = true };
        Assert.True(CellValueComparison.AreEqual(
            unit, new CellValueData { ValueUnitId = 7, IsCalculated = true }));
        Assert.False(CellValueComparison.AreEqual(
            unit, new CellValueData { ValueUnitId = 8, IsCalculated = true }));
    }
}
