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
/// ⚠ Гонку, яку не закрило блокування (перетин двох транзакцій до першого читання), ловить
/// <c>UX_RegistryEntryKey_Live</c>; її мапінг у 409 — крок RT-10b.
/// </remarks>
public sealed class RegistryKeyService(IRegistryKeyStore store, IUnitOfWork uow)
{
    /// <summary>Роздільник частин у людському вигляді ключа (<c>KeyText</c>).</summary>
    public const string KeyTextSeparator = " · ";

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
                await ApplyAsync(definition, entry, keys, token).ConfigureAwait(false);
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

    private async Task ApplyAsync(
        RegistryDef definition, RegistryEntry entry, IReadOnlyList<RegistryKeyDef> keys, CancellationToken ct)
    {
        var fields = definition.Fields.ToDictionary(f => f.Id);
        var values = (await store.ListCurrentValuesAsync(entry, ct).ConfigureAwait(false))
            .ToDictionary(v => v.RegistryFieldDefId);
        var rows = entry.IsPersisted
            ? (await store.ListEntryKeysAsync(entry.Id, ct).ConfigureAwait(false)).ToDictionary(k => k.RegistryKeyDefId)
            : [];

        var computed = keys.Select(key => Compute(definition, key, fields, values)).ToList();
        var names = await ReferenceNamesAsync(computed, ct).ConfigureAwait(false);

        foreach (var key in computed)
        {
            rows.TryGetValue(key.Definition.Id, out var row);

            if (key.Canonical is null)
            {
                row?.Retire();
                continue;
            }

            var hash = RegistryKeyNormalizer.Hash(key.Canonical);
            var keyText = string.Join(KeyTextSeparator, key.Parts.Select(p => Display(p, names)));

            // Видалений запис ключ не тримає (IsLive = 0) — і перевіряти його нема з ким.
            if (!entry.IsDeleted)
            {
                await RequireFreeAsync(definition, entry, key.Definition, hash, ct).ConfigureAwait(false);
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
        RegistryDef definition, RegistryEntry entry, RegistryKeyDef key, byte[] hash, CancellationToken ct)
    {
        var holders = await store
            .FindLiveHoldersForUpdateAsync(key.Id, hash, entry.Id, ct).ConfigureAwait(false);

        // Нетемпоральний довідник: ключ унікальний цілком. Темпоральний — у кожен момент
        // бізнес-часу: дубль у вікні, що не перетинається, законний (§4.4).
        var conflict = holders.FirstOrDefault(h => !definition.IsTemporal || Overlaps(h.Window, entry.Window));
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
        Dictionary<int, RegistryValue> values)
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

        return new ComputedKey(key, RegistryKeyNormalizer.Canonical(parts, key.IgnoreCase), parts);
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

    private sealed record ComputedKey(RegistryKeyDef Definition, string? Canonical, IReadOnlyList<RegistryKeyPart> Parts);

    private sealed record ReferenceNames(IReadOnlyDictionary<long, string> Entries, IReadOnlyDictionary<int, string> Units);
}
