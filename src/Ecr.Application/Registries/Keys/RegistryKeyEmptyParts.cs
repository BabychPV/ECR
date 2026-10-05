// src/Ecr.Application/Registries/Keys/RegistryKeyEmptyParts.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Registries.Keys;

/// <summary>
/// Живі записи без значення в частині первинного ключа (L4-06 / L5-03): такий запис не має рядка
/// ключа (хеш = <c>null</c>, <c>D-153</c>) і тихо лишається поза унікальністю, тож первинний ключ
/// «діє», хоча адресує не всі записи.
/// </summary>
/// <remarks>
/// ⚠ «Порожня» — та сама міра, що в самому ключі: <see cref="RegistryKeyNormalizer.Canonical"/>
/// дає <c>null</c> для відсутнього значення й для рядка з самих пробілів, тож другої реалізації
/// «порожнього» тут немає.
/// </remarks>
public static class RegistryKeyEmptyParts
{
    /// <summary>Скільки записів показувати прикладами.</summary>
    public const int SampleLimit = RegistryKeyDuplicateScan.SampleLimit;

    /// <summary>Шукає живі записи, у яких хоч одна частина ключа порожня.</summary>
    /// <param name="registries">Записи й значення довідника.</param>
    /// <param name="definition">Довідник.</param>
    /// <param name="fields">Поля ключа в порядку частин.</param>
    /// <param name="ignoreCase">Ознака ключа (на порожність не впливає).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Усього таких записів і не більше <see cref="SampleLimit"/> прикладів (за кодом).</returns>
    public static async Task<(int Count, IReadOnlyList<RegistryKeyDuplicateEntryDto> Sample)> FindAsync(
        IRegistryStore registries,
        RegistryDef definition,
        IReadOnlyList<RegistryFieldDef> fields,
        bool ignoreCase,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(fields);

        var live = (await registries.ListEntriesAsync(definition.Id, ct).ConfigureAwait(false))
            .Where(e => !e.IsDeleted)
            .ToList();
        if (live.Count == 0)
        {
            return (0, []);
        }

        var values = (await registries
                .ListValuesForEntriesAsync([.. live.Select(e => e.Id)], ct)
                .ConfigureAwait(false))
            .GroupBy(v => v.RegistryEntryId)
            .ToDictionary(g => g.Key, g => g.GroupBy(v => v.RegistryFieldDefId).ToDictionary(v => v.Key, v => v.First()));

        var empty = new List<Domain.Entities.Dictionaries.RegistryEntry>();
        foreach (var entry in live)
        {
            values.TryGetValue(entry.Id, out var own);
            var parts = fields
                .Select(f => new RegistryKeyPart(
                    f.DataType,
                    RegistryKeyDuplicateScan.StoredPart(f.DataType, own is not null && own.TryGetValue(f.Id, out var v) ? v : null)))
                .ToList();

            if (RegistryKeyNormalizer.Canonical(parts, ignoreCase) is null)
            {
                empty.Add(entry);
            }
        }

        return (
            empty.Count,
            [.. empty.OrderBy(e => e.Code, StringComparer.Ordinal).Take(SampleLimit).Select(e => new RegistryKeyDuplicateEntryDto(e.Id, e.Code))]);
    }

    /// <summary>
    /// Відмова публікації первинного ключа: <c>422 ECR-REG-0422 primaryKeyEmptyParts</c> із прикладами.
    /// </summary>
    /// <param name="definition">Довідник.</param>
    /// <param name="keyCode">Код ключа, що публікується.</param>
    /// <param name="count">Скільки живих записів без значення частини.</param>
    /// <param name="sample">Приклади.</param>
    public static BusinessRuleException Refusal(
        RegistryDef definition, string keyCode, int count, IReadOnlyList<RegistryKeyDuplicateEntryDto> sample)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(sample);

        var entries = count.ToString(CultureInfo.InvariantCulture);
        return new BusinessRuleException(
            "ECR-REG-0422",
            $"Первинний ключ {keyCode} довідника «{definition.Code}» не можна ввімкнути: у {entries} записів немає значення частини ключа.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-REG-0422.primaryKeyEmptyParts",
                ["registryCode"] = definition.Code,
                ["key"] = keyCode,
                ["entries"] = entries,
                ["sample"] = sample,
            });
    }
}
