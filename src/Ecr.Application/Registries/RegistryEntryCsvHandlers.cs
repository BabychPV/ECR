// src/Ecr.Application/Registries/RegistryEntryCsvHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
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
    IClock clock)
{
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

        // ⛔ Наявний запис розв'язується ЗА РЯДКОМ через FindEntryByCodeAsync
        // (той самий метод, яким UpsertRegistryEntryHandler.CreateAsync
        // перевіряє зайнятість коду) — НЕ через ListEntriesAsync. Той читає
        // AsNoTracking (правильно для списків), і об'єкт без відстеження, до
        // якого потім прив'язали б нове значення поля через навігацію
        // RegistryValue.Entry, EF вважає щойно доданим — і на SaveChanges
        // намагається вставити його ЗНОВУ з чужим Id (IDENTITY_INSERT).
        // FindEntryByCodeAsync читає БЕЗ AsNoTracking, тож запис лишається
        // «Unchanged», а не «Added».

        // Один запит на КОЖЕН зустрінутий (довідник-джерело, код) — не на
        // кожен рядок файлу: той самий рядок довідника-джерела в CSV
        // повторюється часто (той самий дозвіл на десятки водних об'єктів).
        var lookupCache = new Dictionary<(int RefRegistryDefId, string Code), long?>();

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

            var entry = await registries.FindEntryByCodeAsync(definition.Id, code, ct).ConfigureAwait(false);
            var isNew = entry is null;

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

            IReadOnlyList<RegistryValueFieldChange> changes;
            try
            {
                // Реюз: та сама перевірка типу, обов'язковості й складу полів,
                // що при ручному редагуванні запису — жодного дубля правила.
                changes = await UpsertRegistryEntryHandler
                    .ApplyValuesAsync(registries, definition, entry, values, ct)
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

            if (isNew)
            {
                added++;
            }
            else if (values.Count == 0)
            {
                // Рядок назвав лише код — жодного поля не передано: наявний
                // запис ніхто не торкнувся.
                unchanged++;
            }
            else
            {
                updated++;
            }
        }

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
            foreach (var (entry, changes) in valueChanges)
            {
                await audit.WriteSecurityEventAsync(
                    new SecurityEventRecord(
                        clock.UtcNow, UpsertRegistryEntryHandler.ValueChangedEventType, TargetUserId: null, TargetRoleId: null,
                        JsonSerializer.Serialize(new
                        {
                            registryDefId = definition.Id,
                            entryId = entry.Id,
                            changes = changes.Select(c => new { field = c.FieldCode, oldValue = c.OldValue, newValue = c.NewValue }),
                        }),
                        userId, currentUser.CorrelationId),
                    innerCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        return new RegistryEntryImportReport(added, updated, unchanged, errors, Applied: true);
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
}
