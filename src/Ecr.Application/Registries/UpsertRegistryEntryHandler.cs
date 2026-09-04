// src/Ecr.Application/Registries/UpsertRegistryEntryHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Registries;

/// <summary>Створення і зміна запису довідника (ФВ-8.6, ФВ-8.7).</summary>
public sealed class UpsertRegistryEntryHandler(
    ICellStore cellStore, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    public Task<long> HandleAsync(RegistryEntryUpsertDto dto, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) Code валідувати як EcrCode (D-89);\n" +
            "2) значення полів — типізовано за RegistryFieldDef.DataType, не рядком;\n" +
            "3) ⛔ фізичне видалення заборонене, якщо на запис посилаються дані → " +
            "   ECR-REG-0409. Це перевіряє FK у базі (ФВ-8.7), а не задача вночі;\n" +
            "4) BumpDataRevision — інакше кеш віддаватиме старі записи;\n" +
            "5) зміна ВІКНА ДІЇ йде не сюди, а в SetEntryValidityHandler: вона " +
            "   тягне перерахунок IsOrphaned.");
}
