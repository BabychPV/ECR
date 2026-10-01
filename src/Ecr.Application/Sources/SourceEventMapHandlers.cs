// src/Ecr.Application/Sources/SourceEventMapHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Sources;

/// <summary>Явна відповідність «значення джерела → запис довідника» у відповіді (§4.7.3).</summary>
/// <param name="Id">Ідентифікатор <c>ext.SourceEventValueMap</c>.</param>
/// <param name="SourceValue">Значення атрибута в джерелі.</param>
/// <param name="RegistryEntryId">Запис довідника Lookup-колонки.</param>
public sealed record SourceEventValueDto(int Id, string SourceValue, long RegistryEntryId);

/// <summary>Поле мапінгу подій «атрибут → колонка» у відповіді (§4.7.3).</summary>
/// <param name="Id">Ідентифікатор <c>ext.SourceEventFieldMap</c>.</param>
/// <param name="TargetColumnDefId">Колонка-ціль.</param>
/// <param name="SourceAttribute">Ім'я атрибута з каталогу або <c>$start</c>/<c>$end</c>/<c>$name</c>.</param>
/// <param name="AttributeScope">Де лежить атрибут.</param>
/// <param name="ValueKind">Як значення лягає в колонку.</param>
/// <param name="SourceUnitId">Одиниця атрибута в джерелі; <c>null</c> — без конверсії.</param>
/// <param name="TargetUnitId">Одиниця колонки; <c>null</c> — без конверсії.</param>
/// <param name="Values">Явні відповідності значень (лише для <see cref="SourceEventValueKind.ValueMap"/>).</param>
public sealed record SourceEventFieldDto(
    int Id,
    int TargetColumnDefId,
    string SourceAttribute,
    SourceEventAttributeScope AttributeScope,
    SourceEventValueKind ValueKind,
    int? SourceUnitId,
    int? TargetUnitId,
    IReadOnlyList<SourceEventValueDto> Values);

/// <summary>Мапінг подій джерела «шаблон подій → динамічна таблиця документа» (§4.7.3).</summary>
/// <param name="Id">Ідентифікатор <c>ext.SourceEventMap</c>.</param>
/// <param name="SourceEntityId">Сутність-шаблон подій.</param>
/// <param name="DocumentId">Документ ділянки, куди лягають події.</param>
/// <param name="TableDefId">Динамічна таблиця.</param>
/// <param name="FilterAttribute">Атрибут звуження; <c>null</c> — без звуження.</param>
/// <param name="FilterScope">Де лежить атрибут звуження.</param>
/// <param name="FilterValue">Значення звуження.</param>
/// <param name="VolumeMode">Звідки береться об'єм (§4.7.5).</param>
/// <param name="IsActive">Чи діє синхронізація за мапінгом.</param>
/// <param name="Fields">Поля «атрибут → колонка».</param>
public sealed record SourceEventMapDto(
    int Id,
    int SourceEntityId,
    long DocumentId,
    int TableDefId,
    string? FilterAttribute,
    SourceEventAttributeScope? FilterScope,
    string? FilterValue,
    SourceEventVolumeMode VolumeMode,
    bool IsActive,
    IReadOnlyList<SourceEventFieldDto> Fields);

/// <summary>Явна відповідність значення у запиті на збереження мапінгу.</summary>
/// <param name="SourceValue">Значення атрибута в джерелі.</param>
/// <param name="RegistryEntryId">Запис довідника колонки.</param>
public sealed record SourceEventValueInput(string SourceValue, long RegistryEntryId);

/// <summary>Поле у запиті на збереження мапінгу.</summary>
/// <param name="TargetColumnDefId">Колонка-ціль.</param>
/// <param name="SourceAttribute">Атрибут з каталогу або <c>$start</c>/<c>$end</c>/<c>$name</c>.</param>
/// <param name="AttributeScope">Де лежить атрибут.</param>
/// <param name="ValueKind">Як значення лягає в колонку.</param>
/// <param name="SourceUnitId">Одиниця джерела; <c>null</c> — без конверсії.</param>
/// <param name="TargetUnitId">Одиниця колонки; <c>null</c> — без конверсії.</param>
/// <param name="Values">Явні відповідності; лише для <see cref="SourceEventValueKind.ValueMap"/>.</param>
public sealed record SourceEventFieldInput(
    int TargetColumnDefId,
    string SourceAttribute,
    SourceEventAttributeScope AttributeScope,
    SourceEventValueKind ValueKind,
    int? SourceUnitId,
    int? TargetUnitId,
    IReadOnlyList<SourceEventValueInput>? Values);

