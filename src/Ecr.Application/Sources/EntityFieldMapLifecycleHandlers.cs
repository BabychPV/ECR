// src/Ecr.Application/Sources/EntityFieldMapLifecycleHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Errors;

namespace Ecr.Application.Sources;

// Дії над наявним мапінгом (директива №15, BE-27): пауза / відновлення,
// приймання зміни одиниці джерела, видалення.
//
// ⛔ До цього файла мапінг можна було лише ЗАВЕСТИ (`CreateEntityFieldMapHandler`)
// і подивитися (`PreviewMappingHandler`). Тобто помилково налаштований мапінг
// лишався в зборі назавжди: єдиним способом його спинити був ручний SQL.
//
// ⚠ Нового права не заводиться: усі три дії — це керування інтеграцією, а не
// окрема влада (`Integration.Manage`, те саме, що й заведення). Право, видане
// під одну кнопку, довелося б окремо роздати всім, хто вже веде мапінги
// (`BE-28` — саме про ціну зайвих прав).
//
// ⚠ Обробники стоять у файлі, де сусіди вже вживають
// `ListTemplatesHandler.RequireAsync` (`EntityFieldMapHandlers.cs`), тож форму
// перевірки права взято ту саму: другий стиль поруч гірший за старий
// (директива №15 §0.3).

/// <summary>
/// Пауза й відновлення мапінгу. Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Пауза — не видалення й не чернетка. Мапінг лишається разом з одиницями,
/// адресою рядка й способом згортання; змінюється рівно одне — збір за ним
/// перестає писати значення. Обидва шляхи збору вже фільтрують за
/// <c>IsActive</c>: <c>CollectionStore.GetFieldMapsAsync</c> вирішує, ЩО
/// читати з джерела, <c>MaterializeCollectedDataJob</c> — що класти в комірки.
/// Тому пауза діє одразу й без жодної нової умови в зборі.
/// </remarks>
public sealed class SetEntityFieldMapPausedHandler(
    ICollectionStore sources,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>Тип події журналу безпеки: мапінг призупинено.</summary>
    public const string PausedEventType = "MappingPaused";

    /// <summary>Тип події журналу безпеки: мапінг повернено у збір.</summary>
    public const string ResumedEventType = "MappingResumed";

    /// <summary>Призупиняє мапінг або повертає його у збір.</summary>
    /// <param name="fieldMapId">Мапінг.</param>
    /// <param name="paused"><c>true</c> — пауза, <c>false</c> — відновлення.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-INT-0404</c> — мапінгу немає.</exception>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0409</c> — мапінг уже в цьому стані.
    /// </exception>
    public async Task<EntityFieldMapDto> HandleAsync(int fieldMapId, bool paused, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = EntityFieldMapLifecycle.RequireUserId(currentUser);
        var map = await EntityFieldMapLifecycle.RequireMapAsync(sources, fieldMapId, ct).ConfigureAwait(false);

        // ⛔ Перехід ухвалює ДОМЕН: повторна пауза — `ECR-INT-0409`, і саме
        // суфікс `-0409` робить із нього 409, а не 422.
        if (paused)
        {
            map.Pause();
        }
        else
        {
            map.Resume();
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Журнал — ПІСЛЯ збереження: `IAuditWriter` комітить власним
        // підключенням одразу, тож запис перед збереженням лишив би доказ
        // події, якої не сталося.
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                paused ? PausedEventType : ResumedEventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new
                {
                    fieldMapId,
                    sourceEntityId = map.SourceEntityId,
                    sourceField = map.SourceField,
                }),
                userId,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return EntityFieldMapLifecycle.Map(map);
    }
}

