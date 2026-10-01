// src/Ecr.Application/Registries/SetEntryValidityHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

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
    IClock clock,
    Keys.RegistryKeyService? keys = null)
{
    // ⚠ `keys` необов'язковий лише для тестів, що будують обробник руками (як в
    // `UpsertRegistryEntryHandler`); контейнер підставляє `RegistryKeyService` завжди. Що
    // на справжньому шляху вікно перевіряється, тримає `RegistryKeyLifecycleHttpTests`.

    /// <summary>Право на зміну даних довідника (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Змінює вікно і перераховує ознаку.</summary>
    /// <param name="registryCode">Код довідника з маршруту.</param>
    /// <param name="registryEntryId">Запис довідника.</param>
    /// <param name="from">Початок вікна; <c>null</c> — без обмеження.</param>
    /// <param name="to">Кінець вікна; <c>null</c> — без обмеження.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки рядків змінили ознаку — у той чи інший бік.</returns>
    /// <exception cref="NotFoundException">
    /// Довідника немає чи він схований забороною, запису немає чи він іншого довідника — <c>ECR-REG-0404</c>.
    /// </exception>
    public async Task<int> HandleAsync(
        string registryCode, long registryEntryId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не змінює довідники.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        // ⛔ S18: спершу довідник ЗА КОДОМ з маршруту і право на нього (глобальне право АБО ресурсний
        // грант Write, A7-58), лише потім запис. Доти код з маршруту ігнорувався: право питалось на
        // довідник ЗАПИСУ, тож запис схованого довідника відповідав `404 registryId` з його
        // `registryDefId`, без гранта — `403`, а неіснуючий — `404 registryEntry`; і запис будь-якого
        // довідника змінювався через чужий код у шляху.
        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess
            .RequireAsync(access, currentUser, Permission, GrantLevel.Write, lookup, ct)
            .ConfigureAwait(false);

        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);
        var entry = await registries.FindEntryAsync(registryEntryId, ct).ConfigureAwait(false);
        if (entry is null || entry.RegistryDefId != definition.Id)
        {
            throw RegistryAccess.EntryNotFound(registryEntryId, registryCode);
        }

        var previousFrom = entry.ValidFrom;
        var previousTo = entry.ValidTo;

        // ⛔ D-211: вікно чинності запису External-довідника — теж дані AF (опис — ДО зміни вікна).
        ExternalRegistryGuard.EnsureManualEditAllowed(definition);

        // Порожнє вікно відхиляє сутність (ECR-REG-0422) — до будь-яких змін
        // у документах.
        entry.SetValidity(from, to);

        definition.BumpDataRevision();

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
        //
        // ⛔ Integration-pending фікс (finding 4, другий дефект у тому самому
        // обробнику — окремий від необробленого `InvalidOperationException`
        // в `OrphanScanner`). Нове вікно ЗБЕРІГАЄТЬСЯ тут ПЕРШИМ, а не
        // востаннє: `entry.SetValidity`/`definition.BumpDataRevision` вище —
        // це зміни в трекері EF, які нікуди не пишуться, доки не
        // викликати `SaveChangesAsync`. `scanner.RescanForEntryAsync`
        // читає `dic.RegistryEntry` НОВИМ запитом через `AsNoTracking()` —
        // тобто буквальним `SELECT`, який до збереження бачить СТАРЕ вікно.
        // Порядок «спершу сканувати, тоді зберегти» (був тут) означав, що
        // перерахунок `IsOrphaned` завжди дивився на вікно, яке щойно
        // замінили, — і на звуження, і на розширення рахував НУЛЬ
        // зачеплених рядків, хоча сама зміна вікна проходила й лягала в
        // базу коректно.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ RT-10b (§4.4): рядки ключів ДЗЕРКАЛЯТЬ вікно запису, тож перераховуються тут,
            // у тій самій транзакції, — інакше в `dic.RegistryEntryKey` лишилося б старе вікно.
            // І перевіряються: нове вікно може перетнутися з дублем ключа, якого старе не
            // зачіпало, — тоді 409 `keyWindowOverlap`, і вікно не змінюється.
            if (keys is not null)
            {
                await keys.ApplyAsync(definition, entry, innerCt).ConfigureAwait(false);
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

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
