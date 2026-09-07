// tests/Ecr.Domain.Tests/ValueObjects/ValidityWindowTests.cs
using System.Globalization;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// Вікно чинності — напівінтервал <c>[FromInclusive, ToExclusive)</c>
/// (директива ПК-1 №05 §7, пастка 5, крок <c>I.10</c>).
/// </summary>
/// <remarks>
/// ⚠ Кожен тест тут ловить помилку РІВНО НА ДЕНЬ. Це найдорожчий клас
/// помилок у темпоральних даних: він не падає, не логується і виявляється
/// один раз на рік — саме того дня, коли межа проходить через звітний період.
/// </remarks>
public sealed class ValidityWindowTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("2024-01-01", true)]   // рівно FromInclusive — чинний
    [InlineData("2024-12-31", true)]   // останній чинний день року
    [InlineData("2025-01-01", false)]  // рівно ToExclusive — уже НЕ чинний
    [InlineData("2023-12-31", false)]  // день до
    public void Верхня_межа_виключна_а_нижня_включна(string date, bool expected)
    {
        // Вікно календарного 2024 року так, як його пише перенос:
        // `2024-12-31 23:59:59` у джерелі → `2025-01-01` у нас.
        var window = new ValidityWindow(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1));

        Assert.Equal(
            expected, window.Contains(DateOnly.Parse(date, CultureInfo.InvariantCulture)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Відсутня_межа_означає_без_обмеження()
    {
        // ⛔ Саме так у нас виглядають сентинели джерела `9999-02-20` і
        // `9999-12-31`: не датою, а її відсутністю.
        Assert.True(ValidityWindow.Always.Contains(new DateOnly(1900, 1, 1)));
        Assert.True(ValidityWindow.Always.Contains(new DateOnly(9999, 12, 31)));

        var openEnd = new ValidityWindow(new DateOnly(2026, 1, 1), null);
        Assert.False(openEnd.Contains(new DateOnly(2025, 12, 31)));
        Assert.True(openEnd.Contains(new DateOnly(2099, 12, 31)));

        var openStart = new ValidityWindow(null, new DateOnly(2026, 7, 1));
        Assert.True(openStart.Contains(new DateOnly(1900, 1, 1)));
        Assert.True(openStart.Contains(new DateOnly(2026, 6, 30)));
        Assert.False(openStart.Contains(new DateOnly(2026, 7, 1)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Рівність_меж_дає_порожнє_вікно()
    {
        // ⛔ Головна відмінність від закритого подання: там `30.06 … 30.06`
        // означало рівно один чинний день, тут — жодного. Якщо цей тест
        // зелений при `to < from` замість `to <= from`, вікно з нуля днів
        // проходить у базу і запис просто ніколи не з'являється у списку.
        var empty = new ValidityWindow(new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 30));

        Assert.True(empty.IsEmpty);
        Assert.False(empty.Contains(new DateOnly(2026, 6, 30)));
        Assert.Null(empty.LastValidDay);

        var oneDay = new ValidityWindow(new DateOnly(2026, 6, 30), new DateOnly(2026, 7, 1));
        Assert.False(oneDay.IsEmpty);
        Assert.True(oneDay.Contains(new DateOnly(2026, 6, 30)));
        Assert.Equal(new DateOnly(2026, 6, 30), oneDay.LastValidDay);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-2.15")]
    public void Перетин_із_місяцем_рахується_на_напівінтервалах()
    {
        var june = new DateOnly(2026, 6, 1);
        var july = new DateOnly(2026, 7, 1);

        // Дозвіл, чинний до 15 червня (виключно — 16-го вже ні), червень
        // усе-таки покриває: викид за першу половину місяця стався в межах
        // дозволу.
        var untilMidJune = new ValidityWindow(null, new DateOnly(2026, 6, 16));
        Assert.True(untilMidJune.OverlapsSegment(june, july));

        // ⚠ Межа рівно на початку місяця: вікно `[…, 1 червня)` червня вже НЕ
        // покриває — це і є той самий день, на якому закрите подання
        // помилялося.
        var untilJune = new ValidityWindow(null, june);
        Assert.False(untilJune.OverlapsSegment(june, july));

        // І симетрично знизу: вікно, що починається 1 липня, червня не
        // покриває, а вікно з 30 червня — покриває одним днем.
        Assert.False(new ValidityWindow(july, null).OverlapsSegment(june, july));
        Assert.True(
            new ValidityWindow(new DateOnly(2026, 6, 30), null).OverlapsSegment(june, july));
    }
}
