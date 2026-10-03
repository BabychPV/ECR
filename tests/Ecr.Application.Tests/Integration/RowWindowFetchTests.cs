using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Чисті правила підтягування за вікном рядка (HSE301 A1, §4.4): вікно в часі проєкту, статуси, повтор.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази (кожен — точковою правкою <see cref="RowWindowFetch"/>):
/// <list type="bullet">
/// <item>вікно береться як UTC без пояса (<c>TryResolveWindow</c>: <c>ConvertTimeToUtc</c> → <c>SpecifyKind(..., Utc)</c>) —
/// червоніє <see cref="Вікно_у_часі_проєкту_Atyrau_переходить_в_UTC"/>;</item>
/// <item>межа порогу покриття <c>&lt;</c> → <c>&lt;=</c> — червоніє <see cref="Покриття_рівно_на_порозі_ще_не_Partial"/>;</item>
/// <item>інтеграл без ділення на знаменник (<c>ConvertFolded</c> для <c>Total</c> → <c>Avg</c>) — червоніє
/// <see cref="Total_Sm3_per_h_за_930_с_лягає_в_Sm3_рівно_0_93"/>.</item>
/// </list>
/// </remarks>
public sealed class RowWindowFetchTests
{
    private const int HourId = 2;
    private const int SecondId = 1;
    private const int StdCubicMetreId = 10;
    private const int StdCubicMetrePerHourId = 11;
    private const int KilogramId = 3;

    private static readonly TimeZoneInfo Atyrau = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();
    private static readonly DateTime Now = new(2026, 2, 5, 9, 0, 0, DateTimeKind.Utc);

