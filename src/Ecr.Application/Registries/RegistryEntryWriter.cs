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
/// оновлюється; немає такого — створюється з назвою, рівною коду (як в імпорті CSV). У довіднику з
/// <c>CodeMode = Auto</c> (<c>D-157</c>) порожній код — новий запис із кодом послідовності, а
/// непорожній, якого в довіднику немає, — помилка рядка <see cref="RegistryEntryWriter.EntryCodeAutomaticKey"/>.
/// </param>
/// <param name="Values">
/// Значення за кодами полів ОПУБЛІКОВАНОГО опису. Тип — будь-який, який приймає ручний upsert
/// (рядок, число, <c>bool</c>, дата, <c>JsonElement</c>); <c>Lookup</c> — Id запису-цілі.
/// Поле, якого тут немає, не змінюється.
/// </param>
public sealed record RegistryEntryWrite(string Code, IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>
    /// Назва НОВОГО запису мовою за замовчуванням (<c>D-212</c>: синк — ім'я елемента AF).
    /// <c>null</c> або порожня — назва дорівнює коду, як і досі. Наявного запису не перейменовує.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Вікно дії запису (<c>D-212</c> PR-7: синк — дати AF). <c>null</c> — не змінювати. Порожнє
    /// вікно — помилка рядка <c>err.validityWindowEmpty</c> (<c>RegistryEntry.SetValidity</c>).
    /// </summary>
    public ValidityWindow? Validity { get; init; }
}

/// <summary>Пакет записів ОДНОГО довідника для <see cref="RegistryEntryWriter.WriteAsync"/>.</summary>
/// <param name="RegistryDefId">Довідник.</param>
/// <param name="Entries">Записи; код не повторюється (без урахування регістру).</param>
public sealed record RegistryEntryWriteBatch(int RegistryDefId, IReadOnlyList<RegistryEntryWrite> Entries)
{
    /// <summary>
    /// Лише оновлювати (S7, синк довідника): запису з таким кодом немає — помилка рядка
    /// <c>err.ECR-REG-0404.registryEntry</c>, а не створення. За замовчуванням <c>false</c> —
    /// поведінка S6 (немає — створюється).
    /// </summary>
    public bool UpdateOnly { get; init; }

    /// <summary>
    /// Лише створювати (<c>D-212</c>, автостворення синком): запис із таким кодом уже є в довіднику
    /// (зокрема видалений логічно — код за ним лишається) — помилка рядка
    /// <see cref="RegistryEntryWriter.EntryCodeTakenKey"/>, а не оновлення. Разом з
    /// <see cref="UpdateOnly"/> — помилка виклику.
    /// </summary>
    public bool CreateOnly { get; init; }

    /// <summary>
    /// Коди нових записів довідника з <c>CodeMode = Auto</c> — заглушки, а не номери послідовності
    /// (RT-14, <c>dryRun</c>). За замовчуванням <c>false</c> — поведінка без змін.
    /// </summary>
    /// <remarks>
    /// ⛔ Лише для запису, який виклик ВІДКОТИТЬ: номер <c>sp_sequence_get_range</c> транзакція не
    /// повертає, і кожна жива перевірка сітки (раз на 600 мс, §8.4) пропалювала б шкалу кодів.
    /// </remarks>
    public bool PlaceholderAutoCodes { get; init; }
}