/// <summary>
/// Приймання зміни одиниці джерела (<c>ФВ-16.9</c>). Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Зміна UOM атрибута в джерелі ЗУПИНЯЄ збір
/// (<c>SourceUnitConverter.EnsureDeclaredUnit</c> → <c>ECR-INT-0422</c>) і
/// ніколи не приймається кодом: мовчазна конверсія «як здається» дає
/// правдоподібні числа, помилку в яких знаходять через місяць на звірці —
/// коли звіт уже подано. Вихід із цього стану рівно один, і він тут: людина
/// каже, що нова одиниця правильна.
///
/// ⚠ Тому рішення мусить лишити слід, з якого через рік читається ВСЯ подія:
/// хто, коли, який мапінг, **з якої одиниці на яку** — обидві, кодами, а не
/// самими ідентифікаторами. Довідник одиниць живий: рядок, на який указує
/// числовий `unitId`, може вже не існувати, коли журнал почнуть читати.
/// </remarks>
public sealed class AcceptSourceUnitChangeHandler(
    ICollectionStore sources,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>Тип події журналу безпеки: прийнято зміну одиниці джерела.</summary>
    public const string AcceptedEventType = "MappingSourceUnitChangeAccepted";

    /// <summary>Записує рішення людини про нову одиницю джерела.</summary>
    /// <param name="fieldMapId">Мапінг.</param>
    /// <param name="requestedSourceUnitId">
    /// Одиниця, яку джерело віддає тепер; <c>null</c> — та, що помітив збір.
    /// </param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">
    /// <c>ECR-INT-0404</c> — мапінгу немає; <c>ECR-UOM-0404</c> — одиниці немає в довіднику.
    /// </exception>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0409</c> — одиниця не оголошена, вже та сама або рішення не чекається;
    /// <c>ECR-INT-0422</c> — помічену одиницю спершу треба завести в довідник.
    /// </exception>
    /// <remarks>
    /// Прийняття зміни, яку помітив збір, ще й знімає паузу — макет обіцяє
    /// «Collection resumed» одразу після рішення.
    /// </remarks>
    public async Task<EntityFieldMapDto> HandleAsync(
        int fieldMapId, int? requestedSourceUnitId, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = EntityFieldMapLifecycle.RequireUserId(currentUser);
        var map = await EntityFieldMapLifecycle.RequireMapAsync(sources, fieldMapId, ct).ConfigureAwait(false);

        // ⚠ Існування одиниці перевіряється ДО зміни — тим самим порядком, що
        // в `CreateEntityFieldMapHandler.ApplyUnitsAsync`: мапінг, який
        // оголошує неіснуючу одиницю, зупинив би збір знову, тепер уже на
        // конверсії.
        if (requestedSourceUnitId is { } requested
            && !await sources.UnitExistsAsync(requested, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                ErrorCodes.UnitNotFound,
                $"Одиниці {requested} немає в довіднику.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-UOM-0404.unitId",
                    ["id"] = requested.ToString(CultureInfo.InvariantCulture),
                });
        }

        // Одиницю з позначки могли прибрати з довідника до рішення — це той
        // самий випадок «її спершу треба завести», що й відсутній id.
        if (requestedSourceUnitId is null
            && map.PendingSourceUnitId is { } pending
            && !await sources.UnitExistsAsync(pending, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceUnitChanged,
                $"Одиниці «{map.PendingSourceUnitCode}» немає в довіднику: спершу заведіть її.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.pendingUnitNotInCatalog",
                    ["sourceField"] = map.SourceField,
                    ["unitCode"] = map.PendingSourceUnitCode,
                });
        }

        var previousUnitId = map.AcceptSourceUnitChange(requestedSourceUnitId);
        var newSourceUnitId = map.SourceUnitId!.Value;

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // Коди читаються ПІСЛЯ збереження й обидва: журнал має відповісти на
        // «з якої на яку» без другого запиту в довідник, якого на той момент
        // може вже не бути.
        var fromCode = await sources.FindUnitCodeAsync(previousUnitId, ct).ConfigureAwait(false);
        var toCode = await sources.FindUnitCodeAsync(newSourceUnitId, ct).ConfigureAwait(false);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                AcceptedEventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new
                {
                    fieldMapId,
                    sourceEntityId = map.SourceEntityId,
                    sourceField = map.SourceField,
                    fromUnitId = previousUnitId,
                    fromUnitCode = fromCode,
                    toUnitId = newSourceUnitId,
                    toUnitCode = toCode,
                }),
                userId,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return EntityFieldMapLifecycle.Map(map);
    }
}

