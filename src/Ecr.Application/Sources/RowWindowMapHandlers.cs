// src/Ecr.Application/Sources/RowWindowMapHandlers.cs
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

/// <summary>Джерело прив'язки «значення селектора → атрибут» у відповіді.</summary>
/// <param name="Id">Ідентифікатор <c>ext.RowWindowSource</c>.</param>
/// <param name="SelectorValue">Значення колонки-селектора; <c>null</c> — для всіх рядків.</param>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="SourceField">Шлях атрибута в джерелі.</param>
/// <param name="SourceUnitId">Одиниця, у якій віддає значення джерело.</param>
public sealed record RowWindowSourceDto(int Id, string? SelectorValue, int SourceEntityId, string SourceField, int SourceUnitId);

/// <summary>Прив'язка «атрибут PI → колонка, вікно = рядок» у відповіді (HSE301 §4.4).</summary>
/// <param name="Id">Ідентифікатор <c>ext.RowWindowMap</c>.</param>
/// <param name="TableDefId">Таблиця прив'язки.</param>
/// <param name="TargetColumnDefId">Колонка-ціль (<c>Decimal</c>).</param>
/// <param name="TargetColumnCode">Код колонки-цілі.</param>
/// <param name="StartColumnDefId">Колонка початку вікна (<c>Date</c>).</param>
/// <param name="StartColumnCode">Код колонки початку.</param>
/// <param name="EndColumnDefId">Колонка кінця вікна (<c>Date</c>).</param>
/// <param name="EndColumnCode">Код колонки кінця.</param>
/// <param name="SelectorColumnDefId">Колонка-селектор; <c>null</c> — один атрибут на всі рядки.</param>
/// <param name="SelectorColumnCode">Код колонки-селектора.</param>
/// <param name="Summary">Спосіб згортки.</param>
/// <param name="IsStep">Ряд ступінчастий.</param>
/// <param name="MaxGapSeconds">Поріг прогалини, секунди; <c>null</c> — порога немає.</param>
/// <param name="TargetUnitId">Одиниця, у якій значення лягає в колонку.</param>
/// <param name="MinPercentGood">Покриття 0–100, нижче якого — <c>Partial</c>.</param>
/// <param name="RefetchWithinDays">Діб повтору за пізніми даними.</param>
/// <param name="IsActive">Чи діє прив'язка (пауза — <c>false</c>).</param>
/// <param name="RowVersion">Версія для оптимістичного блокування (hex).</param>
/// <param name="Sources">Джерела.</param>
public sealed record RowWindowMapDto(
    int Id,
    int TableDefId,
    int TargetColumnDefId,
    string TargetColumnCode,
    int StartColumnDefId,
    string StartColumnCode,
    int EndColumnDefId,
    string EndColumnCode,
    int? SelectorColumnDefId,
    string? SelectorColumnCode,
    RowWindowSummaryKind Summary,
    bool IsStep,
    int? MaxGapSeconds,
    int TargetUnitId,
    decimal MinPercentGood,
    int RefetchWithinDays,
    bool IsActive,
    string RowVersion,
    IReadOnlyList<RowWindowSourceDto> Sources);

/// <summary>Джерело у запиті на збереження прив'язки.</summary>
/// <param name="SelectorValue">Значення колонки-селектора; <c>null</c> чи порожнє — для всіх рядків.</param>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="SourceField">Шлях атрибута в джерелі.</param>
/// <param name="SourceUnitId">Одиниця джерела.</param>
public sealed record RowWindowSourceInput(string? SelectorValue, int SourceEntityId, string SourceField, int SourceUnitId);