    private static UnitCatalogSnapshot Catalog() => new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
        {
            ["s"] = new(SecondId, "s", DimensionId: 4, FactorToBase: 1m),
            ["h"] = new(HourId, "h", DimensionId: 4, FactorToBase: 3600m),
            ["kg"] = new(KilogramId, "kg", DimensionId: 1, FactorToBase: 1m),
            ["Sm3"] = new(StdCubicMetreId, "Sm3", DimensionId: 12, FactorToBase: 1m),
            ["Sm3_per_h"] = new(StdCubicMetrePerHourId, "Sm3_per_h", DimensionId: 13, FactorToBase: 0.000277777777777778m),
        },
        new Dictionary<string, int>(StringComparer.Ordinal) { [$"{StdCubicMetreId}|{HourId}"] = StdCubicMetrePerHourId });

    // Одиниця джерела за замовчуванням не названа: порівнювати нема з чим (ФВ-16.12);
    // звірку фактичної одиниці з оголошеною (ФВ-16.9) перевіряють окремі тести.
    private static WindowResult Result(
        decimal? value, decimal? percentGood = 100m, string? error = null, string? sourceUnitSymbol = null)
        => new(value, sourceUnitSymbol, 4, percentGood, WindowComputedBy.Local, [], error);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Вікно_у_часі_проєкту_Atyrau_переходить_в_UTC()
    {
        var ok = RowWindowFetch.TryResolveWindow(
            new DateTime(2026, 1, 28, 14, 9, 20), new DateTime(2026, 1, 28, 14, 24, 50), Atyrau, out var span);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 1, 28, 9, 9, 20), span.FromUtc);
        Assert.Equal(new DateTime(2026, 1, 28, 9, 24, 50), span.ToUtc);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    [InlineData(null, 5)]
    [InlineData(5, null)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    public void Порожня_межа_і_End_не_пізніше_Start_це_недійсне_вікно(int? startHour, int? endHour)
    {
        DateTime? Hour(int? h) => h is { } v ? new DateTime(2026, 1, 28, v, 0, 0) : null;

        Assert.False(RowWindowFetch.TryResolveWindow(Hour(startHour), Hour(endHour), Atyrau, out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Вікно_довше_32_діб_недійсне_а_рівно_32_доби_дійсне()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0);

        Assert.True(RowWindowFetch.TryResolveWindow(start, start.AddDays(32), Atyrau, out _));
        Assert.False(RowWindowFetch.TryResolveWindow(start, start.AddDays(32).AddSeconds(1), Atyrau, out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Час_у_пропущеній_годині_переходу_поясу_недійсний()
    {
        var berlin = SiteTimeZone.Create("Europe/Berlin").ToTimeZoneInfo();

        // 2026-03-29 02:30 у Берліні не існує (перехід 02:00 → 03:00).
        Assert.False(RowWindowFetch.TryResolveWindow(
            new DateTime(2026, 3, 29, 2, 30, 0), new DateTime(2026, 3, 29, 4, 0, 0), berlin, out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Total_Sm3_per_h_за_930_с_лягає_в_Sm3_рівно_0_93()
    {
        var fold = RowWindowFetch.Fold(
            RowWindowSummaryKind.Total, Result(3348m), 95m, StdCubicMetrePerHourId, StdCubicMetreId, Catalog());

        Assert.Equal(RowWindowValueStatus.Fetched, fold.Status);
        Assert.Equal(0.93m, fold.ValueTarget);
        Assert.Equal(3348m, fold.ValueSource);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Fold_ФактичнаОдиницяІнша_SourceError_а_не_значення_хибне_в_24_рази()
    {
        // Оголошено Sm3_per_h, а джерело повертає Sm3_per_s (UOM атрибута змінили в PI).
        // ⛔ L3-06 / ФВ-16.9: до фіксу — Fetched зі значенням, конвертованим за ОГОЛОШЕНОЮ
        // одиницею. Мутація: прибрати перевірку IsDeclaredUnit у RowWindowFetch.Fold.
        var catalog = Catalog();
        var units = new Dictionary<string, UnitRef>(catalog.Units, StringComparer.OrdinalIgnoreCase)
        {
            ["Sm3_per_s"] = new(12, "Sm3_per_s", DimensionId: 13, FactorToBase: 1m),
        };

        var fold = RowWindowFetch.Fold(
            RowWindowSummaryKind.Total,
            Result(3348m, sourceUnitSymbol: "Sm3_per_s"),
            95m,
            StdCubicMetrePerHourId,
            StdCubicMetreId,
            new UnitCatalogSnapshot(units, catalog.Derived));

        Assert.Equal(
            (RowWindowValueStatus.SourceError, "ECR-INT-0422", (decimal?)null, "Sm3_per_s"),
            (fold.Status, fold.ErrorCode, fold.ValueTarget, fold.SourceUnitSymbol));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Fold_ФактичнаОдиницяЗбігається_Fetched()
    {
        var fold = RowWindowFetch.Fold(
            RowWindowSummaryKind.Total,
            Result(3348m, sourceUnitSymbol: "sm3_PER_h"),
            95m,
            StdCubicMetrePerHourId,
            StdCubicMetreId,
            Catalog());

        Assert.Equal((RowWindowValueStatus.Fetched, 0.93m), (fold.Status, fold.ValueTarget));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Fold_Count_не_звіряє_одиниці()
    {
        var fold = RowWindowFetch.Fold(
            RowWindowSummaryKind.Count, Result(4m, sourceUnitSymbol: "kg"), 95m, StdCubicMetrePerHourId, StdCubicMetreId, Catalog());

        Assert.Equal((RowWindowValueStatus.Fetched, 4m), (fold.Status, fold.ValueTarget));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Покриття_нижче_порога_Partial_але_значення_записується()
    {
        var fold = RowWindowFetch.Fold(
            RowWindowSummaryKind.Average, Result(5m, percentGood: 94.99m), 95m, StdCubicMetreId, StdCubicMetreId, Catalog());

        Assert.Equal((RowWindowValueStatus.Partial, 5m), (fold.Status, fold.ValueTarget));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Покриття_рівно_на_порозі_ще_не_Partial()
    {
        var fold = RowWindowFetch.Fold(
            RowWindowSummaryKind.Average, Result(5m, percentGood: 95m), 95m, StdCubicMetreId, StdCubicMetreId, Catalog());

        Assert.Equal(RowWindowValueStatus.Fetched, fold.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Без_значення_NoData_і_нічого_для_комірки()
    {
        var fold = RowWindowFetch.Fold(
            RowWindowSummaryKind.Total, Result(null, percentGood: 0m), 95m, StdCubicMetrePerHourId, StdCubicMetreId, Catalog());

        Assert.Equal((RowWindowValueStatus.NoData, (decimal?)null), (fold.Status, fold.ValueTarget));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Відмова_джерела_SourceError_з_кодом_а_не_значення()
    {
        var fold = RowWindowFetch.Fold(
            RowWindowSummaryKind.Total, Result(1m, error: "ECR-INT-0503"), 95m, StdCubicMetrePerHourId, StdCubicMetreId, Catalog());

        Assert.Equal((RowWindowValueStatus.SourceError, "ECR-INT-0503", (decimal?)null), (fold.Status, fold.ErrorCode, fold.ValueTarget));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Різні_розмірності_це_SourceError_з_кодом_конверсії_а_не_виняток_задачі()
    {
        var fold = RowWindowFetch.Fold(
            RowWindowSummaryKind.Average, Result(5m), 95m, KilogramId, StdCubicMetreId, Catalog());

        Assert.Equal(RowWindowValueStatus.SourceError, fold.Status);
        Assert.NotNull(fold.ErrorCode);
        Assert.Null(fold.ValueTarget);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Count_не_конвертується_між_одиницями()
    {
        var fold = RowWindowFetch.Fold(
            RowWindowSummaryKind.Count, Result(7m), 95m, KilogramId, StdCubicMetreId, Catalog());

        Assert.Equal((RowWindowValueStatus.Fetched, 7m), (fold.Status, fold.ValueTarget));
    }

    // ── NeedsFetch ───────────────────────────────────────────────────────────

    private static readonly RowWindowSpan Span = new(new DateTime(2026, 1, 28, 9, 0, 0), new DateTime(2026, 1, 28, 10, 0, 0));

    private static RowWindowValue Stored(
        RowWindowValueStatus status, DateTime? from = null, DateTime? to = null, DateTime? retrievedAt = null)
    {
        var value = new RowWindowValue(
            202601, 1, "R1", 5, 1, 1, "tag", from ?? Span.FromUtc, to ?? Span.ToUtc,
            RowWindowSummaryKind.Total, 10, retrievedAt ?? Span.ToUtc.AddMinutes(10));
        value.Record(status, RowWindowComputedBy.Local, 1m, "Sm3", 1m, 1m, 3, 100m, null);

        return value;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Ще_не_підтягували_чи_вікно_змінилося_треба_підтягувати()
    {
        Assert.True(RowWindowFetch.NeedsFetch(null, Span, 7, Now));
        Assert.True(RowWindowFetch.NeedsFetch(Stored(RowWindowValueStatus.Fetched, from: Span.FromUtc.AddMinutes(1)), Span, 7, Now));
        Assert.True(RowWindowFetch.NeedsFetch(Stored(RowWindowValueStatus.Fetched, to: Span.ToUtc.AddMinutes(1)), Span, 7, Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Повне_значення_закритого_вікна_вдруге_не_підтягується()
    {
        Assert.False(RowWindowFetch.NeedsFetch(Stored(RowWindowValueStatus.Fetched), Span, 7, Now));
        Assert.False(RowWindowFetch.NeedsFetch(Stored(RowWindowValueStatus.KeptManual), Span, 7, Now));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    [InlineData(RowWindowValueStatus.NoData)]
    [InlineData(RowWindowValueStatus.Partial)]
    [InlineData(RowWindowValueStatus.SourceError)]
    public void Неповне_значення_повторюється_лише_в_межах_RefetchWithinDays(RowWindowValueStatus status)
    {
        // Вікно закрилося 2026-01-28 10:00Z; тепер 2026-02-05 09:00Z — минуло 7 діб 23 год.
        Assert.False(RowWindowFetch.NeedsFetch(Stored(status), Span, 7, Now));
        Assert.True(RowWindowFetch.NeedsFetch(Stored(status), Span, 8, Now));
        Assert.False(RowWindowFetch.NeedsFetch(Stored(status), Span, 0, Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public void Вікно_що_тривало_на_момент_читання_дочитується_навіть_з_повним_значенням()
    {
        var early = Stored(RowWindowValueStatus.Fetched, retrievedAt: Span.ToUtc.AddMinutes(-5));

        Assert.True(RowWindowFetch.NeedsFetch(early, Span, 0, Now));
    }
}
