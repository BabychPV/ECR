// src/Ecr.Application/Registries/SetEntryValidityHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

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
    IRegistryStore registries,
    IOrphanScanner scanner,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну даних довідника (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Змінює вікно і перераховує ознаку.</summary>
    /// <param name="registryEntryId">Запис довідника.</param>
    /// <param name="from">Початок вікна; <c>null</c> — без обмеження.</param>
    /// <param name="to">Кінець вікна; <c>null</c> — без обмеження.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки рядків змінили ознаку — у той чи інший бік.</returns>
    /// <exception cref="NotFoundException">Запису немає — <c>ECR-REG-0404</c>.</exception>
    public async Task<int> HandleAsync(
        long registryEntryId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не змінює довідники.");

        var entry = await registries.FindEntryAsync(registryEntryId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-REG-0404", $"Запису довідника {registryEntryId} не існує.");

        var previousFrom = entry.ValidFrom;
        var previousTo = entry.ValidTo;

        // Порожнє вікно відхиляє сутність (ECR-REG-0422) — до будь-яких змін
        // у документах.
        entry.SetValidity(from, to);

        var definition = await registries.FindDefinitionByIdAsync(entry.RegistryDefId, ct).ConfigureAwait(false);
        definition?.BumpDataRevision();

        int affected = 0;

        // ⛔ Q-244 (той самий клас дефекту, що Q-243): коментар нижче
        // стверджував «У ТІЙ САМІЙ транзакції», хоч жодної спільної
        // транзакції не було — `scanner.RescanForEntryAsync` (`ExecuteUpdateAsync`)
        // автокомітився окремо від аудиту (сирий SQL без відкритої
        // транзакції) і від фінального `SaveChangesAsync`. Тепер усе троє —
        // одним замиканням `IUnitOfWork.ExecuteInTransactionAsync`.
        //
        // ⚠ У ТІЙ САМІЙ транзакції, що й сама зміна вікна. Інакше між двома
        // комітами існує стан, у якому запис уже нечинний, а рядки ще не
        // позначені: Submit у цю мить проходить і створює зріз, який нічна
        // перевірка потім оголосить осиротілим.
        //
        // Сканер працює В ОБИДВА боки: звуження ставить ознаку, розширення —
        // знімає (ФВ-8.13a).
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            affected = await scanner.RescanForEntryAsync(registryEntryId, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "dic.RegistryEntry",
                    EntityId: checked((int)registryEntryId),
                    ChangeClass: Domain.Enums.ChangeClass.Breaking,
                    Operation: "SetValidity",
                    OldJson: Window(previousFrom, previousTo),
                    NewJson: Window(from, to),
                    ChangeReason: $"Перераховано рядків: {affected}.",
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // Повертається масштаб наслідку, а не «ок»: той, хто звузив вікно, має
        // бачити, скільки рядків щойно заблокував.
        return affected;
    }

    private static string Window(DateOnly? from, DateOnly? to)
        => $"{{\"validFrom\":{Iso(from)},\"validTo\":{Iso(to)}}}";

    private static string Iso(DateOnly? value)
        => value is { } date ? $"\"{date:yyyy-MM-dd}\"" : "null";
}
