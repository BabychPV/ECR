// tests/Ecr.Application.Tests/Integration/SourceEventRowBuilderTests.cs
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Значення події → значення комірок (HSE301 A5b, FEATURE-HSE301-VIEW §4.7.3–4.7.6) і «початок → період».
/// </summary>
/// <remarks>
/// Мутаційні докази (A5b): у <c>SourceEventPeriods.ToProjectTime</c> прибрати переведення в пояс
/// (повернути <c>utc</c>) — червоніє <see cref="Час_події_лягає_в_комірку_у_поясі_проєкту_до_секунди"/>;
/// у <c>SourceEventRowBuilder.Single</c> повертати перший збіг замість «рівно один» — червоніє
/// <see cref="Неоднозначна_назва_довідника_не_вгадується"/>.
/// </remarks>
public sealed class SourceEventRowBuilderTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");
    private static readonly DateTime Start = new(2026, 1, 28, 9, 9, 20, 987, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 1, 28, 9, 24, 50, DateTimeKind.Utc);

    private static readonly SourceEventLookupEntry V8 = new(81, "V8", ["Flaring V8", "Факельное V8"]);
    private static readonly SourceEventLookupEntry V9 = new(91, "V9", ["Flaring V9"]);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Час_події_лягає_в_комірку_у_поясі_проєкту_до_секунди()
    {
        var built = Build(
            Ev(),
            Field(1, "Start", CellDataType.Date, SourceEventMap.StartAttribute),
            Field(2, "End", CellDataType.Date, SourceEventMap.EndAttribute));

        // 09:09:20.987Z + 05:00 = 14:09:20 (мілісекунди відкинуто), Kind — без зони.
        Assert.Equal(new DateTime(2026, 1, 28, 14, 9, 20, DateTimeKind.Unspecified), Date(built, 1));
        Assert.Equal(new DateTime(2026, 1, 28, 14, 24, 50, DateTimeKind.Unspecified), Date(built, 2));
        Assert.Equal(DateTimeKind.Unspecified, Date(built, 1).Kind);
        Assert.Empty(built.Unmapped);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Число_текст_і_назва_події_пишуться_прямо()
    {
        var built = Build(
            Ev(Attr("Volume", numeric: 269.258m), Attr("Note", text: "night flaring")),
            Field(3, "Volume", CellDataType.Decimal, "Volume"),
            Field(4, "Note", CellDataType.String, "Note"),
            Field(5, "Title", CellDataType.String, SourceEventMap.NameAttribute));

        Assert.Equal(269.258m, Assert.IsType<decimal>(Cell(built, 3).Raw));
        Assert.Equal("night flaring", Cell(built, 4).Raw);
        Assert.Equal("Flaring", Cell(built, 5).Raw);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Атрибута_якого_джерело_не_дало_чи_порожнього_комірка_не_чіпається()
    {
        var built = Build(
            Ev(Attr("Note", text: "  ")),
            Field(3, "Volume", CellDataType.Decimal, "Volume"),
            Field(4, "Note", CellDataType.String, "Note"));

        Assert.Empty(built.Cells);
        Assert.Empty(built.Unmapped);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    [InlineData(SourceEventValueKind.LookupByCode, "v8", 81)]
    [InlineData(SourceEventValueKind.LookupByName, " flaring v8 ", 81)]
    [InlineData(SourceEventValueKind.LookupByName, "ФАКЕЛЬНОЕ V8", 81)]
    public void Запис_довідника_за_кодом_чи_назвою_без_регістру(SourceEventValueKind kind, string value, long expectedId)
    {
        var built = Build(Ev(Attr("Category", text: value)), Lookup(6, "Category", kind));

        Assert.Equal(expectedId, Assert.IsType<long>(Cell(built, 6).Raw));
        Assert.Equal(IntegrationValueKind.Lookup, Cell(built, 6).Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Явна_відповідність_значення_джерела_записові_довідника()
    {
        var field = Lookup(6, "Category", SourceEventValueKind.ValueMap) with
        {
            ValueMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["зима"] = 91 },
        };

        var built = Build(Ev(Attr("Category", text: "Зима")), field);

        Assert.Equal(91L, Cell(built, 6).Raw);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    [InlineData(SourceEventValueKind.LookupByCode, "V10")]
    [InlineData(SourceEventValueKind.LookupByName, "Winter")]
    [InlineData(SourceEventValueKind.ValueMap, "V8")]
    public void Значення_без_відповідника_не_вгадується_а_йде_в_незіставлене(SourceEventValueKind kind, string value)
    {
        var built = Build(Ev(Attr("Category", text: value)), Lookup(6, "Category", kind));

        Assert.Empty(built.Cells);
        Assert.Equal(new SourceEventUnmappedValue("Category", value), Assert.Single(built.Unmapped));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Неоднозначна_назва_довідника_не_вгадується()
    {
        var twin = new SourceEventLookupEntry(92, "V9B", ["Flaring V9"]);
        var field = Lookup(6, "Category", SourceEventValueKind.LookupByName) with { Entries = [V9, twin] };

        var built = Build(Ev(Attr("Category", text: "Flaring V9")), field);

        Assert.Empty(built.Cells);
        Assert.Single(built.Unmapped);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Число_яке_не_число_і_несумісні_одиниці_не_пишуться()
    {
        var withUnits = Field(3, "Volume", CellDataType.Decimal, "Volume") with { SourceUnitId = 1, TargetUnitId = 2 };

        var built = Build(
            Ev(Attr("Volume", numeric: 5m), Attr("Bad", text: "abc")),
            withUnits,
            Field(4, "Bad", CellDataType.Decimal, "Bad"));

        // Одиниць у порожньому довіднику немає — конверсія відмовляє, тихого числа немає.
        Assert.Empty(built.Cells);
        Assert.Equal(["Volume", "Bad"], built.Unmapped.Select(u => u.Column));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Фактична_одиниця_атрибута_інша_за_оголошену_комірка_не_пишеться()
    {
        // ⛔ L3-06 / ФВ-16.9: оголошено Sm3_per_h, джерело повертає Sm3_per_s. До фіксу
        // число лягало в комірку як є (конверсія за ОГОЛОШЕНОЮ одиницею) — помилка ×3600.
        // Мутація: прибрати перевірку IsDeclaredUnit у SourceEventRowBuilder.Number.
        var field = Field(3, "Volume", CellDataType.Decimal, "Volume") with { SourceUnitId = 11, TargetUnitId = 11 };

        var changed = SourceEventRowBuilder.Build(
            Ev(Attr("Volume", numeric: 5m, uom: "Sm3_per_s")), [field], Atyrau, FlowUnits());
        var same = SourceEventRowBuilder.Build(
            Ev(Attr("Volume", numeric: 5m, uom: "Sm3_per_h")), [field], Atyrau, FlowUnits());

        Assert.Empty(changed.Cells);
        Assert.Equal("Volume", Assert.Single(changed.Unmapped).Column);
        Assert.Equal(5m, Assert.IsType<decimal>(Cell(same, 3).Raw));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Значення_у_колонку_типу_якого_запис_не_вміє_йде_в_незіставлене()
    {
        var built = Build(Ev(Attr("Flag", text: "1")), Field(7, "Flag", CellDataType.Bool, "Flag"));

        Assert.Empty(built.Cells);
        Assert.Equal("Flag", Assert.Single(built.Unmapped).Column);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    [InlineData("2026-01-31T18:59:59Z", "2026-01-31")]
    [InlineData("2026-01-31T19:00:00Z", "2026-02-01")]
    public void Межа_доби_пояса_проєкту_Atyrau_плюс_п_ять(string utc, string expectedLocalDate)
    {
        var moment = DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

        var local = SourceEventPeriods.ToProjectTime(moment, Atyrau);

        Assert.Equal(expectedLocalDate, local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── Помічники ────────────────────────────────────────────────────────────

    private static SourceEventBuiltRow Build(SourceEvent ev, params SourceEventFieldPlan[] fields)
        => SourceEventRowBuilder.Build(ev, fields, Atyrau, UnitCatalogSnapshot.Empty);

    private static SourceEvent Ev(params SourceEventAttribute[] attrs)
        => new("E1", "FlareEvent", "Flaring", Start, End, null, null, null, attrs);

    private static SourceEventAttribute Attr(string name, decimal? numeric = null, string? text = null, string? uom = null)
        => new(name, SourceEventAttributeScope.Event, numeric, text, uom);

    private static UnitCatalogSnapshot FlowUnits() => new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sm3_per_s"] = new(12, "Sm3_per_s", DimensionId: 13, FactorToBase: 1m),
            ["Sm3_per_h"] = new(11, "Sm3_per_h", DimensionId: 13, FactorToBase: 0.000277777777777778m),
        },
        new Dictionary<string, int>(StringComparer.Ordinal));

    private static SourceEventFieldPlan Field(int columnId, string code, CellDataType type, string attribute)
        => new(columnId, code, type, attribute, SourceEventAttributeScope.Event, SourceEventValueKind.Direct,
            null, null, [], new Dictionary<string, long>());

    private static SourceEventFieldPlan Lookup(int columnId, string code, SourceEventValueKind kind)
        => new(columnId, code, CellDataType.Lookup, code, SourceEventAttributeScope.Event, kind,
            null, null, [V8, V9], new Dictionary<string, long>());

    private static IntegrationValue Cell(SourceEventBuiltRow built, int columnId)
        => built.Cells.Single(c => c.ColumnDefId == columnId).Value;

    private static DateTime Date(SourceEventBuiltRow built, int columnId)
        => Assert.IsType<DateTime>(Cell(built, columnId).Raw);
}
