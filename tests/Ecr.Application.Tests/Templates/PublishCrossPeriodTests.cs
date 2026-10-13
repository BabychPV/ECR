// tests/Ecr.Application.Tests/Templates/PublishCrossPeriodTests.cs
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
/// WW-03 (G3): публікація й МІЖПЕРІОДНІ посилання — граф залежностей <c>[Period:#k]</c> і цикли.
/// </summary>
/// <remarks>
/// ⛔ Що було: міжперіодне ребро викидалося з топологічного порядку цілком — цикл через періоди
/// (A у п.1 читає B у п.5, B у п.5 читає A у п.1) публікація не бачила. Абсолютне посилання
/// <c>[Period:#k]</c> тепер ребро порядку (консервативно: формула шаблону одна на всі періоди,
/// тож #k може бути й власним періодом).
/// </remarks>
public sealed class PublishCrossPeriodTests
{
    private readonly RealFormulaEngine _engine = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public void Цикл_між_періодами_через_абсолютні_посилання_відхиляється_з_шляхом()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var left = builder.Column(table, "Left");
        var right = builder.Column(table, "Right");
        builder.Row(table, "7001001", 1);

        var toLeft = builder.Formula(table, "[Period:#5].[Main].[7001001].[Right]", column: left);
        var toRight = builder.Formula(table, "[Period:#1].[Main].[7001001].[Left]", column: right);

        var text = Render(Run(builder.Version()));

        Assert.Contains(ExpressionErrors.Cycle, text, StringComparison.Ordinal);
        Assert.Contains(
            toLeft.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.Contains(
            toRight.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public void Читання_власної_колонки_через_абсолютний_період_відхиляється_як_цикл()
    {
        // У періоді k формула `#k` читає власний результат — справжній цикл.
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var total = builder.Column(table, "Total");
        builder.Row(table, "7001001", 1);
        builder.Formula(table, "[Period:#1].[Main].[7001001].[Total]", column: total);

        Assert.Contains(ExpressionErrors.Cycle, Render(Run(builder.Version())), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public void Абсолютне_посилання_на_іншу_колонку_публікується_і_лягає_в_граф_видом_5()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "Jan", isMonthColumn: true);
        var total = builder.Column(table, "Total");
        builder.Row(table, "7001001", 1);
        builder.Formula(table, "[Period:#3].[Main].[7001001].[Jan] / 1000", column: total);
        var version = builder.Version();

        Assert.Empty(Run(version));

        var dependency = Assert.Single(
            PublishChecks.Dependencies(version, _engine),
            d => d.DependsOnKind == DependencyExtractor.KindCrossPeriodAbsolute);
        Assert.Equal((short)3, dependency.PeriodOffset);
        Assert.Equal("7001001", dependency.RowKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public void Відносний_зсув_власної_колонки_лишається_допустимим_накопичувальний_залишок()
    {
        // Регресія: `[Period:-1]` на власну колонку — звичайна форма перехідного залишку.
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var total = builder.Column(table, "Total");
        builder.Row(table, "7001001", 1);
        builder.Formula(table, "[Period:-1].[Main].[7001001].[Total] + 1", column: total);

        Assert.Empty(Run(builder.Version()));
    }

    private IReadOnlyList<ExpressionDiagnostic> Run(TemplateVersion version)
    {
        var snapshot = PublishChecks.Snapshot(version);

        return PublishChecks.Run(
            version,
            _engine,
            new SnapshotTypeContext(snapshot),
            new SnapshotUnitContext(snapshot, UnitCatalogSnapshot.Empty));
    }

    private static string Render(IEnumerable<ExpressionDiagnostic> diagnostics)
        => string.Join(" | ", diagnostics.Select(d => $"{d.Code} {d.Message}"));
}
