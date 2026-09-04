// src/Ecr.Application/Security/EndSimulationHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Application.Common;

namespace Ecr.Application.Security;

/// <summary>Завершує сеанс симуляції.</summary>
public sealed class EndSimulationHandler(
    ISimulationService simulation, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(long sessionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) завершити можна ЛИШЕ власний сеанс — інакше один " +
            "   адміністратор обриває чужий і псує його аудит;\n" +
            "2) проставити EndedAt; запис не видаляти (D-25);\n" +
            "3) повернути клієнта до власного профілю — тобто скинути кеш сесії, " +
            "   бо профіль симуляції там і не лежав.");
}
