// tests/Ecr.Domain.Tests/Services/LegacyValidityImportTests.cs
using System.Globalization;
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Services;

/// <summary>
/// Три різні «нескінченності» джерела зводяться до одного напівінтервала
/// (директива ПК-1 №05 §7, пастка 5).
/// </summary>
/// <remarks>
/// ⛔ Найважливіший тест файлу — не про дати, а про ЗВІТ: <c>12:59:59</c> і
/// <c>23:59:59</c> дають ту саму нашу межу, і без окремого рядка різниці між
/// ними не побачить ніхто ніколи. 24 такі рядки в корпусі — це 24 дозволи, у
/// яких кінець доби набрали з помилкою, і мовчазне «дотягування» межі на
/// одинадцять годин — рівно те, що директива забороняє.
/// </remarks>
public sealed class LegacyValidityImportTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("9999-02-20 00:00:00")]
    [InlineData("9999-12-31 00:00:00")]
    [InlineData("9999-12-31 23:59:59")]
    public void Сентинел_9999_стає_відсутністю_межі(string source)
    {
        var mapped = LegacyValidityImport.MapUpperBound(Raw(source));

        // ⛔ Саме `null`, а не дата. Дата, яка означає «ніколи», рано чи пізно
        // потрапляє в різницю дат і дає 7975 років — і це вже не «дивне
        // число», а зіпсований звіт.
        Assert.Equal(LegacyBoundaryKind.Sentinel, mapped.Kind);
        Assert.Null(mapped.Boundary);
        Assert.False(mapped.NeedsReport);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Кінець_доби_стає_наступним_днем_без_рядка_у_звіті()
    {
        var mapped = LegacyValidityImport.MapUpperBound(Raw("2024-12-31 23:59:59"));

        Assert.Equal(LegacyBoundaryKind.EndOfDay, mapped.Kind);
        Assert.Equal(new DateOnly(2025, 1, 1), mapped.Boundary);
        Assert.Equal(TimeSpan.Zero, mapped.Shift);
        Assert.False(mapped.NeedsReport);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Опівніч_уже_є_виключною_межею_і_доби_не_додає()
    {
        // ⚠ Тут найлегше помилитися на добу в інший бік: `+1 день` до кожного
        // значення подовжив би чинність усіх таких дозволів на день.
        var mapped = LegacyValidityImport.MapUpperBound(Raw("2025-01-01 00:00:00"));

        Assert.Equal(LegacyBoundaryKind.Midnight, mapped.Kind);
        Assert.Equal(new DateOnly(2025, 1, 1), mapped.Boundary);
        Assert.False(mapped.NeedsReport);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Набраний_з_помилкою_кінець_доби_дає_ту_саму_межу_і_рядок_у_звіті()
    {
        var mapped = LegacyValidityImport.MapUpperBound(Raw("2024-12-31 12:59:59"));

        // Межа та сама, що в сусіднього рядка з `23:59:59`…
        Assert.Equal(new DateOnly(2025, 1, 1), mapped.Boundary);

        // …і саме тому вона мусить бути НАЗВАНА: зсув рівно 11 годин.
        Assert.Equal(LegacyBoundaryKind.MistypedEndOfDay, mapped.Kind);
        Assert.Equal(TimeSpan.FromHours(11), mapped.Shift);
        Assert.True(mapped.NeedsReport);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Звіт_міграції_містить_рядок_на_кожен_дефектний_дозвіл()
    {
        // Зріз корпусу: 24 рядки з `12:59:59` серед звичайних.
        var notes = new List<LegacyBoundaryNote>();

        for (var i = 0; i < 24; i++)
        {
            LegacyValidityImport.Map(
                $"dic.RegistryEntry#{7001 + i}.ValidTo",
                Raw("2024-01-01 00:00:00"),
                Raw("2024-12-31 12:59:59"),
                notes);
        }

        LegacyValidityImport.Map(
            "dic.RegistryEntry#8000.ValidTo",
            Raw("2024-01-01 00:00:00"),
            Raw("2024-12-31 23:59:59"),
            notes);

        LegacyValidityImport.Map(
            "dic.RegistryEntry#8001.ValidTo", null, Raw("9999-12-31 00:00:00"), notes);

        // ⛔ Рівно 24: не 26 (тоді звіт кричить на здорових рядках і його
        // перестають читати) і не 0 (тоді зсув на 11 годин стається мовчки).
        Assert.Equal(24, notes.Count);

        var line = LegacyValidityImport.Describe(notes[0]);

        // Рядок звіту несе ЧИСЛО, а не слово «розбіжність»: методолог має
        // побачити, наскільки саме зсунуто межу, не відкриваючи джерела.
        Assert.Contains("2024-12-31 12:59:59", line, StringComparison.Ordinal);
        Assert.Contains("2025-01-01", line, StringComparison.Ordinal);
        Assert.Contains("11:00:00", line, StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Перенос_обох_меж_дає_напівінтервал_календарного_року()
    {
        var window = LegacyValidityImport.Map(
            "dic.RegistryEntry#9000.ValidTo",
            Raw("2024-01-01 08:30:00"),
            Raw("2024-12-31 23:59:59"),
            notes: null);

        // Нижня межа включна, тому час доби відкидається: перший чинний день
        // той самий.
        Assert.Equal(new DateOnly(2024, 1, 1), window.FromInclusive);
        Assert.Equal(new DateOnly(2025, 1, 1), window.ToExclusive);

        Assert.True(window.Contains(new DateOnly(2024, 12, 31)));
        Assert.False(window.Contains(new DateOnly(2025, 1, 1)));
    }

    private static DateTime Raw(string value)
        => DateTime.ParseExact(
            value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