/// <summary>Створення прив'язки.</summary>
/// <param name="TableDefId">Таблиця прив'язки; збігається з таблицею колонки-цілі.</param>
/// <param name="TargetColumnDefId">Колонка-ціль (<c>Decimal</c>); одна прив'язка на колонку.</param>
/// <param name="StartColumnDefId">Колонка початку вікна (<c>Date</c>).</param>
/// <param name="EndColumnDefId">Колонка кінця вікна (<c>Date</c>).</param>
/// <param name="SelectorColumnDefId">Колонка-селектор; <c>null</c> — один атрибут на всі рядки.</param>
/// <param name="Summary">Спосіб згортки.</param>
/// <param name="IsStep">Ряд ступінчастий.</param>
/// <param name="TargetUnitId">Одиниця колонки-цілі.</param>
/// <param name="MinPercentGood">Покриття 0–100; <c>null</c> — типове 95.</param>
/// <param name="RefetchWithinDays">Діб повтору 0–366; <c>null</c> — типове 7.</param>
/// <param name="MaxGapSeconds">Поріг прогалини, секунди; <c>null</c> — порога немає.</param>
/// <param name="Sources">Джерела; порожньо — прив'язка без джерел (рядки матимуть <c>NotApplicable</c>).</param>
public sealed record CreateRowWindowMapCommand(
    int TableDefId,
    int TargetColumnDefId,
    int StartColumnDefId,
    int EndColumnDefId,
    int? SelectorColumnDefId,
    RowWindowSummaryKind Summary,
    bool IsStep,
    int TargetUnitId,
    decimal? MinPercentGood,
    int? RefetchWithinDays,
    int? MaxGapSeconds,
    IReadOnlyList<RowWindowSourceInput>? Sources);

/// <summary>
/// Повна заміна налаштувань прив'язки; таблиця й колонка-ціль — її ключ і не змінюються.
/// </summary>
/// <param name="StartColumnDefId">Колонка початку вікна.</param>
/// <param name="EndColumnDefId">Колонка кінця вікна.</param>
/// <param name="SelectorColumnDefId">Колонка-селектор.</param>
/// <param name="Summary">Спосіб згортки.</param>
/// <param name="IsStep">Ряд ступінчастий.</param>
/// <param name="TargetUnitId">Одиниця колонки-цілі.</param>
/// <param name="MinPercentGood">Покриття 0–100; <c>null</c> — типове 95.</param>
/// <param name="RefetchWithinDays">Діб повтору 0–366; <c>null</c> — типове 7.</param>
/// <param name="MaxGapSeconds">Поріг прогалини, секунди.</param>
/// <param name="IsActive">Чи діє прив'язка (пауза — <c>false</c>).</param>
/// <param name="RowVersion">Версія, яку бачив клієнт; інша — <c>409</c>; <c>null</c> — без перевірки.</param>
/// <param name="Sources">Нові джерела замість усіх наявних.</param>
public sealed record UpdateRowWindowMapCommand(
    int StartColumnDefId,
    int EndColumnDefId,
    int? SelectorColumnDefId,
    RowWindowSummaryKind Summary,
    bool IsStep,
    int TargetUnitId,
    decimal? MinPercentGood,
    int? RefetchWithinDays,
    int? MaxGapSeconds,
    bool IsActive,
    string? RowVersion,
    IReadOnlyList<RowWindowSourceInput>? Sources);

/// <summary>Спільні кроки обробників прив'язок вікна рядка (HSE301 A1).</summary>
internal static class RowWindowMapSupport
{
    /// <summary>DTO прив'язки; коди колонок беруться зі <paramref name="columns"/>.</summary>
    public static RowWindowMapDto ToDto(RowWindowMap map, IReadOnlyDictionary<int, ColumnDef> columns)
    {
        string CodeOf(int id) => columns.TryGetValue(id, out var column) ? column.Code.ToString() : $"#{id}";

        return new(
            map.Id,
            map.TableDefId,
            map.TargetColumnDefId,
            CodeOf(map.TargetColumnDefId),
            map.StartColumnDefId,
            CodeOf(map.StartColumnDefId),
            map.EndColumnDefId,
            CodeOf(map.EndColumnDefId),
            map.SelectorColumnDefId,
            map.SelectorColumnDefId is { } selector ? CodeOf(selector) : null,
            map.Summary,
            map.IsStep,
            map.MaxGapSeconds,
            map.TargetUnitId,
            map.MinPercentGood,
            map.RefetchWithinDays,
            map.IsActive,
            Convert.ToHexString(map.RowVersion),
            [.. map.Sources.OrderBy(s => s.Id).Select(s => new RowWindowSourceDto(
                s.Id, s.SelectorValue, s.SourceEntityId, s.SourceField, s.SourceUnitId))]);
    }

