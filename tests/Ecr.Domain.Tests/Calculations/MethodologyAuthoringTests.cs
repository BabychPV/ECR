// tests/Ecr.Domain.Tests/Calculations/MethodologyAuthoringTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Авторство ВМІСТУ версії методології — констант, правил, виходів і тестів
/// (директива №09, <c>W6</c>).
/// </summary>
/// <remarks>
/// ⛔ Правило «чернетку правлять, опубліковану — ні» (ФВ-13.2) уже було
/// доведене для формул (<see cref="MethodologyDraftEditingTests"/>), і саме
/// тому решта наборів небезпечна: доти константи, правила, виходи й тести
/// потрапляли у версію рівно одним шляхом — копіюванням при клонуванні, — тож
/// перевіряти в них було нічого. Тепер їх заводить API, і кожен набір — це
/// четверті двері в ту саму кімнату.
///
/// ⚠ Ціна помилки не однакова, і жодна не має симптому. Дописана в
/// опубліковану версію КОНСТАНТА змінює числа вже поданих форм; дописане
/// ПРАВИЛО змінює те, які рядки взагалі рахуються; дописаний ТЕСТ перетворює
/// «зелений набір на момент публікації» на «зелений набір сьогодні» — тобто
/// знімає саме ту властивість, заради якої ФВ-9.12 його вимагає.
/// </remarks>
public sealed class MethodologyAuthoringTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int TonneUnit = 3;
    private const int KilogramUnit = 4;

    private static readonly DateTime Now = new(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 1, 1);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.2")]
    public void Опублікована_версія_не_приймає_жодного_з_чотирьох_наборів()
    {
        var version = Draft();
        var constant = version.AddNumericConstant(Code("EF"), 2.5m, TonneUnit);
        var rule = version.AddRule(Code("ALL_ROWS"), "{}", 1);
        var output = version.AddOutput(Code("EMISSION"), TonneUnit, 1);
        var testCase = version.AddTestCase("GOLDEN", "{}", "{}", 0m);

        version.Publish(Reviewer, "Уточнено коефіцієнт", From, testsPassed: true, Now);

        foreach (var forbidden in new Action[]
        {
            () => version.AddNumericConstant(Code("EF2"), 1m, TonneUnit),
            () => version.AddTextConstant(Code("SEASON"), "Summer", ConstantKind.Text),
            () => version.EditConstant(constant, 3m, TonneUnit, null, ConstantKind.Numeric),
            () => version.AddRule(Code("STACKS"), "{}", 2),
            () => version.EditRule(rule, """{"kind":"stack"}""", 2, isActive: false),
            () => version.AddOutput(Code("GSEC"), TonneUnit, 2),
            () => version.EditOutput(output, KilogramUnit, 2),
            () => version.AddTestCase("SECOND", "{}", "{}", 0m),
            () => version.EditTestCase(testCase, "{}", """{"EMISSION":1}""", 0m),
        })
        {
            var error = Assert.Throws<DomainException>(forbidden);
            Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        }

        // ⛔ І сам вміст лишився тим, яким його опублікували: відмова мусить
        // бути ДО зміни, а не після неї.
        Assert.Equal(2.5m, constant.Value);
        Assert.Equal("{}", rule.MatchJson);
        Assert.Equal(TonneUnit, output.UnitId);
        Assert.Equal("{}", testCase.ExpectedJson);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.2")]
    public void Дочірній_запис_чужої_версії_не_правиться_через_цю()
    {
        var version = Draft();
        var foreign = new MethodologyRule(9_999, Code("offshore"), "{}", 5);

        // ⛔ Та сама пастка, що й із формулою: обробник читає версію і правило
        // ОКРЕМИМИ запитами, і без звірки правив би вміст опублікованої версії,
        // тримаючи в руках чернетку — перевірка стану дивилася б не на ту.
        var error = Assert.Throws<DomainException>(
            () => version.EditRule(foreign, """{"kind":"stack"}""", 1, isActive: true));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal("{}", foreign.MatchJson);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-16.1")]
    public void Константа_переписується_обома_полями_одразу()
    {
        var version = Draft();
        var constant = version.AddNumericConstant(Code("EF"), 2.5m, TonneUnit);

        version.EditConstant(constant, null, null, "Summer", ConstantKind.Text);

        // ⛔ Число, одиниця й текст переписуються РАЗОМ. Лишений від числового
        // стану `Value` означав би константу, яка одночасно є числом і текстом:
        // `ConstantResolver` підставив би у вираз число, поруч із яким лежить
        // суперечливий рядок, і жодна перевірка публікації цього не назвала б.
        Assert.Equal(ConstantKind.Text, constant.Kind);
        Assert.Null(constant.Value);
        Assert.Null(constant.UnitId);
        Assert.Equal("Summer", constant.TextValue);

        version.EditConstant(constant, 3.5m, KilogramUnit, null, ConstantKind.Numeric);

        Assert.Equal(ConstantKind.Numeric, constant.Kind);
        Assert.Equal(3.5m, constant.Value);
        Assert.Equal(KilogramUnit, constant.UnitId);
        Assert.Null(constant.TextValue);
        Assert.True(constant.IsResolved);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-16.1")]
    public void Числова_константа_без_числа_або_без_одиниці_відхиляється()
    {
        var version = Draft();
        var constant = version.AddNumericConstant(Code("EF"), 2.5m, TonneUnit);

        // ⛔ Порожнє число — не «нуль за замовчуванням», а рішення, якого ніхто
        // не ухвалив: рівно на цьому тримається `IsResolved` і перелік проблем
        // публікації для трьох відомих дефектів корпусу (`'-'`, `''`).
        var noValue = Assert.Throws<DomainException>(
            () => version.EditConstant(constant, null, TonneUnit, null, ConstantKind.Numeric));
        Assert.Equal("ECR-CALC-0422", noValue.ErrorCode);

        var noUnit = Assert.Throws<DomainException>(
            () => version.EditConstant(constant, 1m, null, null, ConstantKind.Numeric));
        Assert.Equal("ECR-CALC-0422", noUnit.ErrorCode);

        var noText = Assert.Throws<DomainException>(
            () => version.EditConstant(constant, null, null, "   ", ConstantKind.Text));
        Assert.Equal("ECR-CALC-0422", noText.ErrorCode);

        Assert.Equal(2.5m, constant.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.4")]
    public void Порожній_предикат_правила_відхиляється_а_не_зберігається()
    {
        var version = Draft();
        var rule = version.AddRule(Code("ALL_ROWS"), "{}", 1);

        // ⛔ Порожній рядок і `{}` — протилежності, а не синоніми. `{}`
        // збігається з УСІМА рядками, а зламаний предикат `MethodologyResolver`
        // вважає таким, що не збігається НІ З ЧИМ: правило з порожнім рядком
        // мовчки не рахувало б жодного рядка документа, і побачити це можна
        // було б лише за нулями у звіті.
        foreach (var empty in new[] { string.Empty, "   " })
        {
            var error = Assert.Throws<DomainException>(
                () => version.EditRule(rule, empty, 1, isActive: true));

            Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        }

        Assert.Equal("{}", rule.MatchJson);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Режими_чернетки_змінюються_а_опублікованої_ні()
    {
        var version = Draft();

        // ⛔ Доти `SetModes` мав рівно одного викликача — `CloneAsDraft`, який
        // ПЕРЕНОСИТЬ режими джерела. Тобто `Strict` увімкнути було неможливо в
        // принципі: конструктор ставить `Legacy`, клон переносить, третього
        // шляху не існувало — а саме `Strict` відрізняє `null` від тихого нуля
        // при діленні на нуль (`ФВ-9.14`).
        Assert.Equal(NumericMode.Legacy, version.NumericMode);

        version.SetModes(NumericMode.Strict, CalendarMode.Fixed360, TraceLevel.Full);

        Assert.Equal(NumericMode.Strict, version.NumericMode);
        Assert.Equal(CalendarMode.Fixed360, version.CalendarMode);
        Assert.Equal(TraceLevel.Full, version.TraceLevel);

        version.AddOutput(Code("EMISSION"), TonneUnit, 1);
        version.Publish(Reviewer, "Причина", From, testsPassed: true, Now);

        // ⛔ Після публікації — ні. Зміна режиму не видна в жодному рядку
        // формули, а числа змінюються всі: поданий звіт можна було б
        // перерахувати інакше, не змінивши нічого видимого.
        var error = Assert.Throws<DomainException>(
            () => version.SetModes(NumericMode.Legacy, CalendarMode.Actual, TraceLevel.Off));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal(NumericMode.Strict, version.NumericMode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.3")]
    public void Прив_язка_міняє_предикат_і_активність_але_не_свою_адресу()
    {
        var binding = new CalculationBinding(
            tableDefId: 11, columnDefId: 22, methodologyId: 33, outputCode: "EMISSION", matchJson: "{}");

        Assert.True(binding.IsActive);

        binding.Update("""{"kind":"stack"}""", isActive: false);

        Assert.Equal("""{"kind":"stack"}""", binding.MatchJson);
        Assert.False(binding.IsActive);

        // ⛔ Адреса — трійка `(ColumnDefId, MethodologyId, OutputCode)`
        // (`UQ_CalculationBinding`), і змінити її не можна: інша трійка — це
        // інша прив'язка, а не редакція цієї. `TableDefId` теж незмінний, бо
        // виводиться з колонки: прив'язка, у якій вони розійшлися, тихо не
        // спрацьовує — планувальник шукає екземпляри таблиць саме за ним.
        Assert.Equal(11, binding.TableDefId);
        Assert.Equal(22, binding.ColumnDefId);
        Assert.Equal(33, binding.MethodologyId);
        Assert.Equal("EMISSION", binding.OutputCode);

        var error = Assert.Throws<DomainException>(() => binding.Update("  ", isActive: true));
        Assert.Equal("ECR-CFG-0422", error.ErrorCode);
        Assert.Equal("""{"kind":"stack"}""", binding.MatchJson);
    }

    private static MethodologyVersion Draft()
        => new(1, "1.0", CalculationLevel.Configuration, Author, Now);

    private static EcrCode Code(string value) => EcrCode.Create(value);
}