/// <summary>Створення мапінгу подій.</summary>
/// <param name="SourceEntityId">Сутність-шаблон подій.</param>
/// <param name="DocumentId">Документ ділянки.</param>
/// <param name="TableDefId">Динамічна таблиця документа.</param>
/// <param name="VolumeMode">Звідки береться об'єм.</param>
/// <param name="FilterAttribute">Атрибут звуження; усі три значення звуження разом або жодного.</param>
/// <param name="FilterScope">Де лежить атрибут звуження.</param>
/// <param name="FilterValue">Значення звуження.</param>
/// <param name="Fields">Поля; серед них обов'язково <c>$start</c> і <c>$end</c>.</param>
public sealed record CreateSourceEventMapCommand(
    int SourceEntityId,
    long DocumentId,
    int TableDefId,
    SourceEventVolumeMode VolumeMode,
    string? FilterAttribute,
    SourceEventAttributeScope? FilterScope,
    string? FilterValue,
    IReadOnlyList<SourceEventFieldInput> Fields);

/// <summary>Повна заміна налаштувань мапінгу подій; документ і таблиця лишаються.</summary>
/// <param name="VolumeMode">Звідки береться об'єм.</param>
/// <param name="IsActive">Чи діє синхронізація за мапінгом (пауза — <c>false</c>).</param>
/// <param name="FilterAttribute">Атрибут звуження.</param>
/// <param name="FilterScope">Де лежить атрибут звуження.</param>
/// <param name="FilterValue">Значення звуження.</param>
/// <param name="Fields">Нові поля замість усіх наявних.</param>
public sealed record UpdateSourceEventMapCommand(
    SourceEventVolumeMode VolumeMode,
    bool IsActive,
    string? FilterAttribute,
    SourceEventAttributeScope? FilterScope,
    string? FilterValue,
    IReadOnlyList<SourceEventFieldInput> Fields);

/// <summary>Спільні кроки обробників мапінгу подій (HSE301 A6).</summary>
internal static class SourceEventMapSupport
{
    /// <summary>DTO мапінгу.</summary>
    public static SourceEventMapDto ToDto(SourceEventMap map) => new(
        map.Id,
        map.SourceEntityId,
        map.DocumentId,
        map.TableDefId,
        map.FilterAttribute,
        map.FilterScope,
        map.FilterValue,
        map.VolumeMode,
        map.IsActive,
        [.. map.Fields
            .OrderBy(f => f.Id)
            .Select(f => new SourceEventFieldDto(
                f.Id,
                f.TargetColumnDefId,
                f.SourceAttribute,
                f.AttributeScope,
                f.ValueKind,
                f.SourceUnitId,
                f.TargetUnitId,
                [.. f.Values.OrderBy(v => v.Id).Select(v => new SourceEventValueDto(v.Id, v.SourceValue, v.RegistryEntryId))]))]);

    /// <summary>Мапінг або 404.</summary>
    public static async Task<SourceEventMap> RequireMapAsync(ISourceEventMapStore store, int id, CancellationToken ct)
        => await store.FindMapAsync(id, ct).ConfigureAwait(false) ?? throw MapNotFound(id);

    /// <summary>
    /// Мапінг, документ якого користувач бачить, або та сама 404, що й на неіснуючий (S18, B-08).
    /// </summary>
    /// <remarks>
    /// ⛔ До S18 перелік і картка віддавали мапінги документів невидимих проєктів, а зміна й видалення на
    /// них відмовляли <c>403 noProjectManageGrant</c> з <c>projectId</c> — підтвердження, що мапінг є, і
    /// номер чужого проєкту. Таблиця подій (<c>ListSourceEventsHandler</c>) такі мапінги вже ховала.
    /// </remarks>
    public static async Task<SourceEventMap> RequireVisibleMapAsync(
        ISourceEventMapStore store, IAccessDecisionService access, AccessProfile profile, int id, CancellationToken ct)
    {
        var map = await RequireMapAsync(store, id, ct).ConfigureAwait(false);

        return await IsVisibleAsync(access, profile, map, ct).ConfigureAwait(false) ? map : throw MapNotFound(id);
    }

    /// <summary>Чи бачить користувач документ, у який пише мапінг.</summary>
    public static async Task<bool> IsVisibleAsync(
        IAccessDecisionService access, AccessProfile profile, SourceEventMap map, CancellationToken ct)
        => (await access.CanReadDocumentAsync(profile, map.DocumentId, ct).ConfigureAwait(false)).IsAllowed;

