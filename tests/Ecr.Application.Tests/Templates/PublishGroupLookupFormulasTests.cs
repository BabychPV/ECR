using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// PRMG: форми <c>REG*</c> над колонкою, що має ціллю групу типів дозволів, не публікуються (v1) - реєстр групи порожній, а в комірці
/// id запису реєстру-члена. Перевірка йде по AST (PG-P3 №3): обходи старого regex-а (регістр коду, кваліфіковані посилання,
/// інша таблиця) закриті, хибні спрацювання на тексті (рядкові літерали, REG* поза читанням колонки) зняті.
/// </summary>
/// <remarks>
/// Мутації: прибрати перевірку <c>groupRegistryIds.Contains</c> - червоніє «Звичайний_довідник…»; прибрати <c>underForm</c> у
/// <c>ScanRegistryForms</c> (рахувати будь-яке посилання) - червоніють «Посилання_поза_аргументами_форми…» і «REG_над_іншою_колонкою…»;
/// замінити AST-обхід на regex по тексту - червоніють усі тести розділу «обходи» і «рядковий літерал».
/// </remarks>
public sealed class PublishGroupLookupFormulasTests
{
    private const int GroupRegistry = 900;
    private const int PlainRegistry = 18;
    private const int GroupColumnId = 10;
    private static readonly DateTime Now = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
    private static readonly IFormulaEngine Engine = new RealFormulaEngine();

    private static void SetId(object entity, int id)
        => typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private sealed record Fixture(TemplateVersion Version, SheetDef Sheet, TableDef Table, ColumnDef Group, FormulaDef Formula);

