using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Пояс проєкту — **ідентифікатор IANA**, обов'язковий (директива ПК-1 №06
/// §3, крок <c>H-13</c>).
/// </summary>
/// <remarks>
/// ⛔ Перевірку не можна звести до <c>FindSystemTimeZoneById</c>, і саме на
/// цьому трималася попередня реалізація. Виміряно на цій машині: він приймає
/// і <c>Asia/Aqtau</c>, і <c>Central Asia Standard Time</c>, і <c>UTC+13</c>.
/// Тобто проєкт заводився з Windows-ідентифікатором, і жоден тест цього не
/// показував — межі періодів рахувалися, просто в іншому поясі.
/// </remarks>
public sealed class ProjectTimeZoneIanaTests
{
    /// <summary>Значення, які виглядають як пояс і поясом IANA не є.</summary>
    /// <remarks>
    /// <c>Central Asia Standard Time</c> — те, що стояло в DEFAULT колонки
    /// <c>doc.Project.TimeZoneId</c> від самої першої міграції.
    /// <c>+05:00</c> — зсув: директива забороняє його саме тому, що він
    /// міняється, а збережене число — ні.
    /// </remarks>
    public static TheoryData<string?> NotIana =>
        new()
        {
            "Central Asia Standard Time",
            "West Asia Standard Time",
            "UTC+13",
            "+05:00",
            "asia/aqtau",
            "Asia/Atlantis",
            "",
            "   ",
            null,
        };

    [Theory]
    [MemberData(nameof(NotIana))]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1b")]
    public void Не_IANA_проєкт_не_створюється_ECR_CFG_4221(string? timeZoneId)
    {
        var error = Assert.Throws<DomainException>(() => Make(timeZoneId!));

        // ⛔ Окремий код, а не `ECR-CFG-0422`: форма створення проєкту має і
        // поле коду, і поле поясу, а клієнт маршрутизує за кодом помилки.
        // Спільний код не дає підсвітити правильне поле.
        Assert.Equal("ECR-CFG-4221", error.ErrorCode);
    }

    [Theory]
    [InlineData("Asia/Aqtau")]
    [InlineData("Asia/Almaty")]
    [InlineData("Asia/Atyrau")]
    [InlineData("Europe/Kyiv")]
    [InlineData("UTC")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1")]
    public void IANA_приймається_і_зберігається_дослівно(string timeZoneId)
    {
        var project = Make(timeZoneId);

        // ⚠ Дослівно, без нормалізації: у базі лежить рядок, і мовчазна
        // заміна на «канонічний» ідентифікатор означала б, що в проєкті
        // записано не те, що обрала людина.
        Assert.Equal(timeZoneId, project.TimeZoneId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1a")]
    public void Зміна_поясу_в_Draft_теж_вимагає_IANA()
    {
        var project = ProjectBuilder.Project();

        // ⛔ Тут стояла сама лише перевірка «непорожньо»: цей шлях приймав
        // Windows-ідентифікатор навіть тоді, коли конструктор його вже не
        // приймав би. Два формулювання одного правила розходяться на першій
        // правці одного з них.
        var error = Assert.Throws<DomainException>(
            () => project.ChangeTimeZone("Central Asia Standard Time"));

        Assert.Equal("ECR-CFG-4221", error.ErrorCode);
        Assert.Equal("Asia/Almaty", project.TimeZoneId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1a")]
    public void Після_відкриття_періоду_відмова_про_незмінність_а_не_про_формат()
    {
        var project = ProjectBuilder.Project();
        var periods = PeriodCalendar.Build(
            project, ProjectBuilder.Policy(), ProjectBuilder.Zone(), existing: []);
        ProjectBuilder.Attach(project, periods);
        periods[0].TransitionTo(PeriodState.Open, new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));

        // ⚠ Значення тут навмисно негодяще. Відповідь «пояс не є IANA»
        // обіцяла б, що з правильним значенням операція пройде — а вона не
        // пройде за жодного: межі вже стали чиїмись зобов'язаннями.
        var error = Assert.Throws<DomainException>(
            () => project.ChangeTimeZone("Central Asia Standard Time"));

        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1b")]
    public void Пояс_дає_ПРАВИЛО_а_не_число_бо_зсув_міняється()
    {
        var zone = SiteTimeZone.Create("Europe/Kyiv").ToTimeZoneInfo();

        var winter = zone.GetUtcOffset(
            DateTime.SpecifyKind(new DateTime(2025, 1, 15, 12, 0, 0), DateTimeKind.Unspecified));
        var summer = zone.GetUtcOffset(
            DateTime.SpecifyKind(new DateTime(2025, 7, 15, 12, 0, 0), DateTimeKind.Unspecified));

        // ⛔ Ось чому зсув НЕ зберігається (директива ПК-1 №06 §3). Той самий
        // пояс дає +02:00 у січні і +03:00 у липні: збережене число пережило б
        // перехід на літній час і почало б брехати рівно на годину — тобто
        // період закривався б на годину не тоді, і жоден тест би цього не
        // показав, бо число саме з собою узгоджене.
        Assert.Equal(TimeSpan.FromHours(2), winter);
        Assert.Equal(TimeSpan.FromHours(3), summer);
        Assert.NotEqual(winter, summer);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.1b")]
    public void Windows_ідентифікатор_втрачає_державу_на_зворотному_шляху()
    {
        // ⛔ Друга причина, чому саме IANA. Виміряно на цій машині:
        // `Asia/Aqtau` → `West Asia Standard Time` → назад дає `Asia/Tashkent`.
        // Тобто Актау, збережений Windows-ідентифікатором, повертається
        // Ташкентом: сьогодні обидва +05:00, але це різні набори правил і
        // різні держави, і розійдуться вони мовчки.
        Assert.True(TimeZoneInfo.TryConvertIanaIdToWindowsId("Asia/Aqtau", out var windows));
        Assert.True(TimeZoneInfo.TryConvertWindowsIdToIanaId(windows!, out var roundTrip));

        Assert.NotEqual("Asia/Aqtau", roundTrip);
    }

    private static Project Make(string timeZoneId)
        => new(
            EcrCode.Create("PLANT_A"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Plant A" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: 2, PeriodKind.Monthly, periodPolicyId: 1, timeZoneId);
}
