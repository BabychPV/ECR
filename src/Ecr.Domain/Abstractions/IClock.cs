// src/Ecr.Domain/Abstractions/IClock.cs
namespace Ecr.Domain.Abstractions;

/// <summary>
/// Єдине джерело поточного часу. Прямі звернення до <see cref="DateTime"/>
/// у доменному і прикладному шарі заборонені — інакше поведінка періодів,
/// offsets і <c>IsLateEdit</c> стає невідтворюваною в тестах.
/// </summary>
public interface IClock
{
    /// <summary>Поточний момент у UTC. Завжди <see cref="DateTimeKind.Utc"/>.</summary>
    public DateTime UtcNow { get; }
}
