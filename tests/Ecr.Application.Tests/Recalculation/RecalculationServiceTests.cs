// tests/Ecr.Application.Tests/Recalculation/RecalculationServiceTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>Інкрементний перерахунок за dirty-set (ФВ-3.5, ФВ-12.1).</summary>
public sealed class RecalculationServiceTests
{
    private static readonly PeriodKey Period = new(202601);

    private static RecalculationService Service()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return new(Substitute.For<ICellStore>(),
               Substitute.For<IRowStore>(),
               Substitute.For<IPeriodStore>(),
               Substitute.For<IMetadataCache>(),
               Substitute.For<ITemplateVersionStore>(),
               Substitute.For<IFormulaEngine>(),
               units,
               Substitute.For<IAuditWriter>(),
               new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
               Substitute.For<IUnitOfWork>());
    }

    private static CellAddress Cell(long rowId, int columnId) => new(Period, rowId, columnId);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Перераховується_лише_залежне_піддерево()
    {
        // Формула 1 читає комірку (1001, 11); формула 2 читає (1002, 11);
        // формула 3 читає результат формули 1.
        var plan = new RecalculationPlan();
        plan.Declare(1, evaluationOrder: 0);
        plan.Declare(2, evaluationOrder: 1);
        plan.Declare(3, evaluationOrder: 2);
        plan.DependsOnCell(1, Cell(1001, 11));
        plan.DependsOnCell(2, Cell(1002, 11));
        plan.DependsOnFormula(3, 1);

        var dirty = new DirtySet();
        dirty.Add(Cell(1001, 11));

        var order = Service().Plan(dirty, plan);

        // 3 потрапляє транзитивно — вона читає результат 1.
        // 2 не потрапляє ЗОВСІМ: перераховувати весь документ на кожну зміну
        // означає вийти з бюджету на порядок.
        Assert.Equal([1, 3], order);
        Assert.DoesNotContain(2, order);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-3.5")]
    public void Крос_аркушний_rollup_відкладається_а_не_рахується_синхронно()
    {
        var plan = new RecalculationPlan();
        plan.Declare(1, evaluationOrder: 0);
        plan.Declare(9, evaluationOrder: 1, isCrossSheet: true);
        plan.DependsOnCell(1, Cell(1001, 11));
        plan.DependsOnFormula(9, 1);

        var dirty = new DirtySet();
        dirty.Add(Cell(1001, 11));

        var service = Service();
        var order = service.Plan(dirty, plan);

        // Синхронний rollup означав би, що зміна однієї комірки тягне ланцюг
        // по всьому документу — і користувач чекає на кожному Tab.
        Assert.Equal([1], order);
        Assert.Equal([9], service.DeferredRollups);
        Assert.Equal(TimeSpan.FromMilliseconds(300), service.RollupDebounce);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порядок_береться_з_публікації_а_не_будується_щоразу()
    {
        // Формули оголошені в порядку 3, 1, 2, а EvaluationOrder каже 1, 2, 3.
        var plan = new RecalculationPlan();
        plan.Declare(30, evaluationOrder: 2);
        plan.Declare(10, evaluationOrder: 0);
        plan.Declare(20, evaluationOrder: 1);
        plan.DependsOnCell(30, Cell(1001, 11));
        plan.DependsOnCell(10, Cell(1001, 11));
        plan.DependsOnCell(20, Cell(1001, 11));

        var dirty = new DirtySet();
        dirty.Add(Cell(1001, 11));

        var order = Service().Plan(dirty, plan);

        // ⚠ Порядок узятий із плану, а не з порядку оголошення й не з
        // топологічного сортування «на місці»: сортувати граф на кожен запит —
        // витрата, якої бюджет не передбачає (ФВ-9.4).
        Assert.Equal([10, 20, 30], order);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.5")]
    public void Повторний_прогін_на_тих_самих_даних_дає_ті_самі_числа()
    {
        var plan = new RecalculationPlan();
        plan.Declare(1, evaluationOrder: 0);
        plan.Declare(2, evaluationOrder: 1);
        plan.Declare(3, evaluationOrder: 2);
        plan.DependsOnCell(1, Cell(1001, 11));
        plan.DependsOnCell(2, Cell(1001, 12));
        plan.DependsOnFormula(3, 1);

        var dirty = new DirtySet();
        dirty.Add(Cell(1001, 11));
        dirty.Add(Cell(1001, 12));

        var first = Service().Plan(dirty, plan);
        var second = Service().Plan(dirty, plan);

        // Детермінізм тут не косметика: перерахунок запускається і з
        // інтерактивного шляху, і з нічної задачі, і звірка з еталоном
        // неможлива, якщо порядок залежить від обходу HashSet.
        Assert.Equal(first, second);
        Assert.Equal([1, 2, 3], first);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Усі_формули_плану_віддаються_в_порядку_обчислення()
    {
        // ⛔ Формула 7 не має ЖОДНОГО ребра: у шаблоні вона є, а в графі — ні.
        // Через `DependentsOf`/`DependentsOfFormula` до неї не дотягнутися
        // нічим, бо обидва починаються з насіння.
        var plan = new RecalculationPlan();
        plan.Declare(30, evaluationOrder: 2);
        plan.Declare(10, evaluationOrder: 0);
        plan.Declare(7, evaluationOrder: 1);
        plan.DependsOnCell(30, Cell(1001, 11));
        plan.DependsOnCell(10, Cell(1001, 11));

        var dirty = new DirtySet();
        dirty.Add(Cell(1001, 11));

        Assert.DoesNotContain(7, Service().Plan(dirty, plan));

        // ⚠ Порядок — той самий, що й в інкрементному шляху: узятий із
        // публікації, а не з порядку оголошення.
        Assert.Equal([10, 7, 30], plan.AllFormulas());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.5")]
    public void Порядок_усіх_формул_детермінований_при_однакових_порядках_обчислення()
    {
        // ⚠ Порядок обчислення НЕ унікальний, а обхід словника не має
        // гарантованого порядку: без другого ключа сортування два прогони на
        // тих самих даних могли б писати комірки в різній послідовності.
        var plan = new RecalculationPlan();
        plan.Declare(50, evaluationOrder: 0);
        plan.Declare(20, evaluationOrder: 0);
        plan.Declare(40, evaluationOrder: 0);

        Assert.Equal([20, 40, 50], plan.AllFormulas());
    }
}
