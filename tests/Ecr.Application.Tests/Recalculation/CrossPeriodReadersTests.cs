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

    // ── Q-17.4: введений стовпець власника читають усі екземпляри року ────

    private const byte Cell = 0;

    /// <summary>Залежність-комірка (звичайне посилання <c>[M11]</c>): зсув <c>null</c> або 0.</summary>
    private static FormulaDependency CellEdge(int formulaId, int column = March, short? offset = null)
        => FormulaDependency.ForFormula(formulaId, Cell, WaterReport, "R1", column, null, offset, 0);

    [Theory]
    [InlineData(null)]
    [InlineData((short)0)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "Q-17")]
    public void Q17_введений_стовпець_власника_має_читачами_усі_наявні_періоди_окрім_джерела(short? offset)
    {
        var readers = CrossPeriodReaders.ReaderSequences(
            [CellEdge(10, offset: offset)], [], [(WaterReport, March, (int?)11)], sourceSequence: 11, Year);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12], readers.ToArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "Q-17")]
    public void Q17_не_місячний_введений_стовпець_якоря_читається_так_само()
    {
        var readers = CrossPeriodReaders.ReaderSequences(
            [CellEdge(10)], [], [(WaterReport, March, (int?)null)], sourceSequence: 1, existingSequences: [1, 4, 9]);

        Assert.Equal([4, 9], readers.ToArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "Q-17")]
    public void Q17_читач_якого_в_документі_немає_відкидається()
    {
        var readers = CrossPeriodReaders.ReaderSequences(
            [CellEdge(10)], [], [(WaterReport, March, (int?)11)], sourceSequence: 11, existingSequences: [11]);

        Assert.Empty(readers);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "Q-17")]
    public void Q17_інша_колонка_і_міжперіодна_залежність_нового_правила_не_вмикають()
    {
        // Залежність-комірка на ІНШУ колонку і міжперіодне ребро на ту саму: ні те, ні інше правила власності не торкається.
        var otherColumn = CrossPeriodReaders.ReaderSequences(
            [CellEdge(10, column: March + 1)], [], [(WaterReport, March, (int?)11)], sourceSequence: 11, Year);
        var crossPeriod = CrossPeriodReaders.ReaderSequences(
            [Edge(10, CrossPeriod, -1)], [], [(WaterReport, March, (int?)11)], sourceSequence: 11, Year);

        Assert.Empty(otherColumn);
        Assert.Empty(crossPeriod);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "Q-17")]
    public void Q17_без_введених_стовпців_старе_правило_лишається_ідентичним()
    {
        // Прапор вимкнено ⇒ введених стовпців немає: залежність-комірка зі зсувом 0 читачів не дає, як і доти.
        var readers = CrossPeriodReaders.ReaderSequences(
            [CellEdge(10)], [(WaterReport, March)], sourceSequence: 11, Year);

        Assert.Empty(readers);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "Q-17")]
    public void Q17_у_задачу_читача_несуться_лише_введені_стовпці_що_хтось_читає_коміркою()
    {
        var carried = CrossPeriodReaders.ReadsRoutedInput(
            [CellEdge(10)],
            [(WaterReport, March, (int?)11), (WaterReport, March + 1, (int?)12), (WaterReport + 1, March, (int?)11)]);

        Assert.Equal([(WaterReport, March, (int?)11)], carried);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "Q-17")]
    public void Q17_цілі_читача_введений_стовпець_власника_дає_формули_з_залежністю_зсуву_нуль_і_залежні()
    {
        var plan = new RecalculationPlan();
        plan.Declare(10, 1);
        plan.Declare(20, 2);
        plan.Declare(30, 3, isSnapshot: true);
        plan.DependsOnFormula(20, 10);
        plan.DependsOnFormula(30, 10);

        var targets = CrossPeriodReaders.Targets(
            [CellEdge(10)],
            [new CrossPeriodSource(11, WaterReport, March, OwnerInput: true)],
            readerSequence: 5, plan, liveFormulas: [10, 20, 30]);

        Assert.Equal([10, 20], targets);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "Q-17")]
    public void Q17_період_каскаду_цілей_не_має_а_звичайне_джерело_залежності_зсуву_нуль_не_читає()
    {
        var plan = new RecalculationPlan();
        plan.Declare(10, 1);

        var atSource = CrossPeriodReaders.Targets(
            [CellEdge(10)], [new CrossPeriodSource(11, WaterReport, March, OwnerInput: true)],
            readerSequence: 11, plan, liveFormulas: [10]);
        var plainSource = CrossPeriodReaders.Targets(
            [CellEdge(10)], [new CrossPeriodSource(11, WaterReport, March)],
            readerSequence: 5, plan, liveFormulas: [10]);

        Assert.Empty(atSource);
        Assert.Empty(plainSource);
    }
}
