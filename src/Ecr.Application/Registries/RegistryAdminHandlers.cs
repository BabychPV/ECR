// src/Ecr.Application/Registries/RegistryAdminHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>Перелік довідників — це метадані, не дані (ФВ-8.2).</summary>
/// <remarks>
/// Записи навмисно не приєднуються: довідників десятки, а записів у
/// <c>Permit</c> — тисячі, і разом вони перетворили б відкриття конфігуратора
/// на вивантаження всієї бази довідників.
/// </remarks>
public sealed class ListRegistriesHandler(IRegistryStore registries)
{
    /// <summary>Читає перелік.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<RegistryDefDto>> HandleAsync(CancellationToken ct)
    {
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
/// Перемикання master-джерела довідника (ФВ-8.9).
/// </summary>
/// <remarks>
/// ⛔ У відкритому періоді заборонено — <c>ECR-REG-0422</c>. Причина не
/// технічна: master визначає, чий набір записів вважається істинним, і зміна
/// посеред періоду означала б, що частина документів заповнена за одним
/// переліком дозволів, а частина — за іншим, без жодної позначки в даних.
/// </remarks>
public sealed class SwitchRegistrySourceHandler(
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Перемикає master.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="kind">Нове джерело.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає.</exception>
    /// <exception cref="BusinessRuleException">Є відкритий період — <c>ECR-REG-0422</c>.</exception>
    public async Task HandleAsync(string registryCode, RegistrySourceKind kind, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не змінює довідники.");

        var definition = await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-REG-0404", $"Довідника «{registryCode}» не існує.");

        if (definition.SourceKind == kind)
        {
            // Перемикання в той самий стан — не помилка, але й не подія:
            // писати аудит тут означало б засмічувати журнал змінами, яких не було.
            return;
        }

        // ⚠ Питання ставиться глобально, а не по проєкту: довідник один на всі
        // проєкти, і перемикання «у закритому проєкті» змінило б перелік
        // записів у сусідньому, відкритому.
        if (await registries.HasOpenPeriodAsync(ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Перемикання джерела довідника «{registryCode}» заборонене, доки є відкриті періоди: "
                + "частина документів заповнилася б за одним переліком записів, частина — за іншим.",
                new Dictionary<string, object?>
                {
                    ["registryCode"] = registryCode,
                    ["from"] = definition.SourceKind.ToString(),
                    ["to"] = kind.ToString(),
                });
        }

        var previous = definition.SourceKind;
        definition.SwitchSource(kind);

        // ⚠ Ревізія даних тут НЕ рухається: змінився власник довідника, а не
        // його вміст. Рухати її означало б інвалідувати кеш там, де нічого не
        // змінилося, і привчити клієнта ігнорувати ревізію.

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                ChangedAt: clock.UtcNow,
                TemplateVersionId: 0,
                EntityType: "cfg.RegistryDef",
                EntityId: definition.Id,
                ChangeClass: ChangeClass.Guarded,
                Operation: "SwitchSource",
                OldJson: $"{{\"sourceKind\":\"{previous}\"}}",
                NewJson: $"{{\"sourceKind\":\"{kind}\"}}",
                ChangeReason: "Перемикання master-джерела (ФВ-8.9); далі — період подвійної звірки (D-49).",
                ChangedByUserId: userId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
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
    IRegistryStore registries, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    /// <summary>Логічно видаляє запис, якщо на нього ніхто не посилається.</summary>
    /// <param name="registryEntryId">Запис.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Запису немає.</exception>
    /// <exception cref="BusinessRuleException">На запис посилаються дані — <c>ECR-REG-0409</c>.</exception>
    public async Task HandleAsync(long registryEntryId, CancellationToken ct)
    {
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
