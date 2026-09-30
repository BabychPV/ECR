// tests/Ecr.Domain.Tests/Documents/PeriodReopenUntilKindTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Аудит 2026-09-28, B6: <c>Period.Reopen</c> зберігає дедлайн у UTC незалежно
/// від <see cref="DateTime.Kind"/>, з яким його передали.
/// </summary>
/// <remarks>
/// ⛔ До фіксу <c>"…T18:00:00+05:00"</c> з API (десеріалізується як
/// <c>Kind=Local</c>, у поясі сервера) записувався як є, а з <c>datetime2</c>
/// читався як UTC: дедлайн зсувався на зміщення сервера. Мутація: у
/// <c>Period.Reopen</c> повернути <c>ReopenedUntil = until</c> → обидва тести
/// червоні на <c>Kind</c> (у будь-якому поясі сервера), а тест Local — ще й на
/// значенні, коли пояс сервера не UTC.
/// </remarks>
public sealed class PeriodReopenUntilKindTests
{
    private static readonly DateTime ClosedAt = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Period ClosedJanuary()
    {
        var period = new Period(
            projectId: 1, new PeriodKey(202601), sequence: 1,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        period.TransitionTo(PeriodState.Open, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        period.TransitionTo(PeriodState.Grace, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        period.TransitionTo(PeriodState.Closed, ClosedAt);
        return period;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public void Дедлайн_із_зміщенням_Kind_Local_зберігається_як_той_самий_момент_у_UTC()
    {
        // Те, що дає System.Text.Json для "2026-03-10T18:00:00+05:00": Local у поясі сервера.
        var sent = new DateTimeOffset(2026, 3, 10, 18, 0, 0, TimeSpan.FromHours(5));
        var local = sent.LocalDateTime;
        Assert.Equal(DateTimeKind.Local, local.Kind);

        var period = ClosedJanuary();
        period.Reopen(local, "уточнення за листом №17", ClosedAt);

        var until = Assert.NotNull(period.ReopenedUntil);
        Assert.Equal(DateTimeKind.Utc, until.Kind);
        Assert.Equal(new DateTime(2026, 3, 10, 13, 0, 0, DateTimeKind.Utc), until);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public void Дедлайн_без_зони_Kind_Unspecified_читається_як_UTC_без_зсуву()
    {
        var period = ClosedJanuary();
        period.Reopen(
            new DateTime(2026, 3, 10, 18, 0, 0, DateTimeKind.Unspecified), "уточнення за листом №17", ClosedAt);

        var until = Assert.NotNull(period.ReopenedUntil);
        Assert.Equal(DateTimeKind.Utc, until.Kind);
        Assert.Equal(new DateTime(2026, 3, 10, 18, 0, 0, DateTimeKind.Utc), until);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public void Дедлайн_у_UTC_зберігається_без_змін()
    {
        var sent = new DateTime(2026, 3, 10, 18, 0, 0, DateTimeKind.Utc);

        var period = ClosedJanuary();
        period.Reopen(sent, "уточнення за листом №17", ClosedAt);

        Assert.Equal(sent, period.ReopenedUntil);
        Assert.Equal(DateTimeKind.Utc, period.ReopenedUntil!.Value.Kind);
    }

    /// <remarks>
    /// Аудит B6, друга половина: дедлайн у минулому або рівно «зараз» давав
    /// <c>Grace</c> і аудит «відкрито», а <c>Effective()</c> одразу рахував
    /// <c>Closed</c>. Мутація: прибрати перевірку <c>deadline &lt;= utcNow</c> у
    /// <c>Period.Reopen</c> → обидва випадки червоні.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    [InlineData(-3600)]
    [InlineData(0)]
    public void Дедлайн_не_в_майбутньому_відмова_і_період_лишається_закритим(int secondsFromNow)
    {
        var period = ClosedJanuary();

        var error = Assert.Throws<Ecr.Domain.Abstractions.DomainException>(
            () => period.Reopen(ClosedAt.AddSeconds(secondsFromNow), "уточнення за листом №17", ClosedAt));

        Assert.Equal("ECR-PRD-0422", error.ErrorCode);
        Assert.Equal("err.ECR-PRD-0422.reopenUntilInPast", error.Details!["messageKey"]);
        Assert.Equal(PeriodState.Closed, period.State);
        Assert.Null(period.ReopenedUntil);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public void Дедлайн_у_минулому_після_приведення_Local_до_UTC_теж_відмова()
    {
        // 01.03 04:00+05:00 = 28.02 23:00Z — раніше за ClosedAt (01.03 00:00Z).
        var local = new DateTimeOffset(2026, 3, 1, 4, 0, 0, TimeSpan.FromHours(5)).LocalDateTime;
        var period = ClosedJanuary();

        Assert.Throws<Ecr.Domain.Abstractions.DomainException>(
            () => period.Reopen(local, "уточнення за листом №17", ClosedAt));
        Assert.Equal(PeriodState.Closed, period.State);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public void Дедлайн_на_секунду_в_майбутньому_відкриває()
    {
        var period = ClosedJanuary();

        period.Reopen(ClosedAt.AddSeconds(1), "уточнення за листом №17", ClosedAt);

        Assert.Equal(PeriodState.Grace, period.State);
        Assert.Equal(ClosedAt.AddSeconds(1), period.ReopenedUntil);
    }
}
