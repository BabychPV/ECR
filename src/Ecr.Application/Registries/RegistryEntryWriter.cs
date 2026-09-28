// src/Ecr.Application/Registries/RegistryEntryWriter.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Keys;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries;

/// <summary>Одна фактична зміна значення поля запису довідника — для аудиту.</summary>
/// <param name="FieldCode">Код поля довідника.</param>
/// <param name="OldValue">Значення до зміни; <c>null</c> — поле не було заповнене.</param>
/// <param name="NewValue">Значення після зміни; <c>null</c> — поле очищене.</param>
internal sealed record RegistryValueFieldChange(string FieldCode, object? OldValue, object? NewValue);

/// <summary>
/// Прочитане пакетом для <see cref="RegistryEntryWriter.ApplyValuesAsync"/>
/// (<c>B-10</c>, імпорт CSV).
/// </summary>
/// <param name="ExistingValues">
/// Наявні значення запису; <c>null</c> — прочитати з бази (<c>ListValuesAsync</c>).
/// </param>
/// <param name="LookupTargets">
/// Уже прочитані записи — цілі <c>Lookup</c>-посилань; чого тут немає, те
/// читається з бази, як і без пакета.
/// </param>
internal sealed record RegistryValuesPrefetch(
    IReadOnlyList<RegistryValue>? ExistingValues,
    IReadOnlyDictionary<long, RegistryEntry>? LookupTargets);

/// <summary>Один запис пакета <see cref="RegistryEntryWriter.WriteAsync"/>.</summary>
/// <param name="Code">
/// Бізнес-код запису. Наявний запис із цим кодом (без урахування регістру, як колація бази)
/// оновлюється; немає такого — створюється з назвою, рівною коду (як в імпорті CSV).
/// </param>
/// <param name="Values">
/// Значення за кодами полів ОПУБЛІКОВАНОГО опису. Тип — будь-який, який приймає ручний upsert
/// (рядок, число, <c>bool</c>, дата, <c>JsonElement</c>); <c>Lookup</c> — Id запису-цілі.
/// Поле, якого тут немає, не змінюється.
/// </param>
public sealed record RegistryEntryWrite(string Code, IReadOnlyDictionary<string, object?> Values);

/// <summary>Пакет записів ОДНОГО довідника для <see cref="RegistryEntryWriter.WriteAsync"/>.</summary>
/// <param name="RegistryDefId">Довідник.</param>
/// <param name="Entries">Записи; код не повторюється (без урахування регістру).</param>
public sealed record RegistryEntryWriteBatch(int RegistryDefId, IReadOnlyList<RegistryEntryWrite> Entries);

/// <summary>Результат <see cref="RegistryEntryWriter.WriteAsync"/>.</summary>
/// <param name="Added">Створених записів.</param>
/// <param name="Updated">Наявних записів, у яких фактично змінилося хоча б одне значення.</param>
/// <param name="Unchanged">Наявних записів без жодної фактичної зміни.</param>
/// <param name="Errors">
/// Помилки записів пакета; <c>Row</c> — номер у пакеті, з 1. Є хоч одна — не записано нічого.
/// </param>
/// <param name="Applied">Чи записано зміни.</param>
public sealed record RegistryEntryWriteResult(
    int Added, int Updated, int Unchanged, IReadOnlyList<RegistryEntryImportError> Errors, bool Applied);

