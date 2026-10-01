// src/Ecr.Application/Sources/SourceEventMapChangeHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Sources;

/// <summary>
/// Змінює мапінг подій: повна заміна режиму об'єму, звуження, стану й полів. Право <c>Integration.Manage</c>
/// і грант <c>Manage</c> на проєкт документа.
/// </summary>
/// <remarks>
/// ⚠ Пауза й відновлення — це <c>isActive</c> у тілі: окремих дій немає, бо зміна стану без зміни полів —
/// той самий запит. Документ і таблиця не змінюються (вони — ключ мапінгу, <c>UQ_SourceEventMap</c>).
/// </remarks>
public sealed class UpdateSourceEventMapHandler(
    ISourceEventMapStore store,
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Операція в журналі структурних змін.</summary>
    public const string AuditOperation = "UpdateSourceEventMap";

    /// <summary>Замінює налаштування мапінгу.</summary>
    /// <param name="id">Мапінг.</param>
    /// <param name="command">Нові налаштування.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<SourceEventMapDto> HandleAsync(int id, UpdateSourceEventMapCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        var map = await SourceEventMapSupport.RequireVisibleMapAsync(store, access, profile, id, ct).ConfigureAwait(false);
        var document = await store.FindDocumentAsync(map.DocumentId, ct).ConfigureAwait(false);
        if (document is not null)
        {
            SourceEventMapSupport.RequireProjectManage(profile, document.ProjectId);
        }

        SourceEventMapSupport.RequireShape(
            command.VolumeMode, command.FilterAttribute, command.FilterScope, command.FilterValue, command.Fields);

        var old = IntegrationConfigAudit.Snapshot(SourceEventMapSupport.ToDto(map));
        var built = await SourceEventMapSupport.BuildSpecsAsync(store, sources, command.Fields, ct).ConfigureAwait(false);

        var specs = built.Select(b => b.Spec).ToList();

        // Спершу перевірка (без змін у мапінгу), потім позначка старих полів до видалення, потім заміна:
        // відмова домену не лишає відстежених полів без власника.
        map.EnsureFieldsAcceptable(specs);
        map.SetVolumeMode(command.VolumeMode);
        map.SetFilter(command.FilterAttribute, command.FilterScope, command.FilterValue);
        store.ReleaseFields(map);
        map.ReplaceFields(specs);
        SourceEventMapSupport.AddValues(map, built);
        if (command.IsActive)
        {
            map.Activate();
        }
        else
        {
            map.Deactivate();
        }

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await store.SaveAsync(innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.EventMapType, map.Id, AuditOperation,
                old, IntegrationConfigAudit.Snapshot(SourceEventMapSupport.ToDto(map)),
                $"Мапінг подій {map.Id} сутності {map.SourceEntityId} змінено.", innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return SourceEventMapSupport.ToDto(map);
    }
}

/// <summary>
/// Видаляє мапінг подій, за яким нічого не синхронізовано. Право <c>Integration.Manage</c> і грант
/// <c>Manage</c> на проєкт документа.
/// </summary>
/// <remarks>
/// ⛔ Мапінг із зв'язками «подія ↔ рядок» не видаляється — <c>409 ECR-INT-0409</c>: зв'язки пояснюють, звідки
/// рядки документа, і зникли б разом з ним. Вихід — пауза (<c>isActive = false</c>), рядки лишаються.
/// </remarks>
public sealed class DeleteSourceEventMapHandler(
    ISourceEventMapStore store,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Операція в журналі структурних змін.</summary>
    public const string AuditOperation = "DeleteSourceEventMap";

    /// <summary>Видаляє мапінг.</summary>
    /// <param name="id">Мапінг.</param>
    /// <param name="ct">Скасування.</param>
    public async Task HandleAsync(int id, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        var map = await SourceEventMapSupport.RequireVisibleMapAsync(store, access, profile, id, ct).ConfigureAwait(false);
        var document = await store.FindDocumentAsync(map.DocumentId, ct).ConfigureAwait(false);
        if (document is not null)
        {
            SourceEventMapSupport.RequireProjectManage(profile, document.ProjectId);
        }

        var links = await store.CountLinksAsync(id, ct).ConfigureAwait(false);
        if (links > 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.EntityFieldMapStateConflict,
                $"За мапінгом подій {id} уже синхронізовано {links} подій: видалення лишило б рядки без пояснення. Призупиніть мапінг.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.eventMapHasLinks",
                    ["links"] = links,
                });
        }

        var removed = IntegrationConfigAudit.Snapshot(SourceEventMapSupport.ToDto(map));
        var entityId = map.SourceEntityId;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await store.RemoveMapAsync(map, innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.EventMapType, id, AuditOperation,
                removed, newJson: null, $"Мапінг подій {id} сутності {entityId} видалено.", innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
