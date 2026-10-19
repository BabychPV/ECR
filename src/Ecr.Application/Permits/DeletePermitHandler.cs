// src/Ecr.Application/Permits/DeletePermitHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Keys;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits;

/// <summary>
/// Видаляє ЧЕРНЕТКУ дозволу разом із рядками всіх його таблиць (логічно, однією транзакцією). Право
/// <c>Registry.EditData</c> (або грант <c>Write</c>) на реєстр шапки (Permits, PM-04).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Лише чернетка: чинний чи закінчений дозвіл видавати «було» не можна - на нього читають аркуші й звіти. Закінчується
/// він датою (<c>end</c>), а не видаленням.
/// </para>
/// <para>
/// ⚠ Рядки таблиць видаляються ЯВНО в обробнику, а не каскадом ядра: поле композиції <c>PERMIT</c> має політику
/// <c>Restrict</c> (PM-03), тож загальне видалення запису шапки відмовило б. Посилання ЗЗОВНІ набору (інший запис, що
/// вказує на рядок чернетки) - <c>409 ECR-PRM-4096 inUse</c>.
/// </para>
/// </remarks>
public sealed class DeletePermitHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    IPermitStore stats,
    RegistryKeyService keys,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну даних реєстрів.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Видаляє чернетку.</summary>
    /// <param name="permitId">Дозвіл.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4096</c>: не чернетка (<c>notDraft</c>) чи є посилання (<c>inUse</c>).</exception>
    public async Task HandleAsync(long permitId, CancellationToken ct)
    {
        var (type, entry) = await new PermitLocator(permits, registries, access, currentUser)
            .FindAsync(permitId, Permission, GrantLevel.Write, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var reader = new PermitReader(permits, registries, rows, stats, clock);
        var view = await reader.ViewAsync(type, ct).ConfigureAwait(false) ?? throw PermitErrors.NotFound(permitId);
        ExternalRegistryGuard.EnsureManualEditAllowed(view.Header);
        foreach (var table in view.Tables)
        {
            ExternalRegistryGuard.EnsureManualEditAllowed(table.Def);
        }

        if (entry.IsActive)
        {
            throw PermitErrors.NotDraft(permitId, PermitStatuses.Active);
        }

        var number = entry.DisplayL10n.Get(Localization.UiStringResolver.DefaultLanguage) ?? entry.Code;
        var tableDefs = view.Tables.ToDictionary(t => t.Def.Id, t => t.Def);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // Під блокуванням запису: дозвіл могли активувати між читанням і видаленням.
            await rows.LockEntriesAsync([permitId], innerCt).ConfigureAwait(false);
            var fresh = await reader.HeadersAsync(view, [permitId], innerCt).ConfigureAwait(false);
            if (fresh.Count == 0)
            {
                throw PermitErrors.NotFound(permitId);
            }

            if (fresh[0].Entry.IsActive)
            {
                throw PermitErrors.NotDraft(permitId, PermitStatuses.Active);
            }

            var parts = (await registries.ListCompositionChildrenAsync([permitId], innerCt).ConfigureAwait(false))
                .Where(c => tableDefs.ContainsKey(c.Entry.RegistryDefId))
                .Select(c => c.Entry)
                .ToList();

            var outside = await registries.CountReferencesFromOutsideAsync([permitId, .. parts.Select(p => p.Id)], innerCt).ConfigureAwait(false);
            if (outside.Total > 0)
            {
                throw PermitErrors.InUse(permitId, outside.Total);
            }

            foreach (var part in parts)
            {
                part.SoftDelete(userId, clock.UtcNow);
                await keys.ReleaseAsync(part, innerCt).ConfigureAwait(false);
            }

            entry.SoftDelete(userId, clock.UtcNow);
            await keys.ReleaseAsync(entry, innerCt).ConfigureAwait(false);

            view.Header.BumpDataRevision();
            foreach (var def in parts.Select(p => tableDefs[p.RegistryDefId]).Distinct())
            {
                def.BumpDataRevision();
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "dic.RegistryEntry",
                    EntityId: checked((int)permitId),
                    ChangeClass: ChangeClass.Breaking,
                    Operation: "DeletePermit",
                    OldJson: System.Text.Json.JsonSerializer.Serialize(new { type = type.Code, number, rows = parts.Count }),
                    NewJson: null,
                    ChangeReason: $"Чернетку дозволу «{number}» ({type.Code}) видалено разом із рядками: {parts.Count}.",
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