    private static NotFoundException MapNotFound(int id)
        => new(
            ErrorCodes.SourceEntityNotFound,
            $"Мапінгу подій {id} немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0404.eventMap",
                ["eventMapId"] = id.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary>
    /// Мапінг пише в документ проєкту: на проєкт потрібен грант <c>Manage</c> — так само, як для
    /// мапінгу поля на колонку (S3 аудиту безпеки).
    /// </summary>
    public static void RequireProjectManage(AccessProfile profile, int projectId)
    {
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403",
                $"Немає гранта Manage на проєкт {projectId}, у документ якого писав би мапінг подій.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectManageGrant",
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>
    /// Перевіряє поля й будує їхній домен: колонки існують, одиниці є в довіднику, а записи відповідностей
    /// належать довіднику своєї Lookup-колонки.
    /// </summary>
    public static async Task<List<(SourceEventFieldSpec Spec, IReadOnlyList<SourceEventValueInput> Values)>> BuildSpecsAsync(
        ISourceEventMapStore store,
        ICollectionStore sources,
        IReadOnlyList<SourceEventFieldInput> fields,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var columns = await store
            .FindColumnsAsync([.. fields.Select(f => f.TargetColumnDefId).Distinct()], ct)
            .ConfigureAwait(false);

        var entryIds = fields.SelectMany(f => f.Values ?? []).Select(v => v.RegistryEntryId).Distinct().ToList();
        var entryDefs = entryIds.Count == 0
            ? new Dictionary<long, int>()
            : new Dictionary<long, int>(await store.FindRegistryEntryDefsAsync(entryIds, ct).ConfigureAwait(false));

        var result = new List<(SourceEventFieldSpec, IReadOnlyList<SourceEventValueInput>)>();
        foreach (var field in fields)
        {
            if (!columns.TryGetValue(field.TargetColumnDefId, out var column))
            {
                throw new NotFoundException(
                    ErrorCodes.EntityFieldMapTargetNotFound,
                    $"Колонки {field.TargetColumnDefId} немає, або її видалено.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0405.column",
                        ["columnDefId"] = field.TargetColumnDefId.ToString(CultureInfo.InvariantCulture),
                    });
            }

            foreach (var unitId in new[] { field.SourceUnitId, field.TargetUnitId }.OfType<int>())
            {
                if (!await sources.UnitExistsAsync(unitId, ct).ConfigureAwait(false))
                {
                    throw new NotFoundException(
                        ErrorCodes.UnitNotFound,
                        $"Одиниці {unitId} немає в довіднику.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-UOM-0404.unitId",
                            ["id"] = unitId.ToString(CultureInfo.InvariantCulture),
                        });
                }
            }

            var values = field.Values ?? [];
            foreach (var value in values)
            {
                if (!entryDefs.TryGetValue(value.RegistryEntryId, out var registryDefId)
                    || column.LookupRegistryDefId != registryDefId)
                {
                    throw new NotFoundException(
                        ErrorCodes.EntityFieldMapTargetNotFound,
                        $"Запису довідника {value.RegistryEntryId} немає в довіднику колонки «{column.Code}».",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-INT-0405.registryEntry",
                            ["registryEntryId"] = value.RegistryEntryId.ToString(CultureInfo.InvariantCulture),
                            ["targetColumn"] = column.Code,
                        });
                }
            }

            result.Add((
                new SourceEventFieldSpec(
                    column,
                    field.SourceAttribute,
                    field.AttributeScope,
                    field.ValueKind,
                    field.SourceUnitId,
                    field.TargetUnitId),
                values));
        }

        return result;
    }

    /// <summary>Кладе відповідності значень у поля щойно збудованого чи замінного переліку.</summary>
    public static void AddValues(
        SourceEventMap map, IEnumerable<(SourceEventFieldSpec Spec, IReadOnlyList<SourceEventValueInput> Values)> built)
    {
        foreach (var (spec, values) in built)
        {
            if (values.Count == 0)
            {
                continue;
            }

            var field = map.Fields.First(f => f.TargetColumnDefId == spec.Target.Id);
            foreach (var value in values)
            {
                field.AddValue(value.SourceValue, value.RegistryEntryId);
            }
        }
    }
}

/// <summary>Перелік мапінгів подій і один мапінг. Право <c>Integration.View</c> або <c>Integration.Manage</c>.</summary>
/// <param name="store">Сховище мапінгів подій.</param>
/// <param name="access">Служба рішень про доступ.</param>
/// <param name="currentUser">Поточний користувач.</param>
public sealed class ListSourceEventMapsHandler(
    ISourceEventMapStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Мапінги сутності, або всі.</summary>
    /// <param name="sourceEntityId">Сутність-шаблон; <c>null</c> — усі.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<SourceEventMapDto>> ListAsync(int? sourceEntityId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAnyAsync(access, currentUser, [ListDataSourcesHandler.Permission, SaveDataSourceHandler.Permission], ct)
            .ConfigureAwait(false);

        var maps = await store.ListMapsAsync(sourceEntityId, ct).ConfigureAwait(false);

        // ⛔ S18: мапінг документа, якого користувач не бачить, для нього відсутній.
        var visible = new List<SourceEventMap>(maps.Count);
        foreach (var map in maps)
        {
            if (await SourceEventMapSupport.IsVisibleAsync(access, profile, map, ct).ConfigureAwait(false))
            {
                visible.Add(map);
            }
        }

        return [.. visible.OrderBy(m => m.Id).Select(SourceEventMapSupport.ToDto)];
    }

    /// <summary>Один мапінг; немає — 404.</summary>
    /// <param name="id">Мапінг.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<SourceEventMapDto> GetAsync(int id, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAnyAsync(access, currentUser, [ListDataSourcesHandler.Permission, SaveDataSourceHandler.Permission], ct)
            .ConfigureAwait(false);

        return SourceEventMapSupport.ToDto(
            await SourceEventMapSupport.RequireVisibleMapAsync(store, access, profile, id, ct).ConfigureAwait(false));
    }
}

