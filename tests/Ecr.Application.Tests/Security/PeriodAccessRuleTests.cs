using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Шість видів правил доступу до періоду (<c>ФВ-2.15</c>) — заміна кнопки
/// <c>Protect</c> чинного рішення.
/// </summary>
/// <remarks>
/// ⛔ У таблиці жив ОДИН механізм із шести: статичний діапазон номерів
/// періодів. Разом із рештою зникало те єдине, що потрібне <c>ФВ-5.20</c>:
/// вікно, взяте з довідника (<c>ApplyPermitMonthLocks</c>).
///
/// ⚠ По тесту на кожен вид — і жоден не кидає «не підтримується». Перелік,
/// половина якого не працює, це та сама мертва гілка, з якою боровся весь
/// <c>A7</c>: механізм оголошений і недосяжний.
/// </remarks>
public sealed class PeriodAccessRuleTests
{
    private const int Version = 1;
    private const int Sheet = 10;
    private const int Table = 20;
    private const int PermitColumn = 31;
    private const int Year = 2026;

    private static readonly IReadOnlySet<int> NoRoles = new HashSet<int>();

    /// <summary>Факти про комірку; усе, чого тест не називає, нейтральне.</summary>
    private static PeriodRuleFacts Facts(
        byte sequence = 3,
        RowKind rowKind = RowKind.Item,
        byte? month = null,
        byte? current = null,
        (DateOnly? From, DateOnly? To)? permit = null,
        (string Expression, bool Holds)? condition = null)
        => new(
            Sheet,
            Table,
            rowKind,
            sequence,
            Year,
            current,
            month,
            permit is { } window
                ? new Dictionary<int, SourceValidity>
                    { [PermitColumn] = new(window.From, window.To) }
                : new Dictionary<int, SourceValidity>(),
            condition is { } expr
                ? new Dictionary<string, bool>(StringComparer.Ordinal) { [expr.Expression] = expr.Holds }
                : new Dictionary<string, bool>(StringComparer.Ordinal));

