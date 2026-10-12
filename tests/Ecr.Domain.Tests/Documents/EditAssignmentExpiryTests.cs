using Ecr.Domain.Entities.Documents;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// EA6-D2: термін дії передачі змінюється й знімається (<see cref="EditAssignment.SetExpiry"/>) з тими самими
/// межами, що й при створенні; нормалізація — UTC і точність до мілісекунди (колонка <c>datetime2(3)</c>).
/// </summary>
public sealed class EditAssignmentExpiryTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    private static EditAssignment Create(DateTime? expiresAt = null)
        => new(
            documentId: 7,
            EditAssignmentSlice.Create("F1", null, 1, null),
            isExclusive: true,
            delegatedByUserId: 10,
            handoffId: Guid.NewGuid(),
            createdByUserId: 10,
            utcNow: T0,
            expiresAt: expiresAt);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Термін_встановлюється_знімається_і_оновлює_слід_змін()
    {
        var a = Create();
        Assert.Null(a.ExpiresAt);

        var later = T0.AddHours(1);
        a.SetExpiry(T0.AddDays(30), byUserId: 11, later);

        Assert.Equal(T0.AddDays(30), a.ExpiresAt);
        Assert.Equal(later, a.ModifiedAt);
        Assert.Equal(11, a.ModifiedByUserId);

        a.SetExpiry(null, byUserId: 10, later);
        Assert.Null(a.ExpiresAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Термін_у_минулому_чи_за_стелею_відхиляється_і_нічого_не_змінює()
    {
        var a = Create(T0.AddDays(10));

        Assert.Throws<ArgumentOutOfRangeException>(() => a.SetExpiry(T0, 10, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.SetExpiry(T0.AddMinutes(-1), 10, T0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => a.SetExpiry(T0.AddDays(EditAssignment.MaxLifetimeDays + 1), 10, T0));

        Assert.Equal(T0.AddDays(10), a.ExpiresAt);
        a.SetExpiry(T0.AddDays(EditAssignment.MaxLifetimeDays), 10, T0);
        Assert.Equal(T0.AddDays(EditAssignment.MaxLifetimeDays), a.ExpiresAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Нормалізація_дає_UTC_і_мілісекунди()
    {
        var withTicks = new DateTime(T0.AddDays(5).Ticks + 1234, DateTimeKind.Unspecified);

        Assert.True(EditAssignment.TryNormalizeExpiry(withTicks, T0, out var expiry));
        Assert.Equal(DateTimeKind.Utc, expiry.Kind);
        Assert.Equal(T0.AddDays(5), expiry);

        var local = T0.AddDays(5).ToLocalTime();
        Assert.True(EditAssignment.TryNormalizeExpiry(local, T0, out var fromLocal));
        Assert.Equal(T0.AddDays(5), fromLocal);

        Assert.False(EditAssignment.TryNormalizeExpiry(T0.AddDays(-1), T0, out _));
    }
}
