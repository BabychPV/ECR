// src/Ecr.Application/Registries/RegistryEntryCsvHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
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

/// <summary>Помилка одного рядка імпорту записів довідника.</summary>
/// <param name="Row">Номер рядка у файлі; заголовок — 1.</param>
/// <param name="Key">Код запису, як його записано у файлі.</param>
/// <param name="Field">
/// Поле, якого стосується помилка; <c>null</c> — помилка самого рядка (код,
/// дублікат), а не конкретного поля.
/// </param>
/// <param name="MessageKey">Ключ тексту відмови в каталозі.</param>
public sealed record RegistryEntryImportError(int Row, string Key, string? Field, string MessageKey);

/// <summary>Звіт імпорту записів довідника (`BE-24`, крок 3).</summary>
/// <param name="Added">Нових записів.</param>
/// <param name="Updated">Записів, у яких змінилося хоча б одне поле.</param>
/// <param name="Unchanged">Наявних записів, для яких файл не передав жодного значення поля.</param>
/// <param name="Errors">Відхилені рядки; є хоч один — не застосовано нічого.</param>
/// <param name="Applied">Чи записано зміни.</param>
public sealed record RegistryEntryImportReport(
    int Added, int Updated, int Unchanged, IReadOnlyList<RegistryEntryImportError> Errors, bool Applied);

