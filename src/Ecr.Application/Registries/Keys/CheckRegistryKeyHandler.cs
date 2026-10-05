// src/Ecr.Application/Registries/Keys/CheckRegistryKeyHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Registries.Keys;

/// <summary>
/// Жива перевірка дублікатів майбутнього ключа на наявних даних — до збереження опису
/// (RT-11, FEATURE-REGISTRY-TABLES §4.5, <c>AC-4</c>). Право <c>Registry.EditDefinition</c>.
/// </summary>
/// <remarks>
/// ⛔ Той самий алгоритм, що й публікація ключа (<see cref="RegistryKeyDuplicateScan"/>): інакше
/// конструктор казав би «дублікатів немає», а публікація відмовляла б 409 — або навпаки.
/// </remarks>
public sealed class CheckRegistryKeyHandler(
    IRegistryStore registries,
    IRegistryKeyStore keys,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на зміну опису довідника (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.EditDefinition";

    /// <summary>Перевіряє, чи є на наявних даних записи з однаковим значенням ключа.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="request">Поля майбутнього ключа і порівняння тексту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає — <c>ECR-REG-0404</c>.</exception>
    /// <exception cref="BusinessRuleException">Склад ключа неприпустимий — <c>ECR-REG-0422</c>.</exception>
    public async Task<RegistryKeyCheckResponse> HandleAsync(
        string code, RegistryKeyCheckRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
            ?? throw SaveRegistryDefinitionHandler.RegistryNotFound(code);

        // ⛔ L5-08: перевірка ключа віддає значення ключів і приклади записів — заборонений довідник 404.
        RegistryAccess.EnsureNotDenied(profile, definition.Id, code);

        // Первинність тут невідома (її немає в запиті) — обов'язковість полів перевіряє публікація.
        var fields = RegistryKeyFields.Resolve(definition, request.FieldCodes, isPrimary: false);

        return await RegistryKeyDuplicateScan
            .ScanAsync(registries, keys, definition, fields, request.IgnoreCase, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Склад ключа за кодами полів — з відмовами людині (<c>ECR-REG-0422</c>) ДО побудови
/// <see cref="RegistryKeyDef"/>, чий конструктор на тих самих умовах кидає
/// <see cref="ArgumentException"/> (помилка коду, а не введення).
/// </summary>
public static class RegistryKeyFields
{
    /// <summary>Поля ключа в порядку частин.</summary>
    /// <param name="definition">Опис довідника (у пам'яті, разом із ще не збереженими полями).</param>
    /// <param name="fieldCodes">Коди полів у порядку частин.</param>
    /// <param name="isPrimary">Первинний ключ: усі поля мусять бути обов'язковими (<c>D-153</c>).</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0422</c>, ключі <c>keyField*</c>.</exception>
    public static IReadOnlyList<RegistryFieldDef> Resolve(
        RegistryDef definition, IReadOnlyList<string>? fieldCodes, bool isPrimary)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var codes = fieldCodes ?? [];
        if (codes.Count is 0 or > RegistryKeyDef.MaxFields)
        {
            var max = RegistryKeyDef.MaxFields.ToString(CultureInfo.InvariantCulture);
            throw Invalid(
                $"Ключ складається з 1–{max} полів, а передано {codes.Count.ToString(CultureInfo.InvariantCulture)}.",
                "err.ECR-REG-0422.keyFieldCount",
                ("max", max));
        }

        var result = new List<RegistryFieldDef>(codes.Count);
        foreach (var fieldCode in codes)
        {
            var field = definition.Fields.FirstOrDefault(
                    f => string.Equals(f.Code, fieldCode, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid(
                    $"У довіднику «{definition.Code}» немає поля «{fieldCode}».",
                    "err.ECR-REG-0422.keyFieldUnknown",
                    ("registryCode", definition.Code),
                    ("fieldCode", fieldCode ?? string.Empty));

            if (result.Contains(field))
            {
                throw Invalid(
                    $"Поле «{field.Code}» повторюється в ключі.",
                    "err.ECR-REG-0422.keyFieldRepeated",
                    ("fieldCode", field.Code));
            }

            if (!RegistryKeyDef.AllowsPartType(field.DataType))
            {
                throw Invalid(
                    $"Поле «{field.Code}» типу {field.DataType} не може бути частиною ключа: його значення в записі не зберігається.",
                    "err.ECR-REG-0422.keyFieldTypeNotAllowed",
                    ("fieldCode", field.Code),
                    ("dataType", field.DataType.ToString()));
            }

            if (isPrimary && !field.IsRequired)
            {
                throw Invalid(
                    $"Поле «{field.Code}» необов'язкове, а первинний ключ вимагає обов'язкових полів: REGFIND мусить мати повну адресу.",
                    "err.ECR-REG-0422.keyFieldNotRequired",
                    ("fieldCode", field.Code));
            }

            result.Add(field);
        }

        return result;
    }

    /// <remarks>
    /// ⚠ Ключ — повним літералом у точці кидка: сторож <c>ErrorTitleCatalogTests</c> звіряє з сідом
    /// саме літерали, і складений рядок він бачив би як «err.ECR-REG-0422.».
    /// </remarks>
    private static BusinessRuleException Invalid(
        string message, string messageKey, params (string Name, string Value)[] details)
    {
        var dictionary = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        foreach (var (name, value) in details)
        {
            dictionary[name] = value;
        }

        return new BusinessRuleException("ECR-REG-0422", message, dictionary);
    }
}

/// <summary>
/// Пошук дублікатів значення ключа серед живих записів (§4.5): значення полів ключа → канонічний
/// рядок <see cref="RegistryKeyNormalizer"/> → групи; для темпорального довідника — лише записи,
/// вікна чинності яких перетинаються (§4.4).
/// </summary>
/// <remarks>
/// ⛔ Одна реалізація на живу перевірку (<see cref="CheckRegistryKeyHandler"/>) і публікацію ключа
/// (<see cref="SaveRegistryDefinitionHandler"/>): §4.5 вимагає «той самий алгоритм».
///
/// ⚠ Порівнюються канонічні рядки, а не хеші: SHA-256 однозначно відповідає рядку, тож групи ті
/// самі, а рядок у пам'яті дешевший за хеш на ≈ 10 тис. записів FLERT.
///
/// ⚠ Рядок, у якому хоч одна частина ключа порожня, у перевірку не входить (<c>D-153</c>): так
/// само <see cref="RegistryKeyService"/> не пише для нього рядка ключа.
/// </remarks>
public static class RegistryKeyDuplicateScan
{
    /// <summary>Скільки груп дублікатів показувати прикладами.</summary>
    public const int SampleLimit = 20;

    /// <summary>Шукає групи записів з однаковим значенням ключа.</summary>
    /// <param name="registries">Записи й значення довідника.</param>
    /// <param name="keys">Коди цілей <c>Lookup</c> і одиниць для людського вигляду.</param>
    /// <param name="definition">Довідник.</param>
    /// <param name="fields">Поля ключа в порядку частин.</param>
    /// <param name="ignoreCase">Порівнювати текст без урахування регістру.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<RegistryKeyCheckResponse> ScanAsync(
        IRegistryStore registries,
        IRegistryKeyStore keys,
        RegistryDef definition,
        IReadOnlyList<RegistryFieldDef> fields,
        bool ignoreCase,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(fields);

        var live = (await registries.ListEntriesAsync(definition.Id, ct).ConfigureAwait(false))
            .Where(e => !e.IsDeleted)
            .ToList();
        if (live.Count == 0)
        {
            return new RegistryKeyCheckResponse(0, 0, []);
        }

        var values = (await registries
                .ListValuesForEntriesAsync([.. live.Select(e => e.Id)], ct)
                .ConfigureAwait(false))
            .GroupBy(v => v.RegistryEntryId)
            .ToDictionary(g => g.Key, g => g.GroupBy(v => v.RegistryFieldDefId).ToDictionary(v => v.Key, v => v.First()));

        var byCanonical = new Dictionary<string, List<(RegistryEntry Entry, IReadOnlyList<RegistryKeyPart> Parts)>>(StringComparer.Ordinal);
        foreach (var entry in live)
        {
            values.TryGetValue(entry.Id, out var own);
            var parts = fields
                .Select(f => new RegistryKeyPart(
                    f.DataType, StoredPart(f.DataType, own is not null && own.TryGetValue(f.Id, out var v) ? v : null)))
                .ToList();

            if (RegistryKeyNormalizer.Canonical(parts, ignoreCase) is not { } canonical)
            {
                continue;
            }

            if (!byCanonical.TryGetValue(canonical, out var group))
            {
                byCanonical[canonical] = group = [];
            }

            group.Add((entry, parts));
        }

        var duplicates = byCanonical.Values
            .Select(group => definition.IsTemporal
                ? group.Where(a => group.Exists(b => !ReferenceEquals(a.Entry, b.Entry)
                                                     && RegistryKeyService.Overlaps(a.Entry.Window, b.Entry.Window))).ToList()
                : group)
            .Where(group => group.Count > 1)
            .Select(group => group.OrderBy(m => m.Entry.Code, StringComparer.Ordinal).ToList())
            .OrderBy(group => group[0].Entry.Code, StringComparer.Ordinal)
            .ToList();

        var sample = duplicates.Take(SampleLimit).ToList();
        var names = await ReferenceNamesAsync(keys, sample.Select(g => g[0].Parts), ct).ConfigureAwait(false);

        return new RegistryKeyCheckResponse(
            live.Count,
            duplicates.Count,
            [.. sample.Select(group => new RegistryKeyDuplicateDto(
                string.Join(RegistryKeyService.KeyTextSeparator, group[0].Parts.Select(p => Display(p, names))),
                [.. group.Select(m => new RegistryKeyDuplicateEntryDto(m.Entry.Id, m.Entry.Code))]))]);
    }

    /// <summary>
    /// Відмова публікації ключа на даних із дублікатами: <c>409 ECR-REG-4092 existingDuplicates</c>
    /// з прикладами (§4.5, п. 2).
    /// </summary>
    /// <param name="definition">Довідник.</param>
    /// <param name="keyCode">Код ключа, що публікується.</param>
    /// <param name="scan">Результат <see cref="ScanAsync"/> з групами.</param>
    public static BusinessRuleException ExistingDuplicates(
        RegistryDef definition, string keyCode, RegistryKeyCheckResponse scan)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(scan);

        var groups = scan.Groups.ToString(CultureInfo.InvariantCulture);
        return new BusinessRuleException(
            ErrorCodes.RegistryKeyConflict,
            $"Ключ {keyCode} довідника «{definition.Code}» не можна ввімкнути: {groups} значень ключа вже мають кілька записів.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-REG-4092.existingDuplicates",
                ["registryCode"] = definition.Code,
                ["key"] = keyCode,
                ["groups"] = groups,
                ["checked"] = scan.Checked.ToString(CultureInfo.InvariantCulture),
                ["sample"] = scan.Sample,
            });
    }

    /// <summary>Збережене значення поля у формі, яку приймає <see cref="RegistryKeyNormalizer"/>.</summary>
    /// <remarks>Те саме відображення, що в <c>RegistryKeyService</c> (рядки ключів при записі).</remarks>
    internal static object? StoredPart(CellDataType dataType, RegistryValue? value)
        => value is null
            ? null
            : dataType switch
            {
                CellDataType.String => value.ValueString,
                CellDataType.Int or CellDataType.Decimal => value.ValueNumeric,
                CellDataType.Bool => value.ValueBool,
                CellDataType.Date => value.ValueDate,
                CellDataType.Lookup => value.ValueRefEntryId,
                CellDataType.Unit => value.ValueUnitId,
                _ => null,
            };

    /// <summary>Коди цілей <c>Lookup</c> і одиниць — лише для груп прикладу, одним запитом на вид.</summary>
    private static async Task<(IReadOnlyDictionary<long, string> Entries, IReadOnlyDictionary<int, string> Units)> ReferenceNamesAsync(
        IRegistryKeyStore keys, IEnumerable<IReadOnlyList<RegistryKeyPart>> samples, CancellationToken ct)
    {
        var parts = samples.SelectMany(p => p).ToList();
        var entryIds = parts.Where(p => p.DataType == CellDataType.Lookup).Select(p => (long)p.Value!).Distinct().ToList();
        var unitIds = parts.Where(p => p.DataType == CellDataType.Unit).Select(p => (int)p.Value!).Distinct().ToList();

        IReadOnlyDictionary<long, string> entries = entryIds.Count == 0
            ? new Dictionary<long, string>()
            : await keys.FindEntryCodesAsync(entryIds, ct).ConfigureAwait(false);
        IReadOnlyDictionary<int, string> units = unitIds.Count == 0
            ? new Dictionary<int, string>()
            : await keys.FindUnitCodesAsync(unitIds, ct).ConfigureAwait(false);

        return (entries, units);
    }

    /// <summary>Людський вигляд частини — як <c>KeyText</c> рядка ключа (§4.2).</summary>
    private static string Display(
        RegistryKeyPart part,
        (IReadOnlyDictionary<long, string> Entries, IReadOnlyDictionary<int, string> Units) names)
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
            _ => RegistryKeyNormalizer.NormalizePart(part.DataType, part.Value)![2..],
        };
    }
}