/// <summary>
/// Одне оновлення наявного запису, адресоване за <c>RegistryEntryId</c>
/// (<see cref="RegistryEntryWriter.UpdateAsync"/>).
/// </summary>
/// <param name="RegistryEntryId">Запис; мусить належати довіднику пакета й не бути видаленим логічно.</param>
/// <param name="Values">Значення за кодами полів — як у <see cref="RegistryEntryWrite.Values"/>.</param>
public sealed record RegistryEntryUpdate(long RegistryEntryId, IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>
    /// Увімкнути (<c>true</c>) чи вимкнути (<c>false</c>) запис (<c>D-212</c>, політика
    /// <c>Deactivate</c> і повернення елемента). <c>null</c> — не змінювати. Фактична зміна
    /// потрапляє в ту саму подію аудиту, що й значення, як поле
    /// <see cref="RegistryEntryWriter.ActiveFieldCode"/>.
    /// </summary>
    public bool? IsActive { get; init; }

    /// <summary>
    /// Нове вікно дії (<c>D-212</c> PR-7); <c>null</c> — не змінювати. Фактична зміна — у тій самій
    /// події аудиту полем <see cref="RegistryEntryWriter.ValidityFieldCode"/>; рядки ключів
    /// дзеркалять нове вікно в тій самій транзакції (RT-10b). ⚠ Перерахунок <c>IsOrphaned</c>
    /// (<c>ФВ-8.13a</c>) робить виклик — writer про документи не знає (як і
    /// <c>SetEntryValidityHandler</c>).
    /// </summary>
    public ValidityWindow? Validity { get; init; }
}

/// <summary>Пакет оновлень ОДНОГО довідника для <see cref="RegistryEntryWriter.UpdateAsync"/>.</summary>
/// <param name="RegistryDefId">Довідник.</param>
/// <param name="Entries">Оновлення; <c>RegistryEntryId</c> не повторюється.</param>
public sealed record RegistryEntryUpdateBatch(int RegistryDefId, IReadOnlyList<RegistryEntryUpdate> Entries);

/// <summary>Результат <see cref="RegistryEntryWriter.WriteAsync"/>.</summary>
/// <param name="Added">Створених записів.</param>
/// <param name="Updated">Наявних записів, у яких фактично змінилося хоча б одне значення.</param>
/// <param name="Unchanged">Наявних записів без жодної фактичної зміни.</param>
/// <param name="Errors">
/// Помилки записів пакета; <c>Row</c> — номер у пакеті, з 1. Є хоч одна — не записано нічого.
/// </param>
/// <param name="Applied">Чи записано зміни.</param>
public sealed record RegistryEntryWriteResult(
    int Added, int Updated, int Unchanged, IReadOnlyList<RegistryEntryImportError> Errors, bool Applied)
{
    /// <summary>
    /// Рядки пакета, що пройшли перевірку значень, — із записом (RT-14: пакет рядків адресує результат
    /// клієнтському рядку, а Id нового запису відомий лише після збереження). Рядок, який далі
    /// відхилила звірка ключів, є і тут, і в <see cref="Errors"/>.
    /// </summary>
    public IReadOnlyList<RegistryEntryWriteRow> Rows { get; init; } = [];
}

