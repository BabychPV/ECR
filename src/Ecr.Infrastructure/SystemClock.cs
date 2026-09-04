using Ecr.Domain.Abstractions;

namespace Ecr.Infrastructure;

/// <summary>
/// Системний годинник — єдине місце в застосунку, де читається справжній час.
/// </summary>
/// <remarks>
/// Саме заради нього прямі звернення до <see cref="DateTime"/> у доменному й
/// прикладному шарі заборонені: без підмінного годинника поведінка періодів,
/// offsets і <c>IsLateEdit</c> невідтворювана в тестах.
///
/// ⚠ Файла немає в дереві `05-skeleton.md` §1: порт <c>IClock</c> там
/// оголошений, реалізація — ні (`Q-050`).
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}
