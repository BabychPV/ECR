// src/Ecr.Application/Registries/RegistryAdminHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>Перелік довідників — це метадані, не дані (ФВ-8.2).</summary>
/// <remarks>
/// Записи навмисно не приєднуються: довідників десятки, а записів у
/// <c>Permit</c> — тисячі, і разом вони перетворили б відкриття конфігуратора
/// на вивантаження всієї бази довідників.
/// </remarks>
public sealed class ListRegistriesHandler(
    IRegistryStore registries, Security.IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає перелік.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<RegistryDefDto>> HandleAsync(CancellationToken ct)
    {
        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var definitions = await registries.ListDefinitionsAsync(ct).ConfigureAwait(false);

        return definitions
            .Select(d => new RegistryDefDto(
                d.Id,
                d.Code,
                d.NameL10n,

                // Ієрархічність — властивість опису: довідник ієрархічний тоді,
                // коли має поле-посилання на самого себе. Окремого прапорця в
                // схемі немає, а рахувати його по записах означало б читати
                // тисячі рядків заради одного bool у переліку метаданих.
                IsHierarchical: d.Fields.Any(f => f.RefRegistryDefId == d.Id),
                d.IsTemporal,
                d.SourceKind,
                d.Fields
                    .OrderBy(f => f.Ordinal)
                    .Select(f => new RegistryFieldDto(
                        f.Id, f.Code, f.NameL10n, f.DataType.ToString(),
                        f.IsRequired,

                        // Звужувати доступ можна за ключовими полями: саме їх
                        // бере RoleAssignment.ScopeJson. Решта для звуження
                        // недоступна — інакше область прав залежала б від
                        // необов'язкового атрибута.
                        IsScopeField: f.IsKey,
                        f.RefRegistryDefId,
                        f.UnitId))
                    .ToList()))
            .ToList();
    }
}