    /// <summary>Ідентифікатори колонок прив'язки.</summary>
    public static IEnumerable<int> ColumnIds(RowWindowMap map)
        => new[] { map.TargetColumnDefId, map.StartColumnDefId, map.EndColumnDefId, map.SelectorColumnDefId ?? 0 }
            .Where(id => id != 0);

    /// <summary>DTO однієї прив'язки з кодами колонок (один запит).</summary>
    public static async Task<RowWindowMapDto> ToDtoAsync(IRowWindowMapStore store, RowWindowMap map, CancellationToken ct)
        => ToDto(map, await store.FindColumnsAsync([.. ColumnIds(map)], ct).ConfigureAwait(false));

    /// <summary>Прив'язка або 404.</summary>
    public static async Task<RowWindowMap> RequireMapAsync(IRowWindowMapStore store, int id, CancellationToken ct)
        => await store.FindMapAsync(id, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               ErrorCodes.SourceEntityNotFound,
               $"Прив'язки вікна рядка {id} немає.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-INT-0404.rowWindowMap",
                   ["rowWindowMapId"] = id.ToString(CultureInfo.InvariantCulture),
               });

    /// <summary>
    /// Прив'язка пише в колонку документів КОЖНОГО проєкту, що її використовує: на кожен потрібен грант
    /// <c>Manage</c> (S3) — так само, як для мапінгу поля на колонку.
    /// </summary>
    public static async Task RequireProjectGrantsAsync(
        ICollectionStore sources, AccessProfile profile, int targetColumnDefId, CancellationToken ct)
    {
        var projects = await sources.FindProjectIdsUsingColumnAsync(targetColumnDefId, ct).ConfigureAwait(false);

        foreach (var projectId in projects.Order())
        {
            if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403",
                    $"Немає гранта Manage на проєкт {projectId}, у який писала б прив'язка.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.noProjectManageGrant",
                        ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                    });
            }
        }
    }

    /// <summary>Колонки за ідентифікаторами; відсутня чи видалена — 404 <c>ECR-INT-0405</c>.</summary>
    public static async Task<IReadOnlyDictionary<int, ColumnDef>> RequireColumnsAsync(
        IRowWindowMapStore store, IEnumerable<int> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        var found = await store.FindColumnsAsync(wanted, ct).ConfigureAwait(false);

        foreach (var id in wanted.Where(id => !found.ContainsKey(id)))
        {
            throw new NotFoundException(
                ErrorCodes.EntityFieldMapTargetNotFound,
                $"Колонки {id} немає, або її видалено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0405.column",
                    ["columnDefId"] = id.ToString(CultureInfo.InvariantCulture),
                });
        }

        return found;
    }

    /// <summary>Форма запиту: відома згортка, непорожні й не задовгі рядки джерел.</summary>
    public static void RequireShape(RowWindowSummaryKind summary, IReadOnlyList<RowWindowSourceInput> inputs)
    {
        if (!Enum.IsDefined(summary))
        {
            throw Invalid("err.ECR-REQ-0422.rowWindowSummaryUnknown", "Невідомий спосіб згортки вікна.");
        }

        foreach (var input in inputs)
        {
            var selector = input.SelectorValue?.Trim();
            if (string.IsNullOrWhiteSpace(input.SourceField)
                || input.SourceField.Length > RowWindowMap.MaxSourceFieldLength
                || selector?.Length > RowWindowMap.MaxSelectorValueLength)
            {
                throw Invalid(
                    "err.ECR-REQ-0422.rowWindowSourceInvalid",
                    "Шлях атрибута обов'язковий (до 200 знаків), значення селектора — до 100 знаків.");
            }
        }
    }

    /// <summary>Одиниці й сутності джерел існують.</summary>
    public static async Task RequireReferencesAsync(
        ICollectionStore sources, int targetUnitId, IReadOnlyList<RowWindowSourceInput> inputs, CancellationToken ct)
    {
        foreach (var unitId in inputs.Select(i => i.SourceUnitId).Append(targetUnitId).Distinct())
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

        foreach (var entityId in inputs.Select(i => i.SourceEntityId).Distinct())
        {
            if (await sources.FindSourceEntityAsync(entityId, ct).ConfigureAwait(false) is null)
            {
                throw new NotFoundException(
                    ErrorCodes.SourceEntityNotFound,
                    $"Сутності джерела {entityId} немає або вона вимкнена.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0404.sourceEntity",
                        ["id"] = entityId.ToString(CultureInfo.InvariantCulture),
                    });
            }
        }
    }

    /// <summary>Пороги підтягування (типові, коли не задані) і перелік джерел.</summary>
    public static void ApplyPolicyAndSources(
        RowWindowMap map,
        decimal? minPercentGood,
        int? refetchWithinDays,
        int? maxGapSeconds,
        IReadOnlyList<RowWindowSourceInput> inputs)
    {
        map.SetFetchPolicy(
            minPercentGood ?? RowWindowMap.DefaultMinPercentGood,
            refetchWithinDays ?? RowWindowMap.DefaultRefetchWithinDays,
            maxGapSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);

        foreach (var input in inputs)
        {
            map.AddSource(input.SelectorValue, input.SourceEntityId, input.SourceField, input.SourceUnitId);
        }
    }

    private static BusinessRuleException Invalid(string messageKey, string message)
        => new(ErrorCodes.RequestInvalid, message, new Dictionary<string, object?> { ["messageKey"] = messageKey });
}

