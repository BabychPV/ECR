using Ecr.Calculations;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Арифметична політика версії методології (директива №05 §5–§6).
/// </summary>
/// <remarks>
/// ⛔ Цих тестів не було, і саме тому помилка прожила так довго.
/// <c>NumericPolicy</c> стверджував, що режими різняться **моментом**
/// округлення — «<c>Legacy</c> округлює кожен крок, <c>Strict</c> лише
/// вихід», — і жоден тест цього не перевіряв. Твердження було вигадане:
/// обидва рахували в <c>decimal</c> і обидва округлювали <c>AwayFromZero</c>.
///
/// ⚠ Вихідні тексти чинної збірки показують протилежне: у всіх 148 файлах
/// немає жодного <c>Math.Round</c>, <c>MidpointRounding</c> чи
/// <c>decimal.Round</c>. Округлення відбувається виключно всередині
/// <c>Round()</c> самої формули.
/// </remarks>
public sealed class NumericPolicyTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16d")]
    public void Legacy_бере_подвійну_точність_і_банківське_округлення()
    {
        var policy = new NumericPolicy(NumericMode.Legacy);

        Assert.IsType<LegacyDoubleArithmetic>(policy.Arithmetic);
        Assert.Equal(RoundingMode.ToEven, policy.Arithmetic.Rounding);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16a")]
    public void Strict_бере_decimal_і_округлення_від_нуля()
    {
        var policy = new NumericPolicy(NumericMode.Strict);

        Assert.IsType<StrictDecimalArithmetic>(policy.Arithmetic);
        Assert.Equal(RoundingMode.AwayFromZero, policy.Arithmetic.Rounding);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Округлення_виходу_йде_за_правилом_режиму_а_не_за_власним()
    {
        // ⛔ Це головне твердження. Друга точка задання правила розійшлася б
        // із першою, і розбіжність була б видима лише як інше число на
        // кожному «.5» — тобто в тому місці звірки, де її найважче пояснити.
        //
        // 0.0000005 на шести знаках: банківське дає 0.000000, від нуля —
        // 0.000001.
        Assert.Equal(0.000000m, new NumericPolicy(NumericMode.Legacy).RoundOutput(0.0000005m));
        Assert.Equal(0.000001m, new NumericPolicy(NumericMode.Strict).RoundOutput(0.0000005m));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Проміжного_округлення_кроку_НЕ_існує()
    {
        // ⛔ Тест, якого бракувало. `NumericPolicy` не має віддавати нічого,
        // що округлює проміжний крок: між формулами чинна система передає
        // результат рядком `G17`, який для `double` круговий — точність не
        // втрачається. Метод із такою роллю означав би, що ми округлюємо там,
        // де еталон не округлює.
        var members = typeof(NumericPolicy)
            .GetMethods()
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain("RoundStep", members);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вихідних_знаків_шість()
    {
        // ⚠ Шість — це ПОДАННЯ. Конвеєр чинної системи несе шістнадцять:
        // проміжні колонки газового складу оголошені `decimal(25,16)`.
        Assert.Equal(6, new NumericPolicy(NumericMode.Legacy).OutputScale);
    }
}