/// <summary>
/// Єдина точка запису записів і значень довідника (<c>dic.RegistryEntry</c>,
/// <c>dic.RegistryValue</c>): перевірка типу, обов'язковості й складу полів, складений ключ
/// (RT-10a/b), ревізія даних (RT-03), подія аудиту <see cref="ValueChangedEventType"/>.
/// </summary>
/// <remarks>
/// ⛔ Через нього пишуть і ручний upsert (<see cref="UpsertRegistryEntryHandler"/>), і імпорт CSV
/// (<see cref="ImportRegistryEntriesHandler"/>), і неінтерактивні виклики
/// (<see cref="WriteAsync"/>). Друга копія будь-якого з правил нижче розійшлася б із цією на
/// першому ж виправленні.
///
/// ⚠ Автор зміни (<c>ChangedByUserId</c>, RT-04) і мітка <c>DataChangedAt</c> (RT-03) ставляться
/// в <c>UnitOfWork.SaveChangesAsync</c> з <see cref="ICurrentUser"/> — того самого, з якого
/// writer бере автора події аудиту. У фоновій задачі це її автор (<c>JobAwareCurrentUser</c>).
///
/// ⚠ Права writer НЕ перевіряє: це робить обробник запиту (<c>Registry.EditData</c> або грант
/// на довідник), а фонова задача має власну модель доступу.
/// </remarks>
public sealed class RegistryEntryWriter(
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    RegistryKeyService? keys = null)
{
    // ⚠ `keys` необов'язковий лише для тестів, що будують writer руками (у їхніх довідниках
    // ключів немає; храповик запитів B-10 так і міряє довідник без ключів). Контейнер підставляє
    // зареєстрований `RegistryKeyService` завжди; що на справжньому шляху ключ перевіряється,
    // тримають HTTP-тести `RegistryKeyConflictHttpTests` і `RegistryKeyLifecycleHttpTests`.

    /// <summary>Тип події журналу безпеки про зміну значень запису довідника.</summary>
    /// <remarks>
    /// Подія пишеться в <c>aud.SecurityEvent</c>: окремої таблиці історії значень для аудиту
    /// немає; перелік змінених полів лежить у <c>DetailsJson</c>.
    /// </remarks>
    public const string ValueChangedEventType = "RegistryValueChanged";

    /// <summary>Служба ключів, з якою працює writer; <c>null</c> — лише в тестах без ключів.</summary>
    internal RegistryKeyService? Keys => keys;

    /// <summary>
    /// Записує пакет записів одного довідника — все або нічого, без HTTP-специфіки (фонова
    /// задача, інтеграції).
    /// </summary>
    /// <remarks>
    /// ⚠ Помилка значення (тип, обов'язковість, невідоме поле, <c>Lookup</c> на чужий чи
    /// неіснуючий запис, невалідний код) — помилка рядка в результаті, а не виняток; є хоч одна —
    /// нічого не зберігається. Ключ, який уже тримає запис поза пакетом, — виняток
    /// <c>ECR-REG-4092</c> на весь пакет, як в імпорті CSV: це перевірка під блокуванням у
    /// транзакції запису.
    ///
    /// ⚠ Пакет без жодної фактичної зміни нічого не зберігає і ревізію не рухає
    /// (<c>Applied = false</c>): мітка <c>DataChangedAt</c> оголосила б застарілими результати
    /// всіх документів, що читають довідник, хоча дані ті самі.
    ///
    /// ⛔ Коли <c>Applied = false</c> через помилки, застосовані до інших записів пакета зміни
    /// лишаються у відстеженні одиниці роботи НЕзбереженими. Виклик має завершити свою область
    /// (DI-scope) без <c>SaveChangesAsync</c> — так само, як запит імпорту CSV.
    /// </remarks>
    /// <param name="batch">Пакет.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="ArgumentException">Порожній код або код, що повторюється в пакеті.</exception>
    /// <exception cref="AccessDeniedException"><c>ECR-AUTH-0401</c>: немає автора.</exception>
    /// <exception cref="NotFoundException"><c>ECR-REG-0404</c>: довідника немає.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-4092</c>: ключ зайнятий записом поза пакетом.</exception>
    public async Task<RegistryEntryWriteResult> WriteAsync(RegistryEntryWriteBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Entries);

        // Порожній чи повторений код — помилка того, хто склав пакет, а не даних: ні помилкою
        // рядка з каталогу (тексти імпорту кажуть «у файлі»), ні «переможцем останнім».
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in batch.Entries)
        {
            var code = item?.Code?.Trim() ?? string.Empty;
            if (code.Length == 0 || !codes.Add(code))
            {
                throw new ArgumentException($"Код запису «{code}» порожній або повторюється в пакеті.", nameof(batch));
            }
        }

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не змінює довідники.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var definition = await registries.FindDefinitionByIdAsync(batch.RegistryDefId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника {batch.RegistryDefId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryId",
                    ["registryDefId"] = batch.RegistryDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // Наявні записи й їхні значення — пакетом (B-10), з відстеженням (як в імпорті).
        var existing = codes.Count == 0
            ? new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase)
            : (await registries.FindEntriesByCodesAsync(definition.Id, codes, ct).ConfigureAwait(false))
                .ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);
        var valuesByEntry = existing.Count == 0
            ? new Dictionary<long, IReadOnlyList<RegistryValue>>()
            : (await registries.ListValuesForEntriesAsync([.. existing.Values.Select(e => e.Id)], ct).ConfigureAwait(false))
                .GroupBy(v => v.RegistryEntryId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<RegistryValue>)[.. g]);
        IReadOnlyList<RegistryKeyDef> keyDefs = keys is null
            ? []
            : await keys.ListActiveKeysAsync(definition.Id, ct).ConfigureAwait(false);

        var errors = new List<RegistryEntryImportError>();
        var staged = new List<RegistryEntry>();
        var valueChanges = new List<(RegistryEntry Entry, IReadOnlyList<RegistryValueFieldChange> Changes)>();
        var (added, updated, unchanged) = (0, 0, 0);

        for (var i = 0; i < batch.Entries.Count; i++)
        {
            var row = i + 1;
            var code = batch.Entries[i].Code.Trim();
            var values = batch.Entries[i].Values ?? new Dictionary<string, object?>();

            if (!EcrCode.TryCreate(code, out var ecrCode))
            {
                errors.Add(new RegistryEntryImportError(row, code, null, "err.ECR-CFG-0422.invalidCode"));
                continue;
            }

            // Проба кожного значення окремо — заради поля в помилці (як ResolveRowAsync імпорту):
            // RegistryValue.Set не знає коду поля.
            if (Probe(definition, values) is { } probe)
            {
                errors.Add(new RegistryEntryImportError(row, code, probe.Field, probe.MessageKey));
                continue;
            }

            var entry = existing.GetValueOrDefault(code);
            var isNew = entry is null;
            entry ??= AddEntry(
                definition.Id,
                ecrCode,
                new LocalizedText(new Dictionary<string, string> { [UiStringResolver.DefaultLanguage] = code }),
                userId);

            IReadOnlyList<RegistryValueFieldChange> changes;
            try
            {
                var prefetch = new RegistryValuesPrefetch(
                    isNew ? null : valuesByEntry.GetValueOrDefault(entry.Id) ?? [], LookupTargets: null);
                changes = await ApplyValuesAsync(definition, entry, values, prefetch, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DomainException or BusinessRuleException)
            {
                errors.Add(new RegistryEntryImportError(row, code, FieldOf(ex), MessageKeyOf(ex)));
                continue;
            }

            staged.Add(entry);
            if (changes.Count > 0)
            {
                valueChanges.Add((entry, changes));
            }

            if (isNew)
            {
                added++;
            }
            else if (changes.Count > 0)
            {
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        if (errors.Count > 0 || added + updated == 0)
        {
            return new RegistryEntryWriteResult(added, updated, unchanged, errors, Applied: false);
        }

        await SaveBatchAsync(definition, keyDefs, keyDefs.Count > 0 ? staged : [], valueChanges, userId, beforeSave: null, ct)
            .ConfigureAwait(false);

        return new RegistryEntryWriteResult(added, updated, unchanged, errors, Applied: true);
    }

    /// <summary>Новий запис довідника, уже доданий у сховище.</summary>
    /// <remarks>
    /// ⚠ Спершу <c>Add</c>, лише потім <see cref="ApplyValuesAsync"/>. Навпаки — EF довантажує
    /// запис у чергу вставки каскадом через навігацію <c>RegistryValue.Entry</c>, і подвійне
    /// додавання дає <c>IDENTITY_INSERT</c>.
    /// </remarks>
    internal RegistryEntry AddEntry(int registryDefId, EcrCode code, LocalizedText display, int userId)
    {
        var entry = new RegistryEntry(registryDefId, code, display, userId, clock.UtcNow);
        registries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Зберігає один запис: ревізія даних, складений ключ (у транзакції, якщо в довідника є
    /// активні ключі) і — після коміту — одна подія аудиту на всі змінені поля.
    /// </summary>
    /// <remarks>
    /// ⚠ Ревізія рухається ЗАВЖДИ, навіть коли змінилася лише назва: у ключі кешу списків лежить
    /// саме вона.
    ///
    /// Журнал — ПІСЛЯ коміту: Id нового запису відомий лише тепер, коли EF підставив згенероване
    /// значення. Подія не пишеться, якщо жодне значення фактично не змінилося.
    /// </remarks>
    internal async Task SaveEntryAsync(
        RegistryDef definition,
        RegistryEntry entry,
        IReadOnlyList<RegistryValueFieldChange> changes,
        int userId,
        CancellationToken ct)
    {
        definition.BumpDataRevision();

        // ⛔ RT-10a: складений ключ (ФВ-8.15) перераховується й перевіряється ПІСЛЯ
        // ApplyValuesAsync і зберігається в тій самій транзакції.
        if (keys is null)
        {
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await keys.SaveAsync(definition, entry, ct).ConfigureAwait(false);
        }

        if (changes.Count > 0)
        {
            await audit.WriteSecurityEventAsync(ValueChangedEvent(definition, entry, changes, userId), ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Зберігає пакет записів однією транзакцією: ревізія даних, ключі пакета, додатковий запис
    /// виклику, збереження, події аудиту — одним пакетним викликом.
    /// </summary>
    /// <remarks>
    /// ⛔ Аудит пише СИРИМ SQL поза відстеженням EF (<c>Q-244</c>): без явної транзакції збій між
    /// журналом і <c>SaveChangesAsync</c> лишив би їх у різних станах.
    ///
    /// ⚠ Події — ПІСЛЯ <c>SaveChangesAsync</c> (Id нових записів), одним
    /// <see cref="IAuditWriter.WriteSecurityEventsAsync"/>: N поштучних round-trip на N записів
    /// не масштабуються.
    /// </remarks>
    /// <param name="definition">Опис довідника.</param>
    /// <param name="keyDefs">Активні ключі довідника.</param>
    /// <param name="keyed">Записи, ключі яких перераховуються (порожньо — ключів не торкатися).</param>
    /// <param name="valueChanges">Фактичні зміни значень за записами.</param>
    /// <param name="userId">Автор події аудиту.</param>
    /// <param name="beforeSave">Запис виклику в тій самій транзакції перед збереженням (журнал імпорту).</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task SaveBatchAsync(
        RegistryDef definition,
        IReadOnlyList<RegistryKeyDef> keyDefs,
        IReadOnlyCollection<RegistryEntry> keyed,
        IReadOnlyList<(RegistryEntry Entry, IReadOnlyList<RegistryValueFieldChange> Changes)> valueChanges,
        int userId,
        Func<CancellationToken, Task>? beforeSave,
        CancellationToken ct)
    {
        definition.BumpDataRevision();

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ RT-10b: ключі — тим самим сервісом, що й для одного запису, у тій самій транзакції,
            // що й записи. Ключ, який тримає запис поза пакетом, — 409 на весь пакет.
            if (keys is not null && keyed.Count > 0)
            {
                await keys.ApplyAsync(definition, keyDefs, keyed, innerCt).ConfigureAwait(false);
            }

            if (beforeSave is not null)
            {
                await beforeSave(innerCt).ConfigureAwait(false);
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            if (valueChanges.Count > 0)
            {
                var events = valueChanges
                    .Select(vc => ValueChangedEvent(definition, vc.Entry, vc.Changes, userId))
                    .ToList();

                await audit.WriteSecurityEventsAsync(events, innerCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Записує значення полів типізовано за <c>RegistryFieldDef.DataType</c>.</summary>
    /// <remarks>
    /// ⚠ Тип береться з опису поля, а не з типу переданого об'єкта. Інакше число, що прийшло
    /// рядком із JSON, лягло б у <c>ValueString</c> — і поле «ліміт» перестало б порівнюватися й
    /// сумуватися, не давши жодної помилки.
    /// <para>
    /// ⛔ Значення проходить через <see cref="CellValueReader.Normalize"/> ПЕРЕД
    /// <c>RegistryValue.Set</c>: через HTTP <c>values</c> приходить
    /// <c>Dictionary&lt;string, object?&gt;</c> із <see cref="JsonElement"/> у кожному значенні,
    /// якого <c>RegistryValue.Set</c> не впізнає. Для готового <c>string</c> (рядок CSV) чи числа
    /// <see cref="CellValueReader.Normalize"/> — тотожність.
    /// </para>
    /// </remarks>
    /// <param name="definition">Опис довідника.</param>
    /// <param name="entry">Запис, якому застосовуються значення.</param>
    /// <param name="values">Значення за кодами полів.</param>
    /// <param name="prefetch">
    /// Прочитане пакетом наперед (<c>B-10</c>): наявні значення цього запису й цілі
    /// <c>Lookup</c>-посилань. <c>null</c> — читати з бази поштучно. Правила ті самі в обох
    /// випадках — змінюється лише ДЖЕРЕЛО тих самих рядків.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<IReadOnlyList<RegistryValueFieldChange>> ApplyValuesAsync(
        RegistryDef definition,
        RegistryEntry entry,
        IReadOnlyDictionary<string, object?> values,
        RegistryValuesPrefetch? prefetch,
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
            ? (prefetch?.ExistingValues
               ?? await registries.ListValuesAsync(entry.Id, ct).ConfigureAwait(false))
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
            // ляже в базу, а не сирий вхід запиту.
            var oldValue = isNew ? null : RawValue(value!, field.DataType);
            value!.Set(field.DataType, CellValueReader.Normalize(raw), field.UnitId);
            var newValue = RawValue(value, field.DataType);

            if (field.DataType == CellDataType.Lookup && value.ValueRefEntryId is { } target)
            {
                await RequireLookupTargetAsync(field, target, prefetch?.LookupTargets, ct).ConfigureAwait(false);
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

    /// <summary>Ключ тексту з винятку валідації; типова фраза домену — запасний варіант.</summary>
    internal static string MessageKeyOf(Exception ex) => Details(ex)?.GetValueOrDefault("messageKey") as string
        ?? "err.ECR-REG-0422.entryImportRowFailed";

    /// <summary>Поле, назване в подробиці винятку (<c>fieldCode</c> одиночного поля, <c>fields</c> — перелік).</summary>
    internal static string? FieldOf(Exception ex)
    {
        var details = Details(ex);
        if (details is null)
        {
            return null;
        }

        return details.GetValueOrDefault("fieldCode") as string ?? details.GetValueOrDefault("fields") as string;
    }

    private static IReadOnlyDictionary<string, object?>? Details(Exception ex) => ex switch
    {
        DomainException de => de.Details,
        BusinessRuleException be => be.Details,
        _ => null,
    };

    /// <summary>
    /// Пробний <see cref="RegistryValue.Set"/> кожного відомого не-<c>Lookup</c> значення на
    /// одноразовому об'єкті поза контекстом — помилка з кодом поля.
    /// </summary>
    private static (string Field, string MessageKey)? Probe(
        RegistryDef definition, IReadOnlyDictionary<string, object?> values)
    {
        var fields = definition.Fields.ToDictionary(f => f.Code, StringComparer.Ordinal);
        foreach (var (code, raw) in values)
        {
            if (raw is null || !fields.TryGetValue(code, out var field) || field.DataType == CellDataType.Lookup)
            {
                continue;
            }

            try
            {
                new RegistryValue(0L, field.Id).Set(field.DataType, CellValueReader.Normalize(raw), field.UnitId);
            }
            catch (DomainException ex)
            {
                return (field.Code, MessageKeyOf(ex));
            }
        }

        return null;
    }

    /// <summary>
    /// Подія <see cref="ValueChangedEventType"/>: формат <c>DetailsJson</c> один для всіх шляхів
    /// запису (registryDefId/entryId/changes).
    /// </summary>
    private SecurityEventRecord ValueChangedEvent(
        RegistryDef definition, RegistryEntry entry, IReadOnlyList<RegistryValueFieldChange> changes, int userId)
        => new(
            clock.UtcNow, ValueChangedEventType, TargetUserId: null, TargetRoleId: null,
            JsonSerializer.Serialize(new
            {
                registryDefId = definition.Id,
                entryId = entry.Id,
                changes = changes.Select(c => new { field = c.FieldCode, oldValue = c.OldValue, newValue = c.NewValue }),
            }),
            userId, currentUser.CorrelationId);

    /// <summary>
    /// Значення поля Lookup мусить бути живим записом САМЕ того довідника, який
    /// оголошує поле (V-08(b), V-17(a), третій раунд UX).
    /// </summary>
    /// <remarks>
    /// ⛔ Доти неіснуючий Id доходив до бази й падав на <c>FK_RegValue_Ref</c> (<c>500</c>), а
    /// Id запису ІНШОГО довідника приймався. Видалений логічно запис — теж «не знайдено».
    /// <para>
    /// ⚠ <paramref name="knownTargets"/> — записи, уже прочитані пакетом (імпорт CSV,
    /// <c>B-10</c>): повторний <c>FindEntryAsync</c> на кожне значення дав би N+1.
    /// </para>
    /// </remarks>
    private async Task RequireLookupTargetAsync(
        RegistryFieldDef field,
        long target,
        IReadOnlyDictionary<long, RegistryEntry>? knownTargets,
        CancellationToken ct)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var entry = knownTargets is not null && knownTargets.TryGetValue(target, out var known)
            ? known
            : await registries.FindEntryAsync(target, ct).ConfigureAwait(false);

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
}
