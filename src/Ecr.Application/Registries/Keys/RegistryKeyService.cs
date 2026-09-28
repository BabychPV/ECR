// src/Ecr.Application/Registries/Keys/RegistryKeyService.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries.Keys;

/// <summary>
/// Складений ключ довідника при записі (RT-10a, FEATURE-REGISTRY-TABLES §4.3, <c>D-151…D-153</c>):
/// значення полів ключа → канонічний рядок → хеш → рядок <c>dic.RegistryEntryKey</c>, з
/// перевіркою під <c>UPDLOCK, HOLDLOCK</c>, що той самий ключ не тримає інший живий запис.
/// </summary>
/// <remarks>
/// ⛔ ЄДИНИЙ, хто пише <c>dic.RegistryEntryKey</c>. Точка виклику — одна:
/// <see cref="SaveAsync"/> замінює <c>uow.SaveChangesAsync</c> шляху запису після
/// <c>ApplyValuesAsync</c>. Так спільний writer записів (крок S6) переносить виклик одним рядком.
///
/// ⚠ Довідник без активних ключів зберігається як досі — без явної транзакції: блокувати нема
/// чого, а поведінка всіх наявних довідників лишається побітно тією самою.
///
/// ⚠ Правила частин (<c>D-153</c>): частина первинного ключа порожньою не буває — її поле
/// обов'язкове, це перевіряє <c>ApplyValuesAsync</c>; рядок альтернативного ключа з порожньою
/// частиною в перевірку не входить (як <c>UNIQUE</c> з різними <c>NULL</c>), і наявний рядок
/// такого ключа виводиться з унікальності (<see cref="RegistryEntryKey.Retire"/>).
///
/// ⚠ Гонку, яку не закрило блокування, ловить <c>UX_RegistryEntryKey_Live</c>, а
/// <c>UnitOfWork.TryMapDuplicateKey</c> перетворює її на 409 <c>keyTakenConcurrently</c> (RT-10b).
///
/// Точки виклику (RT-10b): upsert — <see cref="SaveAsync"/>; зміна вікна чинності —
/// <see cref="ApplyAsync(RegistryDef, RegistryEntry, CancellationToken)"/>; імпорт CSV — пакетний
/// <see cref="ApplyAsync(RegistryDef, IReadOnlyList{RegistryKeyDef}, IReadOnlyCollection{RegistryEntry}, CancellationToken)"/>;
/// видалення — <see cref="ReleaseAsync"/>.
/// </remarks>
public sealed class RegistryKeyService(IRegistryKeyStore store, IUnitOfWork uow)
{
    /// <summary>Роздільник частин у людському вигляді ключа (<c>KeyText</c>).</summary>
    public const string KeyTextSeparator = " · ";

    /// <summary>Активні ключі довідника з полями.</summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryKeyDef>> ListActiveKeysAsync(int registryDefId, CancellationToken ct)
        => store.ListActiveKeysAsync(registryDefId, ct);

