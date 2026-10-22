using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// PRMG: форми <c>REG*</c> над колонкою, що має ціллю групу типів дозволів, не публікуються (v1) - реєстр групи порожній, а в комірці
/// id запису реєстру-члена.
/// </summary>
/// <remarks>
/// Мутації: прибрати перевірку <c>groupRegistryIds.Contains</c> - червоніє «Звичайний_довідник…»; прибрати розбір
/// <c>RegistryFormRegex</c> - червоніє «Формула_без_форми_REG…»; прибрати <c>ReferencesColumn</c> - червоніє «Форма_REG_над_іншою_колонкою…».
/// </remarks>
public sealed class PublishGroupLookupFormulasTests
{
    private const int GroupRegistry = 900;
    private const int PlainRegistry = 18;
    private static readonly DateTime Now = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);

    private static (TemplateVersion Version, TableDef Table) Build(int lookupRegistry, string expression)
    {
        var version = new TemplateVersion(1, "1.0.0.0", 1, Now);
        var sheet = new SheetDef(version.Id, EcrCode.Create("S1"), Name("S1"), 1);
        var table = new TableDef(sheet.Id, EcrCode.Create("T7"), Name("T7"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        var lookup = new ColumnDef(table.Id, EcrCode.Create("PermitNumber"), Name("PermitNumber"), 1, CellDataType.Lookup);
        lookup.SetLookup(lookupRegistry);
        table.AddColumn(lookup);
        table.AddColumn(new ColumnDef(table.Id, EcrCode.Create("Other"), Name("Other"), 2, CellDataType.Decimal));
        table.AddFormula(new FormulaDef(table.Id, FormulaScope.Column, expression, ExpressionDialect.Template));
        sheet.AddTable(table);
        version.AddSheet(sheet);
        return (version, table);
    }

    private static readonly IReadOnlySet<int> Groups = new HashSet<int> { GroupRegistry };

    private static void SetId(object entity, int id)
        => typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static TemplateVersion WithRule(string expression, bool bindToGroupColumn, int lookupRegistry = GroupRegistry)
    {
        var (version, table) = Build(lookupRegistry, "1 + 1");
        var column = table.Columns.First(c => c.Code == "PermitNumber");
        SetId(column, 77);
        table.AddValidationRule(new ValidationRule(
            table.Id, EcrCode.Create("R1"), ValidationSeverity.Error, 0, expression, Name("m"),
            bindToGroupColumn ? 77 : null));
        return version;
    }

    /// <summary>
    /// Правила валідації таблиці: REG* у правилі, що посилається на колонку-групу через [Код] або прив'язане до неї (ColumnDefId), не публікується.
    /// Мутація: прибрати цикл по <c>table.ValidationRules</c> у <c>CheckGroupLookupFormulas</c> - червоніють перші два.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_валідації_з_REGFIELD_над_колонкою_групи_за_посиланням_відхиляється()
    {
        var diagnostic = Assert.Single(PublishChecks.CheckGroupLookupFormulas(WithRule("REGFIELD([PermitNumber], 'Limit') > 0", false), Groups));

        Assert.Equal("err.ECR-TMPL-0422.lookupGroupNoRegistryForms", diagnostic.MessageKey);
        Assert.Contains("R1", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_валідації_прив_язане_до_колонки_групи_з_REGFIELD_відхиляється()
    {
        var diagnostic = Assert.Single(PublishChecks.CheckGroupLookupFormulas(WithRule("REGFIELD(value, 'Limit') > 0", true), Groups));

        Assert.Equal("T7.PermitNumber", diagnostic.MessageParams!["columnCode"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_без_REG_або_над_звичайним_довідником_або_неактивне_проходить()
    {
        Assert.Empty(PublishChecks.CheckGroupLookupFormulas(WithRule("ISBLANK([PermitNumber])", true), Groups));
        Assert.Empty(PublishChecks.CheckGroupLookupFormulas(WithRule("REGFIELD([PermitNumber], 'Limit') > 0", true, PlainRegistry), Groups));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Форма_REGFIELD_над_колонкою_групи_відхиляється_з_ключем_і_кодом_колонки()
    {
        var (version, _) = Build(GroupRegistry, "REGFIELD([PermitNumber], 'Limit')");

        var diagnostic = Assert.Single(PublishChecks.CheckGroupLookupFormulas(version, Groups));

        Assert.Equal(("ECR-TMPL-0422", "err.ECR-TMPL-0422.lookupGroupNoRegistryForms"), (diagnostic.Code, diagnostic.MessageKey));
        Assert.Contains("T7.PermitNumber", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Агрегат_REGSUM_у_нижньому_регістрі_теж_відхиляється()
    {
        var (version, _) = Build(GroupRegistry, "regsum( [ PermitNumber ] , 'Limit')");

        Assert.Single(PublishChecks.CheckGroupLookupFormulas(version, Groups));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Звичайний_довідник_з_тією_самою_формулою_проходить()
    {
        var (version, _) = Build(PlainRegistry, "REGFIELD([PermitNumber], 'Limit')");

        Assert.Empty(PublishChecks.CheckGroupLookupFormulas(version, Groups));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Формула_без_форми_REG_над_колонкою_групи_проходить()
    {
        var (version, _) = Build(GroupRegistry, "IF(ISBLANK([PermitNumber]), 0, 1)");

        Assert.Empty(PublishChecks.CheckGroupLookupFormulas(version, Groups));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Форма_REG_над_іншою_колонкою_у_таблиці_з_колонкою_групи_проходить()
    {
        var (version, _) = Build(GroupRegistry, "REGSUM([Other], 'Limit')");

        Assert.Empty(PublishChecks.CheckGroupLookupFormulas(version, Groups));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Без_груп_у_цілях_версії_перевірка_нічого_не_робить()
    {
        var (version, _) = Build(GroupRegistry, "REGFIELD([PermitNumber], 'Limit')");

        Assert.Empty(PublishChecks.CheckGroupLookupFormulas(version, new HashSet<int>()));
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
