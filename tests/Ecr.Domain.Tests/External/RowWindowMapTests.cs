// tests/Ecr.Domain.Tests/External/RowWindowMapTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.External;

/// <summary>
/// Інваріанти прив'язки «атрибут → колонка, вікно = рядок»
/// (FEATURE-HSE301-VIEW §4.4, крок F5, <c>D-171</c>).
/// </summary>
/// <remarks>
/// ⚠ Швидкий рівень: без бази. Що ті самі інваріанти тримає й сама схема
/// (складені ключі на колонки, <c>UQ_RowWindowMap_Target</c>,
/// <c>UQ_RowWindowSource</c>), доводить <c>RowWindowSchemaTests</c> в
/// <c>Ecr.Infrastructure.Tests</c> — вставкою повз домен.
///
/// Мутаційний доказ (F5): зняти перевірку <c>start.DataType</c> у
/// <see cref="RowWindowMap.Create"/> — червоніє
/// <see cref="Початок_вікна_не_Date_відхиляється"/>.
/// </remarks>
public sealed class RowWindowMapTests
{
    private const int Table = 10;
    private const int OtherTable = 11;
    private const int Sm3 = 501;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Прив_язка_бере_таблицю_цілі_й_типові_пороги()
    {
        var map = RowWindowMap.Create(
            Column(Table, "VOLUME_SM3", CellDataType.Decimal, id: 3),
            Column(Table, "START_AT", CellDataType.Date, id: 1),
            Column(Table, "END_AT", CellDataType.Date, id: 2),
            Column(Table, "PI_SOURCE_KEY", CellDataType.Lookup, id: 4),
            RowWindowSummaryKind.Total,
            isStep: true,
            targetUnitId: Sm3);

        Assert.Equal(
            (Table, 3, 1, 2, (int?)4, RowWindowSummaryKind.Total, true, Sm3),
            (map.TableDefId, map.TargetColumnDefId, map.StartColumnDefId, map.EndColumnDefId,
             map.SelectorColumnDefId, map.Summary, map.IsStep, map.TargetUnitId));

        // Пороги — з §4.4, а не нулі CLR: прив'язка без явних порогів мусить
        // поводитися так, як описано, а не «усе Partial» чи «повторів немає».
        Assert.Equal((95m, 7, (TimeSpan?)null, true), (map.MinPercentGood, map.RefetchWithinDays, map.MaxGap, map.IsActive));
        Assert.Empty(map.Sources);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Початок_вікна_не_Date_відхиляється()
    {
        // String-колонка «Start» виглядає як дата в Excel, але вікном стати не
        // може: кожен рядок дав би InvalidWindow лише під час підтягування.
        var error = Assert.Throws<DomainException>(() => Create(
            start: Column(Table, "START_AT", CellDataType.String, id: 1)));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.windowColumnsNotDate");
        Assert.Equal("START_AT", error.Details!["startColumn"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Кінець_вікна_не_Date_відхиляється()
    {
        var error = Assert.Throws<DomainException>(() => Create(
            end: Column(Table, "END_AT", CellDataType.Decimal, id: 2)));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.windowColumnsNotDate");
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    [InlineData(CellDataType.Int)]
    [InlineData(CellDataType.String)]
    [InlineData(CellDataType.Formula)]
    [InlineData(CellDataType.Calculated)]
    public void Ціль_не_Decimal_відхиляється(CellDataType type)
    {
        // Int округлив би об'єм до цілих без слова, Formula/Calculated
        // обчислюються системою — туди інтеграція не пише взагалі.
        var error = Assert.Throws<DomainException>(() => Create(
            target: Column(Table, "VOLUME_SM3", type, id: 3)));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.targetNotDecimal");
        Assert.Equal(type.ToString(), error.Details!["dataType"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Колонка_вікна_з_іншої_таблиці_відхиляється()
    {
        var error = Assert.Throws<DomainException>(() => Create(
            end: Column(OtherTable, "END_AT", CellDataType.Date, id: 2)));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.windowColumnNotInTable");
        Assert.Equal("END_AT", error.Details!["column"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Селектор_з_іншої_таблиці_відхиляється()
    {
        var error = Assert.Throws<DomainException>(() => Create(
            selector: Column(OtherTable, "PI_SOURCE_KEY", CellDataType.Lookup, id: 4)));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.selectorNotInTable");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Та_сама_колонка_на_початку_й_у_кінці_відхиляється()
    {
        var both = Column(Table, "START_AT", CellDataType.Date, id: 1);

        var error = Assert.Throws<DomainException>(() => Create(start: both, end: both));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.windowColumnsSame");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Дубль_селектора_відхиляється_без_урахування_регістру()
    {
        var map = Create();
        map.AddSource("FLARE_HP", sourceEntityId: 7, "Flare HP|Flow", Sm3);

        // ⛔ Не «останній виграє»: два атрибути на те саме значення зробили б
        // об'єм рядка залежним від порядку обходу. Регістр — як у базі (_CI_).
        var error = Assert.Throws<DomainException>(
            () => map.AddSource("flare_hp", sourceEntityId: 8, "Flare HP2|Flow", Sm3));

        AssertKey(error, "ECR-INT-0409", "err.ECR-INT-0409.rowWindowSelectorTaken");
        Assert.Single(map.Sources);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Джерело_для_всіх_рядків_одне()
    {
        var map = Create();
        map.AddSource(selectorValue: null, sourceEntityId: 7, "Flow", Sm3);

        // Порожній рядок — те саме «для всіх рядків», а не окреме значення:
        // інакше UQ у базі (NULL ≠ '') пропустив би друге джерело за замовчуванням.
        var error = Assert.Throws<DomainException>(() => map.AddSource("  ", 8, "Flow2", Sm3));

        AssertKey(error, "ECR-INT-0409", "err.ECR-INT-0409.rowWindowSelectorTaken");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Значення_селектора_без_колонки_селектора_відхиляється()
    {
        var map = Create(selector: null, noSelector: true);

        var error = Assert.Throws<DomainException>(() => map.AddSource("FLARE_HP", 7, "Flow", Sm3));
        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.rowWindowSelectorWithoutColumn");

        // А джерело для всіх рядків — законне: саме так прив'язка без селектора й працює.
        var source = map.AddSource(null, 7, "Flow", Sm3);
        Assert.Null(source.SelectorValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Значення_селектора_обрізається_й_зберігає_атрибут()
    {
        var map = Create();

        var source = map.AddSource("  FLARE_LP ", sourceEntityId: 9, "Flow LP", Sm3);

        Assert.Equal(("FLARE_LP", 9, "Flow LP", Sm3), (source.SelectorValue, source.SourceEntityId, source.SourceField, source.SourceUnitId));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    [InlineData(-0.01, 7, null, "MinPercentGood")]
    [InlineData(100.01, 7, null, "MinPercentGood")]
    [InlineData(95.001, 7, null, "MinPercentGood")]
    [InlineData(95, -1, null, "RefetchWithinDays")]
    [InlineData(95, 367, null, "RefetchWithinDays")]
    [InlineData(95, 7, 0d, "MaxGap")]
    [InlineData(95, 7, -60d, "MaxGap")]
    [InlineData(95, 7, 0.5, "MaxGap")]
    public void Пороги_поза_межами_відхиляються(double minGood, int refetch, double? maxGapSeconds, string parameter)
    {
        var map = Create();

        var error = Assert.Throws<DomainException>(() => map.SetFetchPolicy(
            (decimal)minGood, refetch, maxGapSeconds is { } s ? TimeSpan.FromSeconds(s) : null));

        AssertKey(error, "ECR-INT-0422", "err.ECR-INT-0422.rowWindowPolicyOutOfRange");
        Assert.Equal(parameter, error.Details!["parameter"]);

        // Значення — рядком: резолвер каталогу підставляє лише string.
        Assert.IsType<string>(error.Details["value"]);

        // Відмова нічого не змінила.
        Assert.Equal((95m, 7, (int?)null), (map.MinPercentGood, map.RefetchWithinDays, map.MaxGapSeconds));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Пороги_на_межах_приймаються()
    {
        var map = Create();

        map.SetFetchPolicy(0m, 0, TimeSpan.FromHours(36));
        Assert.Equal((0m, 0, (int?)129600, (TimeSpan?)TimeSpan.FromHours(36)),
            (map.MinPercentGood, map.RefetchWithinDays, map.MaxGapSeconds, map.MaxGap));

        map.SetFetchPolicy(100m, RowWindowMap.MaxRefetchWithinDays, maxGap: null);
        Assert.Equal((100m, 366, (int?)null), (map.MinPercentGood, map.RefetchWithinDays, map.MaxGapSeconds));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F5")]
    public void Провенанс_округлює_покриття_і_знімає_чинність()
    {
        var from = new DateTime(2026, 1, 28, 9, 9, 20, DateTimeKind.Utc);
        var value = new RowWindowValue(
            202601, tableInstanceId: 5, "EF-1", columnDefId: 3, rowWindowMapId: 1, sourceEntityId: 7, "Flow",
            from, from.AddSeconds(930), RowWindowSummaryKind.Total, Sm3, from.AddHours(1));

        Assert.True(value.IsCurrent);

        value.Record(RowWindowValueStatus.Partial, RowWindowComputedBy.Local, 1042.3m, "Sm3/h", 269.258m,
            0.000277777777777778m, pointCount: 12, percentGood: 93.456m, errorCode: null);

        // decimal(5,2) у базі: округлення явне, а не мовчки драйвером.
        Assert.Equal((RowWindowValueStatus.Partial, 93.46m, 269.258m), (value.Status, value.PercentGood, value.ValueTarget));

        value.Supersede();
        Assert.False(value.IsCurrent);

        Assert.Throws<ArgumentOutOfRangeException>(() => value.Record(
            RowWindowValueStatus.Fetched, RowWindowComputedBy.Local, null, null, null, null, 0, 100.5m, null));
    }

    private static RowWindowMap Create(
        ColumnDef? target = null,
        ColumnDef? start = null,
        ColumnDef? end = null,
        ColumnDef? selector = null,
        bool noSelector = false)
        => RowWindowMap.Create(
            target ?? Column(Table, "VOLUME_SM3", CellDataType.Decimal, id: 3),
            start ?? Column(Table, "START_AT", CellDataType.Date, id: 1),
            end ?? Column(Table, "END_AT", CellDataType.Date, id: 2),
            noSelector ? null : selector ?? Column(Table, "PI_SOURCE_KEY", CellDataType.Lookup, id: 4),
            RowWindowSummaryKind.Total,
            isStep: false,
            targetUnitId: Sm3);

    private static ColumnDef Column(int tableDefId, string code, CellDataType type, int id)
    {
        var column = new ColumnDef(
            tableDefId, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), id, type);

        // Id присвоює база; тут — щоб прив'язка мала що зберегти.
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(column, id);
        return column;
    }

    private static void AssertKey(DomainException error, string code, string messageKey)
    {
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(messageKey, error.Details?["messageKey"]);
    }
}
