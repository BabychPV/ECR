using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Перевірки публікації (<c>02b</c> §12) — на <see cref="PublishChecks"/> напряму.
/// </summary>
/// <remarks>
/// ⛔ Файл замінив <c>PublishTemplateVersionTests</c> (директива №09 §8.2).
/// Той викликав ОБРОБНИК, а обробникові потрібні десять портів, тож усі десять
/// стояли моками — включно зі сховищем структури. Мок сховища віддавав граф
/// <c>Sheets → Tables → Columns</c>, зібраний рефлексією в пам'яті, тобто
/// перевірка тримала власну відповідь на питання «як виглядає версія, що
/// публікується». Саме на цій відмінності між моком і реалізацією колись і
/// стояв дефект (`A7 §4.3`): версія з незакритою дужкою публікувалася
/// кодом `204`, а всі одинадцять тестів були зелені.
///
/// ⛔ Один із них був самопідтвердним до кінця: «публікація зберігає розкриті
/// діапазони» рахувала очікуваний перелік <c>RangeExpander</c>'ом у самому
/// тесті й порівнювала його з ним же. Тут той самий факт доводиться
/// <see cref="PublishChecks.Dependencies"/> — тобто тим кодом, який справді
/// наповнює <c>cfg.FormulaDependency</c>.
///
/// ⚠ Поведінку ОБРОБНИКА (стан версії, аудит, транзакція, скидання кешу,
/// відповідь сервера) доводять наскрізні сценарії <c>S-08</c> і <c>S-09</c>
/// (<c>tests/Ecr.Scenarios.Tests/StructureScenarios.cs</c>) на живій базі й
/// живому HTTP. Повторювати їх тут моками означало б займати місце перевірки,
/// яку через них ніхто не напише.
///
/// ⚠ Жодного мока в цьому файлі немає ЗОВСІМ: <see cref="PublishChecks"/> —
/// чиста функція над структурою, а рушій береться справжній
/// (<see cref="RealFormulaEngine"/> — бойовий <c>FormulaEngine</c> із
/// справжніми парсером, обхідником AST і топологічним сортувальником).
/// Тому цикл тут — справжній цикл, а не <c>OrderingResult</c>, підсунутий
/// заглушкою.
/// </remarks>
public sealed class PublishChecksTests
{
    /// <summary>Розмірність «маса»; номери збігаються з <c>uom.Dimension</c> у seed.</summary>
    private const byte Mass = 1;

    /// <summary>Розмірність «об'єм».</summary>
    private const byte Volume = 2;

    private const int KilogramUnit = 1;
    private const int CubicMetreUnit = 2;
    private const int TonneUnit = 8;