/// <summary>
/// Перемикання master-джерела <b>набором довідників</b> (ФВ-8.9, ФВ-13.10).
/// </summary>
/// <remarks>
/// ⛔ У відкритому періоді заборонено — <c>ECR-REG-0422</c>. Причина не
/// технічна: master визначає, чий набір записів вважається істинним, і зміна
/// посеред періоду означала б, що частина документів заповнена за одним
/// переліком дозволів, а частина — за іншим, без жодної позначки в даних.
///
/// ⛔ Операція <b>над набором</b>, а не над одним довідником, і сутності
/// «група довідників» немає навмисно. Група — це факт ОДНОГО перемикання, а
/// не властивість довідника: через рік після переходу всі довідники будуть
/// у <c>Local</c>, групи виконають свою роль і лишаться в базі довідником
/// довідників, який ніхто не оновлює. Наступний, хто заводитиме довідник,
/// побачить поле «група» і питатиме, що туди писати.
///
/// ⚠ Уся інформація, заради якої була б потрібна група, зберігається в
/// АУДИТІ: один запис із повним набором кодів і причиною. Через рік видно,
/// що саме перемикали разом — і це все, що від групи потрібно.
/// </remarks>
public sealed class SwitchRegistrySourceHandler(
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>
    /// Право на перемикання master — <b>небезпечне</b> (<c>ФВ-6.12</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Не <c>Registry.EditDefinition</c>. Перемикання master — це крок
    /// поетапного переходу (<c>ФВ-11.4</c>), а не правка визначення
    /// довідника: воно міняє, чия система вважається джерелом істини для
    /// цілого блоку даних. Право видається поіменно і в seed не має ніхто.
    /// </remarks>
    public const string Permission = "Integration.Manage";

    /// <summary>Перемикає master для всього набору однією транзакцією.</summary>
    /// <param name="registryCodes">Коди довідників; порожній набір — помилка.</param>
    /// <param name="kind">Нове джерело для всіх.</param>
    /// <param name="reason">Причина; потрапляє в аудит разом із набором.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки довідників справді змінили джерело.</returns>
    /// <exception cref="NotFoundException">Хоча б одного довідника немає — <c>ECR-REG-0404</c>.</exception>
    /// <exception cref="BusinessRuleException">Порожній набір, дублі, порожня причина або відкритий період.</exception>
    public async Task<int> HandleAsync(
        IReadOnlyList<string> registryCodes,
        RegistrySourceKind kind,
        string reason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryCodes);

        await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не змінює довідники.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        if (registryCodes.Count == 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                "Набір довідників порожній: перемикати нічого.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0422.emptySwitchSet" });
        }

        // ⚠ Дубль у наборі — не дрібниця. Він означає, що набір складали не
        // руками, а зліпили з двох переліків, і другий міг містити зайве.
        var duplicates = registryCodes
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Коди повторюються в наборі: {string.Join(", ", duplicates)}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.duplicateCodes",
                    ["codes"] = string.Join(", ", duplicates),
                });
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            // ⛔ Причина обов'язкова: через рік питання «навіщо перемикали цей
            // набір разом» — єдине, на яке треба буде відповісти, і відповідь
            // має бути в журналі, а не в чиїйсь пам'яті.
            throw new BusinessRuleException(
                "ECR-REG-0422",
                "Причина перемикання master обов'язкова.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0422.switchReasonRequired" });
        }

        // ⛔ СПЕРШУ розв'язуються ВСІ коди, і лише потім міняється хоч що
        // одне. Невідомий код у переліку означає, що набір складено помилково,
        // і перемкнути «те, що знайшлося», було б гірше за відмову: половина
        // блоку опинилася б в одному режимі, половина в іншому, і ніхто б не
        // знав, де межа.
        //
        // ⛔ Рядок `RD-06`: розв'язуються вони ОДНИМ запитом, а не
        // `FindDefinitionAsync` у циклі. Набір — це блок довідників (десятки),
        // і по запиту на код означало десятки звернень заради операції, яка
        // далі робить рівно одне збереження.
        //
        // ⚠ `AsNoTracking` тут НЕ додається, і це не недогляд: нижче
        // `definition.SwitchSource(kind)` і `SaveChangesAsync`. Невідстежувані
        // сутності зробили б перемикання порожньою операцією — причому
        // МОВЧКИ: `changed.Count` рахує доменні об'єкти в пам'яті й лишився б
        // тим самим, аудит записався б, а таблиця не змінилася б.
        var found = await registries.FindDefinitionsAsync(registryCodes, ct).ConfigureAwait(false);

        var byCode = new Dictionary<string, RegistryDef>(
            found.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in found)
        {
            byCode.TryAdd(definition.Code, definition);
        }

        var definitions = new List<RegistryDef>(registryCodes.Count);

        foreach (var code in registryCodes)
        {
            definitions.Add(
                byCode.TryGetValue(code, out var definition)
                    ? definition
                    : throw new NotFoundException(
                        "ECR-REG-0404",
                        $"Довідника «{code}» не існує.",
                        new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = code }));
        }

        // ⚠ Питання ставиться ОДИН раз на весь набір і ГЛОБАЛЬНО, а не по
        // проєкту: довідник один на всі проєкти, і перемикання «у закритому
        // проєкті» змінило б перелік записів у сусідньому, відкритому.
        if (await registries.HasOpenPeriodAsync(ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                "Перемикання джерела заборонене, доки є відкриті періоди: частина документів "
                + "заповнилася б за одним переліком записів, частина — за іншим.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.openPeriod",
                    ["registryCodes"] = string.Join(",", registryCodes),
                    ["to"] = kind.ToString(),
                });
        }

        var changed = new List<object>(definitions.Count);

        foreach (var definition in definitions)
        {
            if (definition.SourceKind == kind)
            {
                // Той самий стан — не помилка, але й не подія. У наборі такі
                // трапляються постійно: перемикають блок, частина вже там.
                continue;
            }

            changed.Add(new { code = definition.Code, from = definition.SourceKind.ToString() });
            definition.SwitchSource(kind);

            // ⚠ Ревізія даних НЕ рухається: змінився власник довідника, а не
            // його вміст. Рухати її означало б інвалідувати кеш там, де нічого
            // не змінилося, і привчити клієнта ігнорувати ревізію.
        }

        if (changed.Count == 0)
        {
            return 0;
        }

        // ⛔ Q-244: аудит і фінальне збереження — одна транзакція
        // (`ExecuteInTransactionAsync`), а не просто «одне збереження»:
        // коментар класу вище стверджував «однією транзакцією» до цієї
        // правки, хоч аудит писався сирим SQL БЕЗ жодної відкритої
        // транзакції, а `SaveChangesAsync` комітив окремо.
        //
        // ⚠ ОДИН запис аудиту на весь набір, а не по запису на довідник.
        // Три записи поруч у журналі не відрізняються від трьох випадкових
        // перемикань, зроблених того ж дня, — а саме зв'язок між ними і є тим
        // єдиним, заради чого була б потрібна сутність «група».
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "cfg.RegistryDef",

                    // Набір не має одного ідентифікатора; коди — у JSON нижче.
                    EntityId: 0,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: "SwitchSourceSet",
                    OldJson: JsonSerializer.Serialize(new { registries = changed }),
                    NewJson: JsonSerializer.Serialize(
                        new { sourceKind = kind.ToString(), registryCodes }),
                    ChangeReason: reason,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            // ⛔ Одне збереження на весь набір: або перемкнулися всі, або жоден.
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return changed.Count;
    }
}

