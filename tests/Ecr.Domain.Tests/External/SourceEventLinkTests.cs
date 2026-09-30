// tests/Ecr.Domain.Tests/External/SourceEventLinkTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.External;

/// <summary>
/// Стани зв'язку «подія джерела ↔ рядок» (FEATURE-HSE301-VIEW §4.7.3–4.7.4, крок F9).
/// </summary>
/// <remarks>
/// Мутаційний доказ (F9): у <c>SourceEventLink.IsTransitionAllowed</c> дозволити
/// <c>Open</c> зв'язку з рядком (<c>=&gt; !hasRow</c> → <c>=&gt; true</c>) — червоніють
/// <see cref="Зникла_подія_з_рядком_не_стає_знову_відкритою"/> і рядок
/// <c>Missing → Open</c> у <see cref="Таблиця_переходів"/>.
/// </remarks>
public sealed class SourceEventLinkTests
{
    private const int Map = 3;
    private static readonly DateTime Start = new(2026, 1, 28, 9, 9, 20, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 2, 1, 6, 0, 0, DateTimeKind.Utc);
    private static readonly SourceEventRowRef Row = new(202601, TableInstanceId: 55, "EF-9b1c");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Відкрита_подія_стає_Synced_коли_закінчилась()
    {
        var link = SourceEventLink.FirstSeenUnwritten(Map, Open(), SourceEventLinkStatus.Open, Now);

        Assert.Equal((SourceEventLinkStatus.Open, false, (DateTime?)null), (link.Status, link.HasRow, link.EndUtc));
        Assert.Equal((Now, Now, Now), (link.FirstSeenAt, link.LastSeenAt, link.LastSyncAt));

        var later = Now.AddHours(1);
        link.RecordWritten(Closed(), Row, keptManualJson: null, unmappedJson: null, later);

        Assert.Equal(
            (SourceEventLinkStatus.Synced, (int?)202601, (long?)55, "EF-9b1c", (DateTime?)Start.AddMinutes(15)),
            (link.Status, link.PeriodKey, link.TableInstanceId, link.RowKey, link.EndUtc));
        Assert.Equal((Now, later), (link.FirstSeenAt, link.LastSeenAt));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Synced_і_Missing_переходять_один_в_одного()
    {
        var link = Written();

        link.MarkMissing(Now.AddDays(1));
        Assert.Equal(SourceEventLinkStatus.Missing, link.Status);

        // Рядок не змінюється і не відв'язується (§4.7.4, крок 6); LastSeenAt —
        // коли подію бачили востаннє, а не коли її не знайшли.
        Assert.Equal(("EF-9b1c", Now), (link.RowKey, link.LastSeenAt));
        Assert.Equal(Now.AddDays(1), link.LastSyncAt);

        link.RecordWritten(Closed(), Row, null, null, Now.AddDays(2));
        Assert.Equal(SourceEventLinkStatus.Synced, link.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Зникла_подія_з_рядком_не_стає_знову_відкритою()
    {
        var link = Written();
        link.MarkMissing(Now.AddDays(1));

        var error = Assert.Throws<DomainException>(
            () => link.RecordUnwritten(Open(), SourceEventLinkStatus.Open, Now.AddDays(2)));

        AssertTransition(error, "Missing", "Open");
        Assert.Equal(SourceEventLinkStatus.Missing, link.Status);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    [InlineData(SourceEventLinkStatus.Open)]
    [InlineData(SourceEventLinkStatus.PeriodNotOpen)]
    [InlineData(SourceEventLinkStatus.RowLimit)]
    public void Прив_язаний_рядок_не_повертається_в_стан_без_рядка(SourceEventLinkStatus status)
    {
        var link = Written();

        var error = Assert.Throws<DomainException>(() => link.RecordUnwritten(
            status == SourceEventLinkStatus.Open ? Open() : Closed(), status, Now.AddDays(1)));

        AssertTransition(error, "Synced", status.ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Без_рядка_немає_ні_Missing_ні_PeriodChanged()
    {
        var link = SourceEventLink.FirstSeenUnwritten(Map, Open(), SourceEventLinkStatus.Open, Now);

        AssertTransition(Assert.Throws<DomainException>(() => link.MarkMissing(Now)), "Open", "Missing");
        AssertTransition(
            Assert.Throws<DomainException>(() => link.RecordPeriodChanged(Open(), Now)), "Open", "PeriodChanged");
        Assert.Equal(SourceEventLinkStatus.Open, link.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Прив_язана_подія_не_переїжджає_в_інший_рядок()
    {
        var link = Written();

        // Той самий ID події в іншому рядку — подвоєні викиди (§4.7.4, крок 4).
        var error = Assert.Throws<DomainException>(() => link.RecordWritten(
            Closed(), Row with { PeriodKey = 202602, TableInstanceId = 56 }, null, null, Now.AddDays(1)));

        Assert.Equal("err.ECR-INT-0422.eventLinkTransitionInvalid", error.Details?["messageKey"]);
        Assert.Equal((202601, (long?)55), (link.PeriodKey!.Value, link.TableInstanceId));

        link.RecordPeriodChanged(Closed(), Now.AddDays(1));
        Assert.Equal(SourceEventLinkStatus.PeriodChanged, link.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Незіставлене_значення_дає_Unmapped_а_повне_зіставлення_Synced()
    {
        var link = SourceEventLink.FirstSeenWritten(
            Map, Closed(), Row, keptManualJson: "[\"VOLUME_SM3\"]", unmappedJson: "[{\"column\":\"SEASON\",\"value\":\"зима\"}]", Now);

        Assert.Equal((SourceEventLinkStatus.Unmapped, "[\"VOLUME_SM3\"]"), (link.Status, link.KeptManualJson));

        link.RecordWritten(Closed(), Row, null, null, Now.AddHours(1));
        Assert.Equal((SourceEventLinkStatus.Synced, (string?)null), (link.Status, link.UnmappedJson));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Нова_подія_не_може_початися_з_Missing_чи_PeriodChanged_і_Open_лише_без_кінця()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SourceEventLink.FirstSeenUnwritten(Map, Closed(), SourceEventLinkStatus.Missing, Now));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SourceEventLink.FirstSeenUnwritten(Map, Closed(), SourceEventLinkStatus.PeriodChanged, Now));

        // «Open» із кінцем і «ще не записано» без кінця — не той стан події.
        Assert.Throws<ArgumentException>(
            () => SourceEventLink.FirstSeenUnwritten(Map, Closed(), SourceEventLinkStatus.Open, Now));
        Assert.Throws<ArgumentException>(
            () => SourceEventLink.FirstSeenUnwritten(Map, Open(), SourceEventLinkStatus.PeriodNotOpen, Now));
        Assert.Throws<ArgumentException>(
            () => SourceEventLink.FirstSeenWritten(Map, Open(), Row, null, null, Now));

        var closed = SourceEventLink.FirstSeenUnwritten(Map, Closed(), SourceEventLinkStatus.PeriodClosed, Now);
        Assert.Equal((SourceEventLinkStatus.PeriodClosed, false), (closed.Status, closed.HasRow));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    public void Довга_назва_обрізається_а_довгий_ID_відхиляється()
    {
        var link = SourceEventLink.FirstSeenUnwritten(
            Map, Closed() with { EventName = new string('n', 500) }, SourceEventLinkStatus.PeriodNotOpen, Now);
        Assert.Equal(SourceEventLink.MaxEventNameLength, link.EventName!.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => SourceEventLink.FirstSeenUnwritten(
            Map, Closed() with { SourceEventId = new string('x', 201) }, SourceEventLinkStatus.PeriodNotOpen, Now));
    }

    /// <summary>Уся таблиця переходів — щоб зміна правила не пройшла непоміченою.</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-F9")]
    // Без рядка.
    [InlineData(SourceEventLinkStatus.Open, SourceEventLinkStatus.Synced, false, true)]
    [InlineData(SourceEventLinkStatus.Open, SourceEventLinkStatus.Unmapped, false, true)]
    [InlineData(SourceEventLinkStatus.Open, SourceEventLinkStatus.Open, false, true)]
    [InlineData(SourceEventLinkStatus.Open, SourceEventLinkStatus.PeriodNotOpen, false, true)]
    [InlineData(SourceEventLinkStatus.Open, SourceEventLinkStatus.Missing, false, false)]
    [InlineData(SourceEventLinkStatus.Open, SourceEventLinkStatus.PeriodChanged, false, false)]
    [InlineData(SourceEventLinkStatus.PeriodNotOpen, SourceEventLinkStatus.Synced, false, true)]
    [InlineData(SourceEventLinkStatus.PeriodClosed, SourceEventLinkStatus.Synced, false, true)]
    [InlineData(SourceEventLinkStatus.RowLimit, SourceEventLinkStatus.Synced, false, true)]
    [InlineData(SourceEventLinkStatus.RowLimit, SourceEventLinkStatus.PeriodClosed, false, true)]
    // З рядком.
    [InlineData(SourceEventLinkStatus.Synced, SourceEventLinkStatus.Missing, true, true)]
    [InlineData(SourceEventLinkStatus.Missing, SourceEventLinkStatus.Synced, true, true)]
    [InlineData(SourceEventLinkStatus.Missing, SourceEventLinkStatus.Missing, true, true)]
    [InlineData(SourceEventLinkStatus.Missing, SourceEventLinkStatus.Open, true, false)]
    [InlineData(SourceEventLinkStatus.Missing, SourceEventLinkStatus.PeriodNotOpen, true, false)]
    [InlineData(SourceEventLinkStatus.Synced, SourceEventLinkStatus.Open, true, false)]
    [InlineData(SourceEventLinkStatus.Synced, SourceEventLinkStatus.RowLimit, true, false)]
    [InlineData(SourceEventLinkStatus.Synced, SourceEventLinkStatus.PeriodChanged, true, true)]
    [InlineData(SourceEventLinkStatus.PeriodChanged, SourceEventLinkStatus.Synced, true, true)]
    [InlineData(SourceEventLinkStatus.Unmapped, SourceEventLinkStatus.Missing, true, true)]
    [InlineData(SourceEventLinkStatus.Synced, SourceEventLinkStatus.PeriodClosed, true, true)]
    [InlineData(SourceEventLinkStatus.PeriodClosed, SourceEventLinkStatus.Missing, true, true)]
    // Зіпсований запис: стан «рядок є» без рядка.
    [InlineData(SourceEventLinkStatus.Synced, SourceEventLinkStatus.PeriodClosed, false, false)]
    [InlineData(SourceEventLinkStatus.Missing, SourceEventLinkStatus.Synced, false, false)]
    public void Таблиця_переходів(SourceEventLinkStatus from, SourceEventLinkStatus to, bool hasRow, bool allowed)
        => Assert.Equal(allowed, SourceEventLink.IsTransitionAllowed(from, to, hasRow));

    /// <remarks>
    /// Мутаційний доказ (A5b): у <c>RekeyTo</c> замінити присвоєння на <c>RowKey = …</c> — рядок втратить
    /// ключ від першого ID, і тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-A5b")]
    public void Перестворена_подія_міняє_ключ_зв_язку_а_рядок_лишається()
    {
        var link = Written();

        link.RekeyTo("new-id");

        Assert.Equal(("new-id", "EF-9b1c", (long?)55, SourceEventLinkStatus.Synced), (link.SourceEventId, link.RowKey, link.TableInstanceId, link.Status));
        Assert.Throws<ArgumentException>(() => link.RekeyTo(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => link.RekeyTo(new string('x', SourceEventLink.MaxSourceEventIdLength + 1)));
    }

    /// <remarks>
    /// Мутаційний доказ (M6): у <c>Observe</c> замінити <c>?? PrimaryElement</c> на присвоєння без нього —
    /// подія без елемента затре наявний ключ, і тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Directive", "HSE301-M6")]
    public void Елемент_заповнюється_нормалізованим_і_порожнє_спостереження_його_не_стирає()
    {
        var link = Written();
        Assert.Null(link.PrimaryElement);

        link.RecordWritten(Closed() with { PrimaryElement = "  Flare A " }, Row, null, null, Now);
        Assert.Equal("FLARE A", link.PrimaryElement);

        link.RecordWritten(Closed(), Row, null, null, Now);
        Assert.Equal("FLARE A", link.PrimaryElement);

        Assert.Null(SourceEventLink.NormalizeElement("  "));
        Assert.Equal(SourceEventLink.MaxPrimaryElementLength, SourceEventLink.NormalizeElement(new string('a', 300))!.Length);
    }

    private static SourceEventLink Written()
        => SourceEventLink.FirstSeenWritten(Map, Closed(), Row, keptManualJson: null, unmappedJson: null, Now);

    private static SourceEventObservation Open()
        => new("9b1c", "Flaring 370", Start, EndUtc: null, SourceModifiedUtc: Start);

    private static SourceEventObservation Closed() => Open() with { EndUtc = Start.AddMinutes(15) };

    private static void AssertTransition(DomainException error, string from, string to)
    {
        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.eventLinkTransitionInvalid", error.Details?["messageKey"]);
        Assert.Equal((from, to), ((string?)error.Details!["from"], (string?)error.Details["to"]));
    }
}