/// <summary>Перелік прив'язок і одна прив'язка. Право <c>Integration.View</c> або <c>Integration.Manage</c>.</summary>
/// <param name="store">Сховище прив'язок.</param>
/// <param name="access">Служба рішень про доступ.</param>
/// <param name="currentUser">Поточний користувач.</param>
public sealed class ListRowWindowMapsHandler(
    IRowWindowMapStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Прив'язки таблиці й/або сутності джерела, або всі.</summary>
    /// <param name="tableDefId">Таблиця; <c>null</c> — усі.</param>
    /// <param name="sourceEntityId">Сутність джерела, що має джерело в прив'язці; <c>null</c> — будь-яка.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<RowWindowMapDto>> ListAsync(int? tableDefId, int? sourceEntityId, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAnyAsync(access, currentUser, [ListDataSourcesHandler.Permission, SaveDataSourceHandler.Permission], ct)
            .ConfigureAwait(false);

        var maps = await store.ListMapsAsync(tableDefId, sourceEntityId, ct).ConfigureAwait(false);
        var columns = await store
            .FindColumnsAsync([.. maps.SelectMany(RowWindowMapSupport.ColumnIds).Distinct()], ct)
            .ConfigureAwait(false);

        return [.. maps.Select(m => RowWindowMapSupport.ToDto(m, columns))];
    }

    /// <summary>Одна прив'язка; немає — 404.</summary>
    /// <param name="id">Прив'язка.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<RowWindowMapDto> GetAsync(int id, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAnyAsync(access, currentUser, [ListDataSourcesHandler.Permission, SaveDataSourceHandler.Permission], ct)
            .ConfigureAwait(false);

        var map = await RowWindowMapSupport.RequireMapAsync(store, id, ct).ConfigureAwait(false);

        return await RowWindowMapSupport.ToDtoAsync(store, map, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Заводить прив'язку вікна рядка. Право <c>Integration.Manage</c> і грант <c>Manage</c> на кожен проєкт, що
/// використовує колонку-ціль.
/// </summary>
/// <remarks>
/// ⚠ За зразком <see cref="CreateEntityFieldMapHandler"/>: право першим, існування цілей — до запису, слід у журналі
/// структурних змін (<c>ФВ-12.10</c>) — в одній транзакції із записом. Типи колонок тримає домен
/// (<see cref="RowWindowMap.Create"/>).
/// </remarks>
public sealed class CreateRowWindowMapHandler(
    IRowWindowMapStore store,
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Операція в журналі структурних змін.</summary>
    public const string AuditOperation = "CreateRowWindowMap";

    /// <summary>Заводить прив'язку.</summary>
    /// <param name="command">Налаштування.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<RowWindowMapDto> HandleAsync(CreateRowWindowMapCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        var inputs = command.Sources ?? [];
        RowWindowMapSupport.RequireShape(command.Summary, inputs);

        var columns = await RowWindowMapSupport
            .RequireColumnsAsync(
                store,
                new[] { command.TargetColumnDefId, command.StartColumnDefId, command.EndColumnDefId, command.SelectorColumnDefId ?? 0 }
                    .Where(id => id != 0),
                ct)
            .ConfigureAwait(false);

        var target = columns[command.TargetColumnDefId];
        if (target.TableDefId != command.TableDefId)
        {
            throw new BusinessRuleException(
                "ECR-INT-0422",
                $"Колонка-ціль {command.TargetColumnDefId} не належить таблиці {command.TableDefId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.rowWindowTargetNotInTable",
                    ["tableDefId"] = command.TableDefId.ToString(CultureInfo.InvariantCulture),
                    ["targetColumn"] = target.Code.ToString(),
                });
        }

        await RowWindowMapSupport.RequireProjectGrantsAsync(sources, profile, target.Id, ct).ConfigureAwait(false);
        await RowWindowMapSupport.RequireReferencesAsync(sources, command.TargetUnitId, inputs, ct).ConfigureAwait(false);

        if (await store.TargetTakenAsync(target.TableDefId, target.Id, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                ErrorCodes.EntityFieldMapStateConflict,
                $"На колонку «{target.Code}» уже є прив'язка вікна рядка.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.rowWindowTargetTaken",
                    ["targetColumn"] = target.Code.ToString(),
                });
        }

        var map = RowWindowMap.Create(
            target,
            columns[command.StartColumnDefId],
            columns[command.EndColumnDefId],
            command.SelectorColumnDefId is { } selector ? columns[selector] : null,
            command.Summary,
            command.IsStep,
            command.TargetUnitId);
        RowWindowMapSupport.ApplyPolicyAndSources(map, command.MinPercentGood, command.RefetchWithinDays, command.MaxGapSeconds, inputs);

        RowWindowMap? created = null;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            created = await store.AddMapAsync(map, innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.RowWindowMapType, created.Id, AuditOperation,
                oldJson: null, newJson: IntegrationConfigAudit.Snapshot(RowWindowMapSupport.ToDto(created, columns)),
                reason: $"Прив'язку вікна рядка на колонку «{target.Code}» створено.",
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return RowWindowMapSupport.ToDto(created!, columns);
    }
}

