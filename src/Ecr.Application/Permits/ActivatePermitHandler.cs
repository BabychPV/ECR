// src/Ecr.Application/Permits/ActivatePermitHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits;

/// <summary>
/// Активує чернетку: <c>Draft → Active</c> (Permits, PM-04). Право <c>Registry.EditData</c> (або грант <c>Write</c>) на
/// реєстр шапки.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Активація - момент, коли дозвіл починає ТРИМАТИ ключ (тип + номер): вона звіряється з усіма чинними дозволами того
/// самого номера, і перетин півінтервалів - <c>409 ECR-PRM-4092</c>. Ті самі ключі дає й служба ключів реєстрів (гонка
/// між двома одночасними активаціями ловиться її блокуванням і індексом) - вона перетворюється на той самий код.
/// </para>
/// <para>
/// ⚠ Тип мусить бути опублікованим: за чернеткою нового типу аркуші дозвіл не читатимуть.
/// </para>
/// </remarks>
public sealed class ActivatePermitHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    IPermitStore stats,
    RegistryEntryWriter writer,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну даних реєстрів.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Активує дозвіл.</summary>
    /// <param name="permitId">Дозвіл.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422 typeNotPublished</c>.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4096 notDraft</c>, <c>ECR-PRM-4092</c>.</exception>
    public async Task<PermitDto> HandleAsync(long permitId, CancellationToken ct)
    {
        var (type, entry) = await new PermitLocator(permits, registries, access, currentUser)
            .FindAsync(permitId, Permission, GrantLevel.Write, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        if (type.Status != PermitTypeStatus.Published)
        {
            throw PermitErrors.TypeNotPublished(type.Code);
        }

        var reader = new PermitReader(permits, registries, rows, stats, clock);
        var view = await reader.ViewAsync(type, ct).ConfigureAwait(false) ?? throw PermitErrors.NotFound(permitId);
        ExternalRegistryGuard.EnsureManualEditAllowed(view.Header);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await rows.LockEntriesAsync([permitId], innerCt).ConfigureAwait(false);
            var all = await reader.HeadersAsync(view, null, innerCt).ConfigureAwait(false);
            var current = all.FirstOrDefault(h => h.Entry.Id == permitId) ?? throw PermitErrors.NotFound(permitId);
            if (current.Entry.IsActive)
            {
                throw PermitErrors.NotDraft(permitId, current.StatusOn(reader.Today));
            }


            if (PermitLifecycle.ActiveOverlap(all, permitId, current.Number, current.Entry.Window) is { } other)
            {
                throw PermitErrors.Overlap(permitId, other.Entry.Id, current.Number);
            }

            entry.Activate();
            try
            {
                await writer.SaveEntryAsync(
                    view.Header, entry, [new RegistryValueFieldChange(RegistryEntryWriter.ActiveFieldCode, false, true)], userId, innerCt)
                    .ConfigureAwait(false);
            }
            catch (BusinessRuleException ex) when (ex.ErrorCode == ErrorCodes.RegistryKeyConflict)
            {
                // Гонка: паралельна активація випередила між нашою звіркою й записом ключа - та сама відмова, що й вище.
                var otherId = long.TryParse(ex.Details?.GetValueOrDefault("entryId") as string, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
                throw PermitErrors.Overlap(permitId, otherId, current.Number);
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "dic.RegistryEntry",
                    EntityId: checked((int)permitId),
                    ChangeClass: ChangeClass.Guarded,
                    Operation: "ActivatePermit",
                    OldJson: null,
                    NewJson: System.Text.Json.JsonSerializer.Serialize(new { type = type.Code, number = current.Number }),
                    ChangeReason: $"Дозвіл «{current.Number}» ({type.Code}) активовано.",
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var activated = (await reader.HeadersAsync(view, [permitId], ct).ConfigureAwait(false)).Single();
        return await reader.DtoAsync(view, activated, reader.Today, ct).ConfigureAwait(false);
    }
}
