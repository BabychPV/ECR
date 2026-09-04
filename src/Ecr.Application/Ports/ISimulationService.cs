// src/Ecr.Application/Ports/ISimulationService.cs
namespace Ecr.Application.Ports;

using Ecr.Application.Security;

/// <summary>
/// Симуляція «очима користувача» (<c>ФВ-6.16a</c>, <c>D-96</c>).
/// **Лише читання**: будь-який запис під нею відхиляється з
/// <see cref="EditDenyReason.SimulationReadOnly"/> незалежно від прав суб'єкта.
/// </summary>
public interface ISimulationService
{
    /// <summary>
    /// Починає сеанс і **одразу** пише <c>aud.SimulationSession</c>. Без
    /// запису «подивитися очима» стало б способом безслідно переглянути чужі
    /// дані, тому запис не відкладається і не батчиться.
    /// </summary>
    Task<long> StartAsync(int actorUserId, int subjectUserId, string reason, CancellationToken ct);

    Task EndAsync(long sessionId, CancellationToken ct);

    /// <summary>
    /// Профіль суб'єкта для активного сеансу. **Не кешується** (`ФВ-6.16a` п. 4):
    /// покладений під ключ суб'єкта, він дістався б справжньому користувачеві
    /// з прапорцем <c>IsSimulation</c>. Симуляція рідкісна — перебудова дешева.
    /// </summary>
    Task<AccessProfile> BuildProfileAsync(long sessionId, CancellationToken ct);
}