/// <summary>
/// Імпорт записів довідника з CSV (`BE-24`, крок 3); право <c>Registry.EditData</c>
/// — те саме, що на ручне створення й редагування запису
/// (<see cref="UpsertRegistryEntryHandler"/>), бо імпорт — це той самий запис
/// даних, лише пакетом.
/// </summary>
/// <remarks>
/// ⚠ Все або нічого: помилка хоча б одного рядка — не застосовано жодного
/// (той самий патерн, що <see cref="Localization.UiStringImportHandler"/> для
/// перекладів, BE-13 ч.2). Колонки — коди полів ОПУБЛІКОВАНОГО опису
/// (<see cref="RegistryDef.Fields"/>, не чернетки) плюс <c>code</c> — бізнес-ключ
/// запису.
/// <para>
/// Перевірка типу й обов'язковості полів — ТОЙ САМИЙ код, що й ручний upsert:
/// <see cref="UpsertRegistryEntryHandler.ApplyValuesAsync"/> викликається тут
/// напряму, а не копіюється. Посилання <c>Lookup</c>-поля на запис ІНШОГО
/// довідника резолвиться за бізнес-кодом ТИМ САМИМ методом сховища
/// (<see cref="IRegistryStore.FindEntryByCodeAsync"/>), яким
/// <see cref="UpsertRegistryEntryHandler"/> перевіряє зайнятість коду.
/// </para>
/// </remarks>
public sealed class ImportRegistryEntriesHandler(
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    Keys.RegistryKeyService? keys = null)
{
    // ⚠ `keys` необов'язковий лише для тестів, що будують обробник руками (як в
    // `UpsertRegistryEntryHandler`; храповик запитів B-10 так і міряє довідник без ключів);
    // контейнер підставляє `RegistryKeyService` завжди. Справжній шлях тримає
    // `RegistryKeyLifecycleHttpTests`.

    /// <summary>Ключ помилки рядка: той самий ключ має інший рядок файлу (§4.6).</summary>
    public const string KeyDuplicateInFileKey = "err.ECR-REG-4092.keyDuplicateInFile";

    /// <summary>Ключ помилки рядка: первинний ключ рядка і його код указують на різні записи.</summary>
    public const string KeyCodeMismatchKey = "err.ECR-REG-4092.keyCodeMismatch";

    /// <summary>Стеля розміру файлу, коли конфіг не задає іншої.</summary>
    public const int DefaultMaxBytes = 1024 * 1024;

    /// <summary>Тип події журналу структурних змін.</summary>
    public const string ImportedOperation = "Import";

    /// <summary>Перевіряє файл і, якщо не <paramref name="dryRun"/> і помилок немає, застосовує.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="content">Вміст CSV.</param>
    /// <param name="sizeBytes">Розмір файлу.</param>
    /// <param name="maxBytes">Стеля розміру.</param>
    /// <param name="dryRun">Лише звіт, без запису.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<RegistryEntryImportReport> HandleAsync(
        string registryCode, string content, long sizeBytes, int maxBytes, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        // ⚠ Глобальне право АБО ресурсний грант рівня Write на ЦЕЙ довідник
        // (A7-58) — той самий "OR", що UpsertRegistryEntryHandler: імпорт це
        // той самий запис даних, лише пакетом. Резолвер викликається лише
        // тоді, коли глобального Registry.EditData нема; для нього самого
        // definition нижче все одно читається ще раз — другий запит платить
        // лише користувач без глобального права.
        await RegistryAccess
            .RequireAsync(
                access, currentUser, UpsertRegistryEntryHandler.Permission, GrantLevel.Write,
                async token => (await registries.FindDefinitionAsync(registryCode, token).ConfigureAwait(false))?.Id,
                ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не змінює довідники.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        RequireSize(sizeBytes, maxBytes);

        var definition = await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-REG-0404",
                $"Довідника «{registryCode}» не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = registryCode });

        var records = CsvReader.Parse(content);
        var header = records.Count > 0 ? records[0] : [];
        var codeColumn = IndexOf(header, "code");

        if (codeColumn < 0)
        {
            throw Invalid(
                "err.ECR-REG-0422.entriesCsvHeaderCode",
                $"Заголовок CSV не має колонки code (довідник «{registryCode}»).",
                registryCode);
        }

        // Колонки — лише поля ОПУБЛІКОВАНОГО опису: definition.Fields читає
        // саме його, чернетка (RegistryDefinitionDraft) — окрема сутність.
        var fieldsByCode = definition.Fields.ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);
        var columns = new List<(int Index, RegistryFieldDef Field)>();

        for (var i = 0; i < header.Count; i++)
        {
            if (i == codeColumn)
            {
                continue;
            }

            var name = header[i].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (!fieldsByCode.TryGetValue(name, out var field))
            {
                // Невідома колонка валить УВЕСЬ файл одразу — не рядок: клієнт
                // або переплутав довідник, або читає застарілий опис.
                throw new BusinessRuleException(
                    "ECR-REG-0422",
                    $"Довідник «{registryCode}» не має поля «{name}»: колонка невідома.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-REG-0422.entriesCsvUnknownColumn",
                        ["registryCode"] = registryCode,
                        ["column"] = name,
                    });
            }

            columns.Add((i, field));
        }

        // ⛔ Наявний запис розв'язується через FindEntriesByCodesAsync — З
        // відстеженням, як FindEntryByCodeAsync (яким UpsertRegistryEntryHandler.
        // CreateAsync перевіряє зайнятість коду), — НЕ через ListEntriesAsync.
        // Той читає AsNoTracking (правильно для списків), і об'єкт без
        // відстеження, до якого потім прив'язали б нове значення поля через
        // навігацію RegistryValue.Entry, EF вважає щойно доданим — і на
        // SaveChanges намагається вставити його ЗНОВУ з чужим Id
        // (IDENTITY_INSERT). Тут запис лишається «Unchanged», а не «Added».
        //
        // ⛔ B-10: усе, що рядок читав із бази поштучно, читається ПАКЕТОМ до
        // циклу — наявні записи за кодами, їхні значення, записи-цілі Lookup.
        // Доти 80 рядків із двома Lookup-полями давали 245 SELECT TOP(1) з
        // dic.RegistryEntry і 80 ListValues; тепер набір запитів сталий.
        var prefetched = await PrefetchAsync(definition.Id, records, codeColumn, columns, ct).ConfigureAwait(false);
        var lookupCache = prefetched.LookupCache;

        // ⛔ RT-10b (§4.6): складений ключ довідника. Первинний ключ знаходить наявний запис
        // рядка раніше за код; дубль ключа у файлі — помилка обох рядків; вільність ключа
        // проти бази — у транзакції запису, тим самим сервісом, що й ручний upsert.
        IReadOnlyList<RegistryKeyDef> keyDefs = keys is null
            ? []
            : await keys.ListActiveKeysAsync(definition.Id, ct).ConfigureAwait(false);
        var keyMatch = await MatchPrimaryKeyAsync(definition, keyDefs, records, codeColumn, columns, prefetched, ct)
            .ConfigureAwait(false);
        var keyed = new List<KeyedRow>();
        var seenEntries = new Dictionary<RegistryEntry, (int Row, string Code, bool ByKey, RowOutcome Outcome)>(
            ReferenceEqualityComparer.Instance);
        var flaggedRows = new HashSet<int>();

        var errors = new List<RegistryEntryImportError>();
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var (added, updated, unchanged) = (0, 0, 0);

        // ⛔ Прогалина, яку закриває ця правка: на відміну від ручного
        // редагування (UpsertRegistryEntryHandler.HandleAsync), імпорт CSV
        // досі не писав жодного детального сліду зміни поля — лише сумарну
        // aud.StructureChange на весь файл. Тут накопичуємо ЗАПИС (посилання,
        // не Id — Id нового запису відомий лише після SaveChangesAsync, той
        // самий порядок, що в ручному шляху) разом зі списком фактичних змін
        // його полів; порожні (changes.Count == 0) — код без значень або
        // повторний імпорт тим самим значенням — до акумулятора не йдуть.
        var valueChanges = new List<(RegistryEntry Entry, IReadOnlyList<RegistryValueFieldChange> Changes)>();

        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            if (record.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var rowNumber = i + 1;
            var code = Cell(record, codeColumn).Trim();

            if (code.Length == 0)
            {
                errors.Add(new RegistryEntryImportError(rowNumber, code, null, "err.ECR-REG-0422.entryCodeRequired"));
                continue;
            }

            if (!seenCodes.Add(code))
            {
                errors.Add(new RegistryEntryImportError(rowNumber, code, null, "err.ECR-REG-0422.entryCodeDuplicateInFile"));
                continue;
            }

            if (!EcrCode.TryCreate(code, out var ecrCode))
            {
                errors.Add(new RegistryEntryImportError(rowNumber, code, null, "err.ECR-CFG-0422.invalidCode"));
                continue;
            }

            var byCode = prefetched.EntriesByCode.GetValueOrDefault(code);
            var byKey = keyMatch.EntriesByRow.GetValueOrDefault(i);

            // ⚠ Код називає один наявний запис, первинний ключ — інший: «оновити за ключем» тихо
            // змінило б не той запис, про який думала людина, а «за кодом» — дало б йому ключ,
            // який уже тримає сусід. Обидва гірші за помилку рядка.
            if (byKey is not null && byCode is not null && !ReferenceEquals(byKey, byCode))
            {
                errors.Add(new RegistryEntryImportError(rowNumber, code, keyMatch.PrimaryFields, KeyCodeMismatchKey));
                continue;
            }

            var entry = byKey ?? byCode;
            var isNew = entry is null;

            // Два рядки файлу дійшли до одного запису (один за ключем, інший за кодом): застосувати
            // обидва — означало б мовчки лишити переможцем останній.
            if (entry is not null && seenEntries.TryGetValue(entry, out var first) && (first.ByKey || byKey is not null))
            {
                if (flaggedRows.Add(first.Row))
                {
                    errors.Add(new RegistryEntryImportError(first.Row, first.Code, keyMatch.PrimaryFields, KeyDuplicateInFileKey));
                    (updated, unchanged) = first.Outcome == RowOutcome.Unchanged
                        ? (updated, unchanged - 1)
                        : (updated - 1, unchanged);
                }

                flaggedRows.Add(rowNumber);
                errors.Add(new RegistryEntryImportError(rowNumber, code, keyMatch.PrimaryFields, KeyDuplicateInFileKey));
                continue;
            }

            var (values, refField, refErrorKey) = await ResolveRowAsync(record, columns, lookupCache, ct)
                .ConfigureAwait(false);

            if (refErrorKey is not null)
            {
                errors.Add(new RegistryEntryImportError(rowNumber, code, refField, refErrorKey));
                continue;
            }

            entry ??= new RegistryEntry(
                definition.Id,
                ecrCode,
                new LocalizedText(new Dictionary<string, string> { [UiStringResolver.DefaultLanguage] = code }),
                userId,
                clock.UtcNow);

            // ⚠ Порядок як в UpsertRegistryEntryHandler.CreateAsync: спершу
            // Add, лише потім ApplyValuesAsync. Навпаки — EF довантажує запис
            // у чергу вставки каскадом через навігацію RegistryValue.Entry
            // (сам RegistryValue це й документує), і подвійне додавання дало
            // `IDENTITY_INSERT`: другий Add() ішов уже з клієнтським Id,
            // залишеним від першого проходу.
            if (isNew)
            {
                registries.Add(entry);
            }

            // ⚠ Запис, знайдений за ключем, міг не потрапити в пакет «за кодами» — його значення
            // прочитав пошук за ключем. Порожній список тут означав би «полів немає», і кожне
            // значення вставилося б удруге.
            var existingValues = isNew
                ? null
                : prefetched.ValuesByEntry.GetValueOrDefault(entry.Id)
                  ?? keyMatch.ValuesByEntry.GetValueOrDefault(entry.Id)
                  ?? [];

            IReadOnlyList<RegistryValueFieldChange> changes;
            try
            {
                // Реюз: та сама перевірка типу, обов'язковості й складу полів,
                // що при ручному редагуванні запису — жодного дубля правила.
                var prefetch = new RegistryValuesPrefetch(existingValues, prefetched.LookupTargets);

                changes = await UpsertRegistryEntryHandler
                    .ApplyValuesAsync(registries, definition, entry, values, prefetch, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DomainException or BusinessRuleException)
            {
                errors.Add(new RegistryEntryImportError(rowNumber, code, FieldOf(ex), MessageKeyOf(ex)));
                continue;
            }

            if (changes.Count > 0)
            {
                valueChanges.Add((entry, changes));
            }

            var outcome = isNew ? RowOutcome.Added : values.Count == 0 ? RowOutcome.Unchanged : RowOutcome.Updated;
            switch (outcome)
            {
                case RowOutcome.Added:
                    added++;
                    break;

                // Рядок назвав лише код — жодного поля не передано: наявний
                // запис ніхто не торкнувся.
                case RowOutcome.Unchanged:
                    unchanged++;
                    break;

                default:
                    updated++;
                    break;
            }

            // Лише рядок, що пройшов: відхилений рядок запису не торкнувся і сусіда не блокує.
            if (!isNew)
            {
                seenEntries.TryAdd(entry, (rowNumber, code, byKey is not null, outcome));
            }

            if (keyDefs.Count > 0)
            {
                keyed.Add(new KeyedRow(
                    rowNumber, code, entry, EffectiveValues(definition, existingValues ?? [], values), outcome));
            }
        }

        // ⛔ RT-10b (§4.6): дубль ключа у файлі — помилка ОБОХ рядків, щоб звіт показав і той,
        // що «переміг би» за порядком. Порівнюються значення ПІСЛЯ застосування рядка, тож
        // рядок, що змінює ключ наявного запису, звіряється з новим ключем, а не зі старим.
        foreach (var row in DuplicateKeyRows(definition, keyDefs, keyed))
        {
            if (!flaggedRows.Add(row.Row.Number))
            {
                continue;
            }

            errors.Add(new RegistryEntryImportError(row.Row.Number, row.Row.Code, row.Fields, KeyDuplicateInFileKey));
            switch (row.Row.Outcome)
            {
                case RowOutcome.Added:
                    added--;
                    break;
                case RowOutcome.Unchanged:
                    unchanged--;
                    break;
                default:
                    updated--;
                    break;
            }
        }

        // Помилки ключа додано після циклу — звіт лишається впорядкованим за рядками (OrderBy
        // стабільний: помилки одного рядка зберігають порядок).
        var ordered = errors.OrderBy(e => e.Row).ToList();
        errors.Clear();
        errors.AddRange(ordered);

        if (dryRun || errors.Count > 0 || added + updated == 0)
        {
            return new RegistryEntryImportReport(added, updated, unchanged, errors, Applied: false);
        }

        definition.BumpDataRevision();

        // ⛔ Аудит пише СИРИМ SQL поза відстеженням EF (`Q-244`, той самий
        // урок, що SwitchRegistrySourceHandler): без явної транзакції збій між
        // журналом і SaveChangesAsync лишив би їх у різних станах.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ RT-10b: ключі — тим самим сервісом, що й ручний upsert, у тій самій транзакції,
            // що й записи. Ключ, який тримає запис поза файлом, — 409 на весь файл (не помилка
            // рядка): це перевірка під блокуванням, і в dryRun її немає.
            if (keys is not null && keyed.Count > 0)
            {
                await keys.ApplyAsync(definition, keyDefs, [.. keyed.Select(r => r.Entry)], innerCt)
                    .ConfigureAwait(false);
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "dic.RegistryEntry",

                    // Імпорт — операція над НАБОРОМ рядків, а не над одним
                    // записом; єдиного ідентифікатора немає (той самий вибір,
                    // що в SwitchRegistrySourceHandler).
                    EntityId: 0,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: ImportedOperation,
                    OldJson: null,
                    NewJson: JsonSerializer.Serialize(new { registryCode, added, updated }),
                    ChangeReason: $"Імпорт CSV довідника «{registryCode}»: додано {added}, оновлено {updated}.",
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            // Per-row слід — ПІСЛЯ SaveChangesAsync: Id щойно доданих записів
            // EF підставляє лише тепер (той самий порядок, що
            // UpsertRegistryEntryHandler.HandleAsync). Формат DetailsJson —
            // буквально той самий, що там: registryDefId/entryId/changes,
            // щоб один і той самий запис читав обидва шляхи однаково.
            //
            // ⛔ Один пакетний виклик, а не цикл поштучних await — та сама
            // логіка, що B-10 (ca63ed56) уже застосував до ЧИТАННЯ в цьому ж
            // імпорті: N окремих round-trip на N змінених записів довідника
            // не масштабується для великого CSV.
            if (valueChanges.Count > 0)
            {
                var securityEvents = valueChanges
                    .Select(vc => new SecurityEventRecord(
                        clock.UtcNow, UpsertRegistryEntryHandler.ValueChangedEventType, TargetUserId: null, TargetRoleId: null,
                        JsonSerializer.Serialize(new
                        {
                            registryDefId = definition.Id,
                            entryId = vc.Entry.Id,
                            changes = vc.Changes.Select(c => new { field = c.FieldCode, oldValue = c.OldValue, newValue = c.NewValue }),
                        }),
                        userId, currentUser.CorrelationId))
                    .ToList();

                await audit.WriteSecurityEventsAsync(securityEvents, innerCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        return new RegistryEntryImportReport(added, updated, unchanged, errors, Applied: true);
    }

    /// <summary>
    /// Читає пакетом усе, що цикл рядків інакше читав би поштучно (<c>B-10</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Коди збираються за ТИМИ САМИМИ правилами, що в циклі (обрізка,
    /// <see cref="CsvReader.UnescapeFormula"/>, порожнє пропускається), —
    /// але без відсіву помилкових рядків: зайвий код у запиті нічого не
    /// ламає, а пропущений означав би «запису немає». Порівняння кодів —
    /// регістронезалежне, як колація бази, якою відповідав
    /// <see cref="IRegistryStore.FindEntryByCodeAsync"/>.
    /// </remarks>
    private async Task<ImportPrefetch> PrefetchAsync(
        int registryDefId,
        IReadOnlyList<IReadOnlyList<string>> records,
        int codeColumn,
        List<(int Index, RegistryFieldDef Field)> columns,
        CancellationToken ct)
    {
        var ownCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refCodes = new Dictionary<int, HashSet<string>>();

        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            var code = Cell(record, codeColumn).Trim();
            if (code.Length > 0)
            {
                ownCodes.Add(code);
            }

            foreach (var (idx, field) in columns)
            {
                if (field.DataType != CellDataType.Lookup || field.RefRegistryDefId is not { } refDefId)
                {
                    continue;
                }

                var raw = CsvReader.UnescapeFormula(Cell(record, idx)).Trim();
                if (raw.Length == 0)
                {
                    continue;
                }

                if (!refCodes.TryGetValue(refDefId, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    refCodes[refDefId] = set;
                }

                set.Add(raw);
            }
        }

        var entriesByCode = (await registries.FindEntriesByCodesAsync(registryDefId, ownCodes, ct).ConfigureAwait(false))
            .ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);

        var lookupCache = new Dictionary<(int RefRegistryDefId, string Code), long?>();
        var lookupTargets = new Dictionary<long, RegistryEntry>();

        foreach (var (refDefId, codes) in refCodes)
        {
            var found = (await registries.FindEntriesByCodesAsync(refDefId, codes, ct).ConfigureAwait(false))
                .ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);

            foreach (var code in codes)
            {
                var target = found.GetValueOrDefault(code);
                lookupCache[(refDefId, code)] = target?.Id;
                if (target is not null)
                {
                    lookupTargets[target.Id] = target;
                }
            }
        }

        var valuesByEntry = entriesByCode.Count == 0
            ? new Dictionary<long, IReadOnlyList<RegistryValue>>()
            : (await registries
                    .ListValuesForEntriesAsync([.. entriesByCode.Values.Select(e => e.Id)], ct)
                    .ConfigureAwait(false))
                .GroupBy(v => v.RegistryEntryId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<RegistryValue>)[.. g]);

        return new ImportPrefetch(entriesByCode, valuesByEntry, lookupCache, lookupTargets);
    }

    /// <summary>
    /// Значення полів рядка: <c>Lookup</c> резолвиться в Id запису-джерела за
    /// бізнес-кодом, решта перевіряється ПРОБНИМ викликом
    /// <see cref="RegistryValue.Set"/> — тим самим методом, який реально
    /// застосує значення далі, у <see cref="UpsertRegistryEntryHandler.ApplyValuesAsync"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Проба потрібна САМЕ заради номера поля в звіті. Спільний метод
    /// upsert перевіряє ВСІ поля разом і кидає ОДИН виняток на перше, що не
    /// підійшло, без імені поля в подробиці (<c>RegistryValue.Set</c> знає
    /// лише тип, не код поля) — рядковий звіт CSV без <c>field</c> був би
    /// значно біднішим. Проба працює на одноразовому об'єкті, який ніколи не
    /// додається в контекст (<see cref="RegistryValue(long, int)"/> із
    /// <c>registryEntryId = 0</c>), тож жодного побічного ефекту не лишає;
    /// значення, що пройшло пробу, детерміновано пройде й реальний виклик.
    /// </remarks>
    private async Task<(Dictionary<string, object?> Values, string? Field, string? ErrorKey)> ResolveRowAsync(
        IReadOnlyList<string> record,
        List<(int Index, RegistryFieldDef Field)> columns,
        Dictionary<(int RefRegistryDefId, string Code), long?> lookupCache,
        CancellationToken ct)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (idx, field) in columns)
        {
            var raw = CsvReader.UnescapeFormula(Cell(record, idx));
            if (field.DataType != CellDataType.Lookup)
            {
                if (raw.Length == 0)
                {
                    continue;
                }

                try
                {
                    new RegistryValue(0L, field.Id).Set(field.DataType, raw, field.UnitId);
                }
                catch (DomainException ex)
                {
                    return (values, field.Code, MessageKeyOf(ex));
                }

                values[field.Code] = raw;
                continue;
            }

            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (field.RefRegistryDefId is not { } refDefId)
            {
                return (values, field.Code, "err.ECR-REG-0422.fieldTypeNotAllowed");
            }

            var cacheKey = (refDefId, trimmed);
            if (!lookupCache.TryGetValue(cacheKey, out var resolvedId))
            {
                var refEntry = await registries.FindEntryByCodeAsync(refDefId, trimmed, ct).ConfigureAwait(false);
                resolvedId = refEntry?.Id;
                lookupCache[cacheKey] = resolvedId;
            }

            if (resolvedId is null)
            {
                return (values, field.Code, "err.ECR-REG-0422.entryRefNotFound");
            }

            values[field.Code] = resolvedId.Value;
        }

        return (values, null, null);
    }

    /// <summary>
    /// Наявний запис рядка за ПЕРВИННИМ ключем (§4.6): хеш значень полів ключа з рядка → живий
    /// тримач цього хешу. Пакетом — один запит на тримачів і, для тих, кого не прочитав пакет
    /// «за кодами», ще один на записи й один на їхні значення.
    /// </summary>
    /// <remarks>
    /// ⚠ Ключ не шукається, якщо у файлі немає стовпця хоч одного поля первинного ключа: такий
    /// рядок не змінює ключа, і наявний запис знаходить код, як досі.
    ///
    /// ⚠ Темпоральний довідник може мати кілька живих тримачів того самого ключа (вікна не
    /// перетинаються). Тоді береться той, чий код збігається з кодом рядка; немає такого і
    /// тримачів більше одного — рядок лишається пошуку за кодом: вгадувати вікно нема з чого.
    /// </remarks>
    private async Task<KeyMatch> MatchPrimaryKeyAsync(
        RegistryDef definition,
        IReadOnlyList<RegistryKeyDef> keyDefs,
        IReadOnlyList<IReadOnlyList<string>> records,
        int codeColumn,
        List<(int Index, RegistryFieldDef Field)> columns,
        ImportPrefetch prefetched,
        CancellationToken ct)
    {
        var primary = keyDefs.FirstOrDefault(k => k.IsPrimary);
        var present = columns.Select(c => c.Field.Id).ToHashSet();
        if (keys is null || primary is null || !primary.Fields.All(f => present.Contains(f.RegistryFieldDefId)))
        {
            return KeyMatch.None;
        }

        var fieldCodes = definition.Fields.ToDictionary(f => f.Id, f => f.Code);
        var primaryFields = string.Join(
            ", ", primary.Fields.OrderBy(f => f.Ordinal).Select(f => fieldCodes[f.RegistryFieldDefId]));

        var rowHashes = new Dictionary<int, byte[]>();
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            if (record.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            // Рядок із помилкою значення тут пропускається: основний цикл і так його відхилить.
            var (values, _, errorKey) = await ResolveRowAsync(record, columns, prefetched.LookupCache, ct)
                .ConfigureAwait(false);
            if (errorKey is not null)
            {
                continue;
            }

            if (RegistryKeyService.HashOf(definition, primary, EffectiveValues(definition, [], values)) is { } hash)
            {
                rowHashes[i] = hash;
            }
        }

        var holders = (await keys
                .FindHoldersAsync(primary.Id, [.. rowHashes.Values.DistinctBy(Convert.ToHexString)], ct)
                .ConfigureAwait(false))
            .GroupBy(h => Convert.ToHexString(h.KeyHash!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var codeByRow = new Dictionary<int, string>();
        foreach (var (row, hash) in rowHashes)
        {
            if (!holders.TryGetValue(Convert.ToHexString(hash), out var candidates))
            {
                continue;
            }

            var code = Cell(records[row], codeColumn).Trim();
            var holder = candidates.FirstOrDefault(h => string.Equals(h.EntryCode, code, StringComparison.OrdinalIgnoreCase))
                ?? (candidates.Count == 1 ? candidates[0] : null);
            if (holder is not null)
            {
                codeByRow[row] = holder.EntryCode;
            }
        }

        var missing = codeByRow.Values
            .Where(c => !prefetched.EntriesByCode.ContainsKey(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<RegistryEntry> extra = missing.Count == 0
            ? []
            : await registries.FindEntriesByCodesAsync(definition.Id, missing, ct).ConfigureAwait(false);
        IReadOnlyList<RegistryValue> extraValues = extra.Count == 0
            ? []
            : await registries.ListValuesForEntriesAsync([.. extra.Select(e => e.Id)], ct).ConfigureAwait(false);

        var entriesByCode = new Dictionary<string, RegistryEntry>(prefetched.EntriesByCode, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in extra)
        {
            entriesByCode[entry.Code] = entry;
        }

        return new KeyMatch(
            codeByRow
                .Where(c => entriesByCode.ContainsKey(c.Value))
                .ToDictionary(c => c.Key, c => entriesByCode[c.Value]),
            extraValues
                .GroupBy(v => v.RegistryEntryId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<RegistryValue>)[.. g]),
            primaryFields);
    }

    /// <summary>
    /// Значення полів запису такими, якими їх залишить рядок: наявні, поверх них — передані у
    /// файлі. Для обчислення ключа в пам'яті, без звернення до бази.
    /// </summary>
    /// <remarks>
    /// ⚠ Значення з файлу — пробні об'єкти, як у <see cref="ResolveRowAsync"/>: у контекст вони не
    /// додаються. Порожня клітинка поля не змінює — як і в
    /// <see cref="UpsertRegistryEntryHandler.ApplyValuesAsync"/>, куди вона не потрапляє.
    /// </remarks>
    private static Dictionary<int, RegistryValue> EffectiveValues(
        RegistryDef definition, IReadOnlyList<RegistryValue> existing, IReadOnlyDictionary<string, object?> values)
    {
        var result = existing.ToDictionary(v => v.RegistryFieldDefId);
        var fields = definition.Fields.ToDictionary(f => f.Code, StringComparer.Ordinal);

        foreach (var (code, raw) in values)
        {
            if (fields.TryGetValue(code, out var field))
            {
                var probe = new RegistryValue(0L, field.Id);
                probe.Set(field.DataType, raw, field.UnitId);
                result[field.Id] = probe;
            }
        }

        return result;
    }

    /// <summary>
    /// Рядки, чий ключ має ще хоч один рядок файлу (для темпорального довідника — у вікні, що
    /// перетинається, та сама умова, що <see cref="RegistryKeyService.Overlaps"/>).
    /// </summary>
    /// <returns>Кожен такий рядок — з полями першого ключа, що збігся.</returns>
    private static List<(KeyedRow Row, string Fields)> DuplicateKeyRows(
        RegistryDef definition, IReadOnlyList<RegistryKeyDef> keyDefs, IReadOnlyList<KeyedRow> rows)
    {
        var fieldCodes = definition.Fields.ToDictionary(f => f.Id, f => f.Code);
        var found = new SortedDictionary<int, (KeyedRow Row, string Fields)>();

        foreach (var key in keyDefs)
        {
            var fields = string.Join(
                ", ", key.Fields.OrderBy(f => f.Ordinal).Select(f => fieldCodes[f.RegistryFieldDefId]));

            var groups = rows
                .Select(r => (Row: r, Hash: RegistryKeyService.HashOf(definition, key, r.Values)))
                .Where(x => x.Hash is not null)
                .GroupBy(x => Convert.ToHexString(x.Hash!), StringComparer.Ordinal)
                .Where(g => g.Skip(1).Any());

            foreach (var group in groups)
            {
                var members = group.Select(x => x.Row).ToList();
                for (var a = 0; a < members.Count; a++)
                {
                    for (var b = a + 1; b < members.Count; b++)
                    {
                        if (definition.IsTemporal
                            && !RegistryKeyService.Overlaps(members[a].Entry.Window, members[b].Entry.Window))
                        {
                            continue;
                        }

                        found.TryAdd(members[a].Number, (members[a], fields));
                        found.TryAdd(members[b].Number, (members[b], fields));
                    }
                }
            }
        }

        return [.. found.Values];
    }

    private static void RequireSize(long length, int maxBytes)
    {
        if (length > maxBytes)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Файл {length} байт, стеля {maxBytes}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.registryEntriesCsvTooLarge",
                    ["size"] = length,
                    ["max"] = maxBytes,
                });
        }
    }

    private static BusinessRuleException Invalid(string messageKey, string message, string registryCode)
        => new(
            "ECR-REG-0422",
            message,
            new Dictionary<string, object?> { ["messageKey"] = messageKey, ["registryCode"] = registryCode });

    /// <summary>Ключ тексту з винятку валідації; типова фраза домену — запасний варіант.</summary>
    private static string MessageKeyOf(Exception ex) => Details(ex)?.GetValueOrDefault("messageKey") as string
        ?? "err.ECR-REG-0422.entryImportRowFailed";

    /// <summary>Поле, назване в подробиці винятку (<c>fieldCode</c> одиночного поля, <c>fields</c> — перелік).</summary>
    private static string? FieldOf(Exception ex)
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

    private static int IndexOf(IReadOnlyList<string> header, string name)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Cell(IReadOnlyList<string> record, int index)
        => index < record.Count ? record[index] : string.Empty;

    /// <summary>Прочитане пакетом до циклу рядків.</summary>
    /// <param name="EntriesByCode">Наявні записи довідника за кодом.</param>
    /// <param name="ValuesByEntry">Значення полів наявних записів.</param>
    /// <param name="LookupCache">Код посилання → Id запису-цілі (<c>null</c> — немає).</param>
    /// <param name="LookupTargets">Записи-цілі посилань за Id.</param>
    private sealed record ImportPrefetch(
        IReadOnlyDictionary<string, RegistryEntry> EntriesByCode,
        IReadOnlyDictionary<long, IReadOnlyList<RegistryValue>> ValuesByEntry,
        Dictionary<(int RefRegistryDefId, string Code), long?> LookupCache,
        IReadOnlyDictionary<long, RegistryEntry> LookupTargets);

    /// <summary>Наявні записи, знайдені за первинним ключем (RT-10b).</summary>
    /// <param name="EntriesByRow">Індекс рядка у файлі → запис.</param>
    /// <param name="ValuesByEntry">Значення тих із них, кого не прочитав пакет «за кодами».</param>
    /// <param name="PrimaryFields">Поля первинного ключа — для поля <c>field</c> помилки рядка.</param>
    private sealed record KeyMatch(
        IReadOnlyDictionary<int, RegistryEntry> EntriesByRow,
        IReadOnlyDictionary<long, IReadOnlyList<RegistryValue>> ValuesByEntry,
        string? PrimaryFields)
    {
        public static KeyMatch None { get; } = new(
            new Dictionary<int, RegistryEntry>(), new Dictionary<long, IReadOnlyList<RegistryValue>>(), null);
    }

    /// <summary>Рядок, що пройшов, — для звірки ключів у межах файлу.</summary>
    /// <param name="Number">Номер рядка у файлі.</param>
    /// <param name="Code">Код, як його записано у файлі.</param>
    /// <param name="Entry">Запис рядка.</param>
    /// <param name="Values">Значення полів після застосування рядка.</param>
    /// <param name="Outcome">Як рядок пораховано у звіті.</param>
    private sealed record KeyedRow(
        int Number, string Code, RegistryEntry Entry, IReadOnlyDictionary<int, RegistryValue> Values, RowOutcome Outcome);

    private enum RowOutcome
    {
        Added,
        Updated,
        Unchanged,
    }
}

/// <summary>
/// Прочитане пакетом для <see cref="UpsertRegistryEntryHandler.ApplyValuesAsync"/>
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
