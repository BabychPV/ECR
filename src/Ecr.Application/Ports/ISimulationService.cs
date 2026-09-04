// src/Ecr.Application/Ports/ISimulationService.cs

using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

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
    public Task<long> StartAsync(int actorUserId, int subjectUserId, string reason, CancellationToken ct);

    public Task EndAsync(long sessionId, CancellationToken ct);

    /// <summary>
    /// Профіль суб'єкта для активного сеансу. **Не кешується** (`ФВ-6.16a` п. 4):
    /// покладений під ключ суб'єкта, він дістався б справжньому користувачеві
    /// з прапорцем <c>IsSimulation</c>. Симуляція рідкісна — перебудова дешева.
    /// </summary>
    public Task<AccessProfile> BuildProfileAsync(long sessionId, CancellationToken ct);
}
