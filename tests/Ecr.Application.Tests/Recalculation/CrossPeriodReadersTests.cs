// tests/Ecr.Application.Tests/Recalculation/CrossPeriodReadersTests.cs
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// WW-03: зворотний індекс МІЖПЕРІОДНИХ залежностей — «хто читає змінену колонку з іншого періоду».
/// </summary>
/// <remarks>
/// ⛔ Що було (G2): правка періоду k не перераховувала періоди-читачі — залежність із ненульовим
/// зсувом свідомо не входила в зворотний індекс плану. Це чисті твердження про вибір читачів;
/// постановку задач і пропуск закритих періодів перевіряє <c>CrossPeriodFanOutTests</c>.
/// </remarks>
public sealed class CrossPeriodReadersTests
{
    private const byte CrossPeriod = 3;
    private const byte Absolute = 5;
    private const int WaterReport = 100;
    private const int March = 7;

    private static FormulaDependency Edge(int formulaId, byte kind, int offset, int column = March)
        => FormulaDependency.ForFormula(formulaId, kind, WaterReport, "R1", column, null, (short)offset, 0);

    private static readonly int[] Year = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Абсолютне_посилання_на_період_3_має_читачами_усі_інші_періоди_року()
    {
        var readers = CrossPeriodReaders.ReaderSequences(
            [Edge(10, Absolute, 3)], [(WaterReport, March)], sourceSequence: 3, Year);

        Assert.Equal([1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12], readers.ToArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Правка_періоду_якого_формула_не_називає_читачів_не_дає()
    {
        var readers = CrossPeriodReaders.ReaderSequences(
            [Edge(10, Absolute, 3)], [(WaterReport, March)], sourceSequence: 4, Year);

        Assert.Empty(readers);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Інша_колонка_тієї_ж_таблиці_читачів_не_дає()
    {
        var readers = CrossPeriodReaders.ReaderSequences(
            [Edge(10, Absolute, 3)], [(WaterReport, March + 1)], sourceSequence: 3, Year);

        Assert.Empty(readers);
    }

    [Theory]
    [InlineData(-1, 3, 4)]   // читач p читає p−1: зміна в 3 → читач 4
    [InlineData(-2, 3, 5)]
    [InlineData(1, 3, 2)]    // читач p читає p+1: зміна в 3 → читач 2
    public void Відносний_зсув_читача_виводиться_з_джерела(int offset, int source, int expectedReader)
    {
        var readers = CrossPeriodReaders.ReaderSequences(
            [Edge(10, CrossPeriod, offset)], [(WaterReport, March)], source, Year);

        Assert.Equal([expectedReader], readers.ToArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Читач_якого_в_документі_немає_відкидається()
    {
        var readers = CrossPeriodReaders.ReaderSequences(
            [Edge(10, CrossPeriod, -1)], [(WaterReport, March)], sourceSequence: 3, existingSequences: [3, 5]);

        Assert.Empty(readers);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Колонки_яких_ніхто_не_читає_з_іншого_періоду_до_задачі_не_несуться()
    {
        var relevant = CrossPeriodReaders.ReadAcrossPeriods(
            [Edge(10, Absolute, 3), FormulaDependency.ForFormula(11, 0, WaterReport, "R1", 9, null, null, 0)],
            [(WaterReport, March), (WaterReport, 9), (WaterReport, 8)]);

        Assert.Equal([(WaterReport, March)], relevant);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цілі_читача_містять_залежні_формули_періоду_і_не_містять_знімків()
    {
        // 10 читає #3; 20 читає результат 10 у своєму періоді; 30 — знімок, що читає результат 10.
        var plan = new RecalculationPlan();
        plan.Declare(10, 1);
        plan.Declare(20, 2);
        plan.Declare(30, 3, isSnapshot: true);
        plan.DependsOnFormula(20, 10);
        plan.DependsOnFormula(30, 10);

        var targets = CrossPeriodReaders.Targets(
            [Edge(10, Absolute, 3)],
            [new CrossPeriodSource(3, WaterReport, March)],
            readerSequence: 5, plan, liveFormulas: [10, 20, 30]);

        Assert.Equal([10, 20], targets);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Власний_період_абсолютного_посилання_не_читач_цього_ребра()
    {
        // `[Period:#5]` у періоді 5 — власне читання, його веде план; fan-out його не дублює.
        var plan = new RecalculationPlan();
        plan.Declare(10, 1);

        var targets = CrossPeriodReaders.Targets(
            [Edge(10, Absolute, 5)],
            [new CrossPeriodSource(5, WaterReport, March)],
            readerSequence: 5, plan, liveFormulas: [10]);

        Assert.Empty(targets);
    }
}