/// <summary>
/// Замінює налаштування прив'язки: вікно, селектор, згортку, пороги, стан і джерела. Право <c>Integration.Manage</c>
/// і грант <c>Manage</c> на проєкти колонки-цілі.
/// </summary>
/// <remarks>
/// ⚠ Пауза й відновлення — це <c>isActive</c> у тілі. Таблиця й колонка-ціль не змінюються (ключ прив'язки, на
/// нього посилається провенанс підтягувань).
/// </remarks>
public sealed class UpdateRowWindowMapHandler(
    IRowWindowMapStore store,
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Операція в журналі структурних змін.</summary>
    public const string AuditOperation = "UpdateRowWindowMap";

    /// <summary>Замінює налаштування прив'язки.</summary>
    /// <param name="id">Прив'язка.</param>
    /// <param name="command">Нові налаштування.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<RowWindowMapDto> HandleAsync(int id, UpdateRowWindowMapCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        var map = await RowWindowMapSupport.RequireMapAsync(store, id, ct).ConfigureAwait(false);
        await RowWindowMapSupport.RequireProjectGrantsAsync(sources, profile, map.TargetColumnDefId, ct).ConfigureAwait(false);

        if (command.RowVersion is { } expected
            && !string.Equals(expected, Convert.ToHexString(map.RowVersion), StringComparison.OrdinalIgnoreCase))
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.EntityFieldMapStateConflict,
                $"Прив'язку вікна рядка {id} змінили після того, як ви її прочитали.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.rowWindowConcurrency",
                    ["rowWindowMapId"] = id.ToString(CultureInfo.InvariantCulture),
                });
        }

        var inputs = command.Sources ?? [];
        RowWindowMapSupport.RequireShape(command.Summary, inputs);

        var columns = await RowWindowMapSupport
            .RequireColumnsAsync(
                store,
                new[] { map.TargetColumnDefId, command.StartColumnDefId, command.EndColumnDefId, command.SelectorColumnDefId ?? 0 }
                    .Where(columnId => columnId != 0),
                ct)
            .ConfigureAwait(false);
        await RowWindowMapSupport.RequireReferencesAsync(sources, command.TargetUnitId, inputs, ct).ConfigureAwait(false);

        var old = IntegrationConfigAudit.Snapshot(await RowWindowMapSupport.ToDtoAsync(store, map, ct).ConfigureAwait(false));

        // Спершу перевірка домену (стан не змінюється, доки вона не пройшла), потім позначка старих джерел до
        // видалення, потім заміна: відмова не лишає відстежених джерел без власника.
        map.Reconfigure(
            columns[map.TargetColumnDefId],
            columns[command.StartColumnDefId],
            columns[command.EndColumnDefId],
            command.SelectorColumnDefId is { } selector ? columns[selector] : null,
            command.Summary,
            command.IsStep,
            command.TargetUnitId);
        store.ReleaseSources(map);
        map.ClearSources();
        RowWindowMapSupport.ApplyPolicyAndSources(map, command.MinPercentGood, command.RefetchWithinDays, command.MaxGapSeconds, inputs);
        map.SetActive(command.IsActive);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await store.SaveAsync(innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.RowWindowMapType, map.Id, AuditOperation,
                old, IntegrationConfigAudit.Snapshot(RowWindowMapSupport.ToDto(map, columns)),
                $"Прив'язку вікна рядка {map.Id} змінено.", innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return RowWindowMapSupport.ToDto(map, columns);
    }
}