/// <summary>
/// Видалення запису довідника (ФВ-8.6).
/// </summary>
/// <remarks>
/// ⛔ Запис, на який посилаються дані, не видаляється взагалі —
/// <c>ECR-REG-0409</c>. Правильний спосіб вивести його з обігу — закрити датою
/// через <see cref="SetEntryValidityHandler"/>: історія лишається читабельною,
/// а в нових періодах запис не пропонується.
/// <para>
/// Сам заборон тримає зовнішній ключ <c>doc.CellValue.ValueRegistryEntryId</c>
/// (ФВ-8.7). Перевірка тут існує, щоб віддати зрозумілий код замість помилки
/// провайдера — а не замість неї.
/// </para>
/// </remarks>
public sealed class DeleteRegistryEntryHandler(
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну даних довідника (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Логічно видаляє запис, якщо на нього ніхто не посилається.</summary>
    /// <param name="registryCode">Довідник зі шляху запиту.</param>
    /// <param name="registryEntryId">Запис.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">
    /// Запису немає — або він належить ІНШОМУ довіднику, ніж названий у шляху.
    /// </exception>
    /// <exception cref="BusinessRuleException">На запис посилаються дані — <c>ECR-REG-0409</c>.</exception>
    /// <remarks>
    /// ⚠ <paramref name="registryCode"/> не декоративний. Ідентифікатор запису
    /// наскрізний по всіх довідниках, тож без цієї звірки
    /// <c>DELETE /registries/FuelTypes/entries/{id запису EmissionSources}</c>
    /// мовчки видалив би чужий запис — «бо id збігся». Відповідь на таке —
    /// <c>404</c>, а не видалення і не <c>400</c>: для того, хто питає, запису
    /// в ЦЬОМУ довіднику справді не існує.
    /// </remarks>
    public async Task HandleAsync(string registryCode, long registryEntryId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);

        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не змінює довідники.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var entry = await registries.FindEntryAsync(registryEntryId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Запису довідника {registryEntryId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryEntry",
                    ["entryId"] = registryEntryId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // ⚠ Опис читається ДО перевірки посилань і до видалення — він потрібен
        // двічі: спершу щоб звірити належність довіднику, потім щоб підняти
        // ревізію даних. Другого читання нижче немає навмисно.
        var definition = await registries.FindDefinitionByIdAsync(entry.RegistryDefId, ct).ConfigureAwait(false);
        if (definition is null
            || !string.Equals(definition.Code, registryCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotFoundException(
                "ECR-REG-0404",
                $"Запису {registryEntryId} у довіднику «{registryCode}» не існує.",

                // ⚠ Ключ той самий, що й у решти «запису не існує»: для того,
                // хто питає, факт один — записа з таким Id тут немає. Заводити
                // окремий рядок каталогу заради того, що довідник у шляху
                // чужий, означало б розповісти про внутрішній устрій замість
                // відповіді (`ФВ-14.9a`).
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryEntry",
                    ["entryId"] = registryEntryId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["registryCode"] = registryCode,
                });
        }

        var references = await registries.CountReferencesAsync(registryEntryId, ct).ConfigureAwait(false);
        if (references > 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0409",
                $"Запис «{entry.Code}» не видаляється: на нього посилаються комірок — {references}. "
                + "Закрийте його датою — історія лишиться читабельною, а в нових періодах він не пропонуватиметься.",
                new Dictionary<string, object?>
                {
                    // Сирі числа лишаються для клієнта; резолвер підставляє лише рядки.
                    ["messageKey"] = "err.ECR-REG-0409.entryReferenced",
                    ["code"] = entry.Code,
                    ["referenceCount"] = references.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["registryEntryId"] = registryEntryId,
                    ["references"] = references,
                });
        }

        entry.SoftDelete(userId, clock.UtcNow);

        // ⚠ Той самий `definition`, що вже прочитаний вище для звірки коду.
        // Повторне читання тут було б другим запитом за тим самим рядком — і,
        // що гірше, другою правдою про те, який саме довідник змінюється.
        definition.BumpDataRevision();

        // ⛔ Слід у журналі структурних змін — як у сусідньої дії над тим самим
        // записом (`SetEntryValidityHandler`, `Operation = "SetValidity"`). Без
        // нього видалення лишало по собі лише прапорці `IsDeleted`/`DeletedAt`
        // на самому рядку: побачити «хто прибрав запис, на який учора ще
        // посилалися» можна було тільки в самому довіднику, і тільки доти, доки
        // його не видалять удруге. Журнал довідника (`GET …/{code}/history`)
        // при цьому мовчав, хоч відповідає саме на таке питання.
        //
        // ⚠ Аудит і збереження — ОДНІЄЮ транзакцією (`Q-244`): інакше збій між
        // ними лишає журнал і довідник у різних станах.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "dic.RegistryEntry",
                    EntityId: checked((int)registryEntryId),
                    ChangeClass: ChangeClass.Breaking,
                    Operation: "Delete",
                    OldJson: JsonSerializer.Serialize(
                        new { registry = definition.Code, code = entry.Code }),
                    NewJson: null,
                    ChangeReason: $"Записів довідника «{definition.Code}» прибрано: 1.",
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