    private static PeriodRuleOutcome Run(PeriodAccessRuleDef rule, PeriodRuleFacts facts)
        => PeriodAccessRules.Evaluate([rule], facts, NoRoles);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-2.15")]
    public void AlwaysReadOnly_блокує_беззастережно()
    {
        // Заміняє `Range("A6:E90").Locked = True`: жодних умов, у цьому сенс.
        var outcome = Run(
            PeriodAccessRuleDef.AlwaysReadOnly(Version).ForSheet(Sheet),
            Facts());

        Assert.True(outcome.Blocks);
        Assert.Equal(EditDenyReason.BusinessRule, outcome.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-2.15")]
    public void HeaderRows_блокує_заголовки_і_не_чіпає_решти()
    {
        var rule = PeriodAccessRuleDef.HeaderRows(Version).ForSheet(Sheet);

        Assert.True(Run(rule, Facts(rowKind: RowKind.Header)).Blocks);

        // ⛔ Заміняє масиви номерів рядків у `Protection.bas`, які розсипалися
        // при кожній вставці рядка. Вид рядка переживає вставку, номер — ні.
        Assert.False(Run(rule, Facts(rowKind: RowKind.Item)).Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-2.15")]
    public void EditablePeriodOnly_діє_в_межах_діапазону()
    {
        var rule = PeriodAccessRuleDef
            .EditablePeriodOnly(Version, OutOfWindowBehavior.ReadOnly, fromSequence: 2, toSequence: 4)
            .ForSheet(Sheet);

        Assert.False(Run(rule, Facts(sequence: 3)).Blocks);
        Assert.True(Run(rule, Facts(sequence: 5)).Blocks);
        Assert.Equal(EditDenyReason.OutOfAccessWindow, Run(rule, Facts(sequence: 5)).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-2.15")]
    public void RelativeWindow_рахує_від_поточного_періоду()
    {
        var rule = PeriodAccessRuleDef
            .ForRelativeWindow(Version, offset: 1, OutOfWindowBehavior.ReadOnly)
            .ForSheet(Sheet);

        // «Поточний ± 1»: сусідні місяці доступні, позаминулий — ні.
        Assert.False(Run(rule, Facts(sequence: 5, current: 6)).Blocks);
        Assert.True(Run(rule, Facts(sequence: 3, current: 6)).Blocks);

        // ⛔ Без відомого поточного періоду правило НЕ застосовується: узяти
        // «сьогодні» означало б рахувати доступ від годинника сервера, а не
        // від календаря проєкту (`D-77`).
        Assert.False(Run(rule, Facts(sequence: 3, current: null)).Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.20")]
    public void SourceWindow_блокує_місяці_поза_вікном_дозволу()
    {
        // Дозвіл чинний із січня по червень 2026.
        var rule = PeriodAccessRuleDef
            .ForSourceWindow(Version, PermitColumn, OutOfWindowBehavior.ReadOnly)
            .ForSheet(Sheet);

        var permit = ((DateOnly?)new DateOnly(2026, 1, 1), (DateOnly?)new DateOnly(2026, 6, 30));

        // Січень…червень доступні.
        for (byte month = 1; month <= 6; month++)
        {
            Assert.False(
                Run(rule, Facts(month: month, permit: permit)).Blocks,
                $"місяць {month} мав бути доступний");
        }

        // Липень…грудень — ні: це заявлений викид без дозволу, тобто рівно те,
        // за що штрафує регулятор.
        for (byte month = 7; month <= 12; month++)
        {
            var outcome = Run(rule, Facts(month: month, permit: permit));

            Assert.True(outcome.Blocks, $"місяць {month} мав бути заблокований");
            Assert.Equal(EditDenyReason.OutsidePermitWindow, outcome.Reason);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.20")]
    public void SourceWindow_покриває_місяць_навіть_частково()
    {
        // Дозвіл закінчився 15 червня. Червень усе-таки покритий: викид за
        // першу половину місяця стався в межах дозволу, і заборонити ввід
        // означало б утратити реальні дані.
        var rule = PeriodAccessRuleDef
            .ForSourceWindow(Version, PermitColumn, OutOfWindowBehavior.ReadOnly)
            .ForSheet(Sheet);

        var permit = ((DateOnly?)null, (DateOnly?)new DateOnly(2026, 6, 15));

        Assert.False(Run(rule, Facts(month: 6, permit: permit)).Blocks);
        Assert.True(Run(rule, Facts(month: 7, permit: permit)).Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.20")]
    public void SourceWindow_рахує_межу_виключно_на_першому_дні_місяця()
    {
        // ⛔ Єдина пара дат, на якій закрите й напівінтервальне подання дають
        // РІЗНІ відповіді, — межа рівно на першому дні місяця (крок `I.10`).
        // Із закритим порівнянням (`monthStart > to`) червень тут лишався б
        // доступним, хоча дозвіл скінчився 31 травня: місяць отримував зайву
        // добу чинності, і жоден інший місяць цього не показував.
        var rule = PeriodAccessRuleDef
            .ForSourceWindow(Version, PermitColumn, OutOfWindowBehavior.ReadOnly)
            .ForSheet(Sheet);

        var untilJune = ((DateOnly?)null, (DateOnly?)new DateOnly(2026, 6, 1));

        Assert.False(Run(rule, Facts(month: 5, permit: untilJune)).Blocks);
        Assert.True(Run(rule, Facts(month: 6, permit: untilJune)).Blocks);

        // Симетрично знизу: вікно, що відкривається 1 липня, червень не
        // покриває, а липень покриває цілком.
        var fromJuly = ((DateOnly?)new DateOnly(2026, 7, 1), (DateOnly?)null);

        Assert.True(Run(rule, Facts(month: 6, permit: fromJuly)).Blocks);
        Assert.False(Run(rule, Facts(month: 7, permit: fromJuly)).Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.20")]
    public void SourceWindow_не_блокує_рядок_без_обраного_дозволу()
    {
        // ⛔ Зворотне перетворило б «дозвіл ще не обрали» на «нічого не можна
        // заповнити», і заповнити рядок стало б неможливо в принципі —
        // включно з самим вибором дозволу.
        var rule = PeriodAccessRuleDef
            .ForSourceWindow(Version, PermitColumn, OutOfWindowBehavior.ReadOnly)
            .ForSheet(Sheet);

        Assert.False(Run(rule, Facts(month: 11, permit: null)).Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.20")]
    public void SourceWindow_не_чіпає_немісячних_колонок()
    {
        // Правило про МІСЯЦІ; колонка без місяця не належить жодному з них.
        var rule = PeriodAccessRuleDef
            .ForSourceWindow(Version, PermitColumn, OutOfWindowBehavior.ReadOnly)
            .ForSheet(Sheet);

        var permit = ((DateOnly?)new DateOnly(2026, 1, 1), (DateOnly?)new DateOnly(2026, 1, 31));

        Assert.False(Run(rule, Facts(month: null, permit: permit)).Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-2.15")]
    public void Expression_блокує_коли_умова_виконується()
    {
        const string condition = "[Status] = 'Closed'";

        var rule = PeriodAccessRuleDef
            .ForExpression(Version, condition, OutOfWindowBehavior.ReadOnly)
            .ForSheet(Sheet);

        Assert.True(Run(rule, Facts(condition: (condition, true))).Blocks);
        Assert.False(Run(rule, Facts(condition: (condition, false))).Blocks);

        // ⛔ Необчислений вираз НЕ блокує: заборона за невідомістю зробила б
        // будь-яку помилку конфігурації тихою відмовою в доступі, а причину
        // шукали б у правах.
        Assert.False(Run(rule, Facts()).Blocks);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-2.16")]
    [InlineData(OutOfWindowBehavior.ReadOnly, true)]
    [InlineData(OutOfWindowBehavior.Warn, false)]
    [InlineData(OutOfWindowBehavior.AllowWithConfirmation, false)]
    public void Поведінка_поза_вікном_вирішує_чи_блокувати(
        OutOfWindowBehavior behavior, bool blocks)
    {
        // ⛔ `ФВ-2.16` називає три поведінки, і дві з них НЕ забороняють:
        // «дозволити з позначкою» і «дозволити після підтвердження». Якби
        // блокували всі, три поведінки вимоги стали б однією.
        var rule = PeriodAccessRuleDef
            .EditablePeriodOnly(Version, behavior, fromSequence: 1, toSequence: 2)
            .ForSheet(Sheet);

        var outcome = Run(rule, Facts(sequence: 9));

        Assert.Equal(EditDenyReason.OutOfAccessWindow, outcome.Reason);
        Assert.Equal(blocks, outcome.Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-2.16")]
    public void Наявне_правило_з_приховуванням_продовжує_блокувати()
    {
        // ⛔ Нове правило з <c>Hide</c> завести більше не можна (`H-1`,
        // перевіряється в домені). Але в базі лежать рядки, записані
        // раніше, і вони мусять продовжувати блокувати запис.
        //
        // ⚠ Якби <c>Blocks</c> перестав розуміти <c>Hide</c>, правило, яке
        // вчора забороняло правку, сьогодні її дозволило б — і ніхто не
        // помітив би цього, бо жодна помилка не виникла б.
        var rule = PeriodAccessRuleDef
            .EditablePeriodOnly(Version, OutOfWindowBehavior.ReadOnly, fromSequence: 1, toSequence: 2)
            .ForSheet(Sheet);

        // Наслідуємо матеріалізацію з бази: EF пише властивість напряму,
        // обходячи фабрику.
#pragma warning disable CS0618
        typeof(PeriodAccessRuleDef)
            .GetProperty(nameof(PeriodAccessRuleDef.OnOutOfWindow))!
            .SetValue(rule, OutOfWindowBehavior.Hide);
#pragma warning restore CS0618

        var outcome = Run(rule, Facts(sequence: 9));

        Assert.Equal(EditDenyReason.OutOfAccessWindow, outcome.Reason);
        Assert.True(outcome.Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Заборона_виграє_над_попередженням()
    {
        // Те саме правило, що для грантів (`ФВ-6.6`): свідома жорсткість.
        // Альтернатива «конкретніше перекриває загальніше» дає доступ, який
        // ніхто не може пояснити.
        var soft = PeriodAccessRuleDef
            .EditablePeriodOnly(Version, OutOfWindowBehavior.Warn, fromSequence: 1, toSequence: 2)
            .ForSheet(Sheet);

        var hard = PeriodAccessRuleDef.AlwaysReadOnly(Version).ForSheet(Sheet);

        var outcome = PeriodAccessRules.Evaluate([soft, hard], Facts(sequence: 9), NoRoles);

        Assert.True(outcome.Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Правило_ролі_діє_лише_на_носіїв_цієї_ролі()
    {
        var rule = PeriodAccessRuleDef.AlwaysReadOnly(Version).ForSheet(Sheet).ForRole(7);

        Assert.True(PeriodAccessRules.Evaluate([rule], Facts(), new HashSet<int> { 7 }).Blocks);
        Assert.False(PeriodAccessRules.Evaluate([rule], Facts(), new HashSet<int> { 8 }).Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Правила_іншого_аркуша_не_застосовуються()
    {
        var rule = PeriodAccessRuleDef.AlwaysReadOnly(Version).ForSheet(Sheet + 1);

        Assert.False(Run(rule, Facts()).Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Без_правил_доступ_відкритий()
    {
        // ⛔ Зворотне («немає правила — заборонено») зробило б кожен новий
        // аркуш недоступним, і ніхто б не зрозумів чому.
        Assert.False(PeriodAccessRules.Evaluate([], Facts(), NoRoles).Blocks);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Правило_без_обовязкового_параметра_створити_неможливо()
    {
        // ⛔ База ловить це `CK_PAR_Kind`, але база — остання лінія, а не
        // перша. Правило `SourceWindow` без колонки-джерела не блокує нічого
        // і виглядає працездатним.
        Assert.Throws<DomainException>(
            () => PeriodAccessRuleDef.ForRelativeWindow(Version, 0, OutOfWindowBehavior.ReadOnly));

        Assert.Throws<DomainException>(
            () => PeriodAccessRuleDef.ForExpression(Version, "   ", OutOfWindowBehavior.ReadOnly));
    }
}