/// <summary>
/// Видаляє прив'язку, за якою нічого не підтягнуто. Право <c>Integration.Manage</c> і грант <c>Manage</c> на
/// проєкти колонки-цілі.
/// </summary>
/// <remarks>
/// ⛔ Прив'язка із записами провенансу (<c>ext.RowWindowValue</c>) не видаляється — <c>409 ECR-INT-0409</c>: записи
/// пояснюють, звідки числа в колонці, і зникли б (чи завадили б ключу) разом з нею. Вихід — пауза
/// (<c>isActive = false</c>).
/// </remarks>
public sealed class DeleteRowWindowMapHandler(
    IRowWindowMapStore store,
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Операція в журналі структурних змін.</summary>
    public const string AuditOperation = "DeleteRowWindowMap";

    /// <summary>Видаляє прив'язку.</summary>
    /// <param name="id">Прив'язка.</param>
    /// <param name="ct">Скасування.</param>
    public async Task HandleAsync(int id, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        var map = await RowWindowMapSupport.RequireMapAsync(store, id, ct).ConfigureAwait(false);
        await RowWindowMapSupport.RequireProjectGrantsAsync(sources, profile, map.TargetColumnDefId, ct).ConfigureAwait(false);

        var values = await store.CountValuesAsync(id, ct).ConfigureAwait(false);
        if (values > 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.EntityFieldMapStateConflict,
                $"За прив'язкою вікна рядка {id} уже підтягнуто {values} значень: видалення лишило б числа без пояснення. Призупиніть прив'язку.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.rowWindowMapHasValues",
                    ["values"] = values,
                });
        }

        var removed = IntegrationConfigAudit.Snapshot(await RowWindowMapSupport.ToDtoAsync(store, map, ct).ConfigureAwait(false));

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await store.RemoveMapAsync(map, innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.RowWindowMapType, id, AuditOperation,
                removed, newJson: null, $"Прив'язку вікна рядка {id} видалено.", innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
