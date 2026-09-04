using Ecr.Domain.Abstractions;

namespace Ecr.TestKit;

/// <summary>
/// Керований годинник. Саме заради нього в системі заборонений
/// <c>DateTime.Now</c>: без фіксованого часу поведінка періодів, offsets і
/// <c>IsLateEdit</c> невідтворювана.
/// </summary>
public sealed class TestClock(DateTime utcNow) : IClock
{
    /// <inheritdoc />
    public DateTime UtcNow { get; private set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    /// <summary>Переводить годинник уперед.</summary>
    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);

    /// <summary>Встановлює точний момент.</summary>
    public void Set(DateTime utc) => UtcNow = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
}
