using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Прив'язка методології до рядків **правилами**, а не списком (ФВ-13.3),
/// упорядкованими за <c>Priority</c>, де перший збіг виграє (ФВ-13.4).
/// </summary>
/// <remarks>
/// ⚠ Тестів у резолвера не було жодного, і матриця трасування показувала
/// `ФВ-13.3`, `ФВ-13.4` і `ФВ-13.8` непокритими. Це не формальність: доки
/// метод повертав самі ключі рядків, «перший збіг виграє» було
/// **неспостережним** — порядок правил не міняв відповіді, і вимогу не можна
/// було ані перевірити, ані порушити.
/// </remarks>
public sealed class MethodologyRuleMatchTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int SubstanceColumn = 11;
    private const int SourceColumn = 12;

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();

    private MethodologyResolver Resolver() => new(_store, _cells, _rows);

    /// <summary>Правило з предикатом і пріоритетом.</summary>
    private static MethodologyRule Rule(string code, string matchJson, int priority)
        => new(methodologyVersionId: 1, EcrCode.Create(code), matchJson, priority);

    /// <summary>
    /// Два рядки: у першого речовина <c>CO2</c>, у другого <c>NOx</c>.
    /// </summary>
    private void Arrange(params MethodologyRule[] rules)
    {
        // ⚠ Сховище віддає правила ВЖЕ впорядкованими за `Priority` — так
        // оголошує порт. Тест сортує їх сам, а не покладається на порядок
        // аргументів: інакше він перевіряв би власну фікстуру.
        _store.GetRulesAsync(1, Arg.Any<CancellationToken>())
              .Returns([.. rules.OrderBy(r => r.Priority)]);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, 700, 3, 2, Period));

        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["R1"] = 1001, ["R2"] = 1002 });

        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns(
        [
            Cell(1001, SubstanceColumn, "CO2"),
            Cell(1001, SourceColumn, "Flare"),
            Cell(1002, SubstanceColumn, "NOx"),
            Cell(1002, SourceColumn, "Flare"),
        ]);
    }

    private static CellRecord Cell(long rowId, int columnId, string value)
        => new(
            new CellAddress(new PeriodKey(Period), rowId, columnId),
            TableDefId: 3,
            new CellValueData { ValueString = value });

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.3")]
    [Trait("Requirement", "ФВ-13.8")]
    public async Task Належність_рядка_визначає_правило_а_не_список_ключів()
    {
        // ⛔ Предикат посилається на ЗНАЧЕННЯ рядка (код речовини), а не на
        // конкретний `RowKey`. У цьому весь сенс вимоги: список ключів
        // розсипався б на першому ж новому рядку, а правило накриває і його.
        Arrange(Rule("CO2", $$"""{"{{SubstanceColumn}}":"CO2"}""", priority: 10));

        var matched = await Resolver().MatchRowsAsync(1, TableInstance, CancellationToken.None);

        Assert.Equal(["R1"], matched);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.4")]
    public async Task Перший_збіг_виграє_і_видно_яке_саме_правило()
    {
        // ⛔ Точне правило має вищий пріоритет (менше число), загальне «вся
        // таблиця» — нижчий. Обидва збігаються з рядком R1; виграти має
        // точне, інакше загальне мовчки перекривало б кожен виняток.
        Arrange(
            Rule("EXACT", $$"""{"{{SubstanceColumn}}":"CO2"}""", priority: 10),
            Rule("ALL", "{}", priority: 100));

        var matches = await Resolver().MatchRowsWithRulesAsync(1, TableInstance, CancellationToken.None);

        Assert.Equal("EXACT", matches.Single(m => m.RowKey == "R1").RuleCode);

        // ⚠ Другий рядок точному правилу не відповідає, тому його закриває
        // загальне — і це видно поіменно, а не за фактом «рядок у списку».
        Assert.Equal("ALL", matches.Single(m => m.RowKey == "R2").RuleCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.4")]
    public async Task Порядок_правил_змінює_переможця()
    {
        // ⛔ Доказ від протилежного: та сама пара правил із переставленими
        // пріоритетами дає ІНШОГО переможця. Без цього тесту «перший збіг
        // виграє» лишалося б словами — перевірити його було б нічим.
        Arrange(
            Rule("EXACT", $$"""{"{{SubstanceColumn}}":"CO2"}""", priority: 100),
            Rule("ALL", "{}", priority: 10));

        var matches = await Resolver().MatchRowsWithRulesAsync(1, TableInstance, CancellationToken.None);

        Assert.Equal("ALL", matches.Single(m => m.RowKey == "R1").RuleCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.8")]
    public async Task Кон_юнкція_кількох_полів_звужує_вибір()
    {
        // Усі пари предиката мають збігтися: правило «CO2 з факела» не
        // накриває NOx із того самого факела.
        Arrange(Rule(
            "CO2_FLARE",
            $$"""{"{{SubstanceColumn}}":"CO2","{{SourceColumn}}":"Flare"}""",
            priority: 10));

        var matched = await Resolver().MatchRowsAsync(1, TableInstance, CancellationToken.None);

        Assert.Equal(["R1"], matched);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Зламаний_предикат_не_збігається_ні_з_чим_і_не_валить_прогін()
    {
        // ⚠ Кинути звідси означало б зупинити розрахунок цілої таблиці через
        // одне зіпсоване правило. Помилку ловить перевірка публікації; тут
        // вона не має валити решту — але й «збігтися з усім» не сміє.
        Arrange(Rule("BROKEN", "{ це не json", priority: 10));

        var matched = await Resolver().MatchRowsAsync(1, TableInstance, CancellationToken.None);

        Assert.Empty(matched);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Рядки_без_жодного_правила_лишаються_непокритими()
    {
        // ⛔ Це і є «покриття» з `ФВ-13.9`: рядок, якого не закрило жодне
        // правило, не потрапляє в розрахунок. Мовчазний нуль у звіті для
        // такого рядка виглядав би як виміряне значення.
        Arrange(Rule("SO2", $$"""{"{{SubstanceColumn}}":"SO2"}""", priority: 10));

        var matched = await Resolver().MatchRowsAsync(1, TableInstance, CancellationToken.None);

        Assert.Empty(matched);
    }
}
