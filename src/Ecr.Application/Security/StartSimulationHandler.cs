// src/Ecr.Application/Security/StartSimulationHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>
/// Починає сеанс «очима користувача» (ФВ-6.16a, D-96). Право
/// <c>Security.Simulate</c>.
/// </summary>
/// <remarks>
/// ⚠ Запис у <c>aud.SimulationSession</c> робиться **до** видачі профілю.
/// Інакше збій між видачею і записом лишив би сеанс перегляду чужих даних без
/// сліду — а слід тут і є суттю вимоги.
/// </remarks>
public sealed class StartSimulationHandler(
    ISimulationService simulation, ICurrentUser currentUser, IClock clock)
{
    public Task<long> HandleAsync(int subjectUserId, string reason, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) reason обов'язковий; симуляція себе → ECR-SIM-0422;\n" +
            "2) simulation.StartAsync — запис сеансу ПЕРШИМ, профіль потім;\n" +
            "3) профіль будувати ЗАНОВО і ⛔ НЕ класти в кеш (ФВ-6.16a п. 4): " +
            "   під ключем суб'єкта він дістався б справжньому користувачеві " +
            "   з прапорцем IsSimulation;\n" +
            "4) у профілі виставити IsSimulation, SimulatedForUserId, " +
            "   SimulationActorUserId — автором дій лишається той, хто симулює;\n" +
            "5) клієнт зобов'язаний показувати банер увесь сеанс.");
}
