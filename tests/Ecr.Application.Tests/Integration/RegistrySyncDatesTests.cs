// tests/Ecr.Application.Tests/Integration/RegistrySyncDatesTests.cs
using System.Globalization;
using Ecr.Application.Integration.RegistrySync;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Дати AF, відповіді людини 2026-10-01: <c>Actual_End_Date</c> — ВКЛЮЧНА кінцева дата (вікно
/// напіввідкрите, тож +1 день), <c>Date_Issue</c>-рядок — лише <c>MM/dd/yyyy</c> (чи ISO) з
/// <see cref="CultureInfo.InvariantCulture"/>, будь-що інше — відмова з подією, без здогадів.
/// </summary>
/// <remarks>
/// Докази (мутації): прибрати <c>AddDays(1)</c> для <c>ToInclusive</c> — червоні
/// <see cref="Actual_End_Date_включна_дає_наступну_добу_для_рядка_DateTime_і_DateTimeOffset"/>;
/// <c>InvariantCulture</c> → <c>ru-RU</c>/<c>uk-UA</c> у <c>TryParseFieldDate</c> — червоні
/// <see cref="Рядок_MM_dd_yyyy_розбирається_однаково_за_будь_якої_культури_потоку"/> і
/// <see cref="Date_Issue_MM_dd_yyyy_пишеться_у_поле"/>; формат <c>dd/MM/yyyy</c> замість
/// <c>MM/dd/yyyy</c> — червоні обидва й <see cref="Інші_формати_і_сміття_відмовляються"/>;
/// фолбек <c>DateTime.TryParse</c> — червоний <see cref="Інші_формати_і_сміття_відмовляються"/>.
/// <para>
/// Рішення про час у включній межі (не північ): беремо ДЕНЬ моменту + 1 (як було в D-212 PR-7,
/// <c>2024-12-31T10:00</c> включно → <c>2025-01-01</c>): поле — календарний день дії, а не точка.
/// </para>
/// </remarks>
public sealed class RegistrySyncDatesTests
{
    private const long EntryId = 1001;
    private const int IssueField = 21;
    private const string Guid1 = "F1A2B3C4-0000-0000-0000-000000000001";
    private const string Path1 = @"\\AF\ECR\Permits\P-01";
    private const string To = "Actual_End_Date";

    private static readonly RegistrySyncFieldMapping Issue =
        new(IssueField, "DATE_ISSUE", CellDataType.Date, null, "Date_Issue", IsActive: true);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Actual_End_Date_включна_дає_наступну_добу_для_рядка_DateTime_і_DateTimeOffset()
    {
        var expected = new DateOnly(2026, 4, 30);
        object[] raws =
        [
            "2026-04-29",
            "2026-04-29T00:00:00",
            new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTimeOffset(2026, 4, 29, 0, 0, 0, TimeSpan.Zero),
        ];

        foreach (var raw in raws)
        {
            var plan = RegistrySyncPlanner.Plan(ValidityInput(inclusive: true, raw));

            // Діє по 29.04 ВКЛЮЧНО: перший нечинний день — 30.04.
            Assert.Equal(new ValidityWindow(null, expected), Assert.Single(plan.ValidityChanges).New);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Actual_End_Date_виключна_лишає_ту_саму_північ()
    {
        var plan = RegistrySyncPlanner.Plan(ValidityInput(inclusive: false, "2026-04-29"));

        Assert.Equal(new DateOnly(2026, 4, 29), Assert.Single(plan.ValidityChanges).New.ToExclusive);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    [InlineData("04/29/2026", 2026, 4, 29)]
    [InlineData("12/31/2025", 2025, 12, 31)]
    [InlineData("2026-04-29", 2026, 4, 29)]
    [InlineData("2026-04-29T00:00:00Z", 2026, 4, 29)]
    public void Дозволені_формати_дають_UTC_північ(string text, int y, int m, int d)
    {
        Assert.True(RegistrySyncValidity.TryParseFieldDate(text, out var value));
        Assert.Equal(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc), value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    [InlineData("29/04/2026")]
    [InlineData("13/01/2026")]
    [InlineData("29.04.2026")]
    [InlineData("04.29.2026")]
    [InlineData("2026/04/29")]
    [InlineData("April 29, 2026")]
    [InlineData("29 квітня 2026")]
    [InlineData("не дата")]
    public void Інші_формати_і_сміття_відмовляються(string text)
        => Assert.False(RegistrySyncValidity.TryParseFieldDate(text, out _));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    [InlineData("ru-RU")]
    [InlineData("uk-UA")]
    [InlineData("de-DE")]
    public void Рядок_MM_dd_yyyy_розбирається_однаково_за_будь_якої_культури_потоку(string culture)
    {
        var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);

            Assert.True(RegistrySyncValidity.TryParseFieldDate("04/29/2026", out var value));
            Assert.Equal(new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc), value);
            Assert.False(RegistrySyncValidity.TryParseFieldDate("29/04/2026", out _));
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = saved;
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Date_Issue_MM_dd_yyyy_пишеться_у_поле()
    {
        var plan = RegistrySyncPlanner.Plan(FieldInput("04/29/2026"));

        var update = Assert.Single(plan.Updates);
        Assert.Equal(IssueField, update.RegistryFieldDefId);
        Assert.Equal(new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc), update.NewValue);
        Assert.Empty(plan.Events);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Date_Issue_типу_DateTime_з_AF_іде_без_розбору()
    {
        var moment = new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc);

        var plan = RegistrySyncPlanner.Plan(FieldInput(moment));

        Assert.Equal(moment, Assert.Single(plan.Updates).NewValue);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    [InlineData("29/04/2026")]
    [InlineData("13/01/2026")]
    [InlineData("29.04.2026")]
    [InlineData("сміття")]
    public void Date_Issue_в_іншому_форматі_відмовляється_подією_і_поле_не_чіпається(string raw)
    {
        var plan = RegistrySyncPlanner.Plan(FieldInput(raw));

        Assert.Empty(plan.Updates);
        var rejected = Assert.Single(plan.Events);
        Assert.Equal(RegistrySyncEventKind.ValueRejected, rejected.Kind);
        Assert.Equal("DATE_ISSUE", rejected.FieldCode);
        Assert.Equal(raw, rejected.SourceValue);
        Assert.Equal("ECR-REG-0422", rejected.ErrorCode);
        Assert.Equal(RegistrySyncValidity.DateFormatRefusedKey, rejected.MessageKey);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    [InlineData("")]
    [InlineData("   ")]
    public void Date_Issue_порожній_рядок_за_наявною_семантикою_valueNotDate_а_не_наш_ключ(string raw)
    {
        // Наявна поведінка RegistryValue.Set (до 2026-10-01): порожній рядок — не дата; наш рядковий
        // розбір його не чіпає й не підміняє ключ.
        var plan = RegistrySyncPlanner.Plan(FieldInput(raw));

        Assert.Empty(plan.Updates);
        Assert.Equal("err.ECR-REG-0422.valueNotDate", Assert.Single(plan.Events).MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-212")]
    public void Date_Issue_null_не_дає_ні_оновлення_ні_відмови()
    {
        var plan = RegistrySyncPlanner.Plan(FieldInput(null));

        Assert.Empty(plan.Events);
        Assert.Empty(plan.Updates);
    }

    private static RegistrySyncInput FieldInput(object? raw)
        => new(
            40,
            RegistrySourceKind.External,
            IsCompleteSnapshot: true,
            [new RegistrySyncSourceElement(Guid1, Path1, new Dictionary<string, object?> { ["Date_Issue"] = raw })],
            [new RegistrySyncLink(Guid1, EntryId, Path1)],
            [new RegistrySyncEntryState(EntryId, new Dictionary<int, RegistrySyncCurrentValue>())],
            [Issue]);

    private static RegistrySyncInput ValidityInput(bool inclusive, object? end)
        => new(
            40,
            RegistrySourceKind.External,
            IsCompleteSnapshot: true,
            [new RegistrySyncSourceElement(Guid1, Path1, new Dictionary<string, object?> { [To] = end })],
            [new RegistrySyncLink(Guid1, EntryId, Path1)],
            [new RegistrySyncEntryState(EntryId, new Dictionary<int, RegistrySyncCurrentValue>())],
            [],
            Validity: new RegistrySyncValiditySource(null, To, inclusive, TimeZoneInfo.Utc));
}
