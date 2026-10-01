// tests/Ecr.Domain.Tests/Documents/PeriodUtcBoundsTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Межі періоду в UTC для згортки PI збігаються з межами, які рахує сам період (D16-03).
/// </summary>
/// <remarks>
/// ⛔ Сторож проти ДРУГОЇ арифметики періодів. <see cref="Period.UtcBounds"/>
/// і <see cref="Period.RecomputeBoundaries"/> мусять іти через одне
/// перетворення — інакше згортка й переходи станів мали б різну думку про те,
/// де кінчається місяць. Еталон — <c>ComputedOpenAt</c> із відступом 0
/// (опівніч <c>PeriodStart</c>) і <c>ComputedGraceAt</c> із відступом 1
/// (опівніч <c>PeriodEnd + 1</c>), а не константи: зсув поясу (напр.
/// <c>Europe/Kyiv</c>) залежить від бази tz на машині.
/// </remarks>
public sealed class PeriodUtcBoundsTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "D16-03")]
    [InlineData("Asia/Atyrau", 2026, 1)]
    [InlineData("Europe/Kyiv", 2026, 3)]   // перехід на літній час усередині місяця
    [InlineData("Europe/Kyiv", 2026, 10)]  // і назад
    [InlineData("America/New_York", 2026, 11)]
    [InlineData("UTC", 2026, 2)]
    public void Межі_збігаються_з_межами_які_рахує_Period(string zoneId, int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        var end = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var zone = SiteTimeZone.Create(zoneId).ToTimeZoneInfo();

        var period = new Period(1, new PeriodKey(year * 100 + month), (byte)month, start, end);
        period.RecomputeBoundaries(
            new PeriodPolicy(EcrCode.Create("D1603"), openOffsetDays: 0, graceOffsetDays: 1,
                hardCloseOffsetDays: 1, yearGraceOffsetDays: 0),
            zone);

        var range = Period.UtcBounds(start, end, zone);

        Assert.Equal(period.ComputedOpenAt, range.StartUtc);
        Assert.Equal(period.ComputedGraceAt, range.EndUtc);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "D16-03")]
    public void Перетин_напіввідкритий_з_обох_боків()
    {
        var range = new Period.UtcRange(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        // Вікно, що кінчається рівно на початку періоду, його не зачіпає.
        Assert.False(range.Overlaps(new DateTime(2025, 12, 25, 0, 0, 0, DateTimeKind.Utc), range.StartUtc));

        // Вікно, що починається рівно на кінці періоду, — теж.
        Assert.False(range.Overlaps(range.EndUtc, new DateTime(2026, 2, 8, 0, 0, 0, DateTimeKind.Utc)));

        // Одна тика всередину з будь-якого боку — вже перетин.
        Assert.True(range.Overlaps(new DateTime(2025, 12, 25, 0, 0, 0, DateTimeKind.Utc), range.StartUtc.AddTicks(1)));
        Assert.True(range.Overlaps(range.EndUtc.AddTicks(-1), new DateTime(2026, 2, 8, 0, 0, 0, DateTimeKind.Utc)));
    }
}