/// <summary>
/// Заводить мапінг подій джерела. Право <c>Integration.Manage</c> і грант <c>Manage</c> на проєкт документа.
/// </summary>
/// <remarks>
/// ⚠ За зразком <see cref="CreateEntityFieldMapHandler"/>: право першим, існування цілей — до запису, слід у
/// журналі структурних змін (<c>ФВ-12.10</c>) — в одній транзакції із записом. Домен (<see cref="SourceEventMap"/>)
/// тримає обов'язкові <c>$start</c>/<c>$end</c>, область атрибута, вид значення й динамічність таблиці.
/// </remarks>
public sealed class CreateSourceEventMapHandler(
    ISourceEventMapStore store,
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Операція в журналі структурних змін.</summary>
    public const string AuditOperation = "CreateSourceEventMap";

    /// <summary>Заводить мапінг.</summary>
    /// <param name="command">Налаштування мапінгу.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<SourceEventMapDto> HandleAsync(CreateSourceEventMapCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        _ = await sources.FindSourceEntityAsync(command.SourceEntityId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SourceEntityNotFound,
                $"Сутності джерела {command.SourceEntityId} немає або вона вимкнена.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0404.sourceEntity",
                    ["id"] = command.SourceEntityId.ToString(CultureInfo.InvariantCulture),
                });

        // ⛔ S18: документ невидимого проєкту — та сама 404, що й неіснуючий (B-08), а не 403 з його projectId.
        var document = await store.FindDocumentAsync(command.DocumentId, ct).ConfigureAwait(false);
        if (document is null || !profile.SeesDocumentsOf(document.ProjectId))
        {
            throw Documents.DocumentVisibility.NotFound(command.DocumentId);
        }

        SourceEventMapSupport.RequireProjectManage(profile, document.ProjectId);

        var target = await store.FindTargetTableAsync(command.TableDefId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-TMPL-0404",
                $"Таблиці {command.TableDefId} немає.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.table",
                    ["tableDefId"] = command.TableDefId.ToString(CultureInfo.InvariantCulture),
                    ["versionId"] = document.TemplateVersionId.ToString(CultureInfo.InvariantCulture),
                });

        // ⛔ Таблиця чужої версії шаблону не має екземплярів у документі: синхронізація писала б у порожнечу.
        if (target.TemplateVersionId != document.TemplateVersionId)
        {
            throw new BusinessRuleException(
                "ECR-INT-0422",
                $"Таблиця {command.TableDefId} не належить версії шаблону проєкту документа {command.DocumentId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.eventMapTableNotInDocument",
                    ["tableDefId"] = command.TableDefId.ToString(CultureInfo.InvariantCulture),
                    ["documentId"] = command.DocumentId.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (await store.MapExistsAsync(command.SourceEntityId, command.DocumentId, command.TableDefId, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                ErrorCodes.EntityFieldMapStateConflict,
                "Для цієї сутності, документа й таблиці мапінг подій уже є.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.eventMapExists",
                    ["sourceEntityId"] = command.SourceEntityId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var built = await SourceEventMapSupport.BuildSpecsAsync(store, sources, command.Fields, ct).ConfigureAwait(false);

        var map = SourceEventMap.Create(
            command.SourceEntityId, command.DocumentId, target.Table, built.Select(b => b.Spec), command.VolumeMode);
        map.SetFilter(command.FilterAttribute, command.FilterScope, command.FilterValue);
        SourceEventMapSupport.AddValues(map, built);

        SourceEventMap? created = null;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            created = await store.AddMapAsync(map, innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.EventMapType, created.Id, AuditOperation,
                oldJson: null, newJson: IntegrationConfigAudit.Snapshot(SourceEventMapSupport.ToDto(created)),
                reason: $"Мапінг подій сутності {command.SourceEntityId} у документ {command.DocumentId} створено.",
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return SourceEventMapSupport.ToDto(created!);
    }
}
