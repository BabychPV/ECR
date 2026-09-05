using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Units;

/// <summary>
/// Маршрут конверсії з чотирьох кроків і, головне, **відмова** при різних
/// розмірностях: це те, що не дає щільності стати «конверсією» (ФВ-16.3, 16.5).
/// </summary>
public sealed class UnitConverterTests
{
    /// <summary>Розмірність «маса»; номери збігаються з <c>uom.Dimension</c> у seed.</summary>
    private const byte Mass = 1;

    /// <summary>Розмірність «об'єм».</summary>
    private const byte Volume = 2;

    /// <summary>Розмірність «температура».</summary>
    private const byte Temperature = 5;

    private readonly UnitConverter _converter = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.3")]
    public void Конверсія_в_ту_саму_одиницю_повертає_значення_без_змін()
    {
        var kg = Make("kg", Mass, isBase: true, factor: 1m, id: 1);

        // Тотожність перевіряється ПЕРШОЮ і без арифметики: множення на
        // одиницю з подальшим діленням на одиницю дало б те саме число, але
        // на decimal із хвостом у 20 знаків це вже не гарантовано.
        Assert.Equal(123.456789m, _converter.Convert(123.456789m, kg, kg, null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Тонни_у_кілограми_множаться_на_тисячу()
    {
        var tonne = Make("t", Mass, isBase: false, factor: 1000m, id: 8);
        var kg = Make("kg", Mass, isBase: true, factor: 1m, id: 1);

        Assert.Equal(2500m, _converter.Convert(2.5m, tonne, kg, null));

        // І назад: маршрут симетричний, інакше «туди-назад» давало б інше
        // число, і звіт не сходився б сам із собою.
        Assert.Equal(2.5m, _converter.Convert(2500m, kg, tonne, null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Явна_конверсія_має_пріоритет_над_маршрутом_через_базову_одиницю()
    {
        var tonne = Make("t", Mass, isBase: false, factor: 1000m, id: 8);
        var kg = Make("kg", Mass, isBase: true, factor: 1m, id: 1);

        // LegacyPinned: коефіцієнт, за яким чинна система колись порахувала
        // вже подані числа.
        var pinned = new UnitConversion(tonne.Id, kg.Id, factor: 1016m, offset: 0m, kind: 1, note: "long ton");

        // ⚠ Маршрут через базу дав би «правильніше» 2500 — і розійшовся б із
        // поданим звітом. Пріоритет явної конверсії існує саме для цього
        // випадку, а не для зручності.
        Assert.Equal(2540m, _converter.Convert(2.5m, tonne, kg, pinned));
        Assert.Equal(2500m, _converter.Convert(2.5m, tonne, kg, null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_градусів_Цельсія_у_Кельвіни_враховує_зсув()
    {
        var celsius = Make("degC", Temperature, isBase: false, factor: 1m, id: 19, offset: 273.15m);
        var kelvin = Make("K", Temperature, isBase: true, factor: 1m, id: 5);

        // ⚠ Зсув — єдина причина, чому формула конверсії не «значення × k».
        // Без нього нуль Цельсія став би нулем Кельвіна, тобто абсолютним
        // нулем: помилка на 273 градуси, яку видно лише тому, хто знає фізику.
        Assert.Equal(273.15m, _converter.Convert(0m, celsius, kelvin, null));
        Assert.Equal(373.15m, _converter.Convert(100m, celsius, kelvin, null));
        Assert.Equal(0m, _converter.Convert(273.15m, kelvin, celsius, null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конверсія_між_різними_розмірностями_кидає_ECR_UOM_0422()
    {
        var kg = Make("kg", Mass, isBase: true, factor: 1m, id: 1);
        var cubicMetre = Make("m3", Volume, isBase: true, factor: 1m, id: 2);

        var error = Assert.Throws<DomainException>(() => _converter.Convert(1m, kg, cubicMetre, null));

        Assert.Equal("ECR-UOM-0422", error.ErrorCode);
        Assert.False(_converter.CanConvert(kg, cubicMetre));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.5")]
    public void Конверсія_кубометрів_у_кілограми_неможлива_бо_це_контекстний_коефіцієнт()
    {
        var cubicMetre = Make("m3", Volume, isBase: true, factor: 1m, id: 2);
        var kg = Make("kg", Mass, isBase: true, factor: 1m, id: 1);

        // ⛔ Щільність залежить від речовини й умов і змінюється з часом, тому
        // вона НЕ конверсія одиниць, а константа методології (ФВ-16.5).
        // Дозволити її тут означало б, що те саме число перетворюється
        // по-різному залежно від того, хто заповнив довідник.
        var error = Assert.Throws<DomainException>(() => _converter.Convert(1000m, cubicMetre, kg, null));
        Assert.Equal("ECR-UOM-0422", error.ErrorCode);

        // Навіть із «явною» конверсією між розмірностями: її не існує, бо на
        // рівні БД це блокує CK_Conv_SameDimension.
        Assert.Contains("розмірност", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.5")]
    public void Конверсія_не_втрачає_точності_на_decimal()
    {
        var gram = Make("g", Mass, isBase: false, factor: 0.001m, id: 9);
        var tonne = Make("t", Mass, isBase: false, factor: 1000m, id: 8);

        // 1 г = 0.000001 т. На double цей ланцюг дав би 9.999999999999999E-07,
        // і сума тисячі таких значень розійшлася б із очікуваною вже в
        // шостому знаку (D-30).
        Assert.Equal(0.000001m, _converter.Convert(1m, gram, tonne, null));

        // Хвіст зберігається повністю: decimal не «округлює до розумного».
        Assert.Equal(0.123456789012345m, _converter.Convert(123456.789012345m, gram, tonne, null));
    }

    /// <summary>Одиниця для сценарію; поля, яких не торкаємось, лишаються типовими.</summary>
    private static Unit Make(
        string code, byte dimensionId, bool isBase, decimal factor, int id, decimal offset = 0m)
    {
        var unit = new Ecr.Domain.Entities.Units.Unit(
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            dimensionId,
            isBase,
            factor,
            offset);

        // Id призначає база; у тесті — руками, бо маршрут конверсії починається
        // саме з порівняння ідентифікаторів.
        typeof(Unit).GetProperty(nameof(Unit.Id))!.SetValue(unit, id);
        return unit;
    }
}
