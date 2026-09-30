// tests/Ecr.Domain.Tests/External/RowWindowMapReconfigureTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.External;

/// <summary>
/// Зміна прив'язки вікна рядка (HSE301 A1, CRUD): <see cref="RowWindowMap.Reconfigure"/> тримає ті самі інваріанти,
/// що й <see cref="RowWindowMap.Create"/>, а колонка-ціль — ключ і не змінюється.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <see cref="RowWindowMap.Reconfigure"/> прибрати виклик перевірки колонок — червоніє
/// <see cref="Зміна_не_пускає_вікно_не_з_Date_і_лишає_стан_незайманим"/>.
/// </remarks>
public sealed class RowWindowMapReconfigureTests
{
    private const int Table = 10;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-A1-CRUD")]
    public void Зміна_замінює_вікно_селектор_згортку_й_одиницю_а_ціль_лишає()
    {
        var map = New();

        map.Reconfigure(
            Column("VOLUME", CellDataType.Decimal, 3),
            Column("START_2", CellDataType.Date, 5),
            Column("END_2", CellDataType.Date, 6),
            selector: null,
            RowWindowSummaryKind.Average,
            isStep: true,
            targetUnitId: 502);

        Assert.Equal(
            (3, 5, 6, (int?)null, RowWindowSummaryKind.Average, true, 502),
            (map.TargetColumnDefId, map.StartColumnDefId, map.EndColumnDefId, map.SelectorColumnDefId, map.Summary, map.IsStep, map.TargetUnitId));

        map.SetActive(false);
        Assert.False(map.IsActive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-A1-CRUD")]
    public void Зміна_не_пускає_вікно_не_з_Date_і_лишає_стан_незайманим()
    {
        var map = New();

        var error = Assert.Throws<DomainException>(() => map.Reconfigure(
            Column("VOLUME", CellDataType.Decimal, 3),
            Column("START_2", CellDataType.String, 5),
            Column("END_2", CellDataType.Date, 6),
            selector: null,
            RowWindowSummaryKind.Average,
            isStep: true,
            targetUnitId: 502));

        Assert.Equal("err.ECR-INT-0422.windowColumnsNotDate", error.Details!["messageKey"]);
        Assert.Equal((1, 2, RowWindowSummaryKind.Total, false, 501), (map.StartColumnDefId, map.EndColumnDefId, map.Summary, map.IsStep, map.TargetUnitId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-A1-CRUD")]
    public void Інша_колонка_ціль_відхиляється_бо_вона_ключ_прив_язки()
    {
        var map = New();

        Assert.Throws<InvalidOperationException>(() => map.Reconfigure(
            Column("OTHER", CellDataType.Decimal, 9),
            Column("START", CellDataType.Date, 1),
            Column("END", CellDataType.Date, 2),
            selector: null,
            RowWindowSummaryKind.Total,
            isStep: false,
            targetUnitId: 501));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-A1-CRUD")]
    public void Очищення_джерел_дозволяє_додати_те_саме_значення_селектора_знову()
    {
        var map = New();
        map.AddSource("A", 7, "Tag.One", 501);
        Assert.Throws<DomainException>(() => map.AddSource("a", 7, "Tag.Two", 501));

        map.ClearSources();
        map.AddSource("A", 7, "Tag.Two", 501);

        Assert.Equal("Tag.Two", Assert.Single(map.Sources).SourceField);
    }

    private static RowWindowMap New()
        => RowWindowMap.Create(
            Column("VOLUME", CellDataType.Decimal, 3),
            Column("START", CellDataType.Date, 1),
            Column("END", CellDataType.Date, 2),
            Column("KEY", CellDataType.String, 4),
            RowWindowSummaryKind.Total,
            isStep: false,
            targetUnitId: 501);

    private static ColumnDef Column(string code, CellDataType type, int id)
    {
        var column = new ColumnDef(
            Table, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), id, type);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(column, id);

        return column;
    }
}