/// <summary>Рядок пакета <see cref="RegistryEntryWriter"/>, що пройшов перевірку значень.</summary>
/// <param name="Row">Номер у пакеті, з 1.</param>
/// <param name="Entry">Запис; у нового <c>Id</c> з'являється після збереження.</param>
/// <param name="IsNew">Запис створено цим пакетом.</param>
/// <param name="IsChanged">Хоч одне значення фактично змінилося.</param>
public sealed record RegistryEntryWriteRow(int Row, RegistryEntry Entry, bool IsNew, bool IsChanged);

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

    /// <summary>
    /// Ключ помилки рядка: той самий ключ отримує інший запис цього ж пакета (для темпорального
    /// довідника — у вікні, що перетинається; FEATURE-REGISTRY-TABLES §4.3 крок 3).
    /// </summary>
    public const string KeyDuplicateInBatchKey = "err.ECR-REG-4092.keyDuplicateInBatch";

    /// <summary>
    /// Ключ помилки: код нового запису довідника з <c>CodeMode = Auto</c> видає послідовність
    /// (<c>D-157</c>), а виклик назвав свій — такого запису в довіднику немає.
    /// </summary>
    /// <remarks>
    /// ⚠ Відмова, а не «взяти код із запиту»: дві шкали кодів в одному довіднику зробили б пошук
    /// запису за кодом неоднозначним (саме тому <c>CodeMode</c> ставиться лише при створенні).
    /// Мовчки підмінити код на автоматичний теж не можна — виклик думав би, що запис має його код.
    /// </remarks>
    public const string EntryCodeAutomaticKey = "err.ECR-REG-0422.entryCodeAutomatic";

    /// <summary>
    /// Ключ помилки рядка <see cref="RegistryEntryWriteBatch.CreateOnly"/>: запис із цим кодом у
    /// довіднику вже є. Той самий ключ, що в ручного створення (<c>UpsertRegistryEntryHandler</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Помилка рядка параметрів не несе: <c>{id}</c> шаблону тут не підставлено — виклик, що
    /// показує текст, знає код із <c>Key</c> і читає Id сам.
    /// </remarks>
    public const string EntryCodeTakenKey = "err.ECR-REG-0409.entryCodeTaken";

    /// <summary>
    /// Код «поля» стану запису в події аудиту <see cref="ValueChangedEventType"/>
    /// (<see cref="RegistryEntryUpdate.IsActive"/>). <c>@</c> не буває в коді поля
    /// (<c>EcrCode</c>), тож збігу зі справжнім полем немає.
    /// </summary>
    public const string ActiveFieldCode = "@active";

    /// <summary>Код «поля» вікна дії в події аудиту (<see cref="RegistryEntryUpdate.Validity"/>).</summary>
    public const string ValidityFieldCode = "@validity";

    /// <summary>Служба ключів, з якою працює writer; <c>null</c> — лише в тестах без ключів.</summary>
    internal RegistryKeyService? Keys => keys;

    /// <summary>Код запису з номера послідовності: <c>E</c> + 9 цифр (<c>D-157</c>).</summary>
    /// <param name="number">Номер <c>dic.RegistryEntryCodeSeq</c>.</param>
    /// <remarks>
    /// Латиниця й цифри — задовольняє <c>EcrCode</c>; понад 999 999 999 номер просто довшає.
    /// </remarks>
    public static string AutoCode(long number)
        => "E" + number.ToString("D9", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Коди для <paramref name="count"/> нових записів довідника з <c>CodeMode = Auto</c> — одним
    /// зверненням до послідовності. Спільна точка ВСІХ шляхів запису (ручний upsert, CSV, пакет).
    /// </summary>
    /// <param name="definition">Довідник; у ручному режимі — порожня черга без звернення.</param>
    /// <param name="count">Скільки кодів потрібно.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<Queue<EcrCode>> ReserveAutoCodesAsync(RegistryDef definition, int count, CancellationToken ct)
    {
        var codes = new Queue<EcrCode>();
        if (definition.CodeMode != RegistryCodeMode.Auto || count <= 0)
        {
            return codes;
        }

        var first = await registries.NextEntryCodesAsync(count, ct).ConfigureAwait(false);
        for (var i = 0L; i < count; i++)
        {
            codes.Enqueue(EcrCode.Create(AutoCode(first + i)));
        }

        return codes;
    }

    /// <summary>
    /// Заглушкові коди нових записів (<see cref="RegistryEntryWriteBatch.PlaceholderAutoCodes"/>):
    /// без звернення до послідовності, поза шкалою <c>E</c> + цифри.
    /// </summary>
    private static Queue<EcrCode> PlaceholderCodes(RegistryDef definition, int count)
    {
        var codes = new Queue<EcrCode>();
        for (var i = 0; definition.CodeMode == RegistryCodeMode.Auto && i < count; i++)
        {
            codes.Enqueue(EcrCode.Create($"Z_DRYRUN_{Guid.NewGuid():N}"));
        }

        return codes;
    }

    /// <summary>
    /// Записує пакет записів одного довідника — все або нічого, без HTTP-специфіки (фонова
    /// задача, інтеграції).
    /// </summary>
    /// <remarks>
    /// ⚠ Помилка значення (тип, обов'язковість, невідоме поле, <c>Lookup</c> на чужий чи
    /// неіснуючий запис, невалідний код) — помилка рядка в результаті, а не виняток; є хоч одна —
    /// нічого не зберігається. Два записи пакета з тим самим ключем (темпоральний довідник — у
    /// вікнах, що перетинаються) — помилка обох рядків <see cref="KeyDuplicateInBatchKey"/>. Ключ,
    /// який уже тримає запис поза пакетом, — виняток <c>ECR-REG-4092</c> на весь пакет, як в
    /// імпорті CSV: це перевірка під блокуванням у транзакції запису.
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

        if (batch.UpdateOnly && batch.CreateOnly)
        {
            throw new ArgumentException("Пакет не може бути водночас «лише оновлювати» і «лише створювати».", nameof(batch));
        }

        // Порожній чи повторений код — помилка того, хто склав пакет, а не даних: ні помилкою
        // рядка з каталогу (тексти імпорту кажуть «у файлі»), ні «переможцем останнім».
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anyEmpty = false;
        foreach (var item in batch.Entries)
        {
            var code = item?.Code?.Trim() ?? string.Empty;
            if (code.Length == 0)
            {
                anyEmpty = true;
                continue;
            }

            if (!codes.Add(code))
            {
                throw new ArgumentException($"Код запису «{code}» порожній або повторюється в пакеті.", nameof(batch));
            }
        }

        var userId = RequireUserId();
        var definition = await RequireDefinitionAsync(batch.RegistryDefId, ct).ConfigureAwait(false);

        // ⚠ RT-12 (D-157): порожній код законний лише в довіднику з `CodeMode = Auto` — це новий
        // запис, код якому видасть послідовність. У ручному режимі — помилка виклику, як і досі.
        if (anyEmpty && definition.CodeMode != RegistryCodeMode.Auto)
        {
            throw new ArgumentException("Код запису «» порожній або повторюється в пакеті.", nameof(batch));
        }

        // Наявні записи й їхні значення — пакетом (B-10), з відстеженням (як в імпорті).
        var existing = codes.Count == 0
            ? new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase)
            : (await registries.FindEntriesByCodesAsync(definition.Id, codes, ct).ConfigureAwait(false))
                .ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);

        var targets = batch.Entries
            .Select((item, i) => new WriteTarget(
                i + 1,
                item.Code?.Trim() ?? string.Empty,
                existing.GetValueOrDefault(item.Code?.Trim() ?? string.Empty),
                MayCreate: !batch.UpdateOnly,
                item.Values ?? new Dictionary<string, object?>())
            {
                MustCreate = batch.CreateOnly,
                DisplayName = item.DisplayName,
                Validity = item.Validity,
            })
            .ToList();

        return await WriteTargetsAsync(definition, targets, userId, batch.PlaceholderAutoCodes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Оновлює наявні записи одного довідника, адресовані за <c>RegistryEntryId</c> — лише
    /// оновлення, без створення (S7, синк довідника із зовнішнім джерелом).
    /// </summary>
    /// <remarks>
    /// Правила ті самі, що в <see cref="WriteAsync"/>: все або нічого, <c>Applied = false</c> без
    /// фактичних змін, ревізія й аудит — лише при записі; ⛔ після <c>Applied = false</c> область
    /// виклику закривається без <c>SaveChangesAsync</c>.
    /// <para>
    /// Запису немає, він видалений логічно або належить іншому довіднику — помилка рядка
    /// <c>err.ECR-REG-0404.registryEntry</c> з <c>Key</c> = Id запису.
    /// </para>
    /// <para>
    /// ⚠ Записи читаються поштучно (<see cref="IRegistryStore.FindEntryAsync"/>): пакетного читання
    /// за Id у порту немає, а порт — поза межами кроку S7. Значення — пакетом, як у
    /// <see cref="WriteAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="batch">Пакет.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="ArgumentException">Id запису повторюється в пакеті.</exception>
    /// <exception cref="AccessDeniedException"><c>ECR-AUTH-0401</c>: немає автора.</exception>
    /// <exception cref="NotFoundException"><c>ECR-REG-0404</c>: довідника немає.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-4092</c>: ключ зайнятий записом поза пакетом.</exception>
    public async Task<RegistryEntryWriteResult> UpdateAsync(RegistryEntryUpdateBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Entries);

        var ids = new HashSet<long>();
        foreach (var item in batch.Entries)
        {
            if (item is null || !ids.Add(item.RegistryEntryId))
            {
                throw new ArgumentException(
                    $"Запис {item?.RegistryEntryId} порожній або повторюється в пакеті.", nameof(batch));
            }
        }

        var userId = RequireUserId();
        var definition = await RequireDefinitionAsync(batch.RegistryDefId, ct).ConfigureAwait(false);

        var targets = new List<WriteTarget>(batch.Entries.Count);
        for (var i = 0; i < batch.Entries.Count; i++)
        {
            var item = batch.Entries[i];
            var entry = await registries.FindEntryAsync(item.RegistryEntryId, ct).ConfigureAwait(false);
            if (entry is null || entry.IsDeleted || entry.RegistryDefId != definition.Id)
            {
                entry = null;
            }

            targets.Add(new WriteTarget(
                i + 1,
                entry?.Code ?? item.RegistryEntryId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry,
                MayCreate: false,
                item.Values ?? new Dictionary<string, object?>())
            {
                IsActive = item.IsActive,
                Validity = item.Validity,
            });
        }

        return await WriteTargetsAsync(definition, targets, userId, placeholderAutoCodes: false, ct).ConfigureAwait(false);
    }

    /// <summary>Спільне ядро <see cref="WriteAsync"/> і <see cref="UpdateAsync"/>.</summary>
    private async Task<RegistryEntryWriteResult> WriteTargetsAsync(
        RegistryDef definition, IReadOnlyList<WriteTarget> targets, int userId, bool placeholderAutoCodes, CancellationToken ct)
    {
        var existingIds = targets.Where(t => t.Existing is not null).Select(t => t.Existing!.Id).Distinct().ToList();
        var valuesByEntry = existingIds.Count == 0
            ? new Dictionary<long, IReadOnlyList<RegistryValue>>()
            : (await registries.ListValuesForEntriesAsync(existingIds, ct).ConfigureAwait(false))
                .GroupBy(v => v.RegistryEntryId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<RegistryValue>)[.. g]);
        IReadOnlyList<RegistryKeyDef> keyDefs = keys is null
            ? []
            : await keys.ListActiveKeysAsync(definition.Id, ct).ConfigureAwait(false);

        var errors = new List<RegistryEntryImportError>();
        var staged = new List<RegistryEntry>();
        var keyed = new List<KeyedTarget>();
        var valueChanges = new List<(RegistryEntry Entry, IReadOnlyList<RegistryValueFieldChange> Changes)>();
        var written = new List<RegistryEntryWriteRow>();
        var (added, updated, unchanged) = (0, 0, 0);

        // RT-12 (D-157): коди нових записів без коду — одним зверненням до послідовності на пакет.
        var autoCount = targets.Count(t => t is { Existing: null, MayCreate: true, Code.Length: 0 });
        var autoCodes = placeholderAutoCodes
            ? PlaceholderCodes(definition, autoCount)
            : await ReserveAutoCodesAsync(definition, autoCount, ct).ConfigureAwait(false);

        void Count(RowOutcome outcome, int delta)
        {
            switch (outcome)
            {
                case RowOutcome.Added:
                    added += delta;
                    break;
                case RowOutcome.Updated:
                    updated += delta;
                    break;
                default:
                    unchanged += delta;
                    break;
            }
        }

        foreach (var target in targets)
        {
            var row = target.Row;
            var code = target.Code;
            var values = target.Values;

            // Режим «лише оновлювати»: запису немає — помилка рядка, не створення.
            if (!target.MayCreate && target.Existing is null)
            {
                errors.Add(new RegistryEntryImportError(row, code, null, "err.ECR-REG-0404.registryEntry", Param("entryId", code)));
                continue;
            }

            // ⛔ Аудит 2026-10-03 (L5-01): код логічно видаленого запису лишається зайнятим
            // (UQ_RegistryEntry не фільтрує IsDeleted), і FindEntriesByCodesAsync його повертає.
            // Без цієї перевірки рядок «оновлював» видалений запис: звіт updated/applied=true, а
            // нового рядка в довіднику немає. UpdateAsync видалений відсіює раніше (0404).
            if (target.Existing is { IsDeleted: true } gone)
            {
                errors.Add(new RegistryEntryImportError(row, code, null, EntryCodeTakenKey,
                    new Dictionary<string, string>
                    {
                        ["code"] = code,
                        ["id"] = gone.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }));
                continue;
            }

            // Режим «лише створювати»: код уже зайнятий — помилка рядка, не оновлення чужого запису.
            if (target.MustCreate && target.Existing is not null)
            {
                errors.Add(new RegistryEntryImportError(row, code, null, EntryCodeTakenKey,
                    new Dictionary<string, string>
                    {
                        ["code"] = code,
                        ["id"] = target.Existing!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }));
                continue;
            }

            if (target.Existing is null && definition.CodeMode == RegistryCodeMode.Auto && code.Length > 0)
            {
                errors.Add(new RegistryEntryImportError(row, code, null, EntryCodeAutomaticKey));
                continue;
            }

            EcrCode ecrCode;
            if (code.Length == 0 && target.Existing is null)
            {
                ecrCode = autoCodes.Dequeue();
            }
            else if (!EcrCode.TryCreate(code, out ecrCode))
            {
                errors.Add(new RegistryEntryImportError(row, code, null, "err.ECR-CFG-0422.invalidCode", Param("code", code)));
                continue;
            }

            // Проба кожного значення окремо — заради поля в помилці (як ResolveRowAsync імпорту):
            // RegistryValue.Set не знає коду поля.
            if (Probe(definition, values) is { } probe)
            {
                errors.Add(new RegistryEntryImportError(row, code, probe.Field, probe.MessageKey, probe.Params));
                continue;
            }

            var entry = target.Existing;
            var isNew = entry is null;
            entry ??= AddEntry(
                definition.Id,
                ecrCode,
                new LocalizedText(new Dictionary<string, string>
                {
                    [UiStringResolver.DefaultLanguage] = string.IsNullOrWhiteSpace(target.DisplayName)
                        ? code.Length == 0 ? ecrCode.Value : code
                        : target.DisplayName.Trim(),
                }),
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
                errors.Add(new RegistryEntryImportError(row, code, FieldOf(ex), MessageKeyOf(ex), ParamsOf(ex)));
                continue;
            }

            // D-212: стан запису — та сама подія аудиту, що й значення, «полем» ActiveFieldCode.
            // Видалений запис сюди не доходить (UpdateAsync дає 0404), тож Activate не кидає.
            if (!isNew && target.IsActive is { } active && entry.IsActive != active)
            {
                var wasActive = entry.IsActive;
                if (active)
                {
                    entry.Activate();
                }
                else
                {
                    entry.Deactivate();
                }

                changes = [.. changes, new RegistryValueFieldChange(ActiveFieldCode, wasActive, active)];
            }

            // D-212 PR-7: вікно дії — ДО звірки ключів нижче: рядки ключа дзеркалять вікно (RT-10b), і
            // темпоральний дубль має звірятися з НОВИМ вікном. Порожнє вікно — помилка рядка.
            if (target.Validity is { } window && entry.Window != window)
            {
                var old = entry.Window;
                try
                {
                    entry.SetValidity(window.FromInclusive, window.ToExclusive);
                }
                catch (DomainException ex)
                {
                    errors.Add(new RegistryEntryImportError(row, code, ValidityFieldCode, MessageKeyOf(ex), ParamsOf(ex)));
                    continue;
                }

                changes = [.. changes, new RegistryValueFieldChange(ValidityFieldCode, WindowText(old), WindowText(window))];
            }

            staged.Add(entry);
            if (changes.Count > 0)
            {
                valueChanges.Add((entry, changes));
            }

            var outcome = isNew ? RowOutcome.Added : changes.Count > 0 ? RowOutcome.Updated : RowOutcome.Unchanged;
            Count(outcome, +1);
            written.Add(new RegistryEntryWriteRow(row, entry, isNew, changes.Count > 0));

            if (keyDefs.Count > 0)
            {
                // Значення ПІСЛЯ застосування рядка: рядок, що змінює ключ, звіряється з новим ключем.
                var stored = isNew ? [] : valuesByEntry.GetValueOrDefault(entry.Id) ?? [];
                keyed.Add(new KeyedTarget(
                    new RegistryBatchKeyRow(row, entry, RegistryBatchKeys.EffectiveValues(definition, stored, values)),
                    code,
                    outcome));
            }
        }

        // ⛔ RT-10b (§4.3 крок 3): служба ключів записи пакета між собою НЕ звіряє — тримачі з
        // пакета вона виключає з перевірки проти бази. Без цього кроку нетемпоральний дубль
        // доходив до UX_RegistryEntryKey_Live (keyTakenConcurrently на весь пакет), а темпоральний
        // з різним ValidFrom і вікнами, що перетинаються, записувався. Дубль — помилка ОБОХ рядків;
        // вікна, що не перетинаються, — законні (та сама умова, що в служби й імпорту CSV).
        var keyedByNumber = keyed.ToDictionary(k => k.Row.Number);
        foreach (var (duplicate, fields) in RegistryBatchKeys.DuplicateKeyRows(
                     definition, keyDefs, [.. keyed.Select(k => k.Row)]))
        {
            var target = keyedByNumber[duplicate.Number];
            errors.Add(new RegistryEntryImportError(target.Row.Number, target.Code, fields, KeyDuplicateInBatchKey));
            Count(target.Outcome, -1);
        }

        if (errors.Count > 0)
        {
            var ordered = errors.OrderBy(e => e.Row).ToList();
            errors.Clear();
            errors.AddRange(ordered);
        }

        if (errors.Count > 0 || added + updated == 0)
        {
            return new RegistryEntryWriteResult(added, updated, unchanged, errors, Applied: false) { Rows = written };
        }

        await SaveBatchAsync(definition, keyDefs, keyDefs.Count > 0 ? staged : [], valueChanges, userId, beforeSave: null, ct)
            .ConfigureAwait(false);

        return new RegistryEntryWriteResult(added, updated, unchanged, errors, Applied: true) { Rows = written };
    }

    /// <summary>Вікно для аудиту: <c>[2024-01-01, ∞)</c>.</summary>
    private static string WindowText(ValidityWindow window)
        => $"[{window.FromInclusive?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "-∞"}, "
           + $"{window.ToExclusive?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "∞"})";

    private int RequireUserId()
        => currentUser.UserId
           ?? throw new AccessDeniedException(
               "ECR-AUTH-0401",
               "Анонімний запит не змінює довідники.",
               new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

    private async Task<RegistryDef> RequireDefinitionAsync(int registryDefId, CancellationToken ct)
        => await registries.FindDefinitionByIdAsync(registryDefId, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               "ECR-REG-0404",
               $"Довідника {registryDefId} не існує.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-REG-0404.registryId",
                   ["registryDefId"] = registryDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
               });

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

        // Кінцеві значення запису: збережені, поверх них — застосовані цим викликом.
        var final = new Dictionary<int, RegistryValue>(existing);

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
            final[field.Id] = value;

            if (field.DataType == CellDataType.Lookup && value.ValueRefEntryId is { } target)
            {
                await RequireLookupTargetAsync(field, target, prefetch?.LookupTargets, ct).ConfigureAwait(false);
            }

            if (!Equals(oldValue, newValue))
            {
                changes.Add(new RegistryValueFieldChange(code, oldValue, newValue));
            }
        }

        // ⛔ Аудит 2026-10-03 (L4-06 = L5-03): відсутність рахується за КІНЦЕВИМ значенням. Раніше
        // null у вхідних значеннях наявного запису проходив (поле вже було в `existing`), хоча Set(null)
        // вище його вже стер, — обов'язкове поле очищувалося ручною правкою, пакетом, CSV і синком.
        // Рядок із самих пробілів — теж «не заповнено»: інакше він гасив поле первинного ключа.
        var missing = definition.Fields
            .Where(f => f.IsRequired)
            .Where(f => !final.TryGetValue(f.Id, out var v) || IsBlank(RawValue(v, f.DataType)))
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

    /// <summary>Один плейсхолдер → значення (D1).</summary>
    internal static IReadOnlyDictionary<string, string> Param(string name, string value)
        => new Dictionary<string, string> { [name] = value };

    /// <summary>
    /// Значення плейсхолдерів тексту з подробиці винятку: усі пари, крім <c>messageKey</c>, текстом
    /// (інваріантна культура); <c>null</c>, якщо подробиці порожні.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? ParamsOf(Exception ex)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Details(ex) ?? new Dictionary<string, object?>())
        {
            if (key != "messageKey" && value is not null)
            {
                map[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return map.Count == 0 ? null : map;
    }

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
    private static (string Field, string MessageKey, IReadOnlyDictionary<string, string>? Params)? Probe(
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
                return (field.Code, MessageKeyOf(ex), ParamsOf(ex));
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

        // ⛔ ent6 R1: поле Lookup без цілі приймало запис БУДЬ-ЯКОГО довідника (і з заборонених).
        // Перевіряється ПЕРШИМ — до читання запису, щоб відмова не була оракулом існування id.
        if (field.RefRegistryDefId is null)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Поле «{field.Code}» типу Lookup не має цілі посилання: значення не приймається.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.lookupTargetUnknown",
                    ["field"] = field.Code,
                    ["fieldCode"] = field.Code,
                });
        }

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
            // ⛔ ent6 R2: код ЧУЖОГО запису не віддається (він міг бути із забороненого довідника) —
            // лише його номер, який людина й так ввела.
            var expectedDefinition = await registries
                .FindDefinitionByIdAsync(expected, ct).ConfigureAwait(false);
            var expectedCode = expectedDefinition?.Code ?? expected.ToString(invariant);

            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Поле «{field.Code}» посилається на довідник «{expectedCode}», а запис {target} "
                + "належить іншому довіднику.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0422.lookupWrongRegistry",
                    ["field"] = field.Code,
                    ["value"] = target.ToString(invariant),
                    ["expectedRegistry"] = expectedCode,
                });
        }
    }

    /// <summary>Типізоване значення поля — для порівняння до/після і для аудиту.</summary>
    /// <summary>Значення не заповнене: <c>null</c> або рядок із самих пробілів.</summary>
    private static bool IsBlank(object? raw) => raw is null || raw is string text && string.IsNullOrWhiteSpace(text);

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

    /// <summary>Рядок пакета, уже зіставлений із наявним записом (або без нього).</summary>
    /// <param name="Row">Номер у пакеті, з 1.</param>
    /// <param name="Code">Код запису (для помилки рядка й створення).</param>
    /// <param name="Existing">Наявний запис; <c>null</c> — немає.</param>
    /// <param name="MayCreate">Чи створювати запис, якого немає.</param>
    /// <param name="Values">Значення за кодами полів.</param>
    private sealed record WriteTarget(
        int Row, string Code, RegistryEntry? Existing, bool MayCreate, IReadOnlyDictionary<string, object?> Values)
    {
        /// <summary>Лише створювати: наявний запис — помилка рядка (<see cref="RegistryEntryWriteBatch.CreateOnly"/>).</summary>
        public bool MustCreate { get; init; }

        /// <summary>Назва нового запису; <c>null</c> — код.</summary>
        public string? DisplayName { get; init; }

        /// <summary>Стан запису після оновлення; <c>null</c> — не змінювати.</summary>
        public bool? IsActive { get; init; }

        /// <summary>Вікно дії після запису; <c>null</c> — не змінювати.</summary>
        public ValidityWindow? Validity { get; init; }
    }

    /// <summary>Рядок, що пройшов перевірку значень, — для звірки ключів у межах пакета.</summary>
    /// <param name="Row">Рядок для <see cref="RegistryBatchKeys.DuplicateKeyRows"/>.</param>
    /// <param name="Code">Код запису для помилки рядка.</param>
    /// <param name="Outcome">Як рядок пораховано в результаті.</param>
    private sealed record KeyedTarget(RegistryBatchKeyRow Row, string Code, RowOutcome Outcome);

    private enum RowOutcome
    {
        Added,
        Updated,
        Unchanged,
    }
}
