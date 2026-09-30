// tests/Ecr.Domain.Tests/ValueObjects/KazakhstanSiteZoneOffsetTests.cs
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// Діагностика середовища: база часових поясів цієї машини знає, що з 2024-03-01
/// увесь Казахстан живе на UTC+5 (F-4).
/// </summary>
/// <remarks>
/// ⛔ Це НЕ тест коду, а тест машини. Межі періодів рахуються через
/// <c>TimeZoneInfo.FindSystemTimeZoneById(IANA)</c>, тобто беруться з бази
/// поясів ОС (Windows — реєстр і накопичувальні оновлення, Linux — <c>tzdata</c>),
/// а фіксованого зсуву в продукті немає. Машина із застарілою базою вважає
/// <c>Asia/Almaty</c> все ще <c>+06:00</c>, і кожен період проєкту на цьому поясі
/// закривається на годину пізніше, ніж у сервера з чинною базою.
///
/// ⚠ Падіння цього тесту означає «оновіть tzdata (Linux) або накопичувальне
/// оновлення Windows із часовими поясами», а не «зламаний код».
///
/// ⚠ Очікування ЗАПИСАНІ КОНСТАНТОЮ навмисно: тест, що рахує еталон із тієї ж
/// бази, порівнював би базу саму з собою і ніколи не червонів.
/// </remarks>
public sealed class KazakhstanSiteZoneOffsetTests
{
    private static readonly TimeSpan Utc5 = TimeSpan.FromHours(5);

    public static TheoryData<string, DateTime> ZonesAfterUnification => new()
    {
        // Майданчик NCOC — Атирау; Актау — приклад у коді й API (+05:00, як Атирау).
        { "Asia/Atyrau", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
        { "Asia/Atyrau", new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc) },
        { "Asia/Aqtau", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
        { "Asia/Aqtau", new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc) },
        // Єдиний із трьох, чий зсув змінився 2024-03-01 (було +06:00): саме він
        // виказує застарілу базу.
        { "Asia/Almaty", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
        { "Asia/Almaty", new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc) },
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Finding", "F-4")]
    [MemberData(nameof(ZonesAfterUnification))]
    public void Після_2024_03_01_пояс_Казахстану_це_плюс_п_ять(string zoneId, DateTime utc)
    {
        var zone = SiteTimeZone.Create(zoneId).ToTimeZoneInfo();

        var offset = zone.GetUtcOffset(utc);

        Assert.True(
            offset == Utc5,
            $"База часових поясів цієї машини застаріла: {zoneId} на {utc:yyyy-MM-dd} дає "
            + $"{Format(offset)} замість +05:00 (Казахстан на UTC+5 з 2024-03-01). "
            + "Межі періодів і позначки запізнілих правок для проєктів на цьому поясі будуть "
            + "зсунуті на годину. Оновіть tzdata (Linux) або накопичувальне оновлення Windows "
            + "із часовими поясами; код продукту тут ні до чого.");
    }

    private static string Format(TimeSpan offset)
        => (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
}