    /// <summary>Живі тримачі будь-якого з хешів ключа — пакетом, без блокування (пошук, не перевірка).</summary>
    /// <param name="registryKeyDefId">Ключ довідника.</param>
    /// <param name="keyHashes">Хеші.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<RegistryKeyHolder>> FindHoldersAsync(
        int registryKeyDefId, IReadOnlyCollection<byte[]> keyHashes, CancellationToken ct)
        => keyHashes.Count == 0
            ? []
            : await store.FindLiveHoldersAsync(registryKeyDefId, keyHashes, ct).ConfigureAwait(false);

    /// <summary>
    /// Хеш ключа за значеннями полів; <c>null</c>, якщо хоч одна частина порожня (така частина
    /// в унікальність не входить, <c>D-153</c>).
    /// </summary>
    /// <param name="definition">Опис довідника (з полями).</param>
    /// <param name="key">Ключ.</param>
    /// <param name="valuesByFieldId">Значення за <c>RegistryFieldDefId</c>.</param>
    public static byte[]? HashOf(
        RegistryDef definition, RegistryKeyDef key, IReadOnlyDictionary<int, RegistryValue> valuesByFieldId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(valuesByFieldId);

        return Compute(definition, key, definition.Fields.ToDictionary(f => f.Id), valuesByFieldId).Hash;
    }

    /// <summary>
    /// Перераховує й перевіряє ключі одного запису БЕЗ збереження — всередині вже відкритої
    /// транзакції виклику (зміна вікна чинності, §4.4: нове вікно може перетнутися з дублем).
    /// </summary>
    /// <param name="definition">Опис довідника (з полями).</param>
    /// <param name="entry">Запис.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-4092</c>, як у <see cref="SaveAsync"/>.</exception>
    public async Task ApplyAsync(RegistryDef definition, RegistryEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(entry);

        var keys = await store.ListActiveKeysAsync(definition.Id, ct).ConfigureAwait(false);
        if (keys.Count > 0)
        {
            await ApplyAsync(definition, keys, [entry], ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Перераховує й перевіряє ключі пакета записів БЕЗ збереження — всередині вже відкритої
    /// транзакції виклику (імпорт CSV).
    /// </summary>
    /// <remarks>
    /// ⚠ Записи пакета один з одним тут не звіряються — це робить виклик ДО транзакції (дубль
    /// у файлі, §4.6), бо відповідь на нього — помилка рядка, а не 409. Тому й тримачі, що
    /// самі входять у пакет, у перевірку проти бази не йдуть (§4.3, крок 4: <c>NOT IN (@пакет)</c>):
    /// їхні ключі цей самий виклик щойно переписує.
    /// </remarks>
    /// <param name="definition">Опис довідника (з полями).</param>
    /// <param name="keys">Активні ключі довідника (<see cref="ListActiveKeysAsync"/>).</param>
    /// <param name="entries">Записи, значення яких уже застосовано.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-4092</c>, як у <see cref="SaveAsync"/>.</exception>
    public async Task ApplyAsync(
        RegistryDef definition,
        IReadOnlyList<RegistryKeyDef> keys,
        IReadOnlyCollection<RegistryEntry> entries,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(entries);

        if (keys.Count == 0)
        {
            return;
        }

        var batch = entries.Where(e => e.IsPersisted).Select(e => e.Id).ToHashSet();
        var fields = definition.Fields.ToDictionary(f => f.Id);

        // ⛔ Аудит P9: читання — пакетом на весь набір, не по 3 + K звернення на запис. Значення й
        // рядки ключів — одним читанням, коди цілей — одним, блокування тримачів — одним запитом
        // на ключ (порціями). Рішення по кожному запису — далі тим самим циклом і в тому самому
        // порядку, що й до пакетного читання: перший конфлікт той самий.
        await store.PreloadAsync(entries, ct).ConfigureAwait(false);
        try
        {
            var plans = new List<EntryPlan>(entries.Count);
            foreach (var entry in entries)
            {
                var values = (await store.ListCurrentValuesAsync(entry, ct).ConfigureAwait(false))
                    .ToDictionary(v => v.RegistryFieldDefId);
                var rows = entry.IsPersisted
                    ? (await store.ListEntryKeysAsync(entry.Id, ct).ConfigureAwait(false)).ToDictionary(k => k.RegistryKeyDefId)
                    : [];

                plans.Add(new EntryPlan(entry, rows, [.. keys.Select(key => Compute(definition, key, fields, values))]));
            }

            var names = await ReferenceNamesAsync([.. plans.SelectMany(p => p.Keys)], ct).ConfigureAwait(false);
            await LockHoldersAsync(keys, plans, ct).ConfigureAwait(false);

            foreach (var plan in plans)
            {
                await ApplyAsync(definition, plan, names, batch, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            store.ForgetPreloaded();
        }
    }

    /// <summary>
    /// Виводить усі рядки ключів видаленого запису з унікальності (<c>IsLive = 0</c>): видалений
    /// запис ключ не тримає (§3.2, <c>WHERE IsLive = 1</c>). Без збереження.
    /// </summary>
    /// <remarks>
    /// ⚠ Усі рядки, а не лише активних ключів: вимкнений ключ теж лишив би живий рядок, і
    /// повторне ввімкнення ключа побачило б видалений запис тримачем.
    /// </remarks>
    /// <param name="entry">Запис, щойно видалений логічно.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task ReleaseAsync(RegistryEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!entry.IsPersisted)
        {
            return;
        }

        foreach (var row in await store.ListEntryKeysAsync(entry.Id, ct).ConfigureAwait(false))
        {
            row.Retire();
        }
    }

    /// <summary>
    /// Перераховує ключі запису, перевіряє їх і зберігає одиницю роботи — в одній транзакції,
    /// якщо в довідника є активні ключі.
    /// </summary>
    /// <param name="definition">Опис довідника (з полями).</param>
    /// <param name="entry">Запис, значення якого вже застосовано.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-REG-4092</c>: інший живий запис тримає той самий ключ (для темпорального довідника —
    /// у вікні чинності, що перетинається).
    /// </exception>
    public async Task SaveAsync(RegistryDef definition, RegistryEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(entry);

        var keys = await store.ListActiveKeysAsync(definition.Id, ct).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        // ⛔ Блокування діє лише всередині транзакції (§4.3): перевірка й вставка рядка ключа
        // мусять бути одним блоком, інакше між ними пролізе паралельний запис.
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                await ApplyAsync(definition, keys, [entry], token).ConfigureAwait(false);
                await uow.SaveChangesAsync(token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Чи перетинаються два вікна чинності. Та сама умова, що
    /// <see cref="ValidityWindow.OverlapsSegment"/> (§4.4) — другої реалізації немає.
    /// </summary>
    /// <param name="a">Перше вікно.</param>
    /// <param name="b">Друге вікно.</param>
    /// <returns><c>false</c>, якщо хоч одне з вікон порожнє: воно не чинне ніколи.</returns>
    public static bool Overlaps(ValidityWindow a, ValidityWindow b)
        => !a.IsEmpty && !b.IsEmpty
        && a.OverlapsSegment(b.FromInclusive ?? DateOnly.MinValue, b.ToExclusive ?? DateOnly.MaxValue);

    /// <summary>
    /// Блокування тримачів — одним викликом на ключ для всіх хешів пакета, до перевірок записів.
    /// </summary>
    /// <remarks>
    /// ⚠ Лише хеші живих записів: видалений запис ключ не тримає і не перевіряється (поштучний
    /// <c>ApplyAsync</c> плану), тож і блокувати за ним нічого — як і до пакетного читання.
    ///
    /// ⛔ Ключі — у порядку <c>RegistryKeyDefId</c>, а не в порядку переліку: це старша частина
    /// індексу <c>IX_RegistryEntryKey_Hash</c>, і дві транзакції з перетином хешів на кількох ключах
    /// мусять брати замки одним маршрутом (усередині ключа хеші впорядковує сховище). Порядок
    /// переліку дає <c>ListActiveKeysAsync</c>, але спиратися на нього тут — означало б тримати
    /// відсутність взаємоблокувань на сортуванні в чужому запиті.
    /// </remarks>
    private async Task LockHoldersAsync(
        IReadOnlyList<RegistryKeyDef> keys, IReadOnlyList<EntryPlan> plans, CancellationToken ct)
    {
        foreach (var i in Enumerable.Range(0, keys.Count).OrderBy(i => keys[i].Id))
        {
            var hashes = plans
                .Where(p => !p.Entry.IsDeleted)
                .Select(p => p.Keys[i].Hash)
                .OfType<byte[]>()
                .DistinctBy(Convert.ToHexString)
                .ToList();

            if (hashes.Count > 0)
            {
                await store.LockLiveHoldersAsync(keys[i].Id, hashes, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyAsync(
        RegistryDef definition,
        EntryPlan plan,
        ReferenceNames names,
        IReadOnlySet<long> batch,
        CancellationToken ct)
    {
        var (entry, rows, computed) = plan;

        foreach (var key in computed)
        {
            rows.TryGetValue(key.Definition.Id, out var row);

            if (key.Hash is not { } hash)
            {
                row?.Retire();
                continue;
            }

            var keyText = string.Join(KeyTextSeparator, key.Parts.Select(p => Display(p, names)));

            // Видалений запис ключ не тримає (IsLive = 0) — і перевіряти його нема з ким.
            if (!entry.IsDeleted)
            {
                await RequireFreeAsync(definition, entry, key.Definition, hash, batch, ct).ConfigureAwait(false);
            }

            if (row is null)
            {
                store.Add(new RegistryEntryKey(entry, key.Definition.Id, hash, keyText));
            }
            else
            {
                row.Recompute(entry, hash, keyText);
            }
        }
    }

    private async Task RequireFreeAsync(
        RegistryDef definition,
        RegistryEntry entry,
        RegistryKeyDef key,
        byte[] hash,
        IReadOnlySet<long> batch,
        CancellationToken ct)
    {
        var holders = await store
            .FindLiveHoldersForUpdateAsync(key.Id, hash, entry.Id, ct).ConfigureAwait(false);

        // Нетемпоральний довідник: ключ унікальний цілком. Темпоральний — у кожен момент
        // бізнес-часу: дубль у вікні, що не перетинається, законний (§4.4). Тримач із того самого
        // пакета не конфліктує: його ключ переписує цей самий виклик.
        var conflict = holders
            .Where(h => !batch.Contains(h.EntryId))
            .FirstOrDefault(h => !definition.IsTemporal || Overlaps(h.Window, entry.Window));
        if (conflict is null)
        {
            return;
        }

        // ⚠ `keyText` — вигляд ключа ТОГО запису, що його вже тримає: повідомлення каже «інший
        // запис уже має ключ = …», і показати треба те, що людина знайде в ньому, а не своє
        // щойно введене (хеш той самий, текст може відрізнятися регістром і пробілами).
        var invariant = CultureInfo.InvariantCulture;
        var keyText = conflict.KeyText;
        throw new BusinessRuleException(
            ErrorCodes.RegistryKeyConflict,
            $"Запис «{conflict.EntryCode}» довідника «{definition.Code}» уже має ключ {key.Code} = {keyText}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = definition.IsTemporal
                    ? "err.ECR-REG-4092.keyWindowOverlap"
                    : "err.ECR-REG-4092.keyTaken",
                ["registryCode"] = definition.Code,
                ["key"] = key.Code,
                ["keyText"] = keyText,
                ["entryId"] = conflict.EntryId.ToString(invariant),
                ["entryCode"] = conflict.EntryCode,
            });
    }

    private static ComputedKey Compute(
        RegistryDef definition,
        RegistryKeyDef key,
        Dictionary<int, RegistryFieldDef> fields,
        IReadOnlyDictionary<int, RegistryValue> values)
    {
        var parts = key.Fields
            .OrderBy(f => f.Ordinal)
            .Select(part =>
            {
                var field = fields.TryGetValue(part.RegistryFieldDefId, out var found)
                    ? found
                    : throw new InvalidOperationException(
                        $"Ключ «{key.Code}» довідника «{definition.Code}» посилається на поле {part.RegistryFieldDefId}, якого в довіднику немає.");

                values.TryGetValue(field.Id, out var value);
                return new RegistryKeyPart(field.DataType, StoredPart(field.DataType, value));
            })
            .ToList();

        var canonical = RegistryKeyNormalizer.Canonical(parts, key.IgnoreCase);
        return new ComputedKey(key, canonical, canonical is null ? null : RegistryKeyNormalizer.Hash(canonical), parts);
    }

    /// <summary>Збережене значення поля у формі, яку приймає <see cref="RegistryKeyNormalizer"/>.</summary>
    private static object? StoredPart(CellDataType dataType, RegistryValue? value)
    {
        if (value is null)
        {
            return null;
        }

        return dataType switch
        {
            CellDataType.String => value.ValueString,
            CellDataType.Int or CellDataType.Decimal => value.ValueNumeric,
            CellDataType.Bool => value.ValueBool,
            CellDataType.Date => value.ValueDate,
            CellDataType.Lookup => value.ValueRefEntryId,
            CellDataType.Unit => value.ValueUnitId,
            _ => null,
        };
    }

    /// <summary>Коди цілей <c>Lookup</c> і одиниць — одним запитом на вид, лише якщо вони є.</summary>
    private async Task<ReferenceNames> ReferenceNamesAsync(IReadOnlyList<ComputedKey> keys, CancellationToken ct)
    {
        var parts = keys.Where(k => k.Canonical is not null).SelectMany(k => k.Parts).ToList();
        var entryIds = parts.Where(p => p.DataType == CellDataType.Lookup).Select(p => (long)p.Value!).Distinct().ToList();
        var unitIds = parts.Where(p => p.DataType == CellDataType.Unit).Select(p => (int)p.Value!).Distinct().ToList();

        var entries = entryIds.Count == 0
            ? new Dictionary<long, string>()
            : await store.FindEntryCodesAsync(entryIds, ct).ConfigureAwait(false);
        var units = unitIds.Count == 0
            ? new Dictionary<int, string>()
            : await store.FindUnitCodesAsync(unitIds, ct).ConfigureAwait(false);

        return new ReferenceNames(entries, units);
    }

    /// <summary>
    /// Людський вигляд частини (§4.2): текст як введено (без крайніх пробілів), число
    /// інваріантне без хвостових нулів, <c>Lookup</c> — код цілі, <c>Unit</c> — код одиниці.
    /// </summary>
    /// <remarks>
    /// ⚠ Код цілі, а не її назва: <c>KeyText</c> лежить у базі одним рядком для всіх мов, а назва
    /// багатомовна. Частина без знайденого коду показується числом id — текст лише для
    /// повідомлень, унікальність тримає хеш.
    /// </remarks>
    private static string Display(RegistryKeyPart part, ReferenceNames names)
    {
        var invariant = CultureInfo.InvariantCulture;
        return part.DataType switch
        {
            CellDataType.String => ((string)part.Value!).Trim(),
            CellDataType.Bool => (bool)part.Value! ? "true" : "false",
            CellDataType.Lookup => names.Entries.TryGetValue((long)part.Value!, out var code)
                ? code
                : ((long)part.Value!).ToString(invariant),
            CellDataType.Unit => names.Units.TryGetValue((int)part.Value!, out var unit)
                ? unit
                : ((int)part.Value!).ToString(invariant),

            // Число й дата — канонічна форма без тегу: «N:49.9999977539011» → «49.9999977539011».
            _ => RegistryKeyNormalizer.NormalizePart(part.DataType, part.Value)![2..],
        };
    }

    private sealed record ComputedKey(
        RegistryKeyDef Definition, string? Canonical, byte[]? Hash, IReadOnlyList<RegistryKeyPart> Parts);

    /// <summary>Запис пакета з прочитаним: наявні рядки ключів і ключі, обчислені за значеннями.</summary>
    private sealed record EntryPlan(
        RegistryEntry Entry, Dictionary<int, RegistryEntryKey> Rows, IReadOnlyList<ComputedKey> Keys);

    private sealed record ReferenceNames(IReadOnlyDictionary<long, string> Entries, IReadOnlyDictionary<int, string> Units);
}