    private static Fixture Build(int lookupRegistry, string expression)
    {
        var version = new TemplateVersion(1, "1.0.0.0", 1, Now);
        var sheet = new SheetDef(version.Id, EcrCode.Create("S1"), Name("S1"), 1);
        SetId(sheet, 1);
        var table = new TableDef(sheet.Id, EcrCode.Create("T7"), Name("T7"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, 1);
        var lookup = new ColumnDef(table.Id, EcrCode.Create("PermitNumber"), Name("PermitNumber"), 1, CellDataType.Lookup);
        lookup.SetLookup(lookupRegistry);
        SetId(lookup, GroupColumnId);
        table.AddColumn(lookup);
        var other = new ColumnDef(table.Id, EcrCode.Create("Other"), Name("Other"), 2, CellDataType.Decimal);
        SetId(other, 11);
        table.AddColumn(other);
        table.AddRow(new RowDef(table.Id, RowKey.Create("7001001"), 0, Name("r"), RowKind.Item));
        var formula = new FormulaDef(table.Id, FormulaScope.Column, expression, ExpressionDialect.Template);
        table.AddFormula(formula);
        sheet.AddTable(table);
        version.AddSheet(sheet);
        return new Fixture(version, sheet, table, lookup, formula);
    }

    private static readonly IReadOnlySet<int> Groups = new HashSet<int> { GroupRegistry };

    private static IReadOnlyList<ExpressionDiagnostic> Check(TemplateVersion version, IReadOnlySet<int>? groups = null)
        => PublishChecks.CheckGroupLookupFormulas(version, groups ?? Groups, Engine);

    private static TemplateVersion WithRule(string expression, bool bindToGroupColumn, int lookupRegistry = GroupRegistry)
    {
        var fixture = Build(lookupRegistry, "1 + 1");
        fixture.Table.AddValidationRule(new ValidationRule(
            fixture.Table.Id, EcrCode.Create("R1"), ValidationSeverity.Error, 0, expression, Name("m"),
            bindToGroupColumn ? GroupColumnId : null));
        return fixture.Version;
    }

    /// <summary>Друга таблиця того ж аркуша з формулою <paramref name="expression"/>.</summary>
    private static Fixture WithSecondTableFormula(string expression)
    {
        var fixture = Build(GroupRegistry, "1 + 1");
        var second = new TableDef(fixture.Sheet.Id, EcrCode.Create("T8"), Name("T8"), 2, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(second, 2);
        var amount = new ColumnDef(second.Id, EcrCode.Create("Amount"), Name("Amount"), 1, CellDataType.Decimal);
        SetId(amount, 20);
        second.AddColumn(amount);
        second.AddFormula(new FormulaDef(second.Id, FormulaScope.Column, expression, ExpressionDialect.Template));
        fixture.Sheet.AddTable(second);
        return fixture;
    }

    // ---- правила валідації таблиці ----

    /// <summary>
    /// Правила валідації таблиці: REG* у правилі, що посилається на колонку-групу через [Код] або прив'язане до неї (ColumnDefId), не публікується.
    /// Мутація: прибрати цикл по <c>table.ValidationRules</c> у <c>CheckGroupLookupFormulas</c> - червоніють перші два.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_валідації_з_REGFIELD_над_колонкою_групи_за_посиланням_відхиляється()
    {
        var diagnostic = Assert.Single(Check(WithRule("REGFIELD([PermitNumber], 'Limit') > 0", false)));

        Assert.Equal("err.ECR-TMPL-0422.lookupGroupNoRegistryForms", diagnostic.MessageKey);
        Assert.Contains("R1", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_валідації_прив_язане_до_колонки_групи_з_формою_REG_відхиляється()
    {
        var diagnostic = Assert.Single(Check(WithRule("REGCOUNT('PRM_WATER', ROW.Limit > 0) > 0", true)));

        Assert.Equal("T7.PermitNumber", diagnostic.MessageParams!["columnCode"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_прив_язане_до_колонки_групи_без_форми_REG_проходить()
    {
        Assert.Empty(Check(WithRule("ISBLANK([PermitNumber])", true)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_без_REG_або_над_звичайним_довідником_проходить()
    {
        Assert.Empty(Check(WithRule("ISBLANK([PermitNumber])", false)));
        Assert.Empty(Check(WithRule("REGFIELD([PermitNumber], 'Limit') > 0", true, PlainRegistry)));
    }

    /// <summary>
    /// Вимкнене правило (<c>IsActive = false</c>) із REG* над колонкою-групою не блокує публікацію: воно не виконується.
    /// Мутація: прибрати <c>r.IsActive</c> у фільтрі правил - червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Вимкнене_правило_з_REG_над_колонкою_групи_не_блокує_публікацію()
    {
        const string expression = "REGFIELD([PermitNumber], 'Limit') > 0";
        var version = WithRule(expression, true);
        Assert.Single(Check(version)); // контроль: активне відхиляється

        var rule = version.Sheets.SelectMany(s => s.Tables).SelectMany(t => t.ValidationRules).Single();
        rule.Update(ValidationSeverity.Error, 0, expression, Name("m"), GroupColumnId, isActive: false);

        Assert.False(rule.IsActive);
        Assert.Empty(Check(version));
    }

    /// <summary>
    /// Обхід: правило з регістром коду колонки, що відрізняється (резолвер рантайму регістр не розрізняє, старий regex розрізняв).
    /// </summary>
    [Theory]
    [InlineData("REGFIELD([permitnumber], 'Limit') > 0")]
    [InlineData("REGFIELD([T7].[7001001].[PermitNumber], 'Limit') > 0")]
    [InlineData("REGFIELD([S1].[T7].[7001001].[PERMITNUMBER], 'Limit') > 0")]
    [InlineData("IF(1 = 1, REGFIELD([PermitNumber], 'Limit'), 0) > 0")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Обхід_правило_валідації_із_нерегулярним_записом_посилання_відхиляється(string expression)
    {
        Assert.Single(Check(WithRule(expression, false)));
    }

    // ---- формули таблиць ----

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Форма_REGFIELD_над_колонкою_групи_відхиляється_з_ключем_і_кодом_колонки()
    {
        var diagnostic = Assert.Single(Check(Build(GroupRegistry, "REGFIELD([PermitNumber], 'Limit')").Version));

        Assert.Equal(("ECR-TMPL-0422", "err.ECR-TMPL-0422.lookupGroupNoRegistryForms"), (diagnostic.Code, diagnostic.MessageKey));
        Assert.Contains("T7.PermitNumber", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Агрегат_REGSUM_у_нижньому_регістрі_теж_відхиляється()
    {
        Assert.Single(Check(Build(GroupRegistry, "regsum( 'PRM_WATER' , ROW.Limit, ROW.Id = [ PermitNumber ] )").Version));
    }

    /// <summary>
    /// Форми посилання на колонку-групу, які резолвить рантайм. Старий regex `\[\s*Код\s*\]` пропускав саме регістр коду (випадки з
    /// малими літерами); кваліфіковані форми він ловив випадково (підрядок `.[Код]`) - вони тут як контроль AST-обходу.
    /// </summary>
    [Theory]
    [InlineData("REGFIELD([permitnumber], 'Limit')")]
    [InlineData("REGFIELD([7001001].[PermitNumber], 'Limit')")]
    [InlineData("REGFIELD([PERMITNUMBER], 'Limit')")]
    [InlineData("REGFIELD([T7].[7001001].[PermitNumber], 'Limit')")]
    [InlineData("REGFIELD([S1].[T7].[7001001].[PermitNumber], 'Limit')")]
    [InlineData("REGFIELD([s1].[t7].[7001001].[permitnumber], 'Limit')")]
    [InlineData("IF(1 = 1, REGFIELD([T7].[7001001].[PermitNumber], 'Limit'), 0)")]
    [InlineData("REGSUM('PRM_WATER', ROW.Limit, ROW.Id = [T7].[7001001].[PermitNumber])")]
    [InlineData("1 + REGFIND('PRM_WATER', [T7].[7001001].[PermitNumber])")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Обхід_формула_із_кваліфікованим_або_нерегулярним_посиланням_відхиляється(string expression)
    {
        var diagnostic = Assert.Single(Check(Build(GroupRegistry, expression).Version));

        Assert.Equal("T7.PermitNumber", diagnostic.MessageParams!["columnCode"]);
    }

    /// <summary>Обхід: форма в ІНШІЙ таблиці читає колонку-групу через кваліфіковане посилання (старий цикл дивився лише свою таблицю).</summary>
    [Theory]
    [InlineData("REGFIELD([T7].[7001001].[PermitNumber], 'Limit')")]
    [InlineData("REGFIELD([S1].[T7].[7001001].[PermitNumber], 'Limit')")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Обхід_форма_REG_в_іншій_таблиці_над_колонкою_групи_відхиляється(string expression)
    {
        var diagnostic = Assert.Single(Check(WithSecondTableFormula(expression).Version));

        Assert.Equal("T7.PermitNumber", diagnostic.MessageParams!["columnCode"]);
    }

    /// <summary>Обхід: формула місячної колонки, що є колонкою-групою, посилається на неї через <c>{Month}</c>.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Обхід_плейсхолдер_Month_у_формулі_колонки_групи_відхиляється()
    {
        var fixture = Build(GroupRegistry, "REGFIELD([{Month}], 'Limit')");
        fixture.Formula.AssignColumn(GroupColumnId);

        var diagnostic = Assert.Single(Check(fixture.Version));

        Assert.Equal("T7.PermitNumber", diagnostic.MessageParams!["columnCode"]);
    }

    /// <summary>Дві форми над тією самою колонкою - одне зауваження на колонку, а не на форму.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Дві_форми_над_однією_колонкою_дають_одне_зауваження()
    {
        Assert.Single(Check(Build(GroupRegistry, "REGFIELD([PermitNumber], 'A') + REGFIELD([T7].[7001001].[PermitNumber], 'B')").Version));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Звичайний_довідник_з_тією_самою_формулою_проходить()
    {
        Assert.Empty(Check(Build(PlainRegistry, "REGFIELD([PermitNumber], 'Limit')").Version));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Формула_без_форми_REG_над_колонкою_групи_проходить()
    {
        Assert.Empty(Check(Build(GroupRegistry, "IF(ISBLANK([PermitNumber]), 0, 1)").Version));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Форма_REG_над_іншою_колонкою_у_таблиці_з_колонкою_групи_проходить()
    {
        Assert.Empty(Check(Build(GroupRegistry, "REGSUM('PRM_WATER', ROW.Limit, ROW.Id = [Other])").Version));
    }

    /// <summary>
    /// Хибне спрацювання старого regex-а: форма й колонка-група в одній формулі, але форма колонку не читає.
    /// Мутація: прибрати <c>underForm</c> у <c>ScanRegistryForms</c> - червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Посилання_на_колонку_групи_поза_аргументами_форми_проходить()
    {
        Assert.Empty(Check(Build(GroupRegistry, "REGSUM('PRM_WATER', ROW.Limit) + IF(ISBLANK([PermitNumber]), 0, 1)").Version));
    }

    /// <summary>Хибне спрацювання старого regex-а: назва форми лише всередині рядкового літерала.</summary>
    [Theory]
    [InlineData("IF(ISBLANK([PermitNumber]), 'REGFIELD(', 'x')")]
    [InlineData("'REGSUM(' & [PermitNumber]")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Назва_форми_в_рядковому_літералі_не_відхиляється(string expression)
    {
        Assert.Empty(Check(Build(GroupRegistry, expression).Version));
    }

    /// <summary>Вираз, що не розібрався, не дає нової помилки (синтаксис звітує інша перевірка), і перевірка не падає.</summary>
    [Theory]
    [InlineData("REGFIELD([PermitNumber], ")]
    [InlineData("REGFIELD(((")]
    [InlineData("")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Нерозібраний_вираз_не_дає_зауваження_цієї_перевірки(string expression)
    {
        Assert.Empty(Check(Build(GroupRegistry, expression).Version));
        Assert.Empty(Check(WithRule(expression, true)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Без_груп_у_цілях_версії_перевірка_нічого_не_робить()
    {
        Assert.Empty(Check(Build(GroupRegistry, "REGFIELD([PermitNumber], 'Limit')").Version, new HashSet<int>()));
    }

    [Theory]
    [InlineData("PRMG_WATER:ANY")]
    [InlineData("  PRMG_WATER : X")]
    [InlineData("PRMG_WATER")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Фільтр_активності_рядків_над_групою_дозволів_відхиляється(string filter)
    {
        var ex = Assert.Throws<Ecr.Application.Errors.BusinessRuleException>(
            () => Ecr.Application.Permits.Groups.PermitGroupRegistryGuard.RequireNotRowActivityTarget(filter));

        Assert.Equal(("ECR-CALC-0422", "err.ECR-CALC-0422.rowActivityFilterPermitGroup"), (ex.ErrorCode, ex.Details!["messageKey"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("STATUS:RUNNING")]
    [InlineData("PRM_WATER:ANY")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Фільтр_активності_рядків_над_звичайним_довідником_і_без_фільтра_проходить(string? filter)
        => Ecr.Application.Permits.Groups.PermitGroupRegistryGuard.RequireNotRowActivityTarget(filter);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