/// <summary>
/// Видалення мапінгу з перевіркою наслідків. Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ <b>Мапінг, за яким уже зібрано дані, не видаляється</b> —
/// <c>409 ECR-INT-0409</c> з лічильником точок і межами вікна в
/// <c>details</c>. Це той самий клас рішення, що й «на запис довідника
/// посилаються N комірок» (<c>ECR-REG-0409</c>, <c>BE-01</c>): точки в
/// <c>ext.RawDataPoint</c> пояснює саме мапінг — він каже, у якій одиниці
/// число, у який рядок і як згорталося. Стерти його означало б лишити дані
/// без пояснення, причому дані, які вже могли потрапити в поданий звіт.
///
/// ⚠ Вихід — <b>пауза</b>, і вона є саме для цього: мапінг лишається як
/// пояснення зібраного, а збір за ним більше нічого не пише. Тому відмова тут
/// не глухий кут: клієнт у відповідь пропонує паузу, а не повтор запиту.
///
/// ⚠ Порожній мапінг видаляється фізично, без сліду в самій таблиці: слід —
/// у журналі безпеки разом із тим, що саме зникло.
/// </remarks>
public sealed class DeleteEntityFieldMapHandler(
    ICollectionStore sources,
    IAccessDecisionService access,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>Тип події журналу безпеки: мапінг видалено.</summary>
    public const string DeletedEventType = "MappingDeleted";

    /// <summary>Видаляє мапінг, за яким нічого не зібрано.</summary>
    /// <param name="fieldMapId">Мапінг.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-INT-0404</c> — мапінгу немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-INT-0409</c> — за мапінгом уже зібрано дані; треба пауза.
    /// </exception>
    public async Task HandleAsync(int fieldMapId, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = EntityFieldMapLifecycle.RequireUserId(currentUser);
        var map = await EntityFieldMapLifecycle.RequireMapAsync(sources, fieldMapId, ct).ConfigureAwait(false);

        var collected = await sources
            .CountCollectedAsync(map.SourceEntityId, map.SourceField, ct)
            .ConfigureAwait(false);

        if (collected.Points > 0)
        {
            // ⚠ Числа йдуть У ВІДПОВІДЬ, а не лише в лог: людина ухвалює
            // рішення «пауза замість видалення» саме за ними, і «щось уже
            // зібрано» без «скільки і за коли» — це відмова без підстави.
            throw new BusinessRuleException(
                ErrorCodes.EntityFieldMapStateConflict,
                $"За мапінгом поля «{map.SourceField}» уже зібрано {collected.Points} точок: "
                + "видалення лишило б ці дані без пояснення. Призупиніть мапінг.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.mappingHasCollectedData",
                    ["sourceField"] = map.SourceField,
                    ["collectedPoints"] = collected.Points,
                    ["firstPointAt"] = collected.FirstAt,
                    ["lastPointAt"] = collected.LastAt,
                });
        }

        var sourceEntityId = map.SourceEntityId;
        var sourceField = map.SourceField;

        await sources.RemoveFieldMapAsync(map, ct).ConfigureAwait(false);

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                DeletedEventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new { fieldMapId, sourceEntityId, sourceField }),
                userId,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);
    }
}

/// <summary>Спільні кроки трьох дій над мапінгом (<c>BE-27</c>).</summary>
internal static class EntityFieldMapLifecycle
{
    /// <summary>Мапінг або відмова «такого немає».</summary>
    /// <remarks>
    /// ⚠ Код НЕ новий: <c>ECR-INT-0404</c> уже означає «того, на що посилається
    /// запит до інтеграції, немає», і нового заводити нема за що — цифри в коді
    /// означають НАШ статус відповіді, а він тут той самий 404. Відрізняє
    /// випадки <c>messageKey</c>: <c>sourceEntity</c> проти <c>fieldMap</c>.
    /// </remarks>
    public static async Task<EntityFieldMap> RequireMapAsync(
        ICollectionStore sources, int fieldMapId, CancellationToken ct)
        => await sources.FindFieldMapAsync(fieldMapId, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               ErrorCodes.SourceEntityNotFound,
               $"Мапінгу {fieldMapId} немає.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-INT-0404.fieldMap",
                   ["fieldMapId"] = fieldMapId.ToString(CultureInfo.InvariantCulture),
               });

    /// <summary>Автор події журналу; анонім сюди не доходить, але кидок лишається.</summary>
    public static int RequireUserId(ICurrentUser currentUser)
        => currentUser.UserId
           ?? throw new AccessDeniedException(
               ErrorCodes.Unauthorized,
               "Потрібна автентифікація.",
               new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

    /// <summary>DTO мапінгу для відповіді — та сама форма, що й на створенні.</summary>
    public static EntityFieldMapDto Map(EntityFieldMap map) => new(
        map.Id,
        map.SourceEntityId,
        map.SourceField,
        map.TargetKind,
        map.TargetColumnDefId,
        map.TargetRegistryFieldDefId,
        map.SourceUnitId,
        map.TargetUnitId,
        map.TargetRowKey,
        map.Aggregation,
        map.IsActive,
        PendingSourceUnitChange.From(
            map.PendingSourceUnitCode, map.PendingSourceUnitId, map.PendingSourceUnitDetectedAt));
}
