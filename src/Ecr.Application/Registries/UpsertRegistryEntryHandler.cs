// src/Ecr.Application/Registries/UpsertRegistryEntryHandler.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries;

/// <summary>Одна фактична зміна значення поля запису довідника — для аудиту.</summary>
/// <param name="FieldCode">Код поля довідника.</param>
/// <param name="OldValue">Значення до зміни; <c>null</c> — поле не було заповнене.</param>
/// <param name="NewValue">Значення після зміни; <c>null</c> — поле очищене.</param>
internal sealed record RegistryValueFieldChange(string FieldCode, object? OldValue, object? NewValue);

/// <summary>Створення і зміна запису довідника (ФВ-8.6, ФВ-8.7).</summary>
public sealed class UpsertRegistryEntryHandler(
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>
    /// Право на зміну ДАНИХ довідника (`02-contracts.md` §9).
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Registry.EditData</c> і <c>Registry.EditDefinition</c> — різні
    /// права: змінювати значення і змінювати склад полів довідника може не
    /// той самий користувач.
    /// </remarks>
    public const string Permission = "Registry.EditData";

    /// <summary>Тип події журналу безпеки.</summary>
    /// <remarks>
    /// ⚠ Досі зміна ЗНАЧЕННЯ поля запису довідника не лишала жодного сліду —
    /// на відміну від зміни ОПИСУ довідника (<c>SaveRegistryDefinitionHandler</c>,
    /// <c>aud.StructureChange</c>) чи зміни комірки документа
    /// (<c>aud.CellChange</c>). Подія пишеться в <c>aud.SecurityEvent</c> —
    /// той самий журнал, що вже приймає довільні події через
    /// <c>EventType</c>/<c>DetailsJson</c> (<c>DocumentKeyChanged</c>,
    /// <c>UserLocked</c> тощо) — окрема таблиця історії значень не завелася б
    /// без міграції, а її тут свідомо нема (правило проєкту: одна міграція за
    /// раз, паралельно вже йде інша).
    /// </remarks>
    public const string ValueChangedEventType = "RegistryValueChanged";

    /// <summary>Створює або оновлює запис і повертає його ідентифікатор.</summary>
    /// <param name="dto">Опис запису.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника або запису немає.</exception>
    /// <exception cref="BusinessRuleException">Код зайнятий або невалідний.</exception>
    public async Task<long> HandleAsync(RegistryEntryUpsertDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // ⚠ Глобальне право АБО ресурсний грант рівня Write на ЦЕЙ довідник
        // (A7-58): dto.RegistryDefId уже відомий з запиту, тож жодного
        // додаткового походу в базу перевірка не додає.
        await RegistryAccess
            .RequireAsync(access, currentUser, Permission, GrantLevel.Write, dto.RegistryDefId, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не змінює довідники.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var definition = await registries.FindDefinitionByIdAsync(dto.RegistryDefId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника {dto.RegistryDefId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryId",
                    ["registryDefId"] = dto.RegistryDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // Код валідується як EcrCode (D-89) — тим самим правилом, що коди
        // колонок і шаблонів. Окреме «майже таке саме» правило для довідників
        // розійшлося б із рештою системи на першому ж символі.
        var code = EcrCode.Create(dto.Code);

        var entry = dto.Id is { } id
            ? await LoadAsync(id, definition.Id, ct).ConfigureAwait(false)
            : await CreateAsync(definition.Id, code, dto, userId, ct).ConfigureAwait(false);

        entry.Rename(dto.Display);
        entry.SetParent(dto.ParentEntryId);

        var changes = await ApplyValuesAsync(registries, definition, entry, dto.Values, ct).ConfigureAwait(false);

        // ⛔ Вікно дії сюди НЕ приймається, хоча воно є полем запису: його
        // зміна тягне перерахунок IsOrphaned (ФВ-8.13a), і зроблена мимохідь
        // тут вона лишила б рядки з ознакою, яку ніхто не перерахував. Для
        // цього є SetEntryValidityHandler.

        // ⚠ Ревізія рухається ЗАВЖДИ, навіть коли змінилася лише назва: у
        // ключі кешу списків лежить саме вона, і без інкременту grid показував
        // би старий підпис, поки хтось не перезапустить процес.
        definition.BumpDataRevision();

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // Журнал — ПІСЛЯ коміту, як у ChangeDocumentKeyHandler/DeleteDocumentHandler:
        // IAuditWriter пише власним підключенням, а Id нового запису відомий
        // лише тепер, коли EF підставив згенероване значення.
        //
        // ⚠ Подія — ОДНА на весь виклик, навіть якщо змінилося кілька полів:
        // перелік змін лежить у DetailsJson. Подія не пишеться, якщо жодне
        // значення фактично не змінилося (повторне збереження тим самим
        // значенням, або запит без Values).
        if (changes.Count > 0)
        {
            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    clock.UtcNow, ValueChangedEventType, TargetUserId: null, TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        registryDefId = definition.Id,
                        entryId = entry.Id,
                        changes = changes.Select(c => new { field = c.FieldCode, oldValue = c.OldValue, newValue = c.NewValue }),
                    }),
                    userId, currentUser.CorrelationId),
                ct).ConfigureAwait(false);
        }

        return entry.Id;
    }

    /// <summary>Записує значення полів типізовано за <c>RegistryFieldDef.DataType</c>.</summary>
    /// <remarks>
    /// ⚠ Тип береться з опису поля, а не з типу переданого об'єкта. Інакше
    /// число, що прийшло рядком із JSON, лягло б у <c>ValueString</c> — і поле
    /// «ліміт» перестало б порівнюватися й сумуватися, не давши жодної помилки.
    /// <para>
    /// ⚠ <c>internal static</c>, а не приватний метод екземпляра: єдине місце,
    /// де валідується тип, обов'язковість і склад полів запису довідника, і
    /// імпорт CSV (`BE-24`, <c>ImportRegistryEntriesHandler</c>) кличе САМЕ цей
    /// метод — не копіює правило вдруге. <paramref name="registries"/>
    /// передається параметром замість поля екземпляра: метод раніше читав
    /// лише це поле, тож перетворення на static нічого не втратило.
    /// </para>
    /// <para>
    /// ⛔ Значення проходить через <see cref="CellValueReader.Normalize"/> ПЕРЕД
    /// <c>RegistryValue.Set</c> — той самий крок, який `A7-01` уже додав для
    /// комірок документа. Через HTTP <c>values</c> приходить
    /// <c>Dictionary&lt;string, object?&gt;</c>, і <c>System.Text.Json</c> кладе
    /// в кожне значення <see cref="JsonElement"/>, а не готовий
    /// <c>decimal</c>/<c>bool</c>/<c>DateTime</c>. <c>RegistryValue.Set</c>
    /// приводить значення голими <c>Convert.ToDecimal</c>/<c>ToBoolean</c>/
    /// <c>ToInt64</c> і патерн-матчем для дати — жоден не впізнає
    /// <see cref="JsonElement"/>, тож СПРАВЖНІЙ запит із коректним числом,
    /// булевим чи датою відмовляв би так само, як зіпсований ввід (виміряно
    /// тестом до фіксу: коректне число для поля <c>Int</c> давало
    /// <c>422 err.ECR-REG-0422.valueNotNumber</c>). Для <c>String</c> це
    /// «випадково працювало» — <c>JsonElement.ToString()</c> повертає текст.
    /// CSV-імпорт (<see cref="Registries.ImportRegistryEntriesHandler"/>) цей
    /// самий метод не зачіпає: він передає ГОТОВИЙ <c>string</c> (текст рядка
    /// CSV), а <see cref="CellValueReader.Normalize"/> для не-<c>JsonElement</c>
    /// входу — тотожність.
    /// </para>
    /// </remarks>
    internal static async Task<IReadOnlyList<RegistryValueFieldChange>> ApplyValuesAsync(
        IRegistryStore registries,
        Domain.Entities.Configuration.RegistryDef definition,
        RegistryEntry entry,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken ct)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var fields = definition.Fields.ToDictionary(f => f.Code, StringComparer.Ordinal);

        var unknown = values.Keys.Where(code => !fields.ContainsKey(code)).ToList();
        if (unknown.Count > 0)
        {
            // Невідоме поле — це або друкарська помилка, або клієнт іншої
            // версії. Обидва випадки треба показати: мовчки відкинуте значення
            // виглядає як збережене.
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Довідник «{definition.Code}» не має полів: {string.Join(", ", unknown)}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.unknownFields",
                    ["registryCode"] = definition.Code,
                    ["fields"] = string.Join(", ", unknown),
                });
        }

        var existing = entry.IsPersisted
            ? (await registries.ListValuesAsync(entry.Id, ct).ConfigureAwait(false))
                .ToDictionary(v => v.RegistryFieldDefId)
            : [];

        var changes = new List<RegistryValueFieldChange>();

        foreach (var (code, raw) in values)
        {
            var field = fields[code];
            var isNew = !existing.TryGetValue(field.Id, out var value);

            if (isNew)
            {
                value = new RegistryValue(entry, field.Id);
                registries.AddValue(value);
            }

            // ⚠ «Старе» читається З ЖИВОГО об'єкта ДО Set (Set заноляє всі
            // колонки — RegistryValue.Clear), «нове» — з нього ж ПІСЛЯ: так
            // порівняння бачить те саме типізоване значення, яке реально
            // ляже в базу, а не сирий вхід запиту (він може прийти рядком
            // для числового поля, і порівняння з боксованим decimal завжди
            // «відрізнялося» б).
            var oldValue = isNew ? null : RawValue(value!, field.DataType);
            value!.Set(field.DataType, CellValueReader.Normalize(raw), field.UnitId);
            var newValue = RawValue(value, field.DataType);

            if (field.DataType == CellDataType.Lookup && value.ValueRefEntryId is { } target)
            {
                await RequireLookupTargetAsync(registries, field, target, ct).ConfigureAwait(false);
            }

            if (!Equals(oldValue, newValue))
            {
                changes.Add(new RegistryValueFieldChange(code, oldValue, newValue));
            }
        }

        var missing = definition.Fields
            .Where(f => f.IsRequired)
            .Where(f => !values.TryGetValue(f.Code, out var v) || v is null)
            .Where(f => !existing.ContainsKey(f.Id))
            .Select(f => f.Code)
            .ToList();

        if (missing.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Не заповнені обов'язкові поля довідника «{definition.Code}»: {string.Join(", ", missing)}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.requiredFieldsMissing",
                    ["registryCode"] = definition.Code,
                    ["fields"] = string.Join(", ", missing),
                });
        }

        return changes;
    }

    /// <summary>
    /// Значення поля Lookup мусить бути живим записом САМЕ того довідника, який
    /// оголошує поле (V-08(b), V-17(a), третій раунд UX).
    /// </summary>
    /// <remarks>
    /// ⛔ Доти неіснуючий Id доходив до бази й падав на <c>FK_RegValue_Ref</c> —
    /// <c>500</c> «зверніться до адміністратора» на звичайну описку в полі, — а
    /// Id запису ІНШОГО довідника приймався (<c>201</c>): число валідне,
    /// посилання — ні, і каскади та списки вибору на такому значенні мовчки
    /// показували чуже.
    /// ⚠ Видалений логічно запис — теж «не знайдено»: він поза обігом, і нове
    /// посилання на нього не має з'являтися.
    /// </remarks>
    private static async Task RequireLookupTargetAsync(
        IRegistryStore registries, RegistryFieldDef field, long target, CancellationToken ct)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var entry = await registries.FindEntryAsync(target, ct).ConfigureAwait(false);

        if (entry is null || entry.IsDeleted)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Поле «{field.Code}»: запису довідника {target} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.lookupEntryNotFound",
                    ["field"] = field.Code,
                    ["value"] = target.ToString(invariant),
                });
        }

        if (field.RefRegistryDefId is { } expected && entry.RegistryDefId != expected)
        {
            var expectedDefinition = await registries
                .FindDefinitionByIdAsync(expected, ct).ConfigureAwait(false);
            var expectedCode = expectedDefinition?.Code ?? expected.ToString(invariant);

            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Поле «{field.Code}» посилається на довідник «{expectedCode}», а запис {target} "
                + $"(«{entry.Code}») належить іншому довіднику.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.lookupWrongRegistry",
                    ["field"] = field.Code,
                    ["value"] = target.ToString(invariant),
                    ["entryCode"] = entry.Code,
                    ["expectedRegistry"] = expectedCode,
                });
        }
    }

    /// <summary>Типізоване значення поля — для порівняння до/після і для аудиту.</summary>
    /// <remarks>
    /// ⚠ Той самий вибір колонки за типом, що вже застосовує
    /// <c>ImportDiffBuilder.Display</c> для комірок документа
    /// (<c>Ecr.Adapters.Excel</c>) — тут навмисно НЕ перевикористаний напряму:
    /// той метод читає <c>CellValueData</c> (комірка документа, посилання на
    /// довідник), цей — <c>RegistryValue</c> (сам запис довідника); типи різні,
    /// хоч і структурно схожі.
    /// </remarks>
    private static object? RawValue(RegistryValue value, CellDataType dataType) => dataType switch
    {
        CellDataType.String => value.ValueString,
        CellDataType.Int or CellDataType.Decimal => value.ValueNumeric,
        CellDataType.Bool => value.ValueBool,
        CellDataType.Date => value.ValueDate,
        CellDataType.Lookup => value.ValueRefEntryId,
        CellDataType.Unit => value.ValueUnitId,

        // Formula/Calculated неможливі: RegistryValue.Set кидає раніше, ніж
        // виконання сюди дійде.
        _ => null,
    };

    private async Task<RegistryEntry> LoadAsync(long id, int registryDefId, CancellationToken ct)
    {
        var entry = await registries.FindEntryAsync(id, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Запису довідника {id} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryEntry",
                    ["entryId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // Переносити запис між довідниками не можна: у комірках лежить його Id,
        // а колонка оголошує LookupRegistryDefId — після переносу значення
        // лишилося б валідним числом і невалідним посиланням.
        if (entry.RegistryDefId != registryDefId)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Запис {id} належить довіднику {entry.RegistryDefId}, а не {registryDefId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.entryWrongRegistry",
                    ["entryId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["ownerRegistryDefId"] = entry.RegistryDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["registryDefId"] = registryDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return entry;
    }

    private async Task<RegistryEntry> CreateAsync(
        int registryDefId, EcrCode code, RegistryEntryUpsertDto dto, int userId, CancellationToken ct)
    {
        var duplicate = await registries.FindEntryByCodeAsync(registryDefId, code.Value, ct).ConfigureAwait(false);
        if (duplicate is not null)
        {
            // ⚠ Саме 409, а не «оновити знайдений»: тихе злиття з однойменним
            // записом підмінило б Id у нових комірках, і два різні об'єкти
            // стали б одним заднім числом.
            throw new BusinessRuleException(
                "ECR-REG-0409",
                $"Запис із кодом «{code.Value}» у цьому довіднику вже існує (Id {duplicate.Id}).",
                // ⛔ Q-30x: узагальнений шлях ExceptionHandlingMiddleware
                // (messageKey) — без нього подробиця доїжджала клієнту сирим
                // українським реченням незалежно від мови інтерфейсу.
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0409.entryCodeTaken",
                    ["code"] = code.Value,
                    ["id"] = duplicate.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var entry = new RegistryEntry(registryDefId, code, dto.Display, userId, clock.UtcNow);
        registries.Add(entry);
        return entry;
    }
}
