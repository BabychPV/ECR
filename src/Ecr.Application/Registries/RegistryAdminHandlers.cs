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
            ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не змінює довідники.");

        if (registryCodes.Count == 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422", "Набір довідників порожній: перемикати нічого.");
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
                $"Коди повторюються в наборі: {string.Join(", ", duplicates)}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            // ⛔ Причина обов'язкова: через рік питання «навіщо перемикали цей
            // набір разом» — єдине, на яке треба буде відповісти, і відповідь
            // має бути в журналі, а не в чиїйсь пам'яті.
            throw new BusinessRuleException(
                "ECR-REG-0422", "Причина перемикання master обов'язкова.");
        }

        // ⛔ СПЕРШУ розв'язуються ВСІ коди, і лише потім міняється хоч що
        // одне. Невідомий код у переліку означає, що набір складено помилково,
        // і перемкнути «те, що знайшлося», було б гірше за відмову: половина
        // блоку опинилася б в одному режимі, половина в іншому, і ніхто б не
        // знав, де межа.
        var definitions = new List<RegistryDef>(registryCodes.Count);

        foreach (var code in registryCodes)
        {
            definitions.Add(
                await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("ECR-REG-0404", $"Довідника «{code}» не існує."));
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

        // ⛔ ОДИН запис аудиту на весь набір, а не по запису на довідник.
        // Три записи поруч у журналі не відрізняються від трьох випадкових
        // перемикань, зроблених того ж дня, — а саме зв'язок між ними і є тим
        // єдиним, заради чого була б потрібна сутність «група».
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
            ct).ConfigureAwait(false);

        // ⛔ Одне збереження на весь набір: або перемкнулися всі, або жоден.
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

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
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну даних довідника (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Логічно видаляє запис, якщо на нього ніхто не посилається.</summary>
    /// <param name="registryEntryId">Запис.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Запису немає.</exception>
    /// <exception cref="BusinessRuleException">На запис посилаються дані — <c>ECR-REG-0409</c>.</exception>
    public async Task HandleAsync(long registryEntryId, CancellationToken ct)
    {
        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не змінює довідники.");

        var entry = await registries.FindEntryAsync(registryEntryId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-REG-0404", $"Запису довідника {registryEntryId} не існує.");

        var references = await registries.CountReferencesAsync(registryEntryId, ct).ConfigureAwait(false);
        if (references > 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0409",
                $"Запис «{entry.Code}» не видаляється: на нього посилаються комірок — {references}. "
                + "Закрийте його датою — історія лишиться читабельною, а в нових періодах він не пропонуватиметься.",
                new Dictionary<string, object?>
                {
                    ["registryEntryId"] = registryEntryId,
                    ["references"] = references,
                });
        }

        entry.SoftDelete(userId, clock.UtcNow);

        var definition = await registries.FindDefinitionByIdAsync(entry.RegistryDefId, ct).ConfigureAwait(false);
        definition?.BumpDataRevision();

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
