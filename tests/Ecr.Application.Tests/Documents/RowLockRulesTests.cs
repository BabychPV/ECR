// tests/Ecr.Application.Tests/Documents/RowLockRulesTests.cs
using Ecr.Application.Documents.RowLocks;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ B75: дати блокування (часовий пояс проєкту, кінець включно ↔ UTC, кінець виключно), «діє зараз», заборони стану й
/// режим погодження версії лише в чернетці.
/// </summary>
/// <remarks>
/// Мутації: (1) у <c>ToUtcRange</c> не додавати день до кінця — червоніє «…включно…»; (2) у <c>IsActiveAt</c> прибрати
/// <c>LockFrom &lt;= now</c> — червоніє «…ще_не_почалося…»; (3) у <c>StateDenial</c> прибрати Closed — червоніє рядок
/// закритого періоду.
/// </remarks>
public sealed class RowLockRulesTests
{
    private const string Zone = "Asia/Tashkent"; // UTC+5 без переходу на літній час.

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    public void Дати_проєкту_включно_стають_півінтервалом_UTC_і_повертаються_назад()
    {
        var (from, until) = RowLockRules.ToUtcRange(new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 20), Zone);

        Assert.Equal(new DateTime(2026, 11, 15, 19, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 11, 20, 19, 0, 0, DateTimeKind.Utc), until);

        var (localFrom, localUntil) = RowLockRules.ToLocalDates(from, until, Zone);
        Assert.Equal(new DateOnly(2026, 11, 16), localFrom);
        Assert.Equal(new DateOnly(2026, 11, 20), localUntil);
    }

    /// <remarks>
    /// ✎ F13: у поясі з переходом на літній час ОПІВНОЧІ (America/Santiago: перший неділя вересня, 00:00 → 01:00) початок
    /// доби не існує — проміжок починається з першої існуючої хвилини, а не падає 500. Дата переходу шукається за даними
    /// поясу, а не вписана руками. Мутація N5: прибрати зсув у <c>Midnight</c> — <c>ConvertTimeToUtc</c> кидає, тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    public void Північ_у_переході_на_літній_час_зсувається_на_першу_існуючу_хвилину()
    {
        const string santiago = "America/Santiago";
        var zone = Ecr.Domain.ValueObjects.SiteTimeZone.Create(santiago).ToTimeZoneInfo();

        DateOnly? gap = null;
        for (var day = new DateOnly(2026, 1, 1); day < new DateOnly(2028, 1, 1); day = day.AddDays(1))
        {
            if (zone.IsInvalidTime(day.ToDateTime(TimeOnly.MinValue)))
            {
                gap = day;
                break;
            }
        }

        Assert.True(gap.HasValue, "У поясі America/Santiago 2026–2027 немає переходу опівночі: тест нічого не доводить.");
        var date = gap!.Value;

        var (from, until) = RowLockRules.ToUtcRange(date, date, santiago);

        var firstValid = date.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(firstValid))
        {
            firstValid = firstValid.AddMinutes(30);
        }

        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(firstValid, zone), from);
        Assert.True(until > from);
        Assert.Equal(date, RowLockRules.ToLocalDates(from, until, santiago).From);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    public void Блокування_діє_лише_від_початку_до_кінця_і_до_зняття()
    {
        var created = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var item = RowLock.Create(
            1, 202611, null, "S", "T", "R", created.AddDays(2), created.AddDays(4), 5, null, created);

        Assert.False(item.IsActiveAt(created.AddDays(1)));
        Assert.True(item.IsActiveAt(created.AddDays(2)));
        Assert.False(item.IsActiveAt(created.AddDays(4)));

        Assert.True(item.Release(6, created.AddDays(3)));
        Assert.False(item.IsActiveAt(created.AddDays(3)));
        Assert.False(item.Release(6, created.AddDays(3)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    public void Порожній_або_минулий_проміжок_відхиляється_lockRangeInvalid()
    {
        var now = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

        var empty = Assert.Throws<DomainException>(() => RowLock.Create(1, 202611, null, "S", "T", "R", now, now, 5, null, now));
        var past = Assert.Throws<DomainException>(
            () => RowLock.Create(1, 202611, null, "S", "T", "R", now.AddDays(-3), now.AddDays(-1), 5, null, now));

        Assert.Equal("err.ECR-REQ-0422.lockRangeInvalid", empty.Details!["messageKey"]);
        Assert.Equal("err.ECR-REQ-0422.lockRangeInvalid", past.Details!["messageKey"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    [InlineData(ProjectStatus.Archived, false, PeriodState.Open, EditDenyReason.ProjectArchived)]
    [InlineData(ProjectStatus.Active, true, PeriodState.Open, EditDenyReason.ArchivingInProgress)]
    [InlineData(ProjectStatus.Active, false, PeriodState.Closed, EditDenyReason.PeriodClosed)]
    [InlineData(ProjectStatus.Active, false, PeriodState.Grace, EditDenyReason.None)]
    [InlineData(ProjectStatus.Active, false, PeriodState.Open, EditDenyReason.None)]
    public void Архів_архівація_й_закритий_період_забороняють_блокування(
        ProjectStatus project, bool archiving, PeriodState period, EditDenyReason expected)
    {
        var target = new RowLockTarget(1, 5, project, archiving, period, Zone, 1, "DOC");

        Assert.Equal(expected, RowLockRules.StateDenial(target));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    public void Режим_погодження_змінюється_лише_в_чернетці()
    {
        var now = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var version = new TemplateVersion(1, "1.0", 5, now);

        Assert.Equal(WorkflowMode.SubmitApprove, version.WorkflowMode);
        Assert.True(version.SetWorkflowMode(WorkflowMode.None));
        Assert.False(version.SetWorkflowMode(WorkflowMode.None));

        version.Publish(5, now);
        Assert.Throws<DomainException>(() => version.SetWorkflowMode(WorkflowMode.SubmitApprove));
        Assert.Equal(WorkflowMode.None, version.WorkflowMode);
    }
}
