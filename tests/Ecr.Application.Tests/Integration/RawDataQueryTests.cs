// tests/Ecr.Application.Tests/Integration/RawDataQueryTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Integration.RawData;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// <c>D-463</c>: межі запиту, курсор, ширина кошика, CSV-рядок, злиття покриття й фільтр подій — без бази.
/// </summary>
public sealed class RawDataQueryTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Вікно_типово_тиждень_до_зараз_рік_дозволено_понад_400_діб_і_перевернуте_422()
    {
        Assert.Equal((Now.AddDays(-7), Now), RawDataQuery.Window(null, null, Now));

        var yearFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var (from, to) = RawDataQuery.Window(yearFrom, yearFrom.AddYears(1), Now);
        Assert.Equal(DateTimeKind.Utc, from.Kind);
        Assert.Equal(TimeSpan.FromDays(365), to - from);

        Assert.Equal(
            "err.ECR-REQ-0422.rawDataRange",
            Assert.Throws<BusinessRuleException>(() => RawDataQuery.Window(Now, Now, Now)).Details!["messageKey"]);
        Assert.Equal(
            "err.ECR-REQ-0422.rawDataRangeTooLong",
            Assert.Throws<BusinessRuleException>(() => RawDataQuery.Window(Now.AddDays(-401), Now, Now)).Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Шлях_сторінка_й_кошики_в_межах_нуль_типове()
    {
        Assert.Equal(" a|b ", RawDataQuery.Path(" a|b "));
        Assert.Throws<BusinessRuleException>(() => RawDataQuery.Path(""));
        Assert.Throws<BusinessRuleException>(() => RawDataQuery.Path(new string('x', 401)));

        Assert.Equal(RawDataQuery.DefaultPageSize, RawDataQuery.PageSize(0));
        Assert.Equal(500, RawDataQuery.PageSize(500));
        Assert.Throws<BusinessRuleException>(() => RawDataQuery.PageSize(501));
        Assert.Throws<BusinessRuleException>(() => RawDataQuery.PageSize(-1));

        Assert.Equal(RawDataQuery.DefaultBuckets, RawDataQuery.BucketCount(0));
        Assert.Throws<BusinessRuleException>(() => RawDataQuery.BucketCount(2001));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Курсор_туди_й_назад_зіпсований_початок()
    {
        var at = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);
        Assert.Equal(at, RawDataQuery.DecodeCursor(RawDataQuery.EncodeCursor(at)));
        Assert.Null(RawDataQuery.DecodeCursor("not-base64!"));
        Assert.Null(RawDataQuery.DecodeCursor(Convert.ToBase64String("123"u8.ToArray())));
        Assert.Null(RawDataQuery.DecodeCursor(new string('A', 200)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Кошик_ціла_кількість_секунд_і_не_більше_запитаного()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(15_768, RawDataQuery.BucketSeconds(from, from.AddDays(365), 2000));
        Assert.Equal(1, RawDataQuery.BucketSeconds(from, from.AddSeconds(10), 2000));

        var seconds = RawDataQuery.BucketSeconds(from, from.AddDays(366), 2000);
        Assert.True(Math.Ceiling(366d * 86400 / seconds) <= 2000);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void CSV_текст_нейтралізовано_число_як_є()
    {
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var line = RawDataQuery.ToCsv(new RawPointView(at, -12.5m, "=1+1", "@u", "-bad", at, 7));

        Assert.Equal("2026-01-01T00:00:00.000Z,-12.5,'=1+1,'@u,'-bad,2026-01-01T00:00:00.000Z,7\r\n", line);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Покриття_зливає_обрізає_вікном_і_не_вигадує_хвоста_при_урізанні()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        RawInterval I(int a, int b) => new(t.AddHours(a), t.AddHours(b));

        var (covered, gaps) = RawDataHandler.Islands([I(5, 7), I(-2, 1), I(6, 8), I(10, 11)], t, t.AddHours(12), truncated: false);
        Assert.Equal([I(0, 1), I(5, 8), I(10, 11)], covered);
        Assert.Equal([I(1, 5), I(8, 10), I(11, 12)], gaps);

        var (_, truncatedGaps) = RawDataHandler.Islands([I(0, 1), I(3, 4)], t, t.AddHours(12), truncated: true);
        Assert.Equal([I(1, 3)], truncatedGaps);

        var (none, all) = RawDataHandler.Islands([], t, t.AddHours(2), truncated: false);
        Assert.Empty(none);
        Assert.Equal([I(0, 2)], all);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Подія_стосується_мапінгу_за_полем_або_ідентифікатором_і_лише_статусів_даних()
    {
        var map = new CellSourceMap(42, 1, "E", null, @"\\AF\Stack|CO", "Sum", null, null, true);
        string Env(string key, params (string, string)[] p)
            => Ecr.Application.Ports.JobProgressMessageCodec.Encode(new(key, p.ToDictionary(x => x.Item1, x => x.Item2)));

        Assert.True(CellSourceHandler.Concerns(new(1, "PartialCoverage", Env("k", ("field", @"\\af\stack|co")), Now), map));
        Assert.True(CellSourceHandler.Concerns(new(2, "SkippedNoData", Env("k", ("field", "x"), ("mapId", "42")), Now), map));
        Assert.False(CellSourceHandler.Concerns(new(3, "SkippedNoData", Env("k", ("field", "x"), ("mapId", "43")), Now), map));
        Assert.False(CellSourceHandler.Concerns(new(4, "ConflictKeptManual", Env("k", ("field", @"\\AF\Stack|CO")), Now), map));
        Assert.False(CellSourceHandler.Concerns(new(5, "SkippedPointCeiling", "старий текст", Now), map));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Вікно_згортки_колонка_місяця_лише_свій_місяць_звичайна_увесь_період_поза_періодом_null()
    {
        var zone = Ecr.Domain.ValueObjects.SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();
        var year = (new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var march = (new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        static DateTime U(int y, int m, int d, int h) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

        var whole = Ecr.Domain.Entities.Documents.Period.FoldBounds(year.Item1, year.Item2, null, zone)!.Value;
        Assert.Equal((U(2025, 12, 31, 19), U(2026, 12, 31, 19)), (whole.StartUtc, whole.EndUtc));

        var m3 = Ecr.Domain.Entities.Documents.Period.FoldBounds(year.Item1, year.Item2, 3, zone)!.Value;
        Assert.Equal((U(2026, 2, 28, 19), U(2026, 3, 31, 19)), (m3.StartUtc, m3.EndUtc));

        // Місячний екземпляр березня: M3 — березень, M11 — не комірка цього періоду.
        Assert.Equal(m3, Ecr.Domain.Entities.Documents.Period.FoldBounds(march.Item1, march.Item2, 3, zone));
        Assert.Null(Ecr.Domain.Entities.Documents.Period.FoldBounds(march.Item1, march.Item2, 11, zone));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Порівняння_з_коміркою_лише_з_журналу_змін_комірки()
    {
        var at = Now;
        var value = new CellFold(CellFoldState.Value, 12.345m, 100m);

        Assert.Equal("NotWritten", CellSourceHandler.Compare(value, null, 2).Comparison);
        Assert.Equal("Match", CellSourceHandler.Compare(value, new CellLastWrite("Integration", "12.35", at), 2).Comparison);
        Assert.Equal("NotUpdated", CellSourceHandler.Compare(value, new CellLastWrite("Integration", "11", at), 2).Comparison);
        Assert.Equal("ManualEdit", CellSourceHandler.Compare(value, new CellLastWrite("UserEdit", "11", at), 2).Comparison);
        Assert.Equal("ManualEdit", CellSourceHandler.Compare(value, new CellLastWrite("Import", null, at), 2).Comparison);
        Assert.Equal(
            "NotComparable",
            CellSourceHandler.Compare(new CellFold(CellFoldState.TooManyPoints, null, null), new CellLastWrite("UserEdit", "1", at), 2).Comparison);
    }
}
