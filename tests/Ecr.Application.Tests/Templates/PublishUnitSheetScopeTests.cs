using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ФВ-16.8: одиниці посилання `[АРКУШ].[ТАБЛИЦЯ].[РЯДОК].[КОЛОНКА]` беруться з колонки САМЕ цього аркуша.
/// </summary>
/// <remarks>
/// ⛔ Код таблиці не унікальний між аркушами (у Land `T7_00` є і в «Water Report», і в «7.0»). До фіксу
/// <see cref="SnapshotUnitContext"/> і <see cref="SnapshotTypeContext"/> ігнорували <c>SheetCode</c> й брали
/// ПЕРШУ колонку з таким кодом серед усіх таблиць із цим кодом: баланс `SUM(OpeningBalance, January)` отримував
/// одиницю `January` із чужого аркуша, і публікація версії падала хибним ECR-TMPL-4223 (WW-02, RC37).
/// </remarks>
public sealed class PublishUnitSheetScopeTests
{
    private const int CubicMetreUnit = 2;
    private const int ThousandCubicMetreUnit = 51;

    private readonly RealFormulaEngine _engine = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Посилання_з_аркушем_бере_одиницю_колонки_свого_аркуша_а_не_першого_збігу_за_кодом_таблиці()
    {
        // Чужий аркуш стоїть ПЕРШИМ: саме його колонку січня (тис. м3) брав FirstOrDefault без фільтра за аркушем.
        var version = TwoSheets(
            "SUM([Water].[T7_00].[7001001].[OpeningBalance],[Water].[T7_00].[7001001].[January])");

        var diagnostics = Run(version);

        Assert.DoesNotContain(diagnostics, d => d.Code == "ECR-TMPL-4223");
        Assert.Empty(diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Контроль_різні_одиниці_в_межах_одного_аркуша_лишаються_ECR_TMPL_4223()
    {
        // Без фіксу цей випадок теж червоний, але з іншої причини; після фіксу він доводить, що перевірка
        // одиниць не вимкнена: у «Consolidation» січень у тис. м3, а OpeningBalance — у м3 (колонка того ж аркуша).
        var version = TwoSheets(
            "SUM([Consolidation].[T7_00].[7001001].[OpeningBalance],[Consolidation].[T7_00].[7001001].[January])");

        var diagnostics = Run(version);

        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4223");
    }

    /// <summary>
    /// Два аркуші з однаковим кодом таблиці. «Consolidation» (перший): January — тис. м3, OpeningBalance — м3.
    /// «Water»: January і OpeningBalance — м3; Result (м3) із комірочною формулою.
    /// </summary>
    private static TemplateVersion TwoSheets(string expression)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };

        var other = builder.Sheet("Consolidation");
        var otherTable = builder.Table(other, "T7_00");
        builder.Column(otherTable, "OpeningBalance", unitId: CubicMetreUnit);
        builder.Column(otherTable, "January", isMonthColumn: true, unitId: ThousandCubicMetreUnit);
        builder.Row(otherTable, "7001001", 1);

        var water = builder.Sheet("Water");
        var waterTable = builder.Table(water, "T7_00");
        builder.Column(waterTable, "OpeningBalance", unitId: CubicMetreUnit);
        builder.Column(waterTable, "January", isMonthColumn: true, unitId: CubicMetreUnit);
        var result = builder.Column(waterTable, "Result", unitId: CubicMetreUnit);
        builder.Row(waterTable, "7001001", 1);
        var total = builder.Row(waterTable, "7001002", 2);

        builder.Formula(waterTable, expression, FormulaScope.Cell, result, total);

        return builder.Version();
    }

    private static UnitCatalogSnapshot Units()
        => new(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["m3"] = new(CubicMetreUnit, "m3", 2),
                ["thousand_m3"] = new(ThousandCubicMetreUnit, "thousand_m3", 2, FactorToBase: 1000m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal));

    private IReadOnlyList<ExpressionDiagnostic> Run(TemplateVersion version)
    {
        var snapshot = PublishChecks.Snapshot(version);
        return PublishChecks.Run(
            version,
            _engine,
            new SnapshotTypeContext(snapshot),
            new SnapshotUnitContext(snapshot, Units()));
    }
}
