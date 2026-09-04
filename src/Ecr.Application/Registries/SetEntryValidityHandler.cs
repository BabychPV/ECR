// src/Ecr.Application/Registries/SetEntryValidityHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Application.Common;

namespace Ecr.Application.Registries;

/// <summary>
/// Зміна вікна чинності запису довідника — і **негайний** перерахунок
/// <c>IsOrphaned</c> на рядках, що на нього посилаються (ФВ-8.13a, D-98).
/// </summary>
/// <remarks>
/// Перерахунок тут, а не вночі, з однієї причини: той, хто звузив вікно, має
/// одразу побачити наслідок. Нічна перевірка лишається — вона ловить те, що
/// змінилося іншими шляхами.
/// </remarks>
public sealed class SetEntryValidityHandler(
    IOrphanScanner scanner, IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    public Task<int> HandleAsync(long registryEntryId, DateOnly? from, DateOnly? to, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) entry.SetValidity(from, to);\n" +
            "2) У ТІЙ САМІЙ транзакції — scanner.RescanForEntryAsync(registryEntryId);\n" +
            "3) сканер працює В ОБИДВА боки: звуження ставить ознаку, розширення " +
            "   ЗНІМАЄ. Без другого виправлення довідника не розблокує Submit;\n" +
            "4) повернути кількість зачеплених рядків — користувач має бачити " +
            "   масштаб того, що щойно зробив;\n" +
            "5) аудит зміни вікна.");
}