    private readonly RealFormulaEngine _engine = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Версія_зі_структурою_і_без_формул_не_дає_жодного_зауваження()
    {
        // ⚠ Мінімальна структура БЕЗ формул відрізняє «версія коректна» від
        // «версії немає»: до появи `CheckStructure` (`S-09`) порожня версія
        // теж не давала зауважень — і публікувалася.
        var fixture = Structure();

        Assert.Empty(Run(fixture.Version));
        Assert.Empty(PublishChecks.CheckStructure(fixture.Version));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Версія_без_жодного_аркуша_відхиляється_із_ECR_TMPL_0422()
    {
        // ⛔ Саме цей стан публікувався кодом `204`, виміряно живим прогоном
        // (директива №09 §6.5, `S-09`). Ні `TemplateVersion.Publish` (лише
        // перемикає стан, ФВ-2.9), ні сервер цієї перевірки не мали.
        var version = new TemplateBuilder { TemplateVersionId = 1 }.Version();

        var diagnostic = Assert.Single(PublishChecks.CheckStructure(version));
        Assert.Equal("ECR-TMPL-0422", diagnostic.Code);

        // ⚠ Перевірки виразів мовчать: формул немає, звітувати нема про що.
        // Без `CheckStructure` мовчали б ОБИДВІ — і публікація проходила б.
        Assert.Empty(Run(version));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Синтаксична_помилка_у_виразі_дає_зауваження()
    {
        var fixture = Structure();
        fixture.Formula("SUM([Jan]");

        Assert.NotEmpty(Run(fixture.Version));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Посилання_на_неіснуючу_колонку_названо_поіменно()
    {
        // Нерезолвлене посилання має зупинити ПУБЛІКАЦІЮ, а не зіпсувати
        // число в проді: у рантаймі воно дало б #REF у звіті через місяць.
        var fixture = Structure();
        fixture.Formula("SUM([Jan], [Apr])");

        Assert.Contains("Apr", Render(Run(fixture.Version)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-2.9")]
    public void Перелік_містить_УСІ_проблеми_а_не_лише_першу()
    {
        // Зупинка на першій помилці змусила б користувача публікувати версію
        // десятки разів, виправляючи по одній.
        var fixture = Structure();
        fixture.Formula("SUM([Apr])");
        fixture.Formula("SUM([May])");
        fixture.Formula("SUM([Jun])");

        var text = Render(Run(fixture.Version));

        Assert.Contains("Apr", text, StringComparison.Ordinal);
        Assert.Contains("May", text, StringComparison.Ordinal);
        Assert.Contains("Jun", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Цикл_із_двох_формул_названо_шляхом_циклу()
    {
        // ⛔ Цикл тут СПРАВЖНІЙ: дві колонкові формули читають колонки одна
        // одної, ребра будує бойовий обхід AST, а порядок — справжній
        // `TopologicalSorter`. Попередник цього тесту підсовував
        // `OrderingResult(false, [], [7, 8, 7])` заглушкою і доводив рівно
        // одне: що текст діагностики склеюється з переданих чисел.
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var left = builder.Column(table, "Left");
        var right = builder.Column(table, "Right");
        builder.Row(table, "7001001", 1);

        var toLeft = builder.Formula(table, "[Right]", column: left);
        var toRight = builder.Formula(table, "[Left]", column: right);

        var version = builder.Version();
        var text = Render(Run(version));

        // Шлях циклу — у відповіді. Прапорця «є цикл» замало: у графі на
        // тисячі формул пошук винуватця без шляху — ручна робота.
        Assert.Contains(ExpressionErrors.Cycle, text, StringComparison.Ordinal);
        Assert.Contains(
            toLeft.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            text, StringComparison.Ordinal);
        Assert.Contains(
            toRight.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Формула_яка_читає_власну_колонку_відхиляється_як_цикл()
    {
        // ⛔ Раніше цей випадок не доходив до графа взагалі: обидва викликачі
        // відсіювали самопосилання перед побудовою, хоч рушій документував,
        // що має бачити їх як цикл. Обіцянка була недосяжна.
        //
        // ⚠ Формула колонки `Total`, яка читає `[Total]`, читає те саме, що
        // пише. Порядку обчислення для неї не існує, і в чинній системі вона
        // мовчки не рахувалася зовсім.
        //
        // ⛔ Попередник тесту зупинявся на півдорозі: він ловив мокнутим
        // рушієм перелік вузлів і перевіряв, що саморебро в ньому Є. Що з
        // цього робить ПУБЛІКАЦІЯ — не перевіряв ніхто, і вона могла б
        // мовчки пропустити таку версію.
        var fixture = Structure();
        fixture.Formula("SUM([Total])");

        Assert.Contains(ExpressionErrors.Cycle, Render(Run(fixture.Version)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.7")]
    public void Несумісні_одиниці_без_CONVERT_відхиляють_публікацію()
    {
        // Дві колонки однієї розмірності, але в різних одиницях: тонни й
        // кілограми. Формула складає їх без CONVERT.
        var version = UnitStructure("[Jan] + [Total]");

        // ⛔ ECR-TMPL-4223 саме при ПУБЛІКАЦІЇ (ФВ-16.7). Помилка одиниць,
        // виявлена під час нічного перерахунку, — це неправильні числа у
        // звіті, які хтось помітить через місяць на звірці. Виявлена тут —
        // червоний екран конфігуратора, який виправляють за хвилину.
        Assert.Contains(
            ExpressionErrors.UnitMismatch,
            Render(Run(version, Units())),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.7")]
    public void Той_самий_вираз_із_явним_CONVERT_проходить()
    {
        // ⚠ Друга половина, без якої перша нічого не означає: механізм не
        // «полегшує» конверсію, він вимагає, щоб автор формули сказав, у чому
        // саме він хоче результат (D-74). Без цього тесту перший був би
        // зеленим і від «відхиляти будь-який вираз з одиницями».
        var version = UnitStructure("CONVERT([Jan], 't', 'kg') + [Total]");

        Assert.Empty(Run(version, Units()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Залежності_несуть_формулу_і_колонку()
    {
        // ⛔ `A7-63`. Таблиця `cfg.FormulaDependency` не наповнювалася НІЧИМ,
        // і наслідок був найтихішим із можливих: граф порожній, каскадний
        // перерахунок не бачить похідних комірок, числа лишаються старими —
        // без жодної помилки на екрані.
        var fixture = Structure();
        fixture.Formula("SUM([Jan])");

        var saved = PublishChecks.Dependencies(fixture.Version, _engine);

        Assert.NotEmpty(saved);

        // ⚠ Кожна залежність названа ФОРМУЛОЮ і колонкою: без цього зворотний
        // індекс «які формули залежать від цієї комірки» не побудувати.
        Assert.All(saved, d =>
        {
            Assert.NotNull(d.FormulaDefId);
            Assert.NotNull(d.ColumnDefId);
            Assert.Equal(0, d.SourceKind);
        });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Діапазон_розкривається_у_конкретні_рядки_а_не_лишається_діапазоном()
    {
        // ⛔ Саме це робить пізнішу зміну `Ordinal` безпечною: у рантаймі
        // діапазонів не існує (`B03` §4), тож порядок рядків після публікації
        // на результат не впливає.
        //
        // ⚠ Очікуваний перелік написаний РУКАМИ. Попередник рахував його
        // `RangeExpander`'ом у самому тесті й порівнював із ним же — тобто
        // лишався зеленим і тоді, коли публікація не розкривала нічого.
        var fixture = Structure();
        fixture.Formula("SUM([Main].[7001001:7001003].[Jan])");

        var expanded = PublishChecks.Dependencies(fixture.Version, _engine)
            .Where(d => d.RowKey is not null)
            .Select(d => d.RowKey)
            .ToList();

        Assert.Equal(["7001001", "7001002", "7001003"], expanded);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Порядок_обчислення_фіксується_перевіркою_а_не_рантаймом()
    {
        // Сортувати граф на кожен запит — витрата, якої бюджет не передбачає
        // (ФВ-9.4), тому порядок проставляється один раз тут.
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var jan = builder.Column(table, "Jan", isMonthColumn: true);
        var total = builder.Column(table, "Total");
        builder.Row(table, "7001001", 1);

        // `Total` читає `Jan`, тож формула на `Jan` мусить обчислюватися
        // ПЕРШОЮ. Порядок тут — не «в порядку оголошення», а результат
        // топологічного сортування справжнього графа.
        var first = builder.Formula(table, "1", column: jan);
        var second = builder.Formula(table, "SUM([Jan])", column: total);

        var version = builder.Version();
        Assert.Empty(Run(version));

        Assert.True(
            first.EvaluationOrder < second.EvaluationOrder,
            $"формула на Jan має обчислюватися першою: {first.EvaluationOrder} vs {second.EvaluationOrder}");
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Прогін усіх перевірок виразів із типами й одиницями.</summary>
    private IReadOnlyList<ExpressionDiagnostic> Run(
        TemplateVersion version, UnitCatalogSnapshot? catalogue = null)
    {
        var snapshot = PublishChecks.Snapshot(version);

        // ⚠ Обидва контексти передаються ЯВНО — рівно як в обробнику. Без
        // контексту типів перевірка №3 мовчки не виконується (`Q-072`), без
        // контексту одиниць — перевірка сумісності (ФВ-16.6).
        return PublishChecks.Run(
            version,
            _engine,
            new SnapshotTypeContext(snapshot),
            new SnapshotUnitContext(snapshot, catalogue ?? UnitCatalogSnapshot.Empty));
    }

    /// <summary>Версія з одним аркушем, однією таблицею, двома колонками і трьома рядками.</summary>
    /// <remarks>
    /// ⚠ Будівник повертається разом із версією: формули додаються ПІСЛЯ, і
    /// їхні ідентифікатори мусять іти з того самого лічильника. Другий
    /// будівник почав би нумерацію спочатку, і <c>FormulaDefId</c> збігся б із
    /// <c>ColumnDefId</c> — граф залежностей замкнувся б сам на себе там, де
    /// циклу немає.
    /// </remarks>
    private static Fixture Structure()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "Jan", isMonthColumn: true);
        builder.Column(table, "Total");
        builder.Row(table, "7001001", 1);
        builder.Row(table, "7001002", 2);
        builder.Row(table, "7001003", 3);

        return new Fixture(builder, builder.Version(), table);
    }

    /// <summary>Готова структура: будівник, версія і таблиця, у яку пишуть формули.</summary>
    private sealed record Fixture(TemplateBuilder Builder, TemplateVersion Version, TableDef Table)
    {
        /// <summary>Додає колонкову формулу на <c>Total</c>.</summary>
        public FormulaDef Formula(string expression)
            => Builder.Formula(Table, expression, FormulaScope.Column, Table.Columns[1]);
    }

    /// <summary>
    /// Версія з одиницями: <c>Jan</c> у тоннах, <c>Total</c> у кілограмах.
    /// </summary>
    /// <remarks>
    /// ⛔ Формула пишеться в ТРЕТЮ колонку, а не в `Total`. Інакше вона читала
    /// б власну колонку, і до зауваження про одиниці додалося б зауваження про
    /// цикл — тест про CONVERT став би червоним із зовсім іншої причини.
    /// Попередник цього не бачив, бо тримав топологію заглушеною успіхом.
    /// </remarks>
    private static TemplateVersion UnitStructure(string expression)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "Jan", isMonthColumn: true, unitId: TonneUnit);
        builder.Column(table, "Total", unitId: KilogramUnit);
        var result = builder.Column(table, "Result", unitId: KilogramUnit);
        builder.Row(table, "7001001", 1);

        builder.Formula(table, expression, column: result);

        return builder.Version();
    }

    /// <summary>Довідник одиниць: кілограм, тонна, кубометр.</summary>
    private static UnitCatalogSnapshot Units()
        => new(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["kg"] = new(KilogramUnit, "kg", Mass),
                ["t"] = new(TonneUnit, "t", Mass, FactorToBase: 1000m),
                ["m3"] = new(CubicMetreUnit, "m3", Volume),
            },
            new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>Тексти діагностик, склеєні для пошуку підрядка.</summary>
    private static string Render(IEnumerable<ExpressionDiagnostic> diagnostics)
        => string.Join(" | ", diagnostics.Select(d => $"{d.Code} {d.Message}"));
}
