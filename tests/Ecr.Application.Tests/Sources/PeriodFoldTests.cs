using Ecr.Application.Sources;
using Ecr.Domain.Entities.External;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Згортання точок періоду (<c>D-118</c>).
/// </summary>
/// <remarks>
/// ⛔ Функція спільна для нічного переносу і для попереднього перегляду
/// (<c>ФВ-13.14</c>). Саме тому вона перевіряється окремо: розбіжність між
/// двома копіями згортки виявилася б не помилкою, а ЧИСЛОМ — перегляд показав
/// би одне, а в комірці опинилося б інше.
/// </remarks>
public sealed class PeriodFoldTests
{
    [Theory]
    [InlineData(AggregationKind.Sum, 12.0)]
    [InlineData(AggregationKind.Avg, 4.0)]
    [InlineData(AggregationKind.Min, -2.0)]
    [InlineData(AggregationKind.Max, 10.0)]
    [InlineData(AggregationKind.First, 4.0)]
    [InlineData(AggregationKind.Last, -2.0)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Кожен_спосіб_згортання_дає_своє_число(AggregationKind kind, double expected)
    {
        // ⚠ Серія навмисно НЕ монотонна і не симетрична: на [1,2,3] шість
        // способів згортання дали б лише чотири різні числа, і підміна одного
        // одним лишилася б непоміченою.
        decimal[] series = [4m, 10m, -2m];

        Assert.Equal((decimal)expected, PeriodFold.Fold(kind, series));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void First_і_Last_беруть_краї_за_ПОРЯДКОМ_а_не_за_величиною()
    {
        // ⛔ «Показник лічильника на кінець періоду» — це остання ТОЧКА, а не
        // найбільше значення. Серія навмисно спадна: реалізація через
        // `Min`/`Max` дала б тут ті самі числа, якби серія була зростною, і
        // тест нічого не ловив би.
        decimal[] descending = [100m, 50m, 7m];

        Assert.Equal(100m, PeriodFold.Fold(AggregationKind.First, descending));
        Assert.Equal(7m, PeriodFold.Fold(AggregationKind.Last, descending));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Середнє_рахується_в_decimal_а_не_в_подвійній_точності()
    {
        // ⚠ `D-30`: `float` заборонений. У подвійній точності це дало б
        // 0.30000000000000004 — і розбіжність спливла б на звірці звіту.
        decimal[] series = [0.1m, 0.2m, 0.6m];

        Assert.Equal(0.3m, PeriodFold.Fold(AggregationKind.Avg, series));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Порожня_серія_це_відмова_а_не_нуль()
    {
        // ⛔ Нуль тут — найгірша з можливих відповідей: «точок не було» і
        // «сума точок дорівнює нулю» — різні стани, і другий законний.
        Assert.Throws<ArgumentException>(() => PeriodFold.Fold(AggregationKind.Sum, []));
    }
}
